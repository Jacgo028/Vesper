using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App;

/// <summary>
/// 推荐候选的“给人看”的包装。
/// 界面上不显示内部分数，而是显示可解释的推荐程度，避免让人误以为分数等于保证正确。
/// </summary>
public sealed class RankedCandidateView
{
    public RankedCandidateView(RankedCandidate ranked, string token, int index)
    {
        Ranked = ranked;
        Token = token;
        Index = index;
    }

    public RankedCandidate Ranked { get; }

    /// <summary>只在本次会话有效的字段编号，用于提交修改。</summary>
    public string Token { get; }

    public int Index { get; }

    public FieldCandidate Field => Ranked.Field;

    /// <summary>推荐程度：不用“分数”，改用能读懂的说法。</summary>
    public string MatchLabel => Ranked.Score switch
    {
        >= 100 => "很像",
        >= 40 => "可能是",
        >= 10 => "可以看看",
        _ => "仅列出"
    };

    /// <summary>数值类型的中文说法。</summary>
    public string KindLabel => UiText.KindName(Field.Kind);

    /// <summary>“所属”：把对象路径与组件类型合并成一句人话。</summary>
    public string OwnerSummary
    {
        get
        {
            var objectPart = Shorten(Field.ObjectPath);
            var componentPart = Field.ComponentType;
            if (string.IsNullOrWhiteSpace(objectPart)) return componentPart;
            if (string.IsNullOrWhiteSpace(componentPart)) return objectPart;
            return objectPart + " 上的 " + componentPart;
        }
    }

    /// <summary>推荐依据的人类可读版本；界面上优先用这个，而不是原始理由串。</summary>
    public string ReasonSummary
    {
        get
        {
            if (Ranked.Reasons.Count == 0) return "名字和当前值都没对上，只是把它列出来供你参考。";
            var parts = new List<string>();
            foreach (var reason in Ranked.Reasons) parts.Add(Humanize(reason));
            return string.Join("；", parts) + "。";
        }
    }

    /// <summary>技术详情：保留原始字段信息，方便排查。</summary>
    public string Technical =>
        $"内部评分 {Ranked.Score}｜对象路径 {Field.ObjectPath}｜组件 {Field.ComponentType}｜" +
        $"字段 {Field.FieldName}｜{UiText.KindTechnical(Field.Kind)}｜" +
        $"原始依据 {(Ranked.Reasons.Count == 0 ? "（无）" : string.Join(" / ", Ranked.Reasons))}";

    private static string Humanize(string reason)
    {
        if (reason.StartsWith("字段名命中", StringComparison.Ordinal))
            return "字段名里有“" + reason["字段名命中".Length..].Trim() + "”，像是玩家关心的数值";
        if (reason.StartsWith("组件名命中", StringComparison.Ordinal))
            return "所在组件名里有“" + reason["组件名命中".Length..].Trim() + "”";
        if (reason.StartsWith("对象路径命中", StringComparison.Ordinal))
            return "所在对象名里有“" + reason["对象路径命中".Length..].Trim() + "”";
        if (reason.StartsWith("当前值", StringComparison.Ordinal))
            return "当前数值和你输入的数值一致";
        return reason;
    }

    /// <summary>对象路径可能很长，只显示末尾几段，避免把表格挤爆。</summary>
    private static string Shorten(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 3 ? path : "…/" + string.Join("/", segments[^3..]);
    }
}
