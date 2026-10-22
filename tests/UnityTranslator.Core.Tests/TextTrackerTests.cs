using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class TextTrackerTests
{
    [Fact]
    public void NewSourceIncrementsGenerationAndOwnReplacementDoesNot()
    {
        var tracker = new TextTracker();
        var first = tracker.Observe(17, "Start");
        Assert.NotNull(first);
        Assert.Equal(1, first.Generation);
        tracker.SetTranslated(17, 1, "开始");
        Assert.Null(tracker.Observe(17, "开始"));
        Assert.Null(tracker.Observe(17, "Start"));
        var second = tracker.Observe(17, "Continue");
        Assert.NotNull(second);
        Assert.Equal(2, second.Generation);
    }

    [Fact]
    public void EmptyNumericAndChineseAreSkipped()
    {
        var tracker = new TextTracker();
        Assert.Null(tracker.Observe(1, " "));
        Assert.Null(tracker.Observe(1, "123"));
        Assert.Null(tracker.Observe(1, "开始"));
    }
}
