using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace UnityTranslator.App.ExternalOcr;

// The replaceable boundary owns native capture resources; callers own returned pixels.
internal sealed record NativeCaptureReadback(int Width, int Height, int RowPitch, byte[] Pixels);
internal interface INativeWindowCapture : IDisposable
{
    ValueTask<NativeCaptureReadback> ReadAsync(WindowTarget target, CancellationToken ct);
    void Reset();
}

/// <summary>Only this boundary touches WGC/D3D. Frame callbacks copy GPU-to-GPU;
/// CPU staging/map is performed only for an explicit read request.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class NativeWindowCapture : INativeWindowCapture
{
    private readonly object _sync = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _latest;
    private WindowTarget? _target;
    private Exception? _failure;
    private bool _disposed;
    private TaskCompletionSource _ready = NewReady();

    internal NativeWindowCapture()
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                out device, out _, out context).CheckError();
            _winrtDevice = GraphicsCaptureInterop.CreateDevice(device);
            _device = device;
            _context = context;
        }
        catch { context?.Dispose(); device?.Dispose(); throw; }
    }

    public async ValueTask<NativeCaptureReadback> ReadAsync(WindowTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool changed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = _target != target;
        }
        if (changed) { Reset(); Start(target); }
        Task ready;
        lock (_sync) ready = _ready.Task;
        try { await ready.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new InvalidOperationException("一秒内未收到有效窗口帧。", ex); }
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null) throw new InvalidOperationException("窗口捕获失效。", _failure);
            if (_latest is null || _target != target) throw new InvalidOperationException("没有当前目标的有效窗口帧。");
            _device.DeviceRemovedReason.CheckError();
            var description = _latest.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            using var staging = _device.CreateTexture2D(description);
            _context.CopyResource(staging, _latest);
            _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                var width = checked((int)description.Width);
                var height = checked((int)description.Height);
                var rowBytes = checked(width * 4);
                var pitch = checked((int)mapped.RowPitch);
                if (pitch < rowBytes || mapped.DataPointer == 0)
                    throw new InvalidOperationException("D3D staging row pitch is invalid.");
                // Copy only valid row bytes; driver padding is never exposed or interpreted as pixels.
                var pixels = new byte[checked(rowBytes * height)];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(mapped.DataPointer + checked(y * pitch), pixels, y * rowBytes, rowBytes);
                _device.DeviceRemovedReason.CheckError();
                return new(width, height, rowBytes, pixels);
            }
            finally { _context.Unmap(staging, 0); }
        }
    }

    private void Start(WindowTarget target)
    {
        if (WindowsGameWindowTracker.TryGetTarget(target.Hwnd, target.ProcessId) != target)
            throw new InvalidOperationException("目标窗口在创建捕获前发生变化。");
        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            item = GraphicsCaptureInterop.CreateItem(target.Hwnd);
            var size = item.Size;
            if (size.Width != target.FrameOnScreen.Width || size.Height != target.FrameOnScreen.Height)
                throw new InvalidOperationException("捕获项目与窗口物理边界不一致。");
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            lock (_sync)
            {
                _target = target; _item = item; _pool = pool; _session = session;
                _failure = null; _ready = NewReady();
            }
            item.Closed += OnClosed;
            pool.FrameArrived += OnFrameArrived;
            session.StartCapture();
        }
        catch
        {
            if (pool is not null) pool.FrameArrived -= OnFrameArrived;
            if (item is not null) item.Closed -= OnClosed;
            lock (_sync) { _target = null; _item = null; _pool = null; _session = null; }
            session?.Dispose(); pool?.Dispose();
            throw;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_sync)
        {
            if (_disposed || sender != _pool || _failure is not null) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                if (_target is null || WindowsGameWindowTracker.TryGetTarget(_target.Hwnd, _target.ProcessId) != _target)
                    throw new InvalidOperationException("目标窗口几何或进程已改变。");
                using var surface = frame.Surface;
                using var texture = GraphicsCaptureInterop.GetTexture(surface);
                var description = texture.Description;
                var content = frame.ContentSize;
                // ContentSize can over-report the surface after a window restore.
                // Keep the verified DWM/item extent and actual texture dimensions;
                // use ContentSize only to reject undefined pixels, never to guess
                // a different crop origin or resize this fixed-target stream.
                if (!ExternalFrameRules.HasDefinedCaptureExtent(_target.FrameOnScreen.Width,
                        _target.FrameOnScreen.Height, content.Width, content.Height,
                        description.Width, description.Height) ||
                    description.Format != Format.B8G8R8A8_UNorm || description.ArraySize != 1 ||
                    description.MipLevels != 1 || description.SampleDescription.Count != 1)
                    throw new InvalidOperationException("捕获纹理尺寸或格式无法验证。");
                if (_latest is null)
                {
                    description.Usage = ResourceUsage.Default;
                    description.BindFlags = BindFlags.None;
                    description.CPUAccessFlags = CpuAccessFlags.None;
                    description.MiscFlags = ResourceOptionFlags.None;
                    _latest = _device.CreateTexture2D(description);
                }
                // A real geometry change is retired/rebound by the controller.
                // Copy while checked out; never retain a returned capture surface.
                _context.CopyResource(_latest, texture);
                _device.DeviceRemovedReason.CheckError();
                _ready.TrySetResult();
            }
            catch (Exception ex) { Invalidate(ex); } // Native worker failures become read failures.
        }
    }

    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        lock (_sync)
            if (!_disposed && sender == _item) Invalidate(new InvalidOperationException("目标捕获窗口已关闭。"));
    }

    private void Invalidate(Exception failure)
    {
        _failure = failure;
        _latest?.Dispose(); _latest = null;
        _ready.TrySetResult(); // Wake a waiter to observe _failure; no unobserved task exception.
    }

    public void Reset()
    {
        GraphicsCaptureItem? item;
        Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session;
        lock (_sync)
        {
            item = _item; pool = _pool; session = _session;
            _item = null; _pool = null; _session = null; _target = null;
            _latest?.Dispose(); _latest = null;
            _ready.TrySetResult();
            _failure = null;
        }
        // Close outside the lock: Close can wait for a worker callback that also needs it.
        if (pool is not null) pool.FrameArrived -= OnFrameArrived;
        if (item is not null) item.Closed -= OnClosed;
        try { session?.Dispose(); }
        finally { pool?.Dispose(); }
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        try { Reset(); }
        finally { _winrtDevice.Dispose(); _context.Dispose(); _device.Dispose(); }
    }
    private static TaskCompletionSource NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class WindowsGraphicsCaptureSource : IExternalWindowCapture
{
    private readonly TimeProvider _clock;
    private readonly Func<nint, int, WindowTarget?> _observe;
    private readonly INativeWindowCapture _native;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _sequence;
    private int _disposed;

    [SupportedOSPlatform("windows10.0.19041.0")]
    internal WindowsGraphicsCaptureSource(TimeProvider clock) : this(clock,
        WindowsGameWindowTracker.TryGetTarget, new NativeWindowCapture()) { }

    internal WindowsGraphicsCaptureSource(TimeProvider clock,
        Func<nint, int, WindowTarget?> observe, INativeWindowCapture native)
    {
        _clock = clock;
        _observe = observe;
        _native = native;
    }

    public async ValueTask<CapturedClientFrame> ReadClientAsync(WindowTarget target, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_readGate.Wait(0)) throw new InvalidOperationException("一次只能读取一个窗口帧。");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            ValidateTarget(target);
            var readback = await _native.ReadAsync(target, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            ValidateTarget(target);
            if (!ExternalFrameRules.TryClientCrop(target.FrameOnScreen, target.ClientOnScreen,
                    readback.Width, readback.Height, out var crop) ||
                readback.RowPitch < (long)readback.Width * 4 ||
                readback.Pixels.LongLength < (long)readback.RowPitch * readback.Height ||
                (long)crop.Width * crop.Height > int.MaxValue / 4)
                throw new InvalidOperationException("窗口帧尺寸或客户端裁剪无法验证。");
            var rowBytes = checked(crop.Width * 4);
            var pixels = new byte[checked(rowBytes * crop.Height)];
            for (var y = 0; y < crop.Height; y++)
                Buffer.BlockCopy(readback.Pixels, checked((y + crop.Y) * readback.RowPitch + crop.X * 4),
                    pixels, y * rowBytes, rowBytes);
            return new(target, crop.Width, crop.Height, pixels, ++_sequence, _clock.GetTimestamp());
        }
        catch
        {
            _native.Reset();
            throw;
        }
        finally { _readGate.Release(); }
    }

    private void ValidateTarget(WindowTarget target)
    {
        if (target.Hwnd == 0 || target.ProcessId <= 0 || _observe(target.Hwnd, target.ProcessId) != target)
            throw new InvalidOperationException("目标窗口、进程或物理尺寸已改变。");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _readGate.WaitAsync().ConfigureAwait(false);
        try { _native.Dispose(); }
        finally { _readGate.Release(); _lifetime.Dispose(); }
    }
}
