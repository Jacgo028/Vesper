using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityTranslator.Core.Cheats;

public sealed record FieldCandidate(string ObjectPath, string ComponentType, string FieldName,
    NumericKind Kind, string DisplayValue);

public sealed record RankedCandidate(FieldCandidate Field, int Score, IReadOnlyList<string> Reasons);

public static class CandidateRanker
{
    private static readonly string[] Names =
    [
        "gold", "money", "cash", "coin", "currency", "金钱", "金币",
        "hp", "health", "life", "生命", "体力",
        "exp", "experience", "xp", "经验"
    ];

    public static RankedCandidate Rank(FieldCandidate field, string? displayedValue)
    {
        var score = 0;
        var reasons = new List<string>();
        var fieldMatch = Names.FirstOrDefault(name => field.FieldName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        var componentMatch = fieldMatch is null
            ? Names.FirstOrDefault(name => field.ComponentType.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            : null;
        var pathMatch = fieldMatch is null && componentMatch is null
            ? Names.FirstOrDefault(name => field.ObjectPath.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            : null;
        if (fieldMatch is not null)
        {
            score += 100;
            reasons.Add($"字段名命中 {fieldMatch}");
        }
        else if (componentMatch is not null)
        {
            score += 35;
            reasons.Add($"组件名命中 {componentMatch}");
        }
        else if (pathMatch is not null)
        {
            score += 10;
            reasons.Add($"对象路径命中 {pathMatch}");
        }
        if (NumericValue.TryParse(field.DisplayValue, field.Kind, out var fieldBytes) &&
            NumericValue.TryParse(displayedValue ?? string.Empty, field.Kind, out var displayedBytes) &&
            fieldBytes.SequenceEqual(displayedBytes))
        {
            score += 40;
            reasons.Add($"当前值 {field.DisplayValue} 与输入一致");
        }
        return new RankedCandidate(field, score, reasons);
    }
}
