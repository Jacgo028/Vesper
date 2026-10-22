using UnityTranslator.Core;
using Xunit;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 请求去抖判据的测试（第 100 轮）。
///
/// 这些测试盯的是**用户报过的那个 bug 的机制**：
/// 「返回菜单后再进游戏，翻译明显变慢、稳定性与准确性下降」的根因是
/// 游戏逐字打出台词时，**每一个半截前缀都被当成一句新话发了独立请求**，
/// 把队列挤到 max=17.3 秒。下面每一条都对应一个具体的失效方式。
/// </summary>
public class TranslationDebounceTests
{
    // ── 基本行为 ──────────────────────────────────────────────────────────────

    [Fact]
    public void TextStillChangingIsHeld()
    {
        // 文字刚刚（10ms 前）才变过，停稳界是 450ms → 还不能发。
        // 这一条就是"不再为半截前缀发请求"的核心保证。
        Assert.False(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0.45f, maxWaitSeconds: 2f,
            lastChangedAt: 1.00f, firstSeenAt: 1.00f, now: 1.01f, alreadySent: false));
    }

    [Fact]
    public void TextSettledIsSent()
    {
        // 文字已经 500ms 没变过 → 它不再逐字出现了，发。
        Assert.True(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0.45f, maxWaitSeconds: 2f,
            lastChangedAt: 1.00f, firstSeenAt: 1.00f, now: 1.50f, alreadySent: false));
    }

    [Fact]
    public void ExactlyAtSettleBoundaryIsSent()
    {
        // 边界取"大于等于"：正好 450ms 就算停稳。写成严格大于会让它永远差一帧发不出去。
        Assert.True(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0.45f, maxWaitSeconds: 2f,
            lastChangedAt: 1.00f, firstSeenAt: 1.00f, now: 1.45f, alreadySent: false));
    }

    [Fact]
    public void AlreadySentTextIsNotSentAgain()
    {
        // 同一段文字只发一次。少了这一条，每轮扫描都会重发 ——
        // 那就从"前缀洪水"变成"同一条无限重发"，一样是灾难。
        Assert.False(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0.45f, maxWaitSeconds: 2f,
            lastChangedAt: 0f, firstSeenAt: 0f, now: 100f, alreadySent: true));
    }

    // ── 上限界：忘了它就会"一个字都不翻" ────────────────────────────────────────

    [Fact]
    public void ContinuouslyChangingTextIsStillSentOnceTheCapIsReached()
    {
        // ★ 这是最重要的一条。一个文字一直在动的组件（例如滚动字幕、倒计时），
        //   永远满足不了"停稳 450ms"。如果只看停稳界，它会被**饿死**，一条都翻不出来。
        //   上限界保证它最多等 maxWaitSeconds 就先发一次。
        Assert.True(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0.45f, maxWaitSeconds: 2f,
            lastChangedAt: 5.90f,      // 100ms 前还在变 → 没停稳
            firstSeenAt: 4.00f,        // 但已经存在 2.0 秒 → 触发上限
            now: 6.00f, alreadySent: false));
    }

    [Fact]
    public void ContinuouslyChangingTextBeforeTheCapIsStillHeld()
    {
        // 上限还没到，就继续等 —— 这样逐字台词的前缀仍然不会被发出去。
        Assert.False(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0.45f, maxWaitSeconds: 2f,
            lastChangedAt: 1.90f, firstSeenAt: 1.00f, now: 2.00f, alreadySent: false));
    }

    [Fact]
    public void CapAloneIsEnoughEvenWhenTextChangesEveryFrame()
    {
        // 极端情况：每一帧都在变（lastChangedAt == now），
        // 但只要这段文字存在够久，上限界就必须把它放行。
        Assert.True(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0.45f, maxWaitSeconds: 2f,
            lastChangedAt: 9.00f, firstSeenAt: 7.00f, now: 9.00f, alreadySent: false));
    }

    // ── 关掉去抖＝修复前行为（A/B 对照要用） ────────────────────────────────────

    [Fact]
    public void ZeroSettleDisablesDebouncingEntirely()
    {
        // `UT_DEBOUNCE_SEC=0` 必须精确回到修复前的行为：立刻发。
        // 保留这个开关是为了能在同一场景做修复前后对照，而不是只靠讲道理。
        Assert.True(TranslationDebounce.Disabled(0f));
        Assert.True(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0f, maxWaitSeconds: 2f,
            lastChangedAt: 5f, firstSeenAt: 5f, now: 5f, alreadySent: false));
    }

    [Fact]
    public void NegativeSettleAlsoDisables()
    {
        // 环境变量解析出错时给负数也不该变成"永远不发"。
        Assert.True(TranslationDebounce.Disabled(-1f));
    }

    [Fact]
    public void DisabledStillRespectsAlreadySent()
    {
        // 关掉去抖不等于允许对同一段文字无限重发 —— 那条底线仍在。
        Assert.False(TranslationDebounce.ShouldSendNow(
            settleSeconds: 0f, maxWaitSeconds: 2f,
            lastChangedAt: 5f, firstSeenAt: 5f, now: 5f, alreadySent: true));
    }
}
