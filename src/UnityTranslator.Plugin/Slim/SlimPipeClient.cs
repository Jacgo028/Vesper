#if SLIM_NO_TMP
using System;
using System.Collections.Concurrent;   // ← 精简变体用的是 Slim/SlimNet35Polyfills.cs 里的 net35 垫片
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UnityTranslator.Plugin;

/// <summary>
/// 精简变体的传输层，**替换掉 <c>PipeClient</c>**（后者在精简变体里整份排除）。
///
/// 为什么不能复用 `PipeClient`（进展记录 B79/B80 实测）：
/// 目标游戏 NightofRevenge 跑在 **Unity 5.6.7 / CLR 2.0** 上，它的 Managed 里
///   * 没有 `netstandard.dll`；
///   * 没有 `System.Collections.Concurrent`（.NET 4.0）；
///   * **`System.Core.dll` 只有 260 KB（正常约 700 KB），`System.IO.Pipes` 被裁掉了** ——
///     所以**命名管道在这款游戏上根本不存在**；
///   * 没有 `Newtonsoft.Json.dll`。
/// 于是这里只用 CLR 2.0 就有的东西：同步 socket、`File`、`StringBuilder`、`JsonUtility`。
///
/// 传输链：**TCP 环回 → 文件投递**（没有命名管道这一级）。
/// TCP 由 <c>Plugin.Update()</c> 每帧调用 <see cref="TickTcp"/> 驱动，
/// **全程非阻塞**（`Poll(0, …)` 先判可读可写）—— 与 B77 在 Unity 2020 上验证通过的形态一致。
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly ConcurrentQueue<ReplyMessage> _inbound;
    private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();

    private volatile bool _connected;
    private volatile bool _stopped;

    /// <summary>没有命名管道可退，所以 TCP 默认就是第一级；`UT_FILE_TRANSPORT=1` 可强制走文件。</summary>
    private bool _useFile = Environment.GetEnvironmentVariable("UT_FILE_TRANSPORT") == "1";
    private volatile bool _tcpGaveUp;

    private System.Net.Sockets.Socket _tcpSocket;      // 只在主线程访问
    private int _tcpAttempts;
    private readonly StringBuilder _tcpPending = new StringBuilder();
    private readonly byte[] _tcpBuffer = new byte[8192];

    /// <summary>不做 OCR（精简变体里 `OcrCapture.cs` 整份排除）。</summary>
    public PipeClient(ConcurrentQueue<ReplyMessage> inbound,
                      ConcurrentQueue<OcrOverlayMessage> ocrInbound,
                      ConcurrentQueue<CheatCommandMessage> cheatInbound)
    {
        _inbound = inbound;
    }

    /// <summary>诊断输出（由 Plugin 接到 BepInEx 日志）。</summary>
    public static Action<string> Info { get; set; }

    public bool IsConnected { get { return _connected; } }
    public bool TcpGaveUp { get { return _tcpGaveUp; } }

    /// <summary>什么也不用启动：一切都由主线程的 <see cref="TickTcp"/> 驱动。</summary>
    public void Start()
    {
        if (Info != null) Info("[精简] 传输层使用 TCP/文件（本游戏没有命名管道可用）");
    }

    public void Send(RequestMessage request)
    {
        _outbound.Enqueue(JsonUtility.ToJson(request));
    }

    public void SendOcr(OcrFrameMessage frame) { /* 精简变体不做 OCR */ }

    public void SendCheat(string kind, string requestId, string sessionId, int processId, string payload)
    { /* 精简变体不做作弊功能 */ }

    /// <summary>由 <c>Plugin.Update()</c> 每帧调用（**Unity 主线程**）。</summary>
    public void TickTcp()
    {
        if (_stopped) return;
        if (_useFile) { TickFile(); return; }
        TickTcpOnce();
    }

    // ── 第 1 级：TCP 环回（主线程、非阻塞） ─────────────────────────────────────
    private void TickTcpOnce()
    {
        if (_tcpGaveUp) return;

        if (_tcpSocket == null)
        {
            var port = ReadPort();
            if (port <= 0) return;
            try
            {
                var s = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                s.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
                try { s.Blocking = false; } catch (Exception) { }
                _tcpSocket = s;
                _connected = true;
                Info("[精简·TCP] 已接入 127.0.0.1:" + port);
            }
            catch (Exception ex)
            {
                _tcpAttempts++;
                if (_tcpAttempts == 1 || _tcpAttempts % 120 == 0)
                    Info("[精简·TCP] 连接失败第 " + _tcpAttempts + " 次：" + ex.GetType().Name + "：" + ex.Message);
                if (_tcpAttempts >= 300)
                {
                    _tcpGaveUp = true;
                    _useFile = true;
                    Info("[精简·TCP] 连续失败，改用文件投递");
                }
                return;
            }
        }

        var socket = _tcpSocket;
        try
        {
            var budget = 20;
            while (budget-- > 0 && _outbound.TryPeek(out var request))
            {
                if (!socket.Poll(0, System.Net.Sockets.SelectMode.SelectWrite)) break;
                var bytes = Encoding.UTF8.GetBytes(request + "\n");
                socket.Send(bytes);
                _outbound.TryDequeue(out var _unused);
            }

            while (socket.Poll(0, System.Net.Sockets.SelectMode.SelectRead))
            {
                var n = socket.Receive(_tcpBuffer);
                if (n <= 0) { DropSocket("对端已关闭"); return; }
                _tcpPending.Append(Encoding.UTF8.GetString(_tcpBuffer, 0, n));
                int idx;
                while ((idx = IndexOfNewline(_tcpPending)) >= 0)
                {
                    var line = _tcpPending.ToString(0, idx).TrimEnd(CrOnly);
                    _tcpPending.Remove(0, idx + 1);
                    EnqueueReply(line);
                }
            }
        }
        catch (Exception ex)
        {
            DropSocket(ex.GetType().Name + "：" + ex.Message);
        }
    }

    private void DropSocket(string reason)
    {
        Info("[精简·TCP] 会话断开：" + reason);
        try { if (_tcpSocket != null) _tcpSocket.Close(); } catch (Exception) { }
        _tcpSocket = null;
        _tcpPending.Length = 0;
        _connected = false;
    }

    // ── 第 2 级：文件投递 ───────────────────────────────────────────────────────
    private void TickFile()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".unity-translator-io");
        try
        {
            Directory.CreateDirectory(dir);
            _connected = true;

            var budget = 20;
            while (budget-- > 0 && _outbound.TryPeek(out var request))
            {
                var id = Guid.NewGuid().ToString("N");
                var tmp = Path.Combine(dir, "req-" + id + ".json.tmp");
                var dst = Path.Combine(dir, "req-" + id + ".json");
                File.WriteAllText(tmp, request);
                File.Move(tmp, dst);              // 先写 .tmp 再改名，避免对端读到半截
                _outbound.TryDequeue(out var _unused);
            }

            var files = Directory.GetFiles(dir, "res-*.json");
            for (var i = 0; i < files.Length; i++)
            {
                try
                {
                    var text = File.ReadAllText(files[i]);
                    if (!string.IsNullOrEmpty(text)) EnqueueReply(text);
                    File.Delete(files[i]);
                }
                catch (Exception) { }
            }
        }
        catch (Exception ex)
        {
            _connected = false;
            Info("[精简·文件] 通道出错：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    // ── 公共小工具 ─────────────────────────────────────────────────────────────
    private static readonly char[] CrOnly = { '\r' };

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }

    private void EnqueueReply(string line)
    {
        if (line == null || line.Length == 0 || line.Length > 65536) return;
        try
        {
            var reply = JsonUtility.FromJson<ReplyMessage>(line);
            if (reply != null && reply.Version == 1) _inbound.Enqueue(reply);
        }
        catch (Exception) { }
    }

    private static int ReadPort()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".unity-translator-port");
            if (!File.Exists(path)) return 0;
            var text = File.ReadAllText(path).Trim();
            int port;
            if (!int.TryParse(text, out port) || port <= 0 || port > 65535) return 0;
            return port;
        }
        catch (Exception) { return 0; }
    }

    public void Dispose()
    {
        _stopped = true;
        try { if (_tcpSocket != null) _tcpSocket.Close(); } catch (Exception) { }
        _tcpSocket = null;
        _connected = false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 这几个消息类型原本定义在 PipeClient.cs / OcrCapture.cs 里，而这两个文件在精简变体里都被排除。
// Plugin.cs 仍然声明了对应字段，所以这里补上最小定义。
// **注意（B80 的教训）**：整份排除一个文件会连带它定义的公共类型一起消失，
// 排除之前要先确认没有别的文件引用。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>OCR 叠加层消息（精简变体不做 OCR，仅为让 Plugin.cs 编译通过）。</summary>
public sealed class OcrOverlayMessage
{
    public int Version;
}

/// <summary>OCR 帧消息（同上）。</summary>
public sealed class OcrFrameMessage
{
    public int Version;
    public int Width;
    public int Height;
    public string Png = string.Empty;
}

/// <summary>作弊命令消息（精简变体不做作弊功能）。</summary>
public sealed class CheatCommandMessage
{
    public int Version;
    public string Kind = string.Empty;
    public string RequestId = string.Empty;
    public string SessionId = string.Empty;
    public int ProcessId;
    public string Payload = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// 协议类型。原本定义在 `Plugin.cs` 末尾，而 `Plugin.cs` 在精简变体里被整份排除。
// **字段名与顺序必须与主变体完全一致** —— 它们就是走 JSON 的线路格式，
// 对端（桌面端）用同一套 `PipeProtocol` 解析。
// ─────────────────────────────────────────────────────────────────────────────

[Serializable]
public sealed class RequestMessage
{
    public int Version = 1;
    public string RequestId = string.Empty;
    public string Source = string.Empty;
    public string SourceLanguage = string.Empty;
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
#endif
