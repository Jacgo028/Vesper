using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityTranslator.Core.Cheats;

/// <summary>按可核查的字段描述定位唯一目标的结果。</summary>
public enum FieldResolutionStatus
{
    /// <summary>恰好命中一个字段。</summary>
    Unique,

    /// <summary>游戏里没有名字与类型都匹配的字段。</summary>
    NotFound,

    /// <summary>命中多个字段：可能是两个对象挂了同类型同名字段，或同类型字段被声明多次。</summary>
    Ambiguous,

    /// <summary>描述本身不完整，不应据此写入。</summary>
    Invalid
}

public sealed record FieldResolution(FieldResolutionStatus Status, FieldSnapshot? Field, string Reason)
{
    public bool IsUnique => Status == FieldResolutionStatus.Unique;
}

/// <summary>
/// 字段写入的纯规则。桌面端与游戏插件共用同一份判断，
/// 保证“界面允许写”和“插件真的写”使用同一套条件。
/// </summary>
public static class FieldWriteGuard
{
    /// <summary>
    /// 从发现结果中按对象路径、组件类型、字段名与数值类型唯一定位字段。
    /// 对象消失、类型变化或出现多个同样候选都不猜目标。
    /// </summary>
    public static FieldResolution Resolve(IReadOnlyList<FieldSnapshot>? fields, CheatFieldWriteTarget? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.FieldName)) return Invalid("字段描述不完整");
        if (fields is null || fields.Count == 0) return NotFound($"游戏里没有找到字段 {target.FieldName}");

        var byName = new List<FieldSnapshot>();
        foreach (var field in fields)
            if (string.Equals(field.FieldName, target.FieldName, StringComparison.Ordinal)) byName.Add(field);
        if (byName.Count == 0) return NotFound($"游戏里没有找到字段 {target.FieldName}");

        // 对象路径与组件类型不同时使用“空表示不限”的宽松匹配：路径来自游戏运行时，
        // 实例编号可能变化，因此先按最具体的条件收窄，收窄不到再逐级放宽。
        var matched = Filter(byName, field => Same(field.ObjectPath, target.ObjectPath) &&
                                             Same(field.ComponentType, target.ComponentType));
        if (matched.Count == 0)
            matched = Filter(byName, field => Same(field.ComponentType, target.ComponentType));
        if (matched.Count == 0)
            matched = Filter(byName, field => Same(field.ObjectPath, target.ObjectPath));
        if (matched.Count == 0) matched = byName;

        var typed = Filter(matched, field => field.Kind == target.Kind);
        if (typed.Count == 0)
            return new FieldResolution(FieldResolutionStatus.Invalid, null,
                $"字段 {target.FieldName} 的类型已从 {target.Kind} 变化");
        if (typed.Count > 1)
        {
            var paths = new List<string>();
            foreach (var field in typed)
                if (!paths.Contains(field.ObjectPath)) paths.Add(field.ObjectPath);
            return new FieldResolution(FieldResolutionStatus.Ambiguous, null, paths.Count > 1
                ? $"有 {typed.Count} 个对象含有同名字段 {target.FieldName}，无法确定要改哪一个"
                : $"字段 {target.FieldName} 在同一个组件里有 {typed.Count} 处候选，无法确定要改哪一个");
        }
        return new FieldResolution(FieldResolutionStatus.Unique, typed[0], string.Empty);
    }

    /// <summary>
    /// 写入前的最终条件：只能写实例字段、非只读、非属性，并且当前值必须与用户确认过的值一致。
    /// </summary>
    public static bool CanWrite(bool isStatic, bool isReadonly, bool isProperty, bool matchesExpected) =>
        !isStatic && !isReadonly && !isProperty && matchesExpected;

    /// <summary>
    /// 完整写入判定。返回 Ok 时 <see cref="CheatFieldWriteResult.NewBytes"/> 才是可以写进字段的内容。
    /// </summary>
    public static CheatFieldWriteResult Evaluate(FieldResolution resolution, NumericKind kind,
        string? expectedText, string? newText, bool isStatic, bool isReadonly, bool isProperty)
    {
        if (!resolution.IsUnique || resolution.Field is null)
        {
            var status = resolution.Status == FieldResolutionStatus.Ambiguous
                ? CheatFieldWriteReplyStatus.Ambiguous
                : resolution.Status == FieldResolutionStatus.NotFound
                    ? CheatFieldWriteReplyStatus.NotFound
                    : CheatFieldWriteReplyStatus.Unsupported;
            return CheatFieldWriteResult.Fail(status, resolution.Reason);
        }

        var field = resolution.Field;
        if (field.Kind != kind)
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Unsupported,
                $"字段 {field.FieldName} 的类型已变化，请重新识别");

        if (!NumericValue.TryParse(expectedText ?? string.Empty, kind, out var expectedBytes) ||
            !NumericValue.TryParse(newText ?? string.Empty, kind, out var newBytes))
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Unsupported, "输入的新数值不是有效的数字");

        if (!NumericValue.TryParse(field.DisplayValue, kind, out var currentBytes))
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Unsupported,
                "字段当前值无法解析，请重新识别后再修改");

        var matchesExpected = expectedBytes.SequenceEqual(currentBytes);
        if (!CanWrite(isStatic, isReadonly, isProperty, matchesExpected))
        {
            var reason = isStatic ? "静态字段不修改"
                : isReadonly ? "只读字段不修改"
                : isProperty ? "属性不修改"
                : $"字段当前值已变成 {field.DisplayValue}，与确认时的数值不同";
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.NotWritable, reason);
        }

        return CheatFieldWriteResult.Success(field, newBytes);
    }

    private static FieldResolution Invalid(string reason) => new FieldResolution(FieldResolutionStatus.Invalid, null, reason);
    private static FieldResolution NotFound(string reason) => new FieldResolution(FieldResolutionStatus.NotFound, null, reason);

    private static bool Same(string actual, string expected) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(actual, expected, StringComparison.Ordinal);

    private static List<FieldSnapshot> Filter(List<FieldSnapshot> source, Func<FieldSnapshot, bool> predicate)
    {
        var result = new List<FieldSnapshot>();
        foreach (var item in source) if (predicate(item)) result.Add(item);
        return result;
    }
}

/// <summary>字段写入判定的结果。<see cref="NewBytes"/> 仅在 <see cref="IsOk"/> 为真时有效。</summary>
public sealed record CheatFieldWriteResult(CheatFieldWriteReplyStatus Status, FieldSnapshot? Field,
    byte[]? NewBytes, string Message)
{
    public bool IsOk => Status == CheatFieldWriteReplyStatus.Ok;

    public static CheatFieldWriteResult Success(FieldSnapshot field, byte[] newBytes) =>
        new CheatFieldWriteResult(CheatFieldWriteReplyStatus.Ok, field, newBytes, "数值已修改");

    public static CheatFieldWriteResult Fail(CheatFieldWriteReplyStatus status, string message) =>
        new CheatFieldWriteResult(status, null, null, message);
}
