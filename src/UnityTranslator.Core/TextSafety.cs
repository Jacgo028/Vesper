using System.Text.RegularExpressions;

namespace UnityTranslator.Core;

public static partial class TextSafety
{
    [GeneratedRegex(@"\{[^{}]+\}|%[0-9$]*[sdif]|<[^<>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedTokenRegex();

    public static bool IsSafe(string source, string? translated)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated)) return false;
        if (string.Equals(source.Trim(), translated.Trim(), StringComparison.Ordinal)) return false;
        if (source.Count(character => character == '\n') != translated.Count(character => character == '\n')) return false;
        var originalTokens = ProtectedTokenRegex().Matches(source).Select(match => match.Value);
        var translatedTokens = ProtectedTokenRegex().Matches(translated).Select(match => match.Value);
        return originalTokens.SequenceEqual(translatedTokens, StringComparer.Ordinal);
    }

    /// <summary>
    /// 只在"模型把翻译任务本身说出来"时才会出现的词串。
    /// 正常译文不会提到"需要翻译的""待翻译"这类字眼。
    /// </summary>
    private static readonly string[] TaskMetaPhrases =
    [
        "需要翻译的", "待翻译", "要翻译的", "翻译任务", "翻译内容", "翻译文本",
        "请提供需要翻译", "没有提供需要翻译", "请把需要翻译", "没有可翻译的",
        "作为AI", "作为人工智能", "我是一个AI", "我是一个人工智能"
    ];

    /// <summary>
    /// 客套/说明口吻的开头。只有在整段回复明显比原文长时才拿来当证据，
    /// 免得误伤游戏里本来就有的"抱歉""以下是"这类正常台词。
    /// </summary>
    private static readonly string[] PreamblePhrases =
    [
        "请提供", "请给出", "请发送", "请告诉我", "您没有提供", "你没有提供",
        "抱歉", "很抱歉", "无法翻译", "以下是", "好的"
    ];

    /// <summary>
    /// 判断一条回复是不是"模型在跟你聊天，而不是在翻译"。
    ///
    /// 实测来源：桌面端把纯汉字原文（「設定」「编辑」）按 en 送去翻译，而提示词写的是
    /// "将用户给出的英语游戏文字翻译为简体中文"。模型看到的中文根本不是英语，
    /// 于是很合理地回了一句"请提供需要翻译的英语游戏文字内容。"。
    /// 这句话以前能通过 <see cref="IsSafe"/>（非空、和原文不同、占位符一致），
    /// 于是被写进游戏、还存进了译文库，标题界面上就一直挂着这么一句。
    ///
    /// 提示词那一侧已经改成不谎报语种；这里是最后一道兜底：
    /// **对话式回复永远不许写进游戏**。
    /// </summary>
    public static bool LooksLikeChatReply(string? source, string? translated)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated)) return false;
        var text = translated.Trim();

        foreach (var phrase in TaskMetaPhrases)
            if (text.Contains(phrase, StringComparison.Ordinal)) return true;

        // 正常译文不会带 Markdown 代码块围栏。
        if (text.Contains("```", StringComparison.Ordinal)) return true;

        // 兜底：比原文长出一大截，而且一开口就是客套/说明口吻。
        if (text.Length <= source.Trim().Length * 3 + 8) return false;
        foreach (var phrase in PreamblePhrases)
            if (text.StartsWith(phrase, StringComparison.Ordinal)) return true;
        return false;
    }
}
