using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityTranslator.Core;

namespace UnityTranslator.App;

/// <summary>
/// 备用翻译通道：TCP 环回。
///
/// 为什么需要：实测某些老版本 Mono（Unity 2020.1.2f1）的
/// <c>NamedPipeClientStream</c> **写不进去** —— 每次写都抛
/// `IOException: Win32 IO returned 232`（ERROR_NO_DATA），
/// 而 App 侧连接一直是好的、也一行都收不到；同一份插件代码在新版 Unity 上完全正常。
/// 四种写配置（异步/同步 × StreamWriter/原始字节）全部失败，其中同步那两种
/// 还会把本来正常的游戏一起弄坏，所以不能改管道本身。
///
/// 因此加一条**只绑环回地址**的 TCP 通道作为回退：
/// 命名管道仍是首选，只有当插件侧管道写失败时才会用到这里。
/// 端口号由 App 写进游戏目录下的 <c>.unity-translator-port</c> 供插件读取。
/// </summary>
public sealed class TcpTranslationHost : IDisposable
{
    private readonly ITranslationDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _worker;

    public TcpTranslationHost(ITranslationDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>实际监听的端口（由系统分配，避免和别的程序抢固定端口）。</summary>
    public int Port { get; private set; }

    /// <summary>接入/断开等生命周期信息，接到 app.log 便于排查。</summary>
    public event Action<string>? Diagnostics;

    public void Start()
    {
        if (_worker is not null) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                // **一次瞬时错误不能让整条备用通道永久失效。**
                // 实测（Yidhra / Unity 2020.1.2f1）：插件连上并写出第一条请求后，
                // 对端被强制关闭，紧接着每一次连接都变成"由于目标计算机积极拒绝"
                // —— 就是因为这里 break 掉之后，监听套接字再也没人 accept 了。
                Diagnostics?.Invoke($"TCP 备用通道：accept 出错（{ex.SocketErrorCode}），继续监听");
                try { await Task.Delay(200, _stop.Token); }
                catch (Exception) { break; }
                continue;
            }
            _ = HandleSafelyAsync(client);
        }
        Diagnostics?.Invoke("TCP 备用通道：监听循环已退出");
    }

    /// <summary>
    /// `HandleAsync` 是 fire-and-forget 的，任何漏出的异常都会变成"无人观察的异常"，
    /// 排查时什么线索都没有。这里兜住并记下来。
    /// </summary>
    private async Task HandleSafelyAsync(TcpClient client)
    {
        try { await HandleAsync(client); }
        catch (Exception ex)
        {
            Diagnostics?.Invoke($"TCP 备用通道：处理连接时异常：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        Diagnostics?.Invoke($"TCP 备用通道：客户端已接入（127.0.0.1:{Port}）");
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
            {
                string? line;
                while ((line = await reader.ReadLineAsync(_stop.Token)) is not null)
                {
                    // 和命名管道用同一套协议与同一个调度器，行为完全一致。
                    if (!PipeProtocol.TryReadRequest(line, out var request) || request is null) continue;
                    TranslateReply? reply;
                    try { reply = await _dispatcher.TranslateAsync(request, _stop.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException) { reply = null; }
                    if (reply is not null) await writer.WriteLineAsync(PipeProtocol.WriteReply(reply));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // 断开属于正常情况：游戏退出或插件重连。
        }
        Diagnostics?.Invoke("TCP 备用通道：客户端已断开");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Stop(); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}
