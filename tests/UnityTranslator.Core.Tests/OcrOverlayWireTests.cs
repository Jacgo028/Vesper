using System.Text.Json;

namespace UnityTranslator.Core.Tests;

public sealed class OcrOverlayWireTests
{
    private static readonly OcrFrame Frame = new(10, "unused.png", 960, 600, []);

    // A receiver that drops nested boxes (the observed Unity JsonUtility failure)
    // must fail this real producer-to-consumer round trip.
    [Fact]
    public void Actual_producer_wire_preserves_frame_geometry_and_escaped_Chinese_box()
    {
        var wire = PipeProtocol.WriteOcrOverlay(Frame, [new(37, 34, 173, 32, "继续\n\"下一页\" \\ 路径 😀")]);
        Assert.Contains("\\u", wire);
        var result = Parse(wire);
        Assert.Equal(1, result.Version);
        Assert.Equal("OcrOverlay", result.Kind);
        Assert.Equal(10, result.FrameId);
        Assert.Equal(960, result.Width);
        Assert.Equal(600, result.Height);
        var box = Assert.Single(result.Boxes);
        Assert.Equal(37, box.X);
        Assert.Equal(34, box.Y);
        Assert.Equal(173, box.Width);
        Assert.Equal(32, box.Height);
        Assert.Equal("继续\n\"下一页\" \\ 路径 😀", box.Translated);
    }

    [Fact]
    public void Empty_overlay_is_a_valid_explicit_reply() =>
        Assert.Empty(Parse(PipeProtocol.WriteOcrOverlay(Frame, [])).Boxes);

    [Fact]
    public void Several_boxes_retain_order_and_direct_Unicode()
    {
        var wire = PipeProtocol.WriteOcrOverlay(Frame, [new(0, 0, 4, 4, "甲"), new(900, 560, 60, 40, "乙")]);
        var boxes = Parse(wire).Boxes;
        Assert.Equal(2, boxes.Length);
        Assert.Equal("甲", boxes[0].Translated);
        Assert.Equal("乙", boxes[1].Translated);
        Assert.Equal(900, boxes[1].X);
        Assert.Equal("继续", Parse(Envelope("[]").Replace("[]", "[{\"X\":0,\"Y\":0,\"Width\":10,\"Height\":10,\"Translated\":\"继续\"}]")).Boxes[0].Translated);
    }

    [Theory]
    [InlineData("Version", "0")]
    [InlineData("Version", "2")]
    [InlineData("Version", "1.0")]
    [InlineData("Kind", "\"OcrFrame\"")]
    [InlineData("Kind", "\"ocroverlay\"")]
    [InlineData("FrameId", "0")]
    [InlineData("FrameId", "-1")]
    [InlineData("FrameId", "1.9")]
    [InlineData("FrameId", "2147483648")]
    [InlineData("Width", "0")]
    [InlineData("Height", "-1")]
    [InlineData("Width", "16385")]
    [InlineData("Height", "16385")]
    [InlineData("Width", "\"960\"")]
    [InlineData("Height", "null")]
    public void Invalid_envelope_metadata_is_rejected(string property, string rawValue)
    {
        var members = new Dictionary<string, JsonElement>();
        using var normal = JsonDocument.Parse(Envelope("[]"));
        foreach (var item in normal.RootElement.EnumerateObject()) members[item.Name] = item.Value.Clone();
        using var replacement = JsonDocument.Parse(rawValue); members[property] = replacement.RootElement.Clone();
        Assert.False(TryParse(JsonSerializer.Serialize(members), out _));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("Kind")]
    [InlineData("FrameId")]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("Boxes")]
    public void Missing_required_metadata_is_rejected(string name)
    {
        using var json = JsonDocument.Parse(Envelope("[]"));
        var fields = json.RootElement.EnumerateObject().Where(p => p.Name != name).ToDictionary(p => p.Name, p => p.Value.Clone());
        Assert.False(TryParse(JsonSerializer.Serialize(fields), out _));
    }

    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -1, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, -1)]
    [InlineData(0, 0, 3, 10)]
    [InlineData(951, 0, 10, 10)]
    [InlineData(0, 591, 10, 10)]
    [InlineData(2147483647, 0, 10, 10)]
    [InlineData(0, 0, 2147483647, 10)]
    public void Unsafe_box_geometry_rejects_whole_reply(int x, int y, int width, int height)
    {
        var wire = PipeProtocol.WriteOcrOverlay(Frame, [new(0, 0, 10, 10, "valid"), new(x, y, width, height, "invalid")]);
        Assert.False(TryParse(wire, out var result)); Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_overlay_text_is_rejected(string? text) =>
        Assert.False(TryParse(PipeProtocol.WriteOcrOverlay(Frame, [new(0, 0, 10, 10, text!)]), out _));

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,")]
    [InlineData("{\"Version\":1} trailing")]
    [InlineData("{\"Version\":1,\"Kind\":\"OcrOverlay\",\"FrameId\":1,\"Width\":960,\"Height\":600,\"Boxes\":null}")]
    [InlineData("{\"Version\":1,\"Kind\":\"OcrOverlay\",\"FrameId\":1,\"Width\":960,\"Height\":600,\"Boxes\":[{}]}")]
    public void Malformed_or_wrong_shape_JSON_is_rejected(string json) => Assert.False(TryParse(json, out _));

    [Theory]
    [InlineData("+10")]
    [InlineData("010")]
    [InlineData("1e1")]
    public void Non_integer_wire_syntax_is_not_silently_coerced(string value) =>
        Assert.False(TryParse(Envelope("[]").Replace("\"FrameId\":10", "\"FrameId\":" + value), out _));

    [Fact]
    public void Duplicate_metadata_and_unescaped_control_text_are_rejected()
    {
        Assert.False(TryParse(Envelope("[]").Replace("\"FrameId\":10", "\"FrameId\":10,\"FrameId\":11"), out _));
        Assert.False(TryParse(Envelope("[{\"X\":0,\"Y\":0,\"Width\":10,\"Height\":10,\"Translated\":\"bad\ntext\"}]"), out _));
    }

    [Fact]
    public void Oversized_box_count_text_wire_and_nesting_are_rejected()
    {
        Assert.False(TryParse(PipeProtocol.WriteOcrOverlay(Frame, Enumerable.Range(0, 65).Select(_ => new OcrOverlayItem(0, 0, 10, 10, "继续")).ToArray()), out _));
        Assert.False(TryParse(PipeProtocol.WriteOcrOverlay(Frame, [new(0, 0, 10, 10, new string('中', 4097))]), out _));
        Assert.False(TryParse(Envelope("[]") + new string(' ', 65536), out _));
        Assert.False(TryParse(new string('[', 10000) + "0" + new string(']', 10000), out _));
    }

    private static string Envelope(string boxes) => "{\"Version\":1,\"Kind\":\"OcrOverlay\",\"FrameId\":10,\"Width\":960,\"Height\":600,\"Boxes\":" + boxes + "}";
    private static OcrWireOverlay Parse(string json) { Assert.True(TryParse(json, out var result)); return Assert.IsType<OcrWireOverlay>(result); }
    private static bool TryParse(string? json, out OcrWireOverlay? result) => OcrOverlayWire.TryRead(json, out result);
}
