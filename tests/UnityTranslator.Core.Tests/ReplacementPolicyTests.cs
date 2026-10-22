using UnityTranslator.Plugin;

namespace UnityTranslator.Core.Tests;

public sealed class ReplacementPolicyTests
{
    [Fact]
    public void ReplyRequiresCurrentSourceAndMatchingRequest()
    {
        Assert.True(ReplacementPolicy.ReplyMatches("Start", "Start", 9, "latest", 9, "latest"));
        Assert.False(ReplacementPolicy.ReplyMatches("Start", "Continue", 9, "latest", 9, "latest"));
        Assert.False(ReplacementPolicy.ReplyMatches("Start", "", 9, "latest", 9, "latest"));
        Assert.False(ReplacementPolicy.ReplyMatches("Start", "Start", 9, "latest", 9, "old"));
        Assert.False(ReplacementPolicy.ReplyMatches("Start", "Start", 9, "latest", 8, "latest"));
    }

    [Fact]
    public void RestoreAndReapplyOnlyTouchOwnedText()
    {
        Assert.True(ReplacementPolicy.ShouldReapply("Start", "Start", "开始"));
        Assert.False(ReplacementPolicy.ShouldReapply("Start", "Continue", "开始"));
        Assert.True(ReplacementPolicy.ShouldRestore("开始", "开始"));
        Assert.False(ReplacementPolicy.ShouldRestore("Continue", "开始"));
    }

    // 重试退避的测试已移到 TextSelectionPolicyTests（该逻辑统一放在 Core 的
    // TextSelectionPolicy.RetryDue，插件不再自带一份，避免两处实现漂移）
}
