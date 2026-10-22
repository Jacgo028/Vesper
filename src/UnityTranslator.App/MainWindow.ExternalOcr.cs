using System.ComponentModel;
using System.IO;
using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public partial class MainWindow
{
    private readonly IExternalOcrControllerFactory _externalOcrFactory;
    private IExternalOcrController? _externalOcrController;
    private TranslationCoordinator? _activeTranslationCoordinator;
    private bool _externalOcrCloseStarted;
    private bool _externalOcrDrained;

    private ExternalOcrAvailability ExternalOcrAvailability()
    {
        try { return _externalOcrFactory.GetAvailability(); }
        catch (Exception ex)
        {
            // Optional Windows capture APIs must not become an application startup gate.
            Log("外部覆盖不可用：" + ex);
            return new(false, ex.Message);
        }
    }

    internal void BindTranslationOcrSession(GameProfile game, TranslationCoordinator coordinator)
    {
        Dispatcher.VerifyAccess();
        if (_closing || _processLost || string.IsNullOrEmpty(CheatSessionId) || CheatProcessId <= 0 ||
            _activeGame?.GameId != game.GameId) return;

        _activeTranslationCoordinator = coordinator;
        PublishOcrSessionCapability(game);
        InvalidateOcrRequests();
        var package = PackageCatalog.ResolvePackageName(game, PreferredLoaderFamily(game));
        var mode = OcrModeSelector.Select(package, ExternalOcrAvailability());
        var worker = _ocrWorker ??= new OcrWorkerHost(Path.Combine(AppContext.BaseDirectory, "ocr"));
        if (mode != OcrPresentationMode.External)
        {
            _externalOcrController?.Invalidate(ExternalOcrStopReason.SessionEnded);
            _ocrService = mode == OcrPresentationMode.Embedded ? new OcrTranslationService(worker, coordinator) : null;
            return;
        }

        _ocrService = null;
        try
        {
            if (_externalOcrController is null)
            {
                var controller = _externalOcrFactory.Create();
                _externalOcrController = controller;
                controller.StatusChanged += status => Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentExternalOcrEvent(controller, status.SessionId, status.ProcessId, status.Epoch))
                        SetRunStatus(status.Message, RunState.Warning);
                });
                controller.EnabledChanged += change => Dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrentExternalOcrEvent(controller, change.SessionId, change.ProcessId, change.Epoch)) return;
                    if (_ocrEnabled != change.Enabled)
                    {
                        _ocrEnabled = change.Enabled;
                        InvalidateOcrRequests();
                    }
                    UpdateOcrPreferencePresentation(SelectedLibraryGame);
                });
            }
            _externalOcrController.Start(new(CheatSessionId, game.GameId, CheatProcessId, package!),
                () => new OcrTranslationService(worker, coordinator), _ocrEnabled);
        }
        catch (Exception ex)
        {
            Log("外部覆盖启动失败：" + ex);
            SetRunStatus("外部覆盖不可用：" + ex.Message, RunState.Warning);
        }
    }

    private bool IsCurrentExternalOcrEvent(IExternalOcrController controller, string session, int pid, long epoch) =>
        !_closing && !_processLost && _activeTranslationCoordinator is not null &&
        ReferenceEquals(_externalOcrController, controller) && session == CheatSessionId &&
        pid == CheatProcessId && epoch == controller.Epoch;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _externalOcrController is null || _externalOcrDrained) return;
        e.Cancel = true;
        if (_externalOcrCloseStarted) return;
        _externalOcrCloseStarted = true;
        _closing = true;
        Hide();
        StopTakeOverWaitOnClose();
        InvalidateOcrRequests();
        _ = DrainExternalOcrAndCloseAsync(_externalOcrController);
    }

    private async Task DrainExternalOcrAndCloseAsync(IExternalOcrController controller)
    {
        try { await controller.DisposeAsync(); }
        catch (Exception ex)
        {
            // The controller reports cleanup failures only after every owned task
            // and the presenter have drained. Observe and retain that failure;
            // a second Dispose attempt cannot prove native cleanup succeeded.
            Log("关闭：外部 OCR 资源清理失败（工作已排空）：" + ex);
        }
        _externalOcrDrained = true;
        // Even a synchronously completed disposal must leave the first Close stack.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
