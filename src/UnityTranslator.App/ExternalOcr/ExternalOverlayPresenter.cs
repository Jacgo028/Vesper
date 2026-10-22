using System.Windows.Media;
using System.Windows.Threading;

namespace UnityTranslator.App.ExternalOcr;

internal sealed class ExternalOverlayPresenter : IExternalOverlayPresenter
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IExternalOverlayHost? _host;
    private Dispatcher? _dispatcher;
    private OverlayPlacement? _placement;
    private WindowTarget? _target;
    private long _generation;
    private long _revocation;
    private bool _disposed;
    private Task? _disposeTask;
    private Exception? _deferredAlignmentFailure;

    internal ExternalOverlayPresenter() : this(() => new ExternalOverlayWindow()) { }

    internal ExternalOverlayPresenter(Func<IExternalOverlayHost> createHost)
    {
        ArgumentNullException.ThrowIfNull(createHost);
        var thread = new Thread(() => Run(createHost)) { IsBackground = true, Name = "Vesper external OCR overlay" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public ValueTask<OverlayPlacement> AlignAsync(WindowTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        long revocation;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); revocation = _revocation; }
        return new(DispatchAsync(() => Align(target, revocation, ct), ct));
    }

    public ValueTask PresentAsync(OverlayPlacement placement, ExternalOverlayBatch batch, Func<bool> accept,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(accept);
        long generation;
        lock (_gate) { if (_disposed) return ValueTask.CompletedTask; generation = _generation; }
        // IReadOnlyList does not promise immutable storage. Snapshot before the first await.
        var boxes = batch.Boxes.Take(6).ToArray();
        return new(DispatchAsync(() =>
        {
            Matrix transform;
            lock (_gate)
            {
                if (!CanDraw(placement, batch, generation, ct)) return false;
                transform = _host!.TransformFromDevice;
                if (!Matches(placement, transform)) return false;
            }
            var drawing = ExternalOverlayLayout.CreateDrawing(boxes, batch.Stamp.Width, batch.Stamp.Height, transform);
            if (!accept()) return false; // Caller code: runs on the actual drawing dispatcher.
            lock (_gate)
            {
                // accept may itself Hide/Dispose/Align, or another thread may invalidate meanwhile.
                if (!CanDraw(placement, batch, generation, ct) || !Matches(placement, _host!.TransformFromDevice)) return false;
                if (drawing.Children.Count == 0) _host.Hide();
                else _host.Present(drawing);
                // Native messages may reenter this monitor during the visibility operation.
                if (!CanDraw(placement, batch, generation, ct)) _host.Hide();
            }
            return true;
        }, ct));
    }

    public void Hide()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Revoke(clearTarget: true);
            var generation = _generation;
            if (_dispatcher is { HasShutdownStarted: false } dispatcher)
                _ = dispatcher.InvokeAsync(() =>
                {
                    lock (_gate) if (!_disposed && generation == _generation) _host!.Hide();
                });
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            Revoke(clearTarget: true);
            _disposeTask = StopAsync();
            return new(_disposeTask);
        }
    }

    private OverlayPlacement Align(WindowTarget target, long revocation, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (revocation != _revocation) throw AlignmentRevoked();
            if (_deferredAlignmentFailure is { } failure)
            {
                _deferredAlignmentFailure = null;
                throw new InvalidOperationException("External overlay DPI alignment failed.", failure);
            }
            var rect = target.ClientOnScreen;
            if (target.Hwnd == 0 || target.ProcessId <= 0 || rect.Width <= 0 || rect.Height <= 0 ||
                (long)rect.X + rect.Width > int.MaxValue || (long)rect.Y + rect.Height > int.MaxValue)
                throw new ArgumentException("External overlay requires a valid physical client rectangle.", nameof(target));
            var previous = _placement;
            var transform = _host!.TransformFromDevice;
            if (previous?.Target == target && Matches(previous, transform)) return previous;
            var moveOnly = previous is not null && previous.Target.Hwnd == target.Hwnd &&
                previous.Target.ProcessId == target.ProcessId &&
                previous.Target.ClientOnScreen.Width == rect.Width && previous.Target.ClientOnScreen.Height == rect.Height &&
                Matches(previous, transform);
            _target = target;
            _placement = null;
            _generation++;
            try
            {
                if (!moveOnly) _host.Hide();
                _host.Align(rect, moveOnly);
                // WM_DPICHANGED may have fired synchronously inside SetWindowPos.
                if (revocation != _revocation) throw AlignmentRevoked();
                ct.ThrowIfCancellationRequested();
                transform = _host.TransformFromDevice;
                if (!ExternalOverlayLayout.ValidTransform(transform))
                    throw new InvalidOperationException("External overlay has no valid device-to-DIP transform.");
                return _placement = new(target, 1 / transform.M11, 1 / transform.M22);
            }
            catch (OperationCanceledException) when (revocation != _revocation) { throw; }
            catch
            {
                Revoke(clearTarget: true);
                _host.Hide();
                throw;
            }
        }
    }

    private bool CanDraw(OverlayPlacement placement, ExternalOverlayBatch batch, long generation, CancellationToken ct) =>
        !_disposed && !ct.IsCancellationRequested && generation == _generation && ReferenceEquals(placement, _placement) &&
        batch.Stamp.Hwnd == placement.Target.Hwnd && batch.Stamp.Session.ProcessId == placement.Target.ProcessId &&
        batch.Stamp.Width == placement.Target.ClientOnScreen.Width && batch.Stamp.Height == placement.Target.ClientOnScreen.Height &&
        batch.Stamp.DpiScaleX == placement.DpiScaleX && batch.Stamp.DpiScaleY == placement.DpiScaleY;

    private static bool Matches(OverlayPlacement placement, Matrix transform) => ExternalOverlayLayout.ValidTransform(transform) &&
        placement.DpiScaleX == 1 / transform.M11 && placement.DpiScaleY == 1 / transform.M22;

    private void Revoke(bool clearTarget)
    {
        _generation++;
        _revocation++;
        _placement = null;
        if (clearTarget) _target = null;
    }

    private void DpiChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Revoke(clearTarget: false);
            _host!.Hide();
            var target = _target;
            var revocation = _revocation;
            var generation = _generation;
            // WPF processes the message/updates HwndTarget after the hook returns.
            _ = _dispatcher!.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    // A newer Align supersedes this recovery even if it returns to equal geometry.
                    if (_disposed || revocation != _revocation || generation != _generation || target is null) return;
                    try { Align(target, revocation, default); }
                    catch (OperationCanceledException) when (revocation != _revocation) { }
                    catch (Exception ex) { _deferredAlignmentFailure = ex; }
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private async Task<T> DispatchAsync<T>(Func<T> work, CancellationToken ct)
    {
        var dispatcher = await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        if (dispatcher.CheckAccess()) return work();
        return await dispatcher.InvokeAsync(work, DispatcherPriority.Normal, ct).Task.ConfigureAwait(false);
    }

    private void Run(Func<IExternalOverlayHost> createHost)
    {
        Exception? cleanupFailure = null;
        try
        {
            // The native host enters PMv2 before even Dispatcher creates its hidden message HWND.
            _host = createHost();
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _host.DpiChanged += DpiChanged;
            lock (_gate) _dispatcher = dispatcher;
            _ready.TrySetResult(dispatcher);
            Dispatcher.Run();
        }
        catch (Exception ex) { _ready.TrySetException(new InvalidOperationException($"External overlay initialization failed: {ex.Message}", ex)); }
        finally
        {
            try
            {
                if (_host is not null) { _host.DpiChanged -= DpiChanged; _host.Dispose(); }
            }
            catch (Exception ex) { cleanupFailure = ex; }
            try
            {
                // A constructor failure occurs before Dispatcher.Run, but still owns a dispatcher.
                var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is { HasShutdownStarted: false }) dispatcher.InvokeShutdown();
            }
            catch (Exception ex) { cleanupFailure ??= ex; }
            if (cleanupFailure is null) _stopped.TrySetResult();
            else _stopped.TrySetException(cleanupFailure);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            var dispatcher = await _ready.Task.ConfigureAwait(false);
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
        catch (InvalidOperationException) { /* Failed initialization has already unwound on its owner thread. */ }
        await _stopped.Task.ConfigureAwait(false);
    }

    // A transient invalidation, even when ct is not cancelled. The controller may re-observe/re-align.
    private static OperationCanceledException AlignmentRevoked() => new("External overlay alignment was invalidated; re-align using the latest observation.");
}
