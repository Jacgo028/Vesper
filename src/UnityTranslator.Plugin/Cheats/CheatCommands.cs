using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityTranslator.Core.Cheats;
using UnityTranslator.Plugin.Cheats;

namespace UnityTranslator.Plugin;

/// <summary>
/// 作弊功能的插件侧：在主线程分批发现字段，并按桌面端的查询与写入命令作答。
/// 翻译与 OCR 继续走原有代码路径，两者共用同一条管道但队列独立，互不阻塞。
/// </summary>
public sealed partial class Plugin
{
    private readonly ConcurrentQueue<CheatCommandMessage> _cheatCommands = new ConcurrentQueue<CheatCommandMessage>();
    private readonly UnityFieldProbe _fieldProbe = new UnityFieldProbe();
    private string? _cheatSessionId;
    private int _cheatQueries;
    private int _cheatWrites;

    /// <summary>每个 Unity 帧最多反射多少个组件。太小会让候选出现得很慢，太大又会影响帧率。</summary>
    private const int ObjectsPerFrame = 48;

    /// <summary>单帧最多处理多少条作弊命令，避免一次查询占满一整帧。</summary>
    private const int CommandsPerFrame = 4;

    private void TickCheats()
    {
        if (_pipe == null) return;
        if (!_pipe.IsConnected)
        {
            // 桌面端断开时清空会话与待处理命令：旧会话的命令不能在新会话里执行。
            _cheatSessionId = null;
            while (_cheatCommands.TryDequeue(out _)) { }
            return;
        }
        _fieldProbe.Tick(ObjectsPerFrame);
        for (var handled = 0; handled < CommandsPerFrame; handled++)
        {
            if (!_cheatCommands.TryDequeue(out var command)) return;
            try
            {
                HandleCheatCommand(command);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("作弊命令处理失败：" + ex.Message);
            }
        }
    }

    private void HandleCheatCommand(CheatCommandMessage command)
    {
        if (_pipe == null) return;
        // 第一条合法命令建立会话；之后任何会话不一致的命令一律拒绝，
        // 这样切换游戏或重启桌面端后，旧命令不会作用到新进程。
        if (_cheatSessionId is null) _cheatSessionId = command.SessionId;
        if (!string.Equals(_cheatSessionId, command.SessionId, StringComparison.Ordinal))
        {
            Reply(command, CheatMessageKinds.Error, CheatProtocol.Default.WriteErrorPayload("会话已变化，请重新连接。"));
            return;
        }

        if (command.Kind == CheatMessageKinds.FieldQuery) HandleQuery(command);
        else if (command.Kind == CheatMessageKinds.FieldWrite) HandleWrite(command);
    }

    private void HandleQuery(CheatCommandMessage command)
    {
        if (!CheatProtocol.Default.TryDecodeFieldQuery(command.Payload, out var query) || query is null)
        {
            Reply(command, CheatMessageKinds.Error, CheatProtocol.Default.WriteErrorPayload("查询参数不合法。"));
            return;
        }
        var fields = _fieldProbe.Query(query.NameFilter);
        if (_cheatQueries++ < 5)
            Logger.LogInfo("作弊字段查询：" + fields.Count + " 条候选，已扫描 " + _fieldProbe.ScannedObjects + " 个组件");
        Reply(command, CheatMessageKinds.FieldReply,
            CheatProtocol.Default.WriteFieldReplyPayload(new CheatFieldReply(fields, _fieldProbe.Truncated, _fieldProbe.ScannedObjects)));
    }

    private void HandleWrite(CheatCommandMessage command)
    {
        if (!CheatProtocol.Default.TryDecodeFieldWrite(command.Payload, out var request) || request is null)
        {
            Reply(command, CheatMessageKinds.WriteReply, CheatProtocol.Default.WriteWriteReplyPayload(
                new CheatFieldWriteReply(CheatFieldWriteReplyStatus.Unsupported, null, "写入参数不合法")));
            return;
        }
        var result = _fieldProbe.Write(request);
        if (_cheatWrites++ < 10)
            Logger.LogInfo("作弊字段写入：" + result.Status + " " + result.Message);
        Reply(command, CheatMessageKinds.WriteReply, CheatProtocol.Default.WriteWriteReplyPayload(
            new CheatFieldWriteReply(result.Status, result.Field, result.Message)));
    }

    private void Reply(CheatCommandMessage command, string kind, string payload) =>
        _pipe?.SendCheat(kind, command.RequestId, command.SessionId, command.ProcessId, payload);
}
