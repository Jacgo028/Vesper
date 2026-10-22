using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

internal static class ExternalOcrTestDoubles
{
    internal static ExternalFrameStamp Stamp() => new(
        new ExternalOcrSession("session", "game", 42, "il2cpp-bepinex6"), (nint)123, 7, 32, 24, 1.25, 1.5, 100);

    // CPU-only owned buffers: no WPF dispatcher, window, device, files or settings.
    internal static CapturedClientFrame Frame(int width = 32, int height = 24, byte rgb = 0, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = rgb;
            pixels[offset + 3] = alpha;
        }
        return new CapturedClientFrame(new WindowTarget((nint)123, 42,
            new OcrRect(8, 31, width, height), new OcrRect(0, 0, width + 16, height + 39)),
            width, height, pixels, 1, 100);
    }
}
