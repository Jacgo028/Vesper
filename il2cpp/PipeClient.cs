using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityTranslator.Plugin.Il2Cpp;

/// <summary>
/// 命名管道客户端。与 Mono 插件的实现保持同一份协议（Version=1 的 JSON 行），
/// 所以 App 侧完全不需要区分插件是 Mono 还是 IL2CPP。
/// 序列化用 System.Text.Json：IL2CPP 运行时是 .NET 6，它就在 BCL 里，
/// 而且 App 侧用的也是同一个序列化器，产出的字段名一致。
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<ReplyMessage> _inbound;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private volatile bool _connected;

    public bool IsConnected => _connected;
    public string PipeName { get; }

    public PipeClient(string pipeName, ConcurrentQueue<ReplyMessage> inbound)
    {
        PipeName = pipeName;
        _inbound = inbound;
    }

    public void Start() => Task.Run(Run);
    /// <summary>
    /// 序列化选项。
    ///
    /// ★★★ 必须 <c>IncludeFields = true</c>（本轮定位到的致命问题）：
    ///   <see cref="RequestMessage"/> / <see cref="ReplyMessage"/> 的成员全是**字段**，
    ///   而 <c>System.Text.Json</c> **默认只序列化属性、忽略字段** ——
    ///   于是发出去的 JSON 里根本没有 <c>Version</c> / <c>RequestId</c> / <c>Source</c>，
    ///   对端（App 或测试用假服务）看到的是一个空对象，直接跳过；
    ///   反序列化回来的回复同样全是默认值，匹配永远失败。
    ///   实测表现极具迷惑性：插件日志写着"本次可翻译=8"，而管道对端一条请求都收不到。
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
    };

    public void Send(RequestMessage request) =>
        _outbound.Enqueue(JsonSerializer.Serialize(request, WireOptions));

    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(1000);
                    _connected = true;
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                    {
                        var reading = Task.Run(async () =>
                        {
                            string? line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (line.Length > 65536) continue;
                                try
                                {
                                    var reply = JsonSerializer.Deserialize<ReplyMessage>(line, WireOptions);
                                    if (reply != null && reply.Version == 1) _inbound.Enqueue(reply);
                                }
                                catch (JsonException) { }
                            }
                        });
                        while (!_stop.IsCancellationRequested && pipe.IsConnected && !reading.IsCompleted)
                        {
                            while (_outbound.TryDequeue(out var request)) await writer.WriteLineAsync(request);
                            await Task.Delay(50, _stop.Token);
                        }
                        await reading;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException
                                       || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // 桌面程序没开时游戏照常可玩
            }
            finally
            {
                _connected = false;
                while (_outbound.TryDequeue(out _)) { }
            }
            try { await Task.Delay(1000, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _connected = false;
        _stop.Cancel();
    }
}

[Serializable]
public sealed class RequestMessage
{
    public int Version = 1;
    public string RequestId = string.Empty;
    public string Source = string.Empty;
    public string SourceLanguage = string.Empty;
    // long：IL2CPP 下对象指针是 64 位（实测约 2.4e12），用 int 会溢出
    public long ComponentId;
    public int Generation;
}

[Serializable]
public sealed class ReplyMessage
{
    public int Version;
    public string RequestId = string.Empty;
    public string Translated = string.Empty;
    public long ComponentId;
    public int Generation;
}
