using System;
using System.Collections.Generic;
using System.Text;

namespace UnityTranslator.Core.Cheats;

/// <summary>
/// 作弊命令的编解码与校验。桌面端与 BepInEx 插件共用同一份源码，
/// 因此这里只使用 MiniJson（纯 BCL）而不引入任何序列化库。
/// </summary>
public sealed class CheatProtocol : ICheatProtocol
{
    public static CheatProtocol Default { get; } = new CheatProtocol();

    /// <summary>单条消息的 UTF-8 字节上限，防止畸形或超长消息占用内存。</summary>
    public const int MaxMessageBytes = 65536;

    public const int MaxFields = 500;
    public const int MaxTextLength = 256;

    private static readonly HashSet<string> NumericKindNames = NamesOf<NumericKind>();
    private static readonly HashSet<string> WriteStatusNames = NamesOf<CheatFieldWriteReplyStatus>();

    public string WriteMessage(CheatWireMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        var builder = new StringBuilder(256);
        builder.Append("{\"Version\":").Append(MiniJson.WriteInt(message.Version));
        builder.Append(",\"Kind\":").Append(MiniJson.Text(message.Kind));
        builder.Append(",\"RequestId\":").Append(MiniJson.Text(message.RequestId));
        builder.Append(",\"SessionId\":").Append(MiniJson.Text(message.SessionId));
        builder.Append(",\"ProcessId\":").Append(MiniJson.WriteInt(message.ProcessId));
        builder.Append(",\"Payload\":").Append(MiniJson.Text(message.Payload));
        builder.Append('}');
        return builder.ToString();
    }

    public bool TryReadMessage(string? line, out CheatWireMessage? message)
    {
        message = null;
        return TryParseEnvelope(line, out message, out _);
    }

    /// <summary>
    /// 与 TryReadMessage 相同，但额外给出人可读的拒绝原因，供界面提示与排错。
    /// 翻译与 OCR 消息不属于作弊协议，因此“没有 Kind 字段”不算错误。
    /// </summary>
    public bool TryParseEnvelope(string? line, out CheatWireMessage? message, out string rejectionReason)
    {
        message = null;
        rejectionReason = string.Empty;
        if (string.IsNullOrWhiteSpace(line)) return false;
        // 先按字节数拦截，避免对超大字符串做完整解析。
        if (Encoding.UTF8.GetByteCount(line!) > MaxMessageBytes)
        {
            rejectionReason = $"作弊消息超过 {MaxMessageBytes} 字节上限，已丢弃。";
            return false;
        }
        if (!MiniJson.TryParse(line, out var root) || !root.IsObject)
        {
            rejectionReason = "作弊消息不是有效的 JSON 对象，已丢弃。";
            return false;
        }
        if (!root.TryGetString("Kind", out var kind))
        {
            // 可能是翻译或 OCR 消息，交给原有协议处理，不算拒绝。
            return false;
        }
        if (!CheatMessageKinds.IsKnown(kind))
        {
            rejectionReason = $"未知的作弊消息类型 {kind}，已丢弃。";
            return false;
        }
        if (!root.TryGetInt32("Version", out var version) || version != CheatWire.Version)
        {
            rejectionReason = $"作弊消息版本不是 {CheatWire.Version}，已丢弃。";
            return false;
        }
        if (!root.TryGetString("RequestId", out var requestId) || !IsSafeToken(requestId))
        {
            rejectionReason = "作弊消息的请求编号不合法，已丢弃。";
            return false;
        }
        if (!root.TryGetString("SessionId", out var sessionId) || !IsSafeToken(sessionId))
        {
            rejectionReason = "作弊消息的会话编号不合法，已丢弃。";
            return false;
        }
        if (!root.TryGetInt32("ProcessId", out var processId) || processId < 0)
        {
            rejectionReason = "作弊消息的进程编号不合法，已丢弃。";
            return false;
        }
        if (!root.TryGetString("Payload", out var payload)) payload = string.Empty;
        if (payload.Length > MaxMessageBytes)
        {
            rejectionReason = $"作弊消息内容超过 {MaxMessageBytes} 字节上限，已丢弃。";
            return false;
        }
        message = new CheatWireMessage
        {
            Version = version,
            Kind = kind,
            RequestId = requestId,
            SessionId = sessionId,
            ProcessId = processId,
            Payload = payload
        };
        return true;
    }

    public bool TryDecodeFieldQuery(string payload, out CheatFieldQuery? query)
    {
        query = null;
        if (!TryReadObject(payload, out var root)) return false;
        var filter = string.Empty;
        if (root.TryGetString("NameFilter", out var rawFilter)) filter = rawFilter;
        if (filter.Length > MaxTextLength) return false;
        query = new CheatFieldQuery(filter);
        return true;
    }

    public bool TryDecodeFieldReply(string payload, out CheatFieldReply? reply)
    {
        reply = null;
        if (!TryReadObject(payload, out var root)) return false;
        if (!root.TryGet("Fields", out var fields) || !fields.IsArray) return false;
        if (fields.Items.Count > MaxFields) return false;
        var list = new List<FieldSnapshot>(fields.Items.Count);
        foreach (var item in fields.Items)
        {
            FieldSnapshot? snapshot;
            if (!TryReadField(item, out snapshot)) return false;
            list.Add(snapshot!);
        }
        var truncated = root.TryGetBoolean("Truncated", out var truncatedValue) && truncatedValue;
        var scanned = root.TryGetInt32("ScannedObjects", out var scannedValue) && scannedValue > 0 ? scannedValue : 0;
        reply = new CheatFieldReply(list, truncated, scanned);
        return true;
    }

    public bool TryDecodeFieldWrite(string payload, out CheatFieldWriteRequest? request)
    {
        request = null;
        if (!TryReadObject(payload, out var root)) return false;
        if (!root.TryGet("Target", out var target) || !target.IsObject) return false;
        if (!target.TryGetString("ObjectPath", out var objectPath) ||
            !target.TryGetString("ComponentType", out var componentType) ||
            !target.TryGetString("FieldName", out var fieldName) ||
            !target.TryGetString("Kind", out var kindName)) return false;
        if (string.IsNullOrWhiteSpace(fieldName) || fieldName.Length > MaxTextLength) return false;
        if (objectPath.Length > MaxTextLength || componentType.Length > MaxTextLength) return false;
        if (!NumericKindNames.Contains(kindName)) return false;
        var kind = ParseKind(kindName);
        if (!root.TryGetString("ExpectedValue", out var expectedText) ||
            !root.TryGetString("NewValue", out var newText)) return false;
        if (expectedText.Length > MaxTextLength || newText.Length > MaxTextLength) return false;
        if (!NumericValue.TryParse(expectedText, kind, out var expectedBytes)) return false;
        if (!NumericValue.TryParse(newText, kind, out var newBytes)) return false;
        var descriptor = new CheatFieldWriteTarget(objectPath, componentType, fieldName, kind);
        request = new CheatFieldWriteRequest(descriptor, expectedBytes, newBytes);
        return true;
    }

    public bool TryDecodeWriteReply(string payload, out CheatFieldWriteReply? reply)
    {
        reply = null;
        if (!TryReadObject(payload, out var root)) return false;
        if (!root.TryGetString("Status", out var statusName) || !WriteStatusNames.Contains(statusName)) return false;
        var message = root.TryGetString("Message", out var rawMessage) ? rawMessage : string.Empty;
        if (message.Length > MaxTextLength) return false;
        FieldSnapshot? field = null;
        MiniJsonValue element;
        if (root.TryGet("Field", out element) && element.IsObject)
        {
            if (!TryReadField(element, out field)) return false;
        }
        reply = new CheatFieldWriteReply(ParseWriteStatus(statusName), field, message);
        return true;
    }

    public string WriteFieldQueryPayload(CheatFieldQuery query)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        return "{\"NameFilter\":" + MiniJson.Text(query.NameFilter) + "}";
    }

    public string WriteFieldReplyPayload(CheatFieldReply reply)
    {
        if (reply is null) throw new ArgumentNullException(nameof(reply));
        var builder = new StringBuilder(1024);
        builder.Append("{\"Fields\":[");
        for (var index = 0; index < reply.Fields.Count; index++)
        {
            if (index > 0) builder.Append(',');
            AppendField(builder, reply.Fields[index]);
        }
        builder.Append("],\"Truncated\":").Append(MiniJson.WriteBool(reply.Truncated));
        builder.Append(",\"ScannedObjects\":").Append(MiniJson.WriteInt(reply.ScannedObjects));
        builder.Append('}');
        return builder.ToString();
    }

    public string WriteFieldWritePayload(CheatFieldWriteTarget target, string expectedValue, string newValue)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        var builder = new StringBuilder(256);
        builder.Append("{\"Target\":{\"ObjectPath\":").Append(MiniJson.Text(target.ObjectPath));
        builder.Append(",\"ComponentType\":").Append(MiniJson.Text(target.ComponentType));
        builder.Append(",\"FieldName\":").Append(MiniJson.Text(target.FieldName));
        builder.Append(",\"Kind\":").Append(MiniJson.Text(target.Kind.ToString()));
        builder.Append("},\"ExpectedValue\":").Append(MiniJson.Text(expectedValue));
        builder.Append(",\"NewValue\":").Append(MiniJson.Text(newValue));
        builder.Append('}');
        return builder.ToString();
    }

    public string WriteWriteReplyPayload(CheatFieldWriteReply reply)
    {
        if (reply is null) throw new ArgumentNullException(nameof(reply));
        var builder = new StringBuilder(256);
        builder.Append("{\"Status\":").Append(MiniJson.Text(reply.Status.ToString()));
        builder.Append(",\"Field\":");
        if (reply.Field is null) builder.Append("null");
        else AppendField(builder, reply.Field);
        builder.Append(",\"Message\":").Append(MiniJson.Text(reply.Message));
        builder.Append('}');
        return builder.ToString();
    }

    public string WriteErrorPayload(string message) =>
        "{\"Message\":" + MiniJson.Text(message) + "}";

    public bool TryDecodeError(string payload, out string message)
    {
        message = string.Empty;
        if (!TryReadObject(payload, out var root)) return false;
        if (!root.TryGetString("Message", out var raw)) return false;
        message = raw;
        return true;
    }

    /// <summary>RequestId 与 SessionId 只允许安全字符，避免被当作路径或注入内容使用。</summary>
    public static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value!.Length > 64) return false;
        foreach (var character in value)
            if (!IsAsciiLetterOrDigit(character) && character != '-' && character != '_') return false;
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        (character >= '0' && character <= '9') || (character >= 'a' && character <= 'z') ||
        (character >= 'A' && character <= 'Z');

    private static bool TryReadField(MiniJsonValue element, out FieldSnapshot? field)
    {
        field = null;
        if (!element.IsObject) return false;
        if (!element.TryGetString("ObjectPath", out var objectPath) ||
            !element.TryGetString("ComponentType", out var componentType) ||
            !element.TryGetString("FieldName", out var fieldName) ||
            !element.TryGetString("Kind", out var kindName) ||
            !element.TryGetString("DisplayValue", out var displayValue)) return false;
        if (!NumericKindNames.Contains(kindName)) return false;
        if (string.IsNullOrWhiteSpace(fieldName) || fieldName.Length > MaxTextLength) return false;
        if (objectPath.Length > MaxTextLength || componentType.Length > MaxTextLength) return false;
        if (displayValue.Length > MaxTextLength) return false;
        // Token 允许为空：预设与候选列表不依赖 Token。
        var token = element.TryGetString("Token", out var rawToken) ? rawToken : string.Empty;
        if (token.Length > MaxTextLength) return false;
        field = new FieldSnapshot(token, objectPath, componentType, fieldName, ParseKind(kindName), displayValue);
        return true;
    }

    private static void AppendField(StringBuilder builder, FieldSnapshot field)
    {
        builder.Append("{\"Token\":").Append(MiniJson.Text(field.Token));
        builder.Append(",\"ObjectPath\":").Append(MiniJson.Text(field.ObjectPath));
        builder.Append(",\"ComponentType\":").Append(MiniJson.Text(field.ComponentType));
        builder.Append(",\"FieldName\":").Append(MiniJson.Text(field.FieldName));
        builder.Append(",\"Kind\":").Append(MiniJson.Text(field.Kind.ToString()));
        builder.Append(",\"DisplayValue\":").Append(MiniJson.Text(field.DisplayValue));
        builder.Append('}');
    }

    private static bool TryReadObject(string? payload, out MiniJsonValue root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        if (Encoding.UTF8.GetByteCount(payload!) > MaxMessageBytes) return false;
        return MiniJson.TryParse(payload, out root) && root.IsObject;
    }

    private static HashSet<string> NamesOf<T>() where T : struct, Enum
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues(typeof(T))) names.Add(value.ToString()!);
        return names;
    }

    private static NumericKind ParseKind(string name)
    {
        foreach (var enumValue in Enum.GetValues(typeof(NumericKind)))
            if (enumValue.ToString() == name) return (NumericKind)enumValue;
        throw new ArgumentOutOfRangeException(nameof(name));
    }

    private static CheatFieldWriteReplyStatus ParseWriteStatus(string name)
    {
        foreach (var enumValue in Enum.GetValues(typeof(CheatFieldWriteReplyStatus)))
            if (enumValue.ToString() == name) return (CheatFieldWriteReplyStatus)enumValue;
        throw new ArgumentOutOfRangeException(nameof(name));
    }
}