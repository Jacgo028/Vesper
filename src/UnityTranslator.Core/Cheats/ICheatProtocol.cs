namespace UnityTranslator.Core.Cheats;

/// <summary>作弊命令的信封版本。收发双方都必须等于这个值，否则消息被丢弃。</summary>
public static class CheatWire
{
    public const int Version = 1;
}

/// <summary>
/// 作弊命令的编解码器。桌面端与游戏插件各自使用同一份 Core 源码的
/// <see cref="CheatProtocol"/> 实现，测试可以换成自己的替身。
/// </summary>
public interface ICheatProtocol
{
    string WriteMessage(CheatWireMessage message);

    /// <summary>校验版本、Kind、RequestId、SessionId、ProcessId 与总长度。</summary>
    bool TryReadMessage(string? line, out CheatWireMessage? message);

    /// <summary>与 <see cref="TryReadMessage"/> 相同，但会给出人可读的拒绝原因。</summary>
    bool TryParseEnvelope(string? line, out CheatWireMessage? message, out string rejectionReason);

    bool TryDecodeFieldQuery(string payload, out CheatFieldQuery? query);

    bool TryDecodeFieldReply(string payload, out CheatFieldReply? reply);

    bool TryDecodeFieldWrite(string payload, out CheatFieldWriteRequest? request);

    bool TryDecodeWriteReply(string payload, out CheatFieldWriteReply? reply);

    bool TryDecodeError(string payload, out string message);

    string WriteFieldQueryPayload(CheatFieldQuery query);

    string WriteFieldReplyPayload(CheatFieldReply reply);

    /// <summary>ExpectedValue 与 NewValue 使用界面上的文本；插件侧会再次严格解析。</summary>
    string WriteFieldWritePayload(CheatFieldWriteTarget target, string expectedValue, string newValue);

    string WriteWriteReplyPayload(CheatFieldWriteReply reply);

    string WriteErrorPayload(string message);
}
