using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class ExternalOverlayLayoutTests
{
    // Break caught: refusing ordinary Chinese, or growing text beyond the box/font cap.
    [Theory]
    [InlineData("开始", 120, 44, 80)]
    [InlineData("保存游戏", 160, 36, 30)]
    [InlineData("第一行\n第二行", 180, 90, 34)]
    [InlineData("这是一段需要在原有矩形中换行的中文译文", 180, 100, 34)]
    public void Fits_complete_text_within_original_bounds(string text, double width, double height, double cap) => OnSta(() =>
    {
        Assert.True(ExternalOverlayLayout.TryFit(text, width, height, cap, out var size));
        Assert.InRange(size, 12, Math.Min(34, Math.Min(height, cap)));
        // Independently measure every line: no height cap, line cap, or ellipsis.
        var measured = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.White, 1)
        { MaxTextWidth = width - 2, Trimming = TextTrimming.None };
        Assert.True(measured.Height <= height - 2);
        Assert.True(measured.WidthIncludingTrailingWhitespace <= width - 2);
    });

    // Break caught: treating truncated lines as a fit, reducing below 12, or widening narrow boxes.
    [Theory]
    [InlineData("", 200, 50, 34)]
    [InlineData(" \t\n", 200, 50, 34)]
    [InlineData("开始", 8, 80, 34)]
    [InlineData("开始", 120, 10, 34)]
    [InlineData("开始", 120, 50, 11)]
    [InlineData("甲\n乙\n丙\n丁\n戊\n己", 300, 30, 34)]
    [InlineData("这是非常长的译文无法放入这个很小的原始文字矩形", 42, 20, 34)]
    [InlineData("开始", double.NaN, 50, 34)]
    [InlineData("开始", 120, double.PositiveInfinity, 34)]
    [InlineData("开始", 120, 50, double.NaN)]
    public void Rejects_unfit_or_invalid_text_without_expansion(string text, double width, double height, double cap) => OnSta(() =>
    {
        Assert.False(ExternalOverlayLayout.TryFit(text, width, height, cap, out var size));
        Assert.Equal(0, size);
    });

    [Fact]
    public void Trailing_newlines_count_toward_complete_text_height() => OnSta(() =>
    {
        Assert.False(ExternalOverlayLayout.TryFit("开始\n\n\n\n", 150, 32, 34, out _));
    });

    // Break caught: using target-game 96 DPI, scaling the native screen origin, or expanding the mask.
    [Theory]
    [InlineData(1, 30, 60, 300, 120)]
    [InlineData(1.5, 20, 40, 200, 80)]
    [InlineData(2, 15, 30, 150, 60)]
    public void Drawing_uses_overlay_transform_and_clips_mask_and_white_text(double dpi,
        double x, double y, double width, double height) => OnSta(() =>
    {
        var drawing = ExternalOverlayLayout.CreateDrawing(
            new[] { new OcrOverlayItem(30, 60, 300, 120, "开始游戏") }, 800, 600,
            new Matrix(1 / dpi, 0, 0, 1 / dpi, 0, 0));
        var box = Assert.IsType<DrawingGroup>(Assert.Single(drawing.Children));
        Assert.Equal(new Rect(x, y, width, height), box.ClipGeometry.Bounds);
        var mask = Assert.IsType<GeometryDrawing>(box.Children[0]);
        Assert.Equal(new Rect(x, y, width, height), mask.Geometry.Bounds);
        var brush = Assert.IsType<SolidColorBrush>(mask.Brush);
        Assert.Equal(0.96, brush.Opacity);
        Assert.Equal(brush.Color.R, brush.Color.G);
        Assert.Equal(brush.Color.G, brush.Color.B);
        Assert.InRange(brush.Color.R, (byte)0, (byte)40);
        var glyphs = Glyphs(box).ToArray();
        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, glyph => Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(glyph.ForegroundBrush).Color));
        Assert.True(new Rect(x, y, width, height).Contains(box.Bounds));
    });

    [Fact]
    public void Invalid_overflow_and_unfit_boxes_leave_original_pixels_unmasked() => OnSta(() =>
    {
        var drawing = ExternalOverlayLayout.CreateDrawing(new[]
        {
            new OcrOverlayItem(int.MaxValue - 2, 0, 20, 30, "开始"),
            new OcrOverlayItem(-1, 0, 50, 30, "开始"),
            new OcrOverlayItem(780, 0, 50, 30, "开始"),
            new OcrOverlayItem(0, 0, 0, 30, "开始"),
            new OcrOverlayItem(0, 0, 50, 20, "这是一段无法放入的中文译文"),
            new OcrOverlayItem(0, 0, 100, 40, "")
        }, 800, 600, Matrix.Identity);
        Assert.Empty(drawing.Children);
    });

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing) => drawing switch
    {
        GlyphRunDrawing glyph => new[] { glyph },
        DrawingGroup group => group.Children.SelectMany(Glyphs),
        _ => Array.Empty<GlyphRunDrawing>()
    };

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA text measurement timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
