using System;
using System.Collections.Generic;

namespace UnityTranslator.Core.Cheats;

/// <summary>
/// 作弊命令的消息种类。使用字符串而不是枚举数字，避免两端枚举顺序变化后静默改变含义。
/// </summary>
public static class CheatMessageKinds
{
    public const string FieldQuery = "CheatFieldQuery";
    public const string FieldReply = "CheatFieldReply";
    public const string FieldWrite = "CheatFieldWrite";
    public const string WriteReply = "CheatFieldWriteReply";
    public const string Error = "CheatError";

    private static readonly string[] All = [FieldQuery, FieldReply, FieldWrite, WriteReply, Error];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;

    /// <summary>由桌面端发出、需要游戏插件回答的消息。</summary>
    public static bool IsRequest(string? kind) => kind is FieldQuery or FieldWrite;
}

/// <summary>
/// 作弊命令的版本化信封。翻译与 OCR 继续使用原有 DTO 格式，不受这里影响。
/// </summary>
public sealed class CheatWireMessage
{
    public int Version { get; set; } = CheatWire.Version;
    public string Kind { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string Payload { get; set; } = string.Empty;
}

/// <summary>
/// 单个 Unity 数值字段的可核查描述。Token 只在当前插件会话内有效，不能跨启动保存。
/// </summary>
public sealed record FieldSnapshot(string Token, string ObjectPath, string ComponentType,
    string FieldName, NumericKind Kind, string DisplayValue);

public sealed record CheatFieldQuery(string NameFilter);

public sealed record CheatFieldWriteTarget(string ObjectPath, string ComponentType, string FieldName, NumericKind Kind);

/// <summary>写入请求已经过 NUL、长度与数值合法性校验。</summary>
public sealed record CheatFieldWriteRequest(CheatFieldWriteTarget Target, byte[] ExpectedBytes, byte[] NewBytes);

public sealed record CheatFieldReply(IReadOnlyList<FieldSnapshot> Fields, bool Truncated, int ScannedObjects);

public enum CheatFieldWriteReplyStatus
{
    Ok,
    NotFound,
    Ambiguous,
    NotWritable,
    ValueChanged,
    Unsupported,

    /// <summary>插件拒绝了本次写入；具体原因由 Core 的字段写入守卫给出，或由插件在消息里说明。</summary>
    Refused
}

public sealed record CheatFieldWriteReply(CheatFieldWriteReplyStatus Status, FieldSnapshot? Field, string Message);
