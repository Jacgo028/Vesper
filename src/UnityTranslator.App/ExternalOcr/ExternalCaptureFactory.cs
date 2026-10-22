using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace UnityTranslator.App.ExternalOcr;

internal sealed class ExternalCaptureFactory
{
    private readonly Func<bool> _platformSupported;
    private readonly Func<bool> _apiSupported;
    private readonly Func<IExternalWindowCapture> _createBackend;

    internal ExternalCaptureFactory() : this(
        () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), ProbeSupportedApi, CreateSupportedBackend) { }

    internal ExternalCaptureFactory(Func<bool> platformSupported, Func<bool> apiSupported,
        Func<IExternalWindowCapture> createBackend)
    {
        _platformSupported = platformSupported;
        _apiSupported = apiSupported;
        _createBackend = createBackend;
    }

    internal ExternalOcrAvailability GetAvailability()
    {
        try
        {
            if (!_platformSupported()) return new(false, "外部窗口捕获需要 Windows 10 2004 (19041) 或更高版本。");
            if (!_apiSupported()) return new(false, "此系统不支持 Windows Graphics Capture 窗口捕获。");
            return new(true, "");
        }
        catch (Exception ex) { return new(false, $"窗口捕获 API 不可用：{ex.Message}"); }
    }

    internal IExternalWindowCapture Create()
    {
        var availability = GetAvailability();
        if (!availability.IsAvailable) throw new InvalidOperationException(availability.Reason);
        try { return _createBackend(); }
        catch (Exception ex) { throw new InvalidOperationException($"窗口捕获初始化失败：{ex.Message}", ex); }
    }

    private static bool ProbeSupportedApi() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && ProbeNativeApi();

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static bool ProbeNativeApi() => GraphicsCaptureInterop.IsSupported();

    private static IExternalWindowCapture CreateSupportedBackend()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Windows 10 2004 (19041) required.");
        return CreateNativeBackend();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static IExternalWindowCapture CreateNativeBackend() => new WindowsGraphicsCaptureSource(TimeProvider.System);
}
