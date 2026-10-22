using System.IO;
using System.IO.Pipes;
using System.Text;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App;

/// <summary>
/// 作弊命令的应答方。返回 null 表示没有可回复的内容，管道会回一条 CheatError。
/// </summary>
public delegate Task<CheatWireMessage?> CheatCommandResponder(CheatWireMessage command, CancellationToken cancellationToken);

/// <summary>
/// 桌面端管道服务端。
///
/// 两条设计要点：
/// 1. 读取循环只负责校验与分发。翻译、OCR、作弊命令都在读取循环之外执行，
///    因此一次慢速联网翻译不会阻塞作弊查询或写入。
/// 2. 所有出站消息经过同一把写锁，避免翻译回复、OCR 结果与作弊回复交叉写入同一条管道。
/// </summary>
public sealed class NamedPipeHost : IDisposable
{
    private readonly ITranslationDispatcher _dispatcher;
    private readonly ICheatProtocol _protocol;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
    private StreamWriter? _writer;
    private Task? _worker;

    /// <summary>
    /// 管道重开之间的退避毫秒数。**不能为 0**：`ERROR_PIPE_BUSY` 会变成毫秒级热循环。
    /// </summary>
    private const int PipeRetryDelayMs = 50;

    /// <summary>连续失败次数；只用于把日志限流，避免"重试"本身刷爆日志。</summary>
    private int _pipeErrorStreak;

    public string PipeName { get; }
    public event Action<TranslateRequest, TranslateReply?>? Translated;
    public Func<OcrFrame, CancellationToken, Task<IReadOnlyList<OcrOverlayItem>>>? OcrFrameReceived { get; set; }

    /// <summary>只处理本会话的作弊命令；null 表示尚未进入任何游戏会话。</summary>
    public string? CheatSessionId { get; set; }

    /// <summary>目标游戏进程 ID。大于 0 时，作弊命令必须来自这个进程。</summary>
    public int CheatProcessId { get; set; }

    public CheatCommandResponder? CheatResponder { get; set; }

    /// <summary>被拒绝的作弊消息（版本、会话、进程或格式不符），用于界面提示与排错。</summary>
    public event Action<string>? CheatMessageRejected;

    /// <summary>已接受的作弊命令，便于界面显示最近一次查询或写入。</summary>
    public event Action<CheatWireMessage>? CheatMessageReceived;

    /// <summary>
    /// 收到、但**无法解析因而被丢弃**的管道行。
    /// 排查"插件明明说发了请求，桌面端却毫无反应"时必须靠它 ——
    /// 以前这一行是静默 return，日志里一个字都没有（实测 Yidhra 就是这个症状）。
    /// </summary>
    public event Action<string>? LineRejected;

    /// <summary>
    /// 管道连接的生命周期（接入、读循环为什么退出、异常）。
    /// 排查"插件说写了、桌面端一个字没收到"时必须靠它判断**到底是谁先关的连接**。
    /// </summary>
    public event Action<string>? ConnectionChanged;

    public NamedPipeHost(string pipeName, ITranslationDispatcher dispatcher, ICheatProtocol? protocol = null)
    {
        PipeName = pipeName;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _protocol = protocol ?? CheatProtocol.Default;
    }

    public void Start() => _worker ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_stop.Token);
                // 接上了就把失败计数清零：这样"偶发一次失败"不会被限流吞掉、也不会被误报成连续失败。
                if (_pipeErrorStreak != 0)
                {
                    ConnectionChanged?.Invoke($"管道已恢复（此前连续失败 {_pipeErrorStreak} 次）");
                    _pipeErrorStreak = 0;
                }
                ConnectionChanged?.Invoke("客户端已接入");
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                _writer = writer;
                while (pipe.IsConnected && !_stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null)
                    {
                        ConnectionChanged?.Invoke("读循环退出：对端关闭（ReadLine 返回 null）");
                        break;
                    }
                    // 读取循环内不做业务处理，只做分发，保证作弊与翻译互不阻塞。
                    _ = DispatchAsync(line);
                }
                if (!_stop.IsCancellationRequested && !pipe.IsConnected)
                    ConnectionChanged?.Invoke("读循环退出：pipe.IsConnected 变成 false");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested) break;

                // ★ 必须"退避 + 限流"。这是用户报「等待太长」时实测到的真凶之一：
                //   失败原因是 ERROR_PIPE_BUSY（"所有的管道范例都在使用中"）时，
                //   这个 while 会**毫秒级无限重试** —— 实测宿主日志被刷到每毫秒数百行，
                //   既烧 CPU，又把真正的诊断信息（包括延迟埋点）淹没在噪声里。
                //   限流是为了不让"重试"本身变成日志洪水。
                _pipeErrorStreak++;
                if (_pipeErrorStreak == 1 || _pipeErrorStreak % 50 == 0)
                    ConnectionChanged?.Invoke($"管道异常（第 {_pipeErrorStreak} 次，退避 {PipeRetryDelayMs}ms 后重开实例）："
                        + $"{ex.GetType().Name}：{ex.Message}");
                try { await Task.Delay(PipeRetryDelayMs, _stop.Token); }
                catch (Exception) { break; }
            }
            finally
            {
                _writer = null;
            }
        }
    }

    private async Task DispatchAsync(string line)
    {
        try
        {
            OcrFrame? frame;
            if (PipeProtocol.TryReadOcrFrame(line, out frame) && frame is not null)
            {
                IReadOnlyList<OcrOverlayItem> boxes = Array.Empty<OcrOverlayItem>();
                if (OcrFrameReceived is not null)
                {
                    try { boxes = await OcrFrameReceived(frame, _stop.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or TaskCanceledException)
                    {
                        boxes = Array.Empty<OcrOverlayItem>();
                    }
                }
                await WriteLineAsync(PipeProtocol.WriteOcrOverlay(frame, boxes));
                return;
            }

            CheatWireMessage? cheat;
            var isCheatEnvelope = _protocol.TryParseEnvelope(line, out cheat, out var rejectionReason);
            if (!string.IsNullOrEmpty(rejectionReason)) CheatMessageRejected?.Invoke(rejectionReason);
            if (isCheatEnvelope && cheat is not null)
            {
                await HandleCheatAsync(cheat);
                return;
            }

            TranslateRequest? request;
            if (!PipeProtocol.TryReadRequest(line, out request) || request is null)
            {
                LineRejected?.Invoke(line);
                return;
            }
            TranslateReply? reply;
            try { reply = await _dispatcher.TranslateAsync(request, _stop.Token); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { reply = null; }
            Translated?.Invoke(request, reply);
            if (reply is not null) await WriteLineAsync(PipeProtocol.WriteReply(reply));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 管道断开属于正常情况：游戏退出或桌面端关闭，等待下一次连接即可。
        }
    }

    private async Task HandleCheatAsync(CheatWireMessage command)
    {
        var rejection = Validate(command);
        if (rejection is not null)
        {
            CheatMessageRejected?.Invoke(rejection);
            return;
        }
        CheatMessageReceived?.Invoke(command);
        CheatWireMessage? response = null;
        if (CheatMessageKinds.IsRequest(command.Kind))
        {
            if (CheatResponder is null)
            {
                response = Error(command, "游戏插件尚未连接，无法处理作弊命令。");
            }
            else
            {
                try { response = await CheatResponder(command, _stop.Token); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or
                                               ArgumentException or ObjectDisposedException or OperationCanceledException)
                {
                    response = null;
                }
            }
        }
        if (response is not null) await WriteLineAsync(_protocol.WriteMessage(response));
    }

    /// <summary>返回 null 表示接受；否则返回拒绝原因。</summary>
    private string? Validate(CheatWireMessage command)
    {
        if (CheatSessionId is null)
            return $"收到 {command.Kind} 时还没有选中的游戏会话，已忽略。";
        if (!string.Equals(command.SessionId, CheatSessionId, StringComparison.Ordinal))
            return $"作弊命令的会话与当前游戏不一致（收到 {command.SessionId}），已忽略。";
        if (CheatProcessId > 0 && command.ProcessId != CheatProcessId)
            return $"作弊命令来自进程 {command.ProcessId}，当前游戏是 {CheatProcessId}，已忽略。";
        return null;
    }

    private CheatWireMessage Error(CheatWireMessage command, string message) => new CheatWireMessage
    {
        Version = CheatWire.Version,
        Kind = CheatMessageKinds.Error,
        RequestId = command.RequestId,
        SessionId = command.SessionId,
        ProcessId = command.ProcessId,
        Payload = _protocol.WriteErrorPayload(message)
    };

    private async Task WriteLineAsync(string line) => await WriteLineAsync(line, _stop.Token);

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var writer = _writer;
            if (writer is null) return;
            await writer.WriteLineAsync(line);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 桌面端主动下发一条作弊命令（字段查询或字段写入）。
    /// 只负责写入管道；回复由订阅方从 CheatMessageReceived 处理。
    /// </summary>
    public Task SendCheatCommandAsync(CheatWireMessage command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return WriteLineAsync(_protocol.WriteMessage(command), cancellationToken);
    }

    /// <summary>桌面端向插件下发一条字段查询。</summary>
    public Task SendFieldQueryAsync(string requestId, string sessionId, int processId, string nameFilter,
        CancellationToken cancellationToken = default) =>
        SendCheatCommandAsync(new CheatWireMessage
        {
            Version = CheatWire.Version,
            Kind = CheatMessageKinds.FieldQuery,
            RequestId = requestId,
            SessionId = sessionId,
            ProcessId = processId,
            Payload = _protocol.WriteFieldQueryPayload(new CheatFieldQuery(nameFilter))
        }, cancellationToken);

    /// <summary>桌面端向插件下发一条字段写入，并给出用户确认过的当前值。</summary>
    public Task SendFieldWriteAsync(string requestId, string sessionId, int processId, CheatFieldWriteTarget target,
        string expectedValue, string newValue, CancellationToken cancellationToken = default) =>
        SendCheatCommandAsync(new CheatWireMessage
        {
            Version = CheatWire.Version,
            Kind = CheatMessageKinds.FieldWrite,
            RequestId = requestId,
            SessionId = sessionId,
            ProcessId = processId,
            Payload = _protocol.WriteFieldWritePayload(target, expectedValue, newValue)
        }, cancellationToken);

    public void Dispose()
    {
        _stop.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _writeGate.Dispose();
        _stop.Dispose();
    }
}