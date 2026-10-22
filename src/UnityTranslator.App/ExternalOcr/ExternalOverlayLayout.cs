using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

internal static class ExternalOverlayLayout
{
    private static readonly Typeface Typeface = new("Microsoft YaHei UI");
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly SolidColorBrush Mask = MakeMask();

    internal static bool TryFit(string text, double widthDip, double heightDip, double maxFontDip, out double fontDip)
    {
        var fit = Fit(text, widthDip, heightDip, maxFontDip, 1, out _, out fontDip);
        return fit is not null;
    }

    internal static DrawingGroup CreateDrawing(IReadOnlyList<OcrOverlayItem> boxes, int clientWidth,
        int clientHeight, Matrix fromDevice)
    {
        var drawing = new DrawingGroup();
        if (clientWidth <= 0 || clientHeight <= 0 || !ValidTransform(fromDevice)) return drawing;
        foreach (var item in boxes.Take(6))
        {
            if (item is null || item.X < 0 || item.Y < 0 || item.Width <= 0 || item.Height <= 0 ||
                (long)item.X + item.Width > clientWidth || (long)item.Y + item.Height > clientHeight) continue;
            var bounds = new Rect(item.X * fromDevice.M11, item.Y * fromDevice.M22,
                item.Width * fromDevice.M11, item.Height * fromDevice.M22);
            var text = Fit(item.Translated, bounds.Width, bounds.Height, 34, 1 / fromDevice.M22, out var offset, out _);
            if (text is null) continue; // No mask if the entire translation cannot fit.
            var box = new DrawingGroup { ClipGeometry = new RectangleGeometry(bounds) };
            using (var context = box.Open())
            {
                context.DrawRectangle(Mask, null, bounds);
                context.DrawText(text, new Point(bounds.Left + 1 + offset.X, bounds.Top + 1 + offset.Y));
            }
            drawing.Children.Add(box);
        }
        drawing.Freeze();
        return drawing;
    }

    internal static bool ValidTransform(Matrix transform) =>
        double.IsFinite(transform.M11) && transform.M11 > 0 &&
        double.IsFinite(transform.M22) && transform.M22 > 0 &&
        transform.M12 == 0 && transform.M21 == 0 && transform.OffsetX == 0 && transform.OffsetY == 0;

    private static FormattedText? Fit(string text, double width, double height, double maxFont,
        double pixelsPerDip, out Point offset, out double fontDip)
    {
        offset = default;
        fontDip = 0;
        if (string.IsNullOrWhiteSpace(text) || !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(maxFont) || width <= 2 || height <= 2 || maxFont < 12) return null;
        var cap = Math.Min(34, Math.Min(height, maxFont));
        // Finite descending candidates include exactly 12 even for fractional caps.
        for (var size = cap; size >= 12; size = size > 12 && size < 12.5 ? 12 : size - 0.5)
        {
            var formatted = new FormattedText(text, Culture, FlowDirection.LeftToRight,
                Typeface, size, Brushes.White, pixelsPerDip)
            {
                MaxTextWidth = width - 2,
                Trimming = TextTrimming.None
            };
            // Do not set MaxTextHeight/MaxLineCount: shortened text is not a fit.
            var ink = formatted.BuildGeometry(default).Bounds;
            var left = ink.IsEmpty ? 0 : Math.Min(0, ink.Left);
            var top = ink.IsEmpty ? 0 : Math.Min(0, ink.Top);
            var right = Math.Max(formatted.WidthIncludingTrailingWhitespace, ink.IsEmpty ? 0 : ink.Right);
            var bottom = Math.Max(formatted.Height, ink.IsEmpty ? 0 : ink.Bottom);
            if (right - left <= width - 2 && bottom - top <= height - 2)
            {
                offset = new Point(-left, -top);
                fontDip = size;
                return formatted;
            }
        }
        return null;
    }

    private static SolidColorBrush MakeMask()
    {
        var brush = new SolidColorBrush(Color.FromRgb(24, 24, 24)) { Opacity = 0.96 };
        brush.Freeze();
        return brush;
    }
}
