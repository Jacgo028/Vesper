using System.Collections.Generic;

namespace UnityTranslator.Core;

/// <summary>
/// 中文字体替换的守门规则。
///
/// 背景（实测踩过的坑）：
/// 1. 直接给文字组件换字体，一旦新字体资源有问题（字符表为空、图集里没有位图、
///    atlasTexture 为空等），原本能正常显示的文字会整个消失或错位 —— 比"缺字形显示方框"更糟。
/// 2. 字体资源"看起来创建成功"也不代表能渲染：TMP 的 HasCharacters 在
///    "字符表有条目、但图集里没有可用字形"时会返回 true，
///    于是译文照样写进组件，游戏用原字体渲染，缺字就变成方框（□）。
///
/// 所以换字体必须先过这里的检查，任何一条不满足就完全放弃替换、保持原样；
/// 并且"能不能真的渲染这段文字"要按**缺失字形数**判断，不能只看 HasCharacters。
///
/// 底线（只加强，不放松）：宁可留方框，也不能让正常文字消失或错位 ——
/// 也绝不能把渲染不出来的译文写进游戏。
/// </summary>
public static class FontReplacementPolicy
{
    /// <summary>替换前的检查结果。</summary>
    public enum Decision
    {
        /// <summary>可以替换。</summary>
        Replace,

        /// <summary>当前字体已经有全部字形，不需要换。</summary>
        NotNeeded,

        /// <summary>字体资源创建失败。</summary>
        AssetUnavailable,

        /// <summary>字体资源的字符表是空的 —— 换上去会让文字消失。</summary>
        AssetHasNoCharacters,

        /// <summary>字体资源的图集没准备好 —— 换上去同样会让文字消失。</summary>
        AssetAtlasUnavailable,

        /// <summary>字体资源里没有译文需要的字形。</summary>
        MissingGlyphs,
    }

    public static bool CanReplace(Decision decision) => decision == Decision.Replace;

    /// <summary>
    /// 是否可以写入译文：当前字体已有全部字形，或候选字体通过所有检查。
    /// 字体不可用、或字体缺字形时应保留原文：
    /// 写进去的字渲染不出来只会多出一串方框，不如保留原本能看懂的原文。
    /// </summary>
    public static bool CanShowTranslation(Decision decision) =>
        decision is Decision.NotNeeded or Decision.Replace;

    /// <summary>
    /// 决定要不要把 <paramref name="currentFont"/> 换成 <paramref name="candidate"/>。
    /// </summary>
    /// <param name="text">要显示的译文。</param>
    /// <param name="currentFontHasText">当前字体是否已包含译文全部字形。</param>
    /// <param name="candidateExists">候选字体资源是否创建成功。</param>
    /// <param name="candidateCharacterCount">候选字体的字符表条目数。</param>
    /// <param name="candidateHasText">候选字体是否包含译文全部字形。</param>
    /// <param name="candidateMissingGlyphCount">
    /// 候选字体**渲染不出**的字符数（由 TryAddCharacters 的缺失列表算出）。
    /// 只要大于 0，即使 <paramref name="candidateHasText"/> 是 true 也不能替换：
    /// 那种情况正是实测里"字体创建成功但显示方框"的来源。
    /// </param>
    /// <param name="candidateAtlasReady">
    /// 候选字体的图集是否可用（atlasTexture 非空且尺寸非零）。
    /// 默认 true 只是为了兼容不关心图集的调用方；真实判定必须按实际检测结果传入。
    /// </param>
    public static Decision Decide(
        string? text,
        bool currentFontHasText,
        bool candidateExists,
        int candidateCharacterCount,
        bool candidateHasText,
        int candidateMissingGlyphCount = 0,
        bool candidateAtlasReady = true)
    {
        // 当前字体够用就别动它 —— 不换就不会坏
        if (currentFontHasText) return Decision.NotNeeded;

        // 译文为空或只有空白时换字体毫无意义，而且容易触发未知问题
        if (string.IsNullOrWhiteSpace(text)) return Decision.NotNeeded;

        if (!candidateExists) return Decision.AssetUnavailable;

        // 字符表为空 = 字体资源不完整。上一轮就是把这种资源换上去，文字整个消失了。
        if (candidateCharacterCount <= 0) return Decision.AssetHasNoCharacters;

        // 图集没准备好，换上去同样什么都画不出来。
        if (!candidateAtlasReady) return Decision.AssetAtlasUnavailable;

        // 缺字形优先于 HasCharacters 判定：HasCharacters 在这两种情况下都可能说谎。
        if (candidateMissingGlyphCount > 0) return Decision.MissingGlyphs;

        if (!candidateHasText) return Decision.MissingGlyphs;

        return Decision.Replace;
    }

    /// <summary>
    /// 判断一个字符是否**必须**有字形才能正常显示。
    ///
    /// 空格、换行、制表符和零宽字符本来就不画出来，它们"没有字形"不算缺字；
    /// 如果把它们也算进缺失列表，就会因为一个换行导致整条译文被判定为缺字形、
    /// 白白放弃替换。这个规则放在 Core 里是为了能被自动化测试覆盖。
    /// </summary>
    public static bool RequiresGlyph(char value)
    {
        if (char.IsWhiteSpace(value) || char.IsControl(value)) return false;
        // 零宽与格式化字符：不占位、不绘制。
        return value switch
        {
            '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' => false,
            '\u00AD' => false, // 软连字符
            '\u200E' or '\u200F' or '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' => false,
            _ => true
        };
    }

    /// <summary>
    /// 从一段文字里挑出所有必须有字形的字符（去重，保持出现顺序）。
    /// 用于逐字符校验字体是否真的能渲染这段译文。
    /// </summary>
    public static IReadOnlyList<char> RequiredGlyphs(string? text)
    {
        var required = new List<char>();
        if (string.IsNullOrEmpty(text)) return required;
        var seen = new HashSet<char>();
        foreach (var value in text)
            if (RequiresGlyph(value) && seen.Add(value))
                required.Add(value);
        return required;
    }

    /// <summary>
    /// 把缺失字形整理成日志用的一句话，例如"3 个：鸣、谢、□"。
    /// 缺失字符会写进日志，方便以后按字符去查是哪个字体缺哪个字。
    /// </summary>
    public static string DescribeMissingGlyphs(IReadOnlyList<char>? missing, int maxShown = 12)
    {
        if (missing is null || missing.Count == 0) return "0 个";
        var shown = new System.Text.StringBuilder();
        var limit = maxShown <= 0 ? missing.Count : System.Math.Min(missing.Count, maxShown);
        for (var index = 0; index < limit; index++)
        {
            if (index > 0) shown.Append('、');
            shown.Append(missing[index]);
        }
        if (missing.Count > limit) shown.Append("…（只列出前 ").Append(limit).Append(" 个）");
        return missing.Count + " 个：" + shown;
    }

    /// <summary>给日志用的简短说明。</summary>
    public static string Describe(Decision decision) => decision switch
    {
        Decision.Replace => "将替换为中文回退字体",
        Decision.NotNeeded => "当前字体已够用，保持原样",
        Decision.AssetUnavailable => "字体资源创建失败，保持原样",
        Decision.AssetHasNoCharacters => "字体资源没有字符（换上去会让文字消失），保持原样",
        Decision.AssetAtlasUnavailable => "字体资源的图集不可用（换上去会让文字消失），保持原样",
        Decision.MissingGlyphs => "字体资源缺少所需字形，保持原样",
        _ => "未知状态，保持原样",
    };
}
