using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;
using static UnityTranslator.App.Tests.ExternalOcrTestDoubles;

namespace UnityTranslator.App.Tests;

public sealed class ExternalFrameRulesTests
{
    [Fact]
    public void Client_crop_preserves_negative_monitor_coordinates()
    {
        Assert.True(ExternalFrameRules.TryClientCrop(new(-1000, -500, 816, 639),
            new(-992, -469, 800, 600), 816, 639, out var crop));
        Assert.Equal(new OcrRect(8, 31, 800, 600), crop);
    }

    [Fact]
    public void Borderless_client_can_fill_the_entire_frame()
    {
        Assert.True(ExternalFrameRules.TryClientCrop(new(100, 200, 800, 600),
            new(100, 200, 800, 600), 800, 600, out var crop));
        Assert.Equal(new OcrRect(0, 0, 800, 600), crop);
    }

    [Theory]
    [InlineData(0, 0, 0, 20)]
    [InlineData(0, 0, 20, 0)]
    [InlineData(0, 0, -1, 20)]
    [InlineData(0, 0, 20, -1)]
    [InlineData(-1, 0, 20, 20)]
    [InlineData(0, -1, 20, 20)]
    [InlineData(81, 0, 20, 20)]
    [InlineData(0, 81, 20, 20)]
    [InlineData(int.MaxValue, 0, 20, 20)]
    [InlineData(0, int.MaxValue, 20, 20)]
    [InlineData(1, 0, int.MaxValue, 20)]
    [InlineData(0, 1, 20, int.MaxValue)]
    public void Crop_rejects_empty_negative_outside_or_overflowing_client(int x, int y, int width, int height) =>
        Assert.False(ExternalFrameRules.TryClientCrop(new(0, 0, 100, 100), new(x, y, width, height), 100, 100, out _));

    [Theory]
    [InlineData(0, 100, 0, 100)]
    [InlineData(-1, 100, -1, 100)]
    [InlineData(100, 0, 100, 0)]
    [InlineData(100, -1, 100, -1)]
    [InlineData(100, 100, 99, 100)]
    [InlineData(100, 100, 100, 99)]
    public void Crop_requires_exact_positive_frame_dimensions(int screenWidth, int screenHeight, int pixelsWidth, int pixelsHeight) =>
        Assert.False(ExternalFrameRules.TryClientCrop(new(0, 0, screenWidth, screenHeight), new(0, 0, 20, 20), pixelsWidth, pixelsHeight, out _));

    [Fact]
    public void Screen_offset_subtraction_cannot_wrap_into_a_valid_crop() =>
        Assert.False(ExternalFrameRules.TryClientCrop(new(int.MinValue, 0, 100, 100),
            new(int.MaxValue, 0, 20, 20), 100, 100, out _));

    [Theory]
    [InlineData(0, true)]
    [InlineData(11999, true)]
    [InlineData(12000, false)]
    [InlineData(12001, false)]
    [InlineData(-1, false)]
    public void Result_age_must_be_nonnegative_and_strictly_less_than_twelve_seconds(int milliseconds, bool accepted) =>
        Assert.Equal(accepted, ExternalFrameRules.CanAccept(Stamp(), Stamp(), TimeSpan.FromMilliseconds(milliseconds)));

    [Theory]
    [InlineData("session")]
    [InlineData("game")]
    [InlineData("pid")]
    [InlineData("package")]
    [InlineData("hwnd")]
    [InlineData("epoch")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("dpi-x")]
    [InlineData("dpi-y")]
    public void Any_identity_or_placement_change_invalidates_an_awaited_result(string change)
    {
        var sent = Stamp();
        var current = change switch
        {
            "session" => sent with { Session = sent.Session with { SessionId = "new" } },
            "game" => sent with { Session = sent.Session with { GameId = "new" } },
            "pid" => sent with { Session = sent.Session with { ProcessId = 43 } },
            "package" => sent with { Session = sent.Session with { PackageName = "legacy-mono" } },
            "hwnd" => sent with { Hwnd = (nint)124 },
            "epoch" => sent with { Epoch = 8 },
            "width" => sent with { Width = 33 },
            "height" => sent with { Height = 25 },
            "dpi-x" => sent with { DpiScaleX = 1.5 },
            "dpi-y" => sent with { DpiScaleY = 1.25 },
            _ => throw new ArgumentException(change)
        };
        Assert.False(ExternalFrameRules.CanAccept(sent, current, TimeSpan.Zero));
    }

    [Fact]
    public void A_newer_sample_timestamp_does_not_discard_the_same_valid_context() =>
        Assert.True(ExternalFrameRules.CanAccept(Stamp(), Stamp() with { Timestamp = 200 }, TimeSpan.FromSeconds(2)));

    [Theory]
    [InlineData(0, 24, 1.25, 1.5)]
    [InlineData(32, -1, 1.25, 1.5)]
    [InlineData(32, 24, 0, 1.5)]
    [InlineData(32, 24, 1.25, -1)]
    [InlineData(32, 24, double.NaN, 1.5)]
    [InlineData(32, 24, 1.25, double.PositiveInfinity)]
    public void Matching_but_invalid_geometry_is_not_accepted(int width, int height, double dpiX, double dpiY)
    {
        var invalid = Stamp() with { Width = width, Height = height, DpiScaleX = dpiX, DpiScaleY = dpiY };
        Assert.False(ExternalFrameRules.CanAccept(invalid, invalid, TimeSpan.Zero));
    }

    [Fact]
    public void Independent_identical_readbacks_match_without_a_game_redraw()
    {
        var before = Frame();
        var after = Frame() with { Sequence = 2, Timestamp = 200 };
        Assert.True(ExternalFrameRules.RegionMatches(before, after, new(0, 0, 32, 24)));
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(255, false)]
    public void RGB_average_uses_the_existing_point_twelve_threshold(byte delta, bool matches) =>
        Assert.Equal(matches, ExternalFrameRules.RegionMatches(Frame(), Frame(rgb: delta), new(0, 0, 32, 24)));

    [Fact]
    public void Alpha_changes_do_not_count_as_RGB_changes() =>
        Assert.True(ExternalFrameRules.RegionMatches(Frame(alpha: 0), Frame(alpha: 255), new(0, 0, 32, 24)));

    [Fact]
    public void Only_the_requested_top_left_oriented_region_is_compared()
    {
        var before = Frame();
        var pixels = Frame(rgb: 255).Bgra32.ToArray();
        // Restore the upper-left 8x8 box; everything around/below it differs.
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                for (var channel = 0; channel < 3; channel++) pixels[(y * 32 + x) * 4 + channel] = 0;
        var after = before with { Bgra32 = pixels };
        Assert.True(ExternalFrameRules.RegionMatches(before, after, new(0, 0, 8, 8)));
        Assert.False(ExternalFrameRules.RegionMatches(before, after, new(24, 16, 8, 8)));
    }

    [Fact]
    public void Sampling_checks_cell_centres_instead_of_the_box_corner()
    {
        var before = Frame(16, 16);
        var pixels = before.Bgra32.ToArray();
        for (var y = 1; y < 16; y += 2)
            for (var x = 1; x < 16; x += 2)
                for (var channel = 0; channel < 3; channel++) pixels[(y * 16 + x) * 4 + channel] = 255;
        Assert.False(ExternalFrameRules.RegionMatches(before, before with { Bgra32 = pixels }, new(0, 0, 16, 16)));
    }

    [Theory]
    [InlineData(0, 0, 3, 4)]
    [InlineData(0, 0, 4, 3)]
    [InlineData(0, 0, 0, 4)]
    [InlineData(0, 0, 4, -1)]
    [InlineData(-1, 0, 4, 4)]
    [InlineData(0, -1, 4, 4)]
    [InlineData(29, 0, 4, 4)]
    [InlineData(0, 21, 4, 4)]
    [InlineData(int.MaxValue, 0, 4, 4)]
    [InlineData(1, 0, int.MaxValue, 4)]
    [InlineData(0, 1, 4, int.MaxValue)]
    public void Region_rejects_too_small_outside_or_overflowing_boxes(int x, int y, int width, int height) =>
        Assert.False(ExternalFrameRules.RegionMatches(Frame(), Frame(), new(x, y, width, height)));

    [Fact]
    public void Four_pixel_box_at_bottom_right_is_valid() =>
        Assert.True(ExternalFrameRules.RegionMatches(Frame(), Frame(), new(28, 20, 4, 4)));

    [Theory]
    [InlineData("different-size")]
    [InlineData("short-before")]
    [InlineData("short-after")]
    [InlineData("long-before")]
    [InlineData("long-after")]
    [InlineData("zero-size")]
    [InlineData("negative-size")]
    [InlineData("overflow-size")]
    public void Region_requires_equal_dimensions_and_two_exact_tight_BGRA_buffers(string problem)
    {
        var before = Frame();
        var after = Frame();
        switch (problem)
        {
            case "different-size": after = Frame(33, 24); break;
            case "short-before": before = before with { Bgra32 = new byte[3071] }; break;
            case "short-after": after = after with { Bgra32 = new byte[3071] }; break;
            case "long-before": before = before with { Bgra32 = new byte[3073] }; break;
            case "long-after": after = after with { Bgra32 = new byte[3073] }; break;
            case "zero-size": before = before with { Width = 0 }; after = before; break;
            case "negative-size": before = before with { Height = -1 }; after = before; break;
            case "overflow-size": before = before with { Width = int.MaxValue, Height = int.MaxValue }; after = before; break;
        }
        Assert.False(ExternalFrameRules.RegionMatches(before, after, new(0, 0, 4, 4)));
    }
}
