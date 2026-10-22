using System.IO;

namespace UnityTranslator.App.ExternalOcr;

internal sealed class ExternalOcrControllerFactory : IExternalOcrControllerFactory
{
    private readonly OcrWorkerHost _worker;
    private readonly ExternalCaptureFactory _captureFactory;

    internal ExternalOcrControllerFactory() : this(Path.Combine(AppContext.BaseDirectory, "ocr"), new ExternalCaptureFactory()) { }

    internal ExternalOcrControllerFactory(string runtimeDirectory, ExternalCaptureFactory captureFactory)
    {
        _worker = new(runtimeDirectory);
        _captureFactory = captureFactory;
    }

    // File/API checks only: neither this method nor construction opens a capture device.
    public ExternalOcrAvailability GetAvailability() => !_worker.IsAvailable
        ? new(false, "本机画面识别组件未安装。") : _captureFactory.GetAvailability();

    public IExternalOcrController Create() => new ExternalOcrController(new WindowsGameWindowTracker(),
        _captureFactory.Create, new ExternalOverlayPresenter(), TimeProvider.System, () => new WindowsF9Source());
}
