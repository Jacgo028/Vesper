using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UnityTranslator.App;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

/// <summary>
/// 任务 4 的协议级测试：作弊命令必须带版本、会话与进程校验，
/// 并且不能在翻译很慢时被阻塞。
/// </summary>
public sealed class CheatPipeTests
{
    private const string Session = "sess-abc123";
    private const int GamePid = 4242;

    private static CheatWireMessage Query(string requestId, string sessionId = Session, int processId = GamePid,
        string nameFilter = "gold") => new CheatWireMessage
        {
            Version = CheatWire.Version,
            Kind = CheatMessageKinds.FieldQuery,
            RequestId = requestId,
            SessionId = sessionId,
            ProcessId = processId,
            Payload = CheatProtocol.Default.WriteFieldQueryPayload(new CheatFieldQuery(nameFilter))
        };

    private static NamedPipeHost CreateHost(out string pipeName, ITranslationDispatcher dispatcher)
    {
        pipeName = "UnityTranslatorCheatTest" + Guid.NewGuid().ToString("N");
        return new NamedPipeHost(pipeName, dispatcher);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(NamedPipeHost host)
    {
        var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    [Fact]
    public async Task FieldQueryRoundTripsWithSessionAndProcessChecks()
    {
        using var host = CreateHost(out _, new StubDispatcher());
        host.CheatSessionId = Session;
        host.CheatProcessId = GamePid;
        var handled = new List<CheatWireMessage>();
        host.CheatResponder = (command, _) =>
        {
            handled.Add(command);
            return Task.FromResult<CheatWireMessage?>(new CheatWireMessage
            {
                Version = CheatWire.Version,
                Kind = CheatMessageKinds.FieldReply,
                RequestId = command.RequestId,
                SessionId = command.SessionId,
                ProcessId = command.ProcessId,
                Payload = CheatProtocol.Default.WriteFieldReplyPayload(new CheatFieldReply(
                    new[] { new FieldSnapshot("tok-1", "Player/Gold", "Stats", "gold", NumericKind.Int32, "50") },
                    false, 12))
            });
        };
        host.Start();
        using var pipe = await ConnectAsync(host);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync(CheatProtocol.Default.WriteMessage(Query("req-1")));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(line);
        Assert.True(CheatProtocol.Default.TryReadMessage(line, out var reply));
        Assert.Equal(CheatMessageKinds.FieldReply, reply!.Kind);
        Assert.Equal("req-1", reply.RequestId);
        Assert.Equal(Session, reply.SessionId);
        Assert.Equal(GamePid, reply.ProcessId);
        Assert.True(CheatProtocol.Default.TryDecodeFieldReply(reply.Payload, out var payload));
        Assert.Equal("gold", payload!.Fields[0].FieldName);
        Assert.Equal(NumericKind.Int32, payload.Fields[0].Kind);
        Assert.Equal(12, payload.ScannedObjects);

        // 插件确实收到了可解码的查询参数，而不是被转发成看不懂的字符串。
        var only = Assert.Single(handled);
        Assert.True(CheatProtocol.Default.TryDecodeFieldQuery(only.Payload, out var query));
        Assert.Equal("gold", query!.NameFilter);
    }

    [Fact]
    public async Task MalformedAndUnsupportedMessagesAreRejectedWithoutBreakingThePipe()
    {
        using var host = CreateHost(out _, new StubDispatcher());
        host.CheatSessionId = Session;
        host.CheatProcessId = GamePid;
        var handled = 0;
        host.CheatResponder = (command, _) =>
        {
            Interlocked.Increment(ref handled);
            return Task.FromResult<CheatWireMessage?>(new CheatWireMessage
            {
                Version = CheatWire.Version,
                Kind = CheatMessageKinds.FieldReply,
                RequestId = command.RequestId,
                SessionId = command.SessionId,
                ProcessId = command.ProcessId,
                Payload = CheatProtocol.Default.WriteFieldReplyPayload(new CheatFieldReply(
                    Array.Empty<FieldSnapshot>(), false, 0))
            });
        };
        var rejections = new List<string>();
        host.CheatMessageRejected += reason => { lock (rejections) rejections.Add(reason); };
        host.Start();
        using var pipe = await ConnectAsync(host);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        // 畸形 JSON、错误版本、未知 Kind：都按“不是作弊消息”处理，不进入作弊处理函数。
        await writer.WriteLineAsync("{\"Version\":1,\"Kind\":\"CheatFieldQuery\",");
        await writer.WriteLineAsync("{\"Version\":99,\"Kind\":\"CheatFieldQuery\",\"RequestId\":\"r\",\"SessionId\":\"" + Session +
                                   "\",\"ProcessId\":" + GamePid + ",\"Payload\":\"{}\"}");
        await writer.WriteLineAsync("{\"Version\":1,\"Kind\":\"CheatDropTable\",\"RequestId\":\"r\",\"SessionId\":\"" + Session +
                                   "\",\"ProcessId\":" + GamePid + ",\"Payload\":\"{}\"}");
        // 会话与进程不符：解析通过但必须被拒绝，并且不回复任何内容。
        await writer.WriteLineAsync(CheatProtocol.Default.WriteMessage(Query("req-wrong-session", "sess-other")));
        await writer.WriteLineAsync(CheatProtocol.Default.WriteMessage(Query("req-wrong-pid", Session, GamePid + 1)));
        // 超过 64 KiB 的消息必须在解析前被丢弃。
        await writer.WriteLineAsync("{\"Version\":1,\"Kind\":\"CheatFieldQuery\",\"RequestId\":\"r\",\"SessionId\":\"" + Session +
                                   "\",\"ProcessId\":" + GamePid + ",\"Payload\":\"" + new string('x', 70000) + "\"}");

        // 一条完全正常的消息仍然要成功，证明管道没有被前面的畸形消息破坏。
        await writer.WriteLineAsync(CheatProtocol.Default.WriteMessage(Query("req-good")));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(line);
        Assert.True(CheatProtocol.Default.TryReadMessage(line, out var reply));
        Assert.Equal("req-good", reply!.RequestId);

        // 只有那条正常消息到达了作弊处理函数。
        Assert.Equal(1, Volatile.Read(ref handled));
        // 但会话/进程不符与超长消息被明确记录，便于界面提示。
        Assert.True(rejections.Count >= 3, $"expected at least 3 rejections, got {rejections.Count}");
        Assert.Contains(rejections, reason => reason.Contains("会话", StringComparison.Ordinal));
        Assert.Contains(rejections, reason => reason.Contains("进程", StringComparison.Ordinal));

        // 超长消息必须先于解析被截获：这里用一条前缀合法、尾部长噪声的消息验证。
        var oversized = "{\"Version\":1,\"Kind\":\"CheatFieldQuery\",\"RequestId\":\"r\",\"SessionId\":\"" + Session +
                        "\",\"ProcessId\":" + GamePid + ",\"Payload\":\"" + new string('y', 70000) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > CheatProtocol.MaxMessageBytes);
        Assert.False(CheatProtocol.Default.TryReadMessage(oversized, out _));
    }

    [Fact]
    public async Task CheatCommandIsHandledWhileTranslationIsStillBlocked()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translatorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = CreateHost(out _, new StubDispatcher(async (_, _) =>
        {
            translatorEntered.TrySetResult();
            await release.Task;
            return "已翻译";
        }));
        host.CheatSessionId = Session;
        host.CheatProcessId = GamePid;
        host.CheatResponder = (command, _) => Task.FromResult<CheatWireMessage?>(new CheatWireMessage
        {
            Version = CheatWire.Version,
            Kind = CheatMessageKinds.FieldReply,
            RequestId = command.RequestId,
            SessionId = command.SessionId,
            ProcessId = command.ProcessId,
            Payload = CheatProtocol.Default.WriteFieldReplyPayload(new CheatFieldReply(
                Array.Empty<FieldSnapshot>(), false, 0))
        });
        host.Start();
        using var pipe = await ConnectAsync(host);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        // 先发翻译请求：服务端会把它挂在一个永不完成的委托上。
        await writer.WriteLineAsync("{\"Version\":1,\"RequestId\":\"t1\",\"Source\":\"Start\",\"SourceLanguage\":\"en\",\"ComponentId\":7,\"Generation\":2}");
        await translatorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(release.Task.IsCompleted);

        // 翻译仍然卡住时，作弊命令必须能拿到回复。
        await writer.WriteLineAsync(CheatProtocol.Default.WriteMessage(Query("req-during-translation")));
        var cheatLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(cheatLine);
        Assert.True(CheatProtocol.Default.TryReadMessage(cheatLine, out var cheatReply));
        Assert.Equal("req-during-translation", cheatReply!.RequestId);
        Assert.False(release.Task.IsCompleted);

        // 放行翻译后，翻译回复也要照常到达。
        release.SetResult();
        var translationLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(translationLine);
        using (var json = JsonDocument.Parse(translationLine!))
        {
            Assert.Equal("t1", json.RootElement.GetProperty("RequestId").GetString());
            Assert.Equal("已翻译", json.RootElement.GetProperty("Translated").GetString());
        }
    }

    [Fact]
    public async Task FieldWriteRoundTripsExpectedAndNewValues()
    {
        CheatFieldWriteRequest? seen = null;
        using var host = CreateHost(out _, new StubDispatcher());
        host.CheatSessionId = Session;
        host.CheatProcessId = GamePid;
        host.CheatResponder = (command, _) =>
        {
            CheatProtocol.Default.TryDecodeFieldWrite(command.Payload, out seen);
            return Task.FromResult<CheatWireMessage?>(new CheatWireMessage
            {
                Version = CheatWire.Version,
                Kind = CheatMessageKinds.WriteReply,
                RequestId = command.RequestId,
                SessionId = command.SessionId,
                ProcessId = command.ProcessId,
                Payload = CheatProtocol.Default.WriteWriteReplyPayload(new CheatFieldWriteReply(
                    CheatFieldWriteReplyStatus.Ok,
                    new FieldSnapshot("tok-9", "Player", "Stats", "gold", NumericKind.Int64, "9999"),
                    "数值已修改"))
            });
        };
        host.Start();
        using var pipe = await ConnectAsync(host);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync(CheatProtocol.Default.WriteMessage(new CheatWireMessage
        {
            Version = CheatWire.Version,
            Kind = CheatMessageKinds.FieldWrite,
            RequestId = "w-1",
            SessionId = Session,
            ProcessId = GamePid,
            Payload = CheatProtocol.Default.WriteFieldWritePayload(
                new CheatFieldWriteTarget("Player", "Stats", "gold", NumericKind.Int64), "50", "9999")
        }));

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(line);
        Assert.True(CheatProtocol.Default.TryReadMessage(line, out var reply));
        Assert.Equal(CheatMessageKinds.WriteReply, reply!.Kind);
        Assert.True(CheatProtocol.Default.TryDecodeWriteReply(reply.Payload, out var writeReply));
        Assert.Equal(CheatFieldWriteReplyStatus.Ok, writeReply!.Status);
        Assert.Equal("9999", writeReply.Field!.DisplayValue);

        Assert.NotNull(seen);
        Assert.Equal("gold", seen!.Target.FieldName);
        Assert.Equal(NumericKind.Int64, seen.Target.Kind);
        Assert.Equal(50L, BitConverter.ToInt64(seen.ExpectedBytes, 0));
        Assert.Equal(9999L, BitConverter.ToInt64(seen.NewBytes, 0));
    }

    [Fact]
    public async Task RequestWithoutResponderStillGetsATypedError()
    {
        using var host = CreateHost(out _, new StubDispatcher());
        host.CheatSessionId = Session;
        host.CheatProcessId = GamePid;
        host.Start();
        using var pipe = await ConnectAsync(host);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync(CheatProtocol.Default.WriteMessage(Query("req-no-responder")));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(line);
        Assert.True(CheatProtocol.Default.TryReadMessage(line, out var reply));
        Assert.Equal(CheatMessageKinds.Error, reply!.Kind);
        Assert.Equal("req-no-responder", reply.RequestId);
        Assert.True(CheatProtocol.Default.TryDecodeError(reply.Payload, out var message));
        Assert.Contains("插件", message, StringComparison.Ordinal);
    }

    private sealed class StubDispatcher : ITranslationDispatcher
    {
        private readonly Func<TranslateRequest, CancellationToken, Task<string?>> _handler;

        public StubDispatcher(Func<TranslateRequest, CancellationToken, Task<string?>>? handler = null) =>
            _handler = handler ?? ((_, _) => Task.FromResult<string?>(null));

        public async Task<TranslateReply?> TranslateAsync(TranslateRequest request, CancellationToken cancellationToken)
        {
            var translated = await _handler(request, cancellationToken);
            return translated is null ? null : new TranslateReply(request.RequestId, translated, request.ComponentId, request.Generation);
        }
    }
}
