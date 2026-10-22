using System.Text.Json;
using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class ReplyGateTests
{
    [Fact]
    public void StaleReplyIsRejected()
    {
        Assert.False(ReplyGate.Accept(7, 2, new TranslateReply("old", "旧", 7, 1)));
        Assert.True(ReplyGate.Accept(7, 2, new TranslateReply("new", "新", 7, 2)));
        Assert.False(ReplyGate.Accept(7, 2, new TranslateReply("wrong", "错", 8, 2)));
    }

    [Fact]
    public void OversizedAndMalformedRequestsAreRejected()
    {
        Assert.False(PipeProtocol.TryReadRequest("not-json", out _));
        Assert.False(PipeProtocol.TryReadRequest(new string('x', PipeProtocol.MaxCharacters + 1), out _));
    }

    [Fact]
    public void RequestProtocolPreserves64BitIl2CppComponentIds()
    {
        const long componentId = 2_400_000_000_000L;
        var line = "{\"Version\":1,\"RequestId\":\"r1\",\"Source\":\"はい\",\"SourceLanguage\":\"ja\",\"ComponentId\":"
            + componentId + ",\"Generation\":1}";

        Assert.True(PipeProtocol.TryReadRequest(line, out var request));
        Assert.Equal(componentId, (long)request!.ComponentId);

        var tracker = new TextTracker();
        var observation = tracker.Observe(componentId, "こんにちは");
        Assert.Equal(componentId, observation!.ComponentId);
        tracker.SetTranslated(componentId, observation.Generation, "你好");
        Assert.Equal("こんにちは", tracker.Original(componentId));

        var reply = new TranslateReply(request.RequestId, "你好", request.ComponentId, request.Generation);
        Assert.True(ReplyGate.Accept(componentId, request.Generation, reply));
        using var serialized = JsonDocument.Parse(PipeProtocol.WriteReply(reply));
        Assert.Equal(componentId, serialized.RootElement.GetProperty("ComponentId").GetInt64());
    }

    [Fact]
    public void OcrFrameRequiresValidSizeAndRectangles()
    {
        const string valid = "{\"Version\":1,\"Kind\":\"OcrFrame\",\"FrameId\":2,\"ImagePath\":\"C:\\\\temp\\\\f.png\",\"Width\":640,\"Height\":480,\"KnownUiBoxes\":[{\"X\":1,\"Y\":2,\"Width\":3,\"Height\":4}]}";
        Assert.True(PipeProtocol.TryReadOcrFrame(valid, out var frame));
        Assert.Equal(2, frame!.FrameId);
        Assert.Single(frame.KnownUiBoxes);
        Assert.False(PipeProtocol.TryReadOcrFrame(valid.Replace("640", "0"), out _));
    }
}
