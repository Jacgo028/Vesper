using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

internal static class ExternalFrameRules
{
    internal static bool HasDefinedCaptureExtent(int expectedWidth, int expectedHeight,
        int contentWidth, int contentHeight, uint textureWidth, uint textureHeight) =>
        expectedWidth > 0 && expectedHeight > 0 &&
        textureWidth == expectedWidth && textureHeight == expectedHeight &&
        contentWidth >= expectedWidth && contentHeight >= expectedHeight;

    internal static bool TryClientCrop(OcrRect frameScreen, OcrRect clientScreen,
        int frameWidth, int frameHeight, out OcrRect crop)
    {
        crop = new OcrRect(0, 0, 0, 0);
        if (frameWidth <= 0 || frameHeight <= 0 || frameScreen.Width != frameWidth ||
            frameScreen.Height != frameHeight || clientScreen.Width <= 0 || clientScreen.Height <= 0)
            return false;

        // Screen coordinates can be negative. Widen before subtraction/addition so
        // an overflowing rectangle cannot wrap back into the captured frame.
        var x = (long)clientScreen.X - frameScreen.X;
        var y = (long)clientScreen.Y - frameScreen.Y;
        if (x < 0 || y < 0 || x + clientScreen.Width > frameWidth || y + clientScreen.Height > frameHeight)
            return false;

        crop = new OcrRect((int)x, (int)y, clientScreen.Width, clientScreen.Height);
        return true;
    }

    internal static bool CanAccept(ExternalFrameStamp sent, ExternalFrameStamp current, TimeSpan age) =>
        age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(12) &&
        sent.Session == current.Session && sent.Hwnd == current.Hwnd && sent.Epoch == current.Epoch &&
        sent.Width > 0 && sent.Height > 0 && sent.Width == current.Width && sent.Height == current.Height &&
        double.IsFinite(sent.DpiScaleX) && sent.DpiScaleX > 0 && sent.DpiScaleX == current.DpiScaleX &&
        double.IsFinite(sent.DpiScaleY) && sent.DpiScaleY > 0 && sent.DpiScaleY == current.DpiScaleY;

    internal static bool RegionMatches(CapturedClientFrame before, CapturedClientFrame after, OcrRect box)
    {
        if (before.Width != after.Width || before.Height != after.Height ||
            !HasTightPixels(before) || !HasTightPixels(after) ||
            box.Width < 4 || box.Height < 4 || box.X < 0 || box.Y < 0 ||
            (long)box.X + box.Width > before.Width || (long)box.Y + box.Height > before.Height)
            return false;

        var original = before.Bgra32.Span;
        var current = after.Bgra32.Span;
        var delta = 0;
        for (var row = 0; row < 8; row++)
            for (var column = 0; column < 8; column++)
            {
                var x = box.X + (int)((column * 2L + 1) * box.Width / 16);
                var y = box.Y + (int)((row * 2L + 1) * box.Height / 16);
                var offset = (y * before.Width + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                    delta += Math.Abs(original[offset + channel] - current[offset + channel]);
            }
        return delta / (8 * 8 * 3 * 255d) < 0.12;
    }

    private static bool HasTightPixels(CapturedClientFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return false;
        var pixels = (long)frame.Width * frame.Height;
        return pixels <= int.MaxValue / 4 && frame.Bgra32.Length == pixels * 4;
    }
}
