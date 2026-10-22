using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

internal enum OcrPresentationMode { Unavailable, Embedded, External }
internal sealed record ExternalOcrAvailability(bool IsAvailable, string Reason);
internal sealed record ExternalOcrSession(string SessionId, string GameId, int ProcessId, string PackageName);
internal sealed record WindowTarget(nint Hwnd, int ProcessId, OcrRect ClientOnScreen, OcrRect FrameOnScreen);
internal sealed record WindowObservation(WindowTarget? Target, bool IsForeground, bool IsMinimized,
    bool IsVisible, bool IsCloaked);
internal sealed record F9InputLease(string SessionId, int ProcessId, nint Hwnd, long Generation);
internal sealed record F9InputEvent(F9InputLease? Lease, long Sequence, long Timestamp, bool Failed = false);
internal interface IExternalF9Source : IAsyncDisposable
{
    void SetTarget(F9InputLease? target);
    ValueTask<F9InputEvent> ReadAsync(CancellationToken ct);
}
internal sealed record OverlayPlacement(WindowTarget Target, double DpiScaleX, double DpiScaleY);

/// <summary>
/// Tightly packed, four-byte BGRA rows owned by the capture producer. The producer
/// must independently copy pixels before returning, never mutate them afterward,
/// and never expose released GPU memory. Sequence counts independent readbacks;
/// Timestamp is their monotonic clock time, even when the game has not redrawn.
/// </summary>
internal sealed record CapturedClientFrame(WindowTarget Target, int Width, int Height,
    ReadOnlyMemory<byte> Bgra32, long Sequence, long Timestamp);

internal sealed record ExternalFrameStamp(ExternalOcrSession Session, nint Hwnd, long Epoch,
    int Width, int Height, double DpiScaleX, double DpiScaleY, long Timestamp);
internal sealed record ExternalOverlayBatch(ExternalFrameStamp Stamp, IReadOnlyList<OcrOverlayItem> Boxes);

internal interface IExternalWindowTracker
{
    WindowObservation Observe(int processId);
}

internal interface IExternalWindowCapture : IAsyncDisposable
{
    ValueTask<CapturedClientFrame> ReadClientAsync(WindowTarget target, CancellationToken ct);
}

internal interface IExternalOverlayPresenter : IAsyncDisposable
{
    ValueTask<OverlayPlacement> AlignAsync(WindowTarget target, CancellationToken ct);
    /// <summary>Re-evaluate accept on the actual drawing Dispatcher before drawing.</summary>
    ValueTask PresentAsync(OverlayPlacement placement, ExternalOverlayBatch batch, Func<bool> accept, CancellationToken ct);
    void Hide();
}

internal interface IExternalOcrControllerFactory
{
    ExternalOcrAvailability GetAvailability();
    IExternalOcrController Create();
}

/// <summary>A controller can be rebound with Start; Epoch must be safe to read from any thread.</summary>
internal interface IExternalOcrController : IAsyncDisposable
{
    void Start(ExternalOcrSession session, Func<OcrTranslationService> createService, bool enabled);
    void SetEnabled(bool enabled);
    void Invalidate(ExternalOcrStopReason reason);
    long Epoch { get; }
    event Action<ExternalOcrStatus> StatusChanged;
    event Action<ExternalOcrEnabledChange> EnabledChanged;
}

internal sealed record ExternalOcrStatus(string SessionId, int ProcessId, long Epoch, string Message);
internal sealed record ExternalOcrEnabledChange(string SessionId, int ProcessId, long Epoch, bool Enabled);
internal enum ExternalOcrStopReason { FocusLost, Disabled, ProcessLost, TargetChanged, CaptureFailed, SessionEnded, Closing }
