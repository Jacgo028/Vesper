using System;

namespace UnityTranslator.Core;

/// <summary>
/// 「这段文字停稳了没有」——决定要不要**现在**为它发一条联网翻译请求。
///
/// ★ 为什么要有这个类（第 100 轮实测根因）：
///   游戏把台词**一个字一个字打出来**，于是文字组件的文本每隔几帧就变一次：
///     <c>黒岩市が</c> → <c>黒岩市が全域封鎖されてから半月が過</c> → … → 整句
///   每一个中间值都是**不同的字符串**，旧实现就为**每一个半截前缀**都发了一次联网翻译。
///   实机一局实测：约 8 句话产生了 **63 条**未命中请求，队列被自己挤爆 ——
///   API P50=2174ms、P95=5468ms、**max=17349ms**（那一条的原文只是「エ」一个字符）。
///   它同时解释了用户报的三个症状：变慢（排队）、不稳（抖动 1.1s~17.3s）、
///   不准（半截话被独立翻译并写进译文库）。
///
/// ★ 为什么抽成一个**纯函数**而不是留在插件里：
///   `Plugin.cs` 依赖 UnityEngine，单元测试项目引用不到，里面写的判据**测不了**。
///   而这个判据正是"该不该发请求"的开关，改错的代价是"整款游戏一个字都不翻"
///   或者"半截前缀洪水复现"，两种都很难在实机上当场看出来。
///   抽到这里之后它可以被测试永久钉住（见 <c>TranslationDebounceTests</c>）。
///
/// 判据是**两个界**，两个都必须有：
///   * <b>停稳界</b>：文字连续 <paramref name="settleSeconds"/> 没变过 → 它不再逐字出现了；
///   * <b>上限界</b>：文字已经存在 <paramref name="maxWaitSeconds"/> → 就算一直在动也先发一次。
/// 少了上限界，一个持续动画的组件会被"永远等它停"**饿死**，一条都翻不出来 ——
/// 这正是需要测试钉住的那个失效模式。
/// </summary>
public static class TranslationDebounce
{
    /// <summary><paramref name="settleSeconds"/> 为 0（或负数）表示**完全关掉去抖**。</summary>
    public static bool Disabled(float settleSeconds) => settleSeconds <= 0f;

    /// <summary>
    /// 现在该不该把这段文字发出去。
    /// </summary>
    /// <param name="settleSeconds">
    /// 文字要保持不变多久才算停稳。**0 表示关掉去抖**（＝修复前的行为），
    /// 保留这个取值是为了能在同一场景做修复前后 A/B 对照。
    /// </param>
    /// <param name="maxWaitSeconds">最长等待上限，防止一直在动的文字永远不被翻译。</param>
    /// <param name="lastChangedAt">文字**最后一次变化**的时刻（秒，同一时基）。</param>
    /// <param name="firstSeenAt">这段文字**第一次出现**的时刻（秒，同一时基）。</param>
    /// <param name="now">当前时刻（秒，同一时基）。</param>
    /// <param name="alreadySent">已经为这段文字发过请求了 —— 就不必再发。</param>
    public static bool ShouldSendNow(
        float settleSeconds, float maxWaitSeconds,
        float lastChangedAt, float firstSeenAt, float now, bool alreadySent)
    {
        if (alreadySent) return false;
        if (Disabled(settleSeconds)) return true;

        var settled = now - lastChangedAt >= settleSeconds;
        var capped = now - firstSeenAt >= maxWaitSeconds;
        return settled || capped;
    }
}
