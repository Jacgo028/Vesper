using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

/// <summary>
/// Keeps state observation separate from capture/provider waits. One global batch
/// slot survives Start/rebinding so mutable OCR history is never used concurrently.
/// </summary>
internal sealed class ExternalOcrController(IExternalWindowTracker tracker,
    Func<IExternalWindowCapture> createCapture, IExternalOverlayPresenter presenter, TimeProvider clock,
    Func<IExternalF9Source>? createF9 = null) : IExternalOcrController
{
    private readonly object _gate = new();
    private readonly List<Task> _runs = [];
    private Binding? _current;
    private Task _batch = Task.CompletedTask;
    private Task _captureRetirement = Task.CompletedTask;
    private Task _inputRetirement = Task.CompletedTask;
    private readonly List<(IExternalF9Source Source, Exception Error)> _failedInputSources = [];
    private long _inputGeneration;
    // A failed native Dispose may make later calls no-ops. Keep both ownership and
    // the first failure until controller teardown; success cannot be inferred by retry.
    private readonly List<(IExternalWindowCapture Capture, Exception Error)> _failedRetirements = [];
    private Task? _dispose;
    private long _epoch;
    private long? _lastOcr;
    private int _frameId;
    private bool _disposed;
    public long Epoch { get { lock (_gate) return _epoch; } }
    public event Action<ExternalOcrStatus>? StatusChanged;
    public event Action<ExternalOcrEnabledChange>? EnabledChanged;
    public void Start(ExternalOcrSession session, Func<OcrTranslationService> createService, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(createService);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is { } old) Stop(old);
            var binding = new Binding(session, createService, enabled);
            _current = binding;
            Revoke(binding);
            if (createF9 is not null && ValidSession(binding))
            {
                var previous = _inputRetirement;
                binding.Input = _inputRetirement = Task.Run(() => RunInputAsync(binding, previous));
            }
            // Capture/service creation remains lazy. The timer is armed before Start returns.
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
            _runs.Add(Task.Run(() => RunAsync(binding, timer)));
        }
    }

    public void SetEnabled(bool enabled)
    {
        ExternalOcrEnabledChange? change;
        lock (_gate)
        {
            change = null;
            if (_current is { } binding && IsLive(binding))
            {
                var lease = binding.InputLease;
                if (lease is not null && !binding.InputFailed)
                {
                    // One atomic buffer replacement flushes old edges without an untargeted gap.
                    binding.InputLease = lease with { Generation = ++_inputGeneration };
                    binding.F9Source?.SetTarget(binding.InputLease);
                }
                else ResetInput(binding);
                change = ChangeEnabled(binding, enabled);
            }
        }
        if (change is not null) EnabledChanged?.Invoke(change);
    }

    public void Invalidate(ExternalOcrStopReason reason)
    {
        lock (_gate)
        {
            if (_current is not { } binding || !IsLive(binding)) return;
            if (binding.InvalidatedReason == reason) return;
            Revoke(binding);
            binding.InvalidatedReason = reason;
            ResetInput(binding);
            if (reason is ExternalOcrStopReason.ProcessLost or ExternalOcrStopReason.SessionEnded or ExternalOcrStopReason.Closing)
            {
                binding.Stop.Cancel();
                _current = null;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            if (_current is { } binding) Stop(binding);
            _current = null;
            _disposed = true;
            _dispose = DrainAsync();
            return new(_dispose);
        }
    }

    private async Task DrainAsync()
    {
        var errors = new List<Exception>();
        var runs = Task.WhenAll(_runs);
        try { await runs.ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (runs.Exception is { } failures) errors.AddRange(failures.InnerExceptions);
            else errors.Add(ex);
        }
        try { await presenter.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        lock (_gate) errors.AddRange(_failedRetirements.Select(failure => failure.Error));
        lock (_gate) errors.AddRange(_failedInputSources.Select(failure => failure.Error));
        if (errors.Count != 0) throw new AggregateException("External OCR cleanup did not complete successfully.", errors);
    }

    private async Task RunAsync(Binding binding, PeriodicTimer timer)
    {
        try
        {
            do
            {
                try { await ObserveAsync(binding).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* Hide/DPI can revoke Align while its caller token is live. */ }
                catch (Exception ex) { Fail(binding, ex); }
            } while (await timer.WaitForNextTickAsync(binding.Stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (binding.Stop.IsCancellationRequested) { }
        finally
        {
            timer.Dispose();
            // Ignored cancellation must not release the capture or shared coordinator early.
            try { await Task.WhenAll(binding.Batch, binding.Validation, binding.Retirement, binding.Input).ConfigureAwait(false); }
            finally
            {
                // Normally Revoke already retired it; also cover unexpected observation-loop exit.
                try { if (binding.Capture is not null) await RetireCaptureAsync(binding).ConfigureAwait(false); }
                finally
                {
                    binding.ReadGate.Dispose();
                    binding.Work.Dispose();
                    foreach (var retired in binding.RetiredTokens) retired.Dispose();
                    binding.Stop.Dispose();
                }
            }
        }
    }

    private async Task ObserveAsync(Binding binding)
    {
        lock (_gate) if (!IsLive(binding)) return;
        var observation = tracker.Observe(binding.Session.ProcessId);
        WindowTarget target;
        long epoch;
        lock (_gate)
        {
            if (!IsLive(binding)) return;
            UpdateInput(binding, observation);
            var normal = _failedRetirements.Count == 0 && ValidTarget(binding, observation);
            if (!normal)
            {
                if (binding.Eligible || binding.Placement is not null) Revoke(binding);
                return;
            }
            target = observation.Target!;
            if (binding.Target is not null && binding.Target != target) Revoke(binding);
            binding.Target = target;
            binding.Eligible = binding.Enabled;
            if (binding.Eligible) binding.InvalidatedReason = null;
            epoch = _epoch;
        }
        lock (_gate) if (!IsLive(binding) || !binding.Eligible) return;
        var placement = await presenter.AlignAsync(target, binding.Stop.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (!IsLive(binding) || !binding.Eligible || epoch != _epoch || binding.Target != target) return;
            if (binding.Placement is not null && !ReferenceEquals(binding.Placement, placement))
                Revoke(binding, hide: false); // Align already revoked old geometry/DPI content.
            binding.Eligible = binding.Enabled;
            binding.Target = target;
            binding.Placement = placement;
            var now = clock.GetTimestamp();
            if (binding.Display is { } display &&
                (!Accept(binding, display.Stamp, placement) || clock.GetElapsedTime(display.ShownAt, now) >= TimeSpan.FromSeconds(8)))
            {
                Revoke(binding);
                return;
            }
            if (binding.Display is { } visible && binding.Validation.IsCompleted &&
                (binding.LastRead is null || clock.GetElapsedTime(binding.LastRead.Value, now) >= TimeSpan.FromMilliseconds(250)))
            {
                var token = binding.Work.Token;
                binding.Validation = Task.Run(() => ValidateAsync(binding, visible, placement, token));
            }
            if (_batch.IsCompleted && (_lastOcr is null || clock.GetElapsedTime(_lastOcr.Value, now) >= TimeSpan.FromSeconds(2)))
            {
                _lastOcr = now;
                var stamp = Stamp(binding, placement, now);
                var token = binding.Work.Token;
                binding.Batch = _batch = Task.Run(() => ProcessAsync(binding, placement, stamp, token));
            }
        }
    }

    private async Task ProcessAsync(Binding binding, OverlayPlacement placement, ExternalFrameStamp requested, CancellationToken ct)
    {
        try
        {
            var frame = await ReadAsync(binding, placement.Target, ct).ConfigureAwait(false);
            var stamp = requested with { Timestamp = frame.Timestamp };
            OcrTranslationService service;
            bool freshService;
            lock (_gate)
            {
                if (!Accept(binding, stamp, placement)) return;
                if (!ValidFrame(frame, placement.Target))
                    throw new InvalidOperationException("Capture returned an invalid client frame.");
                freshService = binding.ServiceEpoch != stamp.Epoch;
            }
            // Factories can be slow too. No external work runs while holding the observation lock.
            service = freshService ? binding.CreateService() : binding.Service!;
            lock (_gate)
            {
                if (!Accept(binding, stamp, placement)) return;
                if (freshService)
                {
                    // Only the controller-wide exclusive batch may touch mutable service history.
                    binding.Service = service;
                    binding.ServiceEpoch = stamp.Epoch;
                    binding.LastSequence = null;
                }
                if (binding.LastSequence is { } previous && frame.Sequence <= previous) return;
                binding.LastSequence = frame.Sequence;
            }
            using var owned = await OwnedOcrFrame.CreateAsync(frame, Interlocked.Increment(ref _frameId), ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Accept(binding, stamp, placement)) return;
                // Slow capture/encoding must not consume part of the next OCR interval.
                _lastOcr = clock.GetTimestamp();
            }
            var boxes = (await service.ProcessAsync(owned.Frame, ct).ConfigureAwait(false)).Take(6).ToArray();
            lock (_gate) if (!Accept(binding, stamp, placement)) return;
            if (boxes.Length == 0)
            {
                lock (_gate) if (Accept(binding, stamp, placement) && binding.Display is not null) Revoke(binding);
                return;
            }
            var latest = await ReadAsync(binding, placement.Target, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Accept(binding, stamp, placement)) return;
                if (!RegionsMatch(frame, latest, boxes)) { Revoke(binding); return; }
            }
            await presenter.PresentAsync(placement, new(stamp, boxes), () =>
            {
                lock (_gate)
                {
                    if (!Accept(binding, stamp, placement)) return false;
                    binding.Display = new(stamp, frame, boxes, clock.GetTimestamp());
                    return true;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Fail(binding, ex, requested.Epoch); }
    }

    private async Task ValidateAsync(Binding binding, Displayed display, OverlayPlacement placement, CancellationToken ct)
    {
        try
        {
            var current = await ReadAsync(binding, placement.Target, ct).ConfigureAwait(false);
            lock (_gate)
                if (ReferenceEquals(binding.Display, display) &&
                    (!Accept(binding, display.Stamp, placement) || !RegionsMatch(display.Frame, current, display.Boxes)))
                    Revoke(binding);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Fail(binding, ex, display.Stamp.Epoch); }
    }

    private async Task<CapturedClientFrame> ReadAsync(Binding binding, WindowTarget target, CancellationToken ct)
    {
        Task retirement;
        lock (_gate) retirement = _captureRetirement;
        // Rebinding also waits for the previous session's native stream to finish disposal.
        await retirement.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate)
            if (_failedRetirements.Count != 0)
                throw new AggregateException("A previous capture stream could not be retired safely.",
                    _failedRetirements.Select(failure => failure.Error));
        await binding.ReadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TimeSpan wait;
            lock (_gate) wait = binding.LastRead is { } last
                ? TimeSpan.FromMilliseconds(250) - clock.GetElapsedTime(last) : TimeSpan.Zero;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!IsLive(binding) || !binding.Eligible || binding.Target != target) throw new OperationCanceledException(ct);
            }
            // ReadGate owns backend creation; binding shutdown drains this task before disposal.
            binding.Capture ??= createCapture();
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!IsLive(binding) || !binding.Eligible || binding.Target != target) throw new OperationCanceledException(ct);
                binding.LastRead = clock.GetTimestamp();
            }
            return await binding.Capture.ReadClientAsync(target, ct).ConfigureAwait(false);
        }
        finally { binding.ReadGate.Release(); }
    }

    private bool Accept(Binding binding, ExternalFrameStamp stamp, OverlayPlacement placement) =>
        _failedRetirements.Count == 0 && IsLive(binding) && binding.Enabled && binding.Eligible && ReferenceEquals(placement, binding.Placement) &&
        ExternalFrameRules.CanAccept(stamp, Stamp(binding, placement, clock.GetTimestamp()), clock.GetElapsedTime(stamp.Timestamp));

    private ExternalFrameStamp Stamp(Binding binding, OverlayPlacement placement, long timestamp) =>
        new(binding.Session, placement.Target.Hwnd, _epoch, placement.Target.ClientOnScreen.Width,
            placement.Target.ClientOnScreen.Height, placement.DpiScaleX, placement.DpiScaleY, timestamp);

    private static bool ValidFrame(CapturedClientFrame frame, WindowTarget target) => frame.Target == target &&
        frame.Width == target.ClientOnScreen.Width && frame.Height == target.ClientOnScreen.Height &&
        frame.Width > 0 && frame.Height > 0 && (long)frame.Width * frame.Height * 4 == frame.Bgra32.Length;

    private static bool RegionsMatch(CapturedClientFrame before, CapturedClientFrame after, IReadOnlyList<OcrOverlayItem> boxes) =>
        ValidFrame(after, before.Target) && boxes.All(box => ExternalFrameRules.RegionMatches(before, after,
            new(box.X, box.Y, box.Width, box.Height)));

    private bool IsLive(Binding binding) => !_disposed && ReferenceEquals(binding, _current) && !binding.Stop.IsCancellationRequested;

    private static bool ValidSession(Binding binding) => binding.Session.ProcessId > 0 &&
        !string.IsNullOrWhiteSpace(binding.Session.SessionId) &&
        OcrModeSelector.Select(binding.Session.PackageName, new(true, "")) == OcrPresentationMode.External;

    private static bool ValidTarget(Binding binding, WindowObservation observation) => ValidSession(binding) &&
        observation.Target is { Hwnd: not 0 } target && target.ProcessId == binding.Session.ProcessId &&
        observation.IsForeground && observation.IsVisible && !observation.IsMinimized && !observation.IsCloaked;

    private void ResetInput(Binding binding)
    {
        binding.InputLease = null;
        binding.F9Source?.SetTarget(null);
    }

    private void UpdateInput(Binding binding, WindowObservation observation)
    {
        if (binding.InputFailed || !ValidTarget(binding, observation)) { ResetInput(binding); return; }
        var target = observation.Target!;
        if (binding.InputLease is { } lease && lease.ProcessId == target.ProcessId && lease.Hwnd == target.Hwnd) return;
        binding.InputLease = new(binding.Session.SessionId, target.ProcessId, target.Hwnd, ++_inputGeneration);
        binding.F9Source?.SetTarget(binding.InputLease);
    }

    private async Task RunInputAsync(Binding binding, Task previous)
    {
        IExternalF9Source? source = null;
        try
        {
            await previous.ConfigureAwait(false);
            bool previousFailed;
            lock (_gate)
            {
                if (!IsLive(binding)) return;
                previousFailed = _failedInputSources.Count != 0;
            }
            if (previousFailed) { InputFailed(binding); return; }
            source = createF9!();
            lock (_gate)
            {
                if (!IsLive(binding)) return;
                binding.F9Source = source;
                source.SetTarget(binding.InputLease);
            }
            while (true)
            {
                var input = await source.ReadAsync(binding.Stop.Token).ConfigureAwait(false);
                if (input.Failed) { InputFailed(binding); break; }
                var observation = tracker.Observe(binding.Session.ProcessId);
                ExternalOcrEnabledChange? change = null;
                lock (_gate)
                {
                    if (!IsLive(binding) || binding.InputFailed) continue;
                    if (!ValidTarget(binding, observation)) { ResetInput(binding); continue; }
                    if (input.Lease is null || input.Lease != binding.InputLease ||
                        input.Lease.Hwnd != observation.Target!.Hwnd || input.Sequence <= binding.LastInputSequence) continue;
                    var age = clock.GetElapsedTime(input.Timestamp);
                    if (age < TimeSpan.Zero || age > TimeSpan.FromMilliseconds(500)) continue;
                    binding.LastInputSequence = input.Sequence;
                    change = ChangeEnabled(binding, !binding.Enabled);
                }
                if (change is not null) EnabledChanged?.Invoke(change);
            }
        }
        catch (OperationCanceledException) when (binding.Stop.IsCancellationRequested) { }
        catch { InputFailed(binding); }
        finally
        {
            if (source is not null)
            {
                try { source.SetTarget(null); await source.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { lock (_gate) _failedInputSources.Add((source, ex)); InputFailed(binding); }
                finally { lock (_gate) if (ReferenceEquals(binding.F9Source, source)) binding.F9Source = null; }
            }
        }
    }

    private void InputFailed(Binding binding)
    {
        ExternalOcrStatus? status = null;
        lock (_gate)
        {
            if (!IsLive(binding) || binding.InputFailed) return;
            binding.InputFailed = true;
            ResetInput(binding);
            status = new(binding.Session.SessionId, binding.Session.ProcessId, _epoch,
                "F9 快捷键暂不可用，请使用软件中的识图开关");
        }
        StatusChanged?.Invoke(status);
    }

    private ExternalOcrEnabledChange? ChangeEnabled(Binding binding, bool enabled)
    {
        if (binding.Enabled == enabled) return null;
        binding.Enabled = enabled;
        Revoke(binding);
        return new(binding.Session.SessionId, binding.Session.ProcessId, _epoch, enabled);
    }

    private void Revoke(Binding binding, bool hide = true)
    {
        _epoch++;
        binding.Eligible = false;
        binding.Placement = null;
        binding.Display = null;
        binding.Work.Cancel();
        binding.RetiredTokens.Add(binding.Work);
        binding.Work = new();
        if (hide) presenter.Hide();
        var previousRetirement = _captureRetirement;
        var epoch = _epoch;
        binding.Retirement = _captureRetirement = Task.Run(async () =>
        {
            await previousRetirement.ConfigureAwait(false);
            await RetireCaptureAsync(binding, epoch).ConfigureAwait(false);
        });
    }

    private async Task RetireCaptureAsync(Binding binding, long? expectedEpoch = null)
    {
        // A read that ignores cancellation still owns its backend until it returns.
        // This wait never holds the controller state lock or blocks its monitor.
        await binding.ReadGate.WaitAsync().ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            if (binding.RetirementFailure is not null) return;
            var capture = binding.Capture;
            if (capture is null) return;
            try
            {
                await capture.DisposeAsync().ConfigureAwait(false);
                binding.Capture = null; // Release ownership only after confirmed successful retirement.
            }
            catch (Exception ex)
            {
                failure = ex;
                lock (_gate)
                {
                    binding.RetirementFailure = ex;
                    _failedRetirements.Add((capture, ex));
                }
            }
        }
        finally { binding.ReadGate.Release(); }
        // UI relevance may have changed; retained ownership/error state above never depends on it.
        if (failure is not null) Fail(binding, failure, expectedEpoch);
    }

    private void Stop(Binding binding)
    {
        ResetInput(binding);
        Revoke(binding);
        binding.Stop.Cancel();
    }

    private void Fail(Binding binding, Exception error, long? expectedEpoch = null)
    {
        ExternalOcrStatus? status = null;
        lock (_gate)
            if (IsLive(binding) && (expectedEpoch is null || expectedEpoch == _epoch))
            {
                Revoke(binding);
                status = new(binding.Session.SessionId, binding.Session.ProcessId, _epoch, $"外部画面识别暂不可用：{error.Message}");
            }
        if (status is not null) StatusChanged?.Invoke(status);
    }

    private sealed class Binding(ExternalOcrSession session, Func<OcrTranslationService> createService, bool enabled)
    {
        internal readonly ExternalOcrSession Session = session;
        internal readonly Func<OcrTranslationService> CreateService = createService;
        internal readonly CancellationTokenSource Stop = new();
        internal CancellationTokenSource Work = new();
        internal readonly List<CancellationTokenSource> RetiredTokens = [];
        internal readonly SemaphoreSlim ReadGate = new(1, 1);
        internal bool Enabled = enabled, Eligible, InputFailed;
        internal F9InputLease? InputLease;
        internal IExternalF9Source? F9Source;
        internal long LastInputSequence;
        internal Task Input = Task.CompletedTask;
        internal ExternalOcrStopReason? InvalidatedReason;
        internal WindowTarget? Target;
        internal OverlayPlacement? Placement;
        internal IExternalWindowCapture? Capture;
        internal Exception? RetirementFailure;
        internal OcrTranslationService? Service;
        internal long ServiceEpoch = -1;
        internal long? LastSequence, LastRead;
        internal Task Batch = Task.CompletedTask, Validation = Task.CompletedTask, Retirement = Task.CompletedTask;
        internal Displayed? Display;
    }

    private sealed record Displayed(ExternalFrameStamp Stamp, CapturedClientFrame Frame, IReadOnlyList<OcrOverlayItem> Boxes, long ShownAt);
}
