using System;

namespace UnityTranslator.Plugin;

public static class ReplacementPolicy
{
    public static bool ReplyMatches(string source, string? current, int generation, string requestId,
        int replyGeneration, string replyRequestId) =>
        current == source && generation == replyGeneration && requestId == replyRequestId;

    public static bool ShouldReapply(string source, string? current, string? translated) =>
        // 不用 string.IsNullOrWhiteSpace：那是 .NET 4.0 才有的，
        // 精简变体跑在 CLR 2.0 上会直接编译失败。见进展记录 B83。
        current == source && !(translated == null || translated.Trim().Length == 0);

    public static bool ShouldRestore(string? current, string? translated) =>
        translated != null && current == translated;
}
