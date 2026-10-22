using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Threading;

namespace UnityTranslator.App.ExternalOcr;

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate nint F9HookProc(int code, nuint message, nint data);
internal interface IWindowsF9Native
{
    nint Install(F9HookProc callback);
    bool Uninstall(nint hook);
    bool IsF9Down();
    (nint Hwnd, int Pid) Foreground();
    nint Next(int code, nuint message, nint data);
}

internal sealed class WindowsF9Source : IExternalF9Source
{
    private readonly F9EventBuffer _buffer;
    private readonly IWindowsF9Native _native;
    private readonly F9HookProc _callback;
    private readonly Thread _thread;
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _dispose;
    private int _stop;

    internal WindowsF9Source() : this(TimeProvider.System, new WindowsF9Native()) { }
    internal WindowsF9Source(TimeProvider clock, IWindowsF9Native native)
    {
        _buffer = new(clock, true);
        _native = native;
        _callback = Hook;
        _thread = new Thread(Run) { IsBackground = true, Name = "Vesper F9 input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetTarget(F9InputLease? target) => _buffer.SetTarget(target);
    public ValueTask<F9InputEvent> ReadAsync(CancellationToken ct) => _buffer.Events.ReadAsync(ct);
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_dispose ??= StopAsync());
    }

    private async Task StopAsync()
    {
        Interlocked.Exchange(ref _stop, 1);
        _buffer.SetTarget(null);
        try
        {
            var dispatcher = await _ready.Task.ConfigureAwait(false);
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        finally
        {
            try { await _stopped.Task.ConfigureAwait(false); }
            finally { await Task.Run(_thread.Join).ConfigureAwait(false); }
        }
    }

    private void Run()
    {
        nint hook = 0;
        Exception? cleanupFailure = null;
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ready.TrySetResult(dispatcher);
            if (Volatile.Read(ref _stop) != 0) return;
            hook = _native.Install(_callback);
            if (hook == 0) { _buffer.Fail(); return; }
            // On the installing thread, before pumping keyboard callbacks. Never inside Hook.
            _buffer.InitializeKeyState(_native.IsF9Down());
            if (Volatile.Read(ref _stop) == 0) Dispatcher.Run();
        }
        catch { _buffer.Fail(); }
        finally
        {
            if (hook != 0)
            {
                try
                {
                    if (!_native.Uninstall(hook))
                        cleanupFailure = new Win32Exception(Marshal.GetLastWin32Error(), "F9 listener cleanup failed.");
                }
                catch (Exception ex) { cleanupFailure = ex; }
            }
            _buffer.Complete();
            _ready.TrySetException(new InvalidOperationException("F9 input thread could not start."));
            if (cleanupFailure is null) _stopped.TrySetResult();
            else _stopped.TrySetException(cleanupFailure);
            GC.KeepAlive(_callback);
        }
    }

    private nint Hook(int code, nuint message, nint data)
    {
        try
        {
            // Only inspect virtual-key identity for non-F9 events. No text/scancode/history.
            if (Volatile.Read(ref _stop) == 0 && code == 0 && message is 0x100 or 0x101 or 0x104 or 0x105 &&
                Marshal.ReadInt32(data) == 0x78)
            {
                var down = message is 0x100 or 0x104;
                var foreground = down ? _native.Foreground() : default;
                _buffer.Key(down, foreground.Hwnd, foreground.Pid);
            }
        }
        catch { _buffer.Fail(); }
        try { return _native.Next(code, message, data); }
        catch { _buffer.Fail(); return 0; } // Never throw across the native callback or consume input.
    }
}

internal sealed class WindowsF9Native : IWindowsF9Native
{
    public nint Install(F9HookProc callback) => SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
    public bool Uninstall(nint hook) => UnhookWindowsHookEx(hook);
    public bool IsF9Down() => (GetAsyncKeyState(0x78) & 0x8000) != 0;
    public (nint Hwnd, int Pid) Foreground()
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        return (hwnd, unchecked((int)pid));
    }
    public nint Next(int code, nuint message, nint data) => CallNextHookEx(0, code, message, data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, F9HookProc callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
}
