using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class OcrRegionFilterTests
{
    [Fact]
    public void DropsLowConfidenceAndOverlappingUi()
    {
        var detections = new[]
        {
            new OcrBox(0, 0, 100, 30, "Hello", 0.4f),
            new OcrBox(0, 50, 100, 30, "Start", 0.95f),
            new OcrBox(200, 50, 100, 30, "こんにちは", 0.90f)
        };
        var selected = OcrRegionFilter.Select(detections, [new OcrRect(0, 45, 110, 40)], 0.8f);
        Assert.Single(selected);
        Assert.Equal("こんにちは", selected[0].Text);
    }

    [Fact]
    public void RequiresRepeatedStableText()
    {
        var tracker = new OcrStabilityTracker();
        var box = new OcrBox(10, 20, 90, 20, "Continue", 0.9f);
        Assert.Empty(tracker.Observe([box]));
        Assert.Single(tracker.Observe([box]));
        Assert.Empty(tracker.Observe([]));
    }
}
