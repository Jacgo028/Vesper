using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.Plugin;

public sealed class PipeClient : IDisposable
{
    private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<ReplyMessage> _inbound;
    private readonly ConcurrentQueue<OcrOverlayMessage> _ocrInbound;
    private readonly ConcurrentQueue<CheatCommandMessage> _cheatInbound;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private volatile bool _connected;
    public bool IsConnected => _connected;

    public PipeClient(ConcurrentQueue<ReplyMessage> inbound, ConcurrentQueue<OcrOverlayMessage> ocrInbound,
        ConcurrentQueue<CheatCommandMessage> cheatInbound)
    {
        _inbound = inbound;
        _ocrInbound = ocrInbound;
        _cheatInbound = cheatInbound;
    }

    public void Start()
    {
        RunTcpProbe();
        Task.Run(Run);
    }

    /// <summary><c>Run()</c> 用来判断要不要降级到文件投递。</summary>
    public bool TcpGaveUp => _tcpGaveUp;

    /// <summary>
    /// **主线程**驱动的 TCP 回退（由 <c>Plugin.Update()</c> 每帧调用）。
    ///
    /// 全程非阻塞：`Connect` 失败立刻返回；收发都先用 `Poll(0, …)` 判可读可写，
    /// 每帧最多发 20 条，绝不阻塞主线程。
    /// 这是三轮失败之后唯一可行的形态 —— 见字段区那段注释。
    /// </summary>
    public void TickTcp()
    {
        if (!_useTcp || _useFile || _tcpGaveUp || _stop.IsCancellationRequested) return;

        if (_tcpSocket is null)
        {
            var port = ReadTcpPort();
            if (port <= 0) return;
            try
            {
                var s = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
                try { s.Blocking = false; } catch (Exception) { }
                _tcpSocket = s;
                _connected = true;
                _tcpOk = true;
                Info?.Invoke($"[TCP] 主线程已接入 127.0.0.1:{port}（用时 {sw.ElapsedMilliseconds} ms，非阻塞模式）");
            }
            catch (Exception ex)
            {
                _tcpConnectAttempts++;
                if (_tcpConnectAttempts == 1 || _tcpConnectAttempts % 60 == 0)
                    Info?.Invoke($"[TCP] 主线程连接失败第 {_tcpConnectAttempts} 次：{ex.GetType().Name}：{ex.Message}");
                if (_tcpConnectAttempts >= 120)
                {
                    _tcpGaveUp = true;
                    Info?.Invoke("[TCP] 主线程连续连接失败，放弃 TCP 回退，改用文件投递");
                }
                return;
            }
        }

        var socket = _tcpSocket;
        if (socket is null) return;
        try
        {
            var budget = 20;
            while (budget-- > 0 && _outbound.TryPeek(out var request))
            {
                if (!socket.Poll(0, System.Net.Sockets.SelectMode.SelectWrite)) break;
                var bytes = Encoding.UTF8.GetBytes(request + "\n");
                socket.Send(bytes);
                _outbound.TryDequeue(out _);
                _pumpSent++;
                if (_pumpSent == 1) Info?.Invoke("[TCP] 第一条请求已真正写出（主线程通道）");
            }

            while (socket.Poll(0, System.Net.Sockets.SelectMode.SelectRead))
            {
                var n = socket.Receive(_tcpBuffer);
                if (n <= 0) { DropTcpSocket("对端已关闭"); return; }
                _pumpRecv += n;
                _tcpPending.Append(Encoding.UTF8.GetString(_tcpBuffer, 0, n));
                int idx;
                while ((idx = IndexOfNewline(_tcpPending)) >= 0)
                {
                    var line = _tcpPending.ToString(0, idx).TrimEnd(CrOnly);
                    _tcpPending.Remove(0, idx + 1);
                    if (line.Length == 0 || line.Length > 65536) continue;
                    try
                    {
                        var reply = JsonUtility.FromJson<ReplyMessage>(line);
                        EnqueueReply(reply);
                    }
                    catch (ArgumentException) { }
                }
            }
        }
        catch (Exception ex)
        {
            DropTcpSocket(ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>
    /// 延迟埋点：此刻的**单调**毫秒数（与 <see cref="TextState.MonoNow"/> 同一时基）。
    ///
    /// 为什么用 <c>Stopwatch.GetTimestamp()</c> 而不是 <c>DateTime</c>：
    /// 后者会被系统对时（NTP）跳变污染，算出负值或虚假的尖峰。
    /// 为什么不用实例化的 <c>Stopwatch</c> 对象：四个入队点分布在管道读取线程、
    /// TCP 主线程驱动与文件兜底循环里，共享一个实例的 Start/Stop 语义会互相干扰；
    /// 静态的 GetTimestamp 是线程安全且无状态的。
    /// </summary>
    internal static long MonoNowMs() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

    /// <summary>
    /// 收包入队的**唯一**通道。集中在这里打时间戳，而不是在四个调用点各写一遍 ——
    /// 命名管道 / TCP / 文件兜底三条路径都必须有，漏掉任何一条都会让那一条的
    /// "发送→回包"永远算不出来，而**静默缺失的埋点比没有埋点更危险**（会被读成 0ms）。
    /// </summary>
    private void EnqueueReply(ReplyMessage? reply)
    {
        if (reply is null || reply.Version != 1) return;
        reply.RecvMonoMs = MonoNowMs();   // ★ 收包瞬间，早于任何排队/主线程等待
        _inbound.Enqueue(reply);
    }

    private void DropTcpSocket(string reason)
    {
        Info?.Invoke($"[TCP] 会话断开：{reason}");
        try { _tcpSocket?.Close(); } catch (Exception) { }
        _tcpSocket = null;
        _tcpPending.Clear();
        _connected = false;
    }

    /// <summary>
    /// 环回 TCP 自检（`UT_TCP_PROBE=1` 时启用）。
    ///
    /// 为什么需要：在 Unity 2020.1.2f1（Yidhra）上，TCP 回退**连接一直超时**，
    /// 而同一份代码在 Unity 2022.3.62f2（Leticia）上秒连。为了区分
    /// "**这个运行时/进程根本做不了环回 TCP**" 和 "**够不到 App 的监听端口**"，
    /// 这里在游戏进程内部**自己监听一个端口再连自己**：
    ///   - 自连接也失败 → 是该运行时的 TCP 栈问题，与 App 无关；
    ///   - 自连接成功、连 App 端口失败 → 问题在 App 侧或端口文件；
    /// 同时对 App 端口做 3 次重试（立即 / +5 秒 / +15 秒），
    /// 以排除"探针跑得比 App 监听更早"这种时序因素。
    /// </summary>
    private static void RunTcpProbe()
    {
        if (Environment.GetEnvironmentVariable("UT_TCP_PROBE") != "1") return;
        var worker = new Thread(() =>
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                var selfPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                Info?.Invoke($"[PROBE] 已在进程内自监听 127.0.0.1:{selfPort}；线程={Thread.CurrentThread.Name ?? "(unnamed)"}");
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using (var c = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp))
                    {
                        c.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, selfPort));
                    }
                    Info?.Invoke($"[PROBE] 自连接成功，用时 {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    Info?.Invoke($"[PROBE] 自连接失败：{ex.GetType().Name}：{ex.Message}");
                }
                listener.Stop();

                var portPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".unity-translator-port");
                if (!System.IO.File.Exists(portPath))
                {
                    Info?.Invoke("[PROBE] 没有端口文件，跳过 App 端口测试");
                    return;
                }
                if (!int.TryParse(System.IO.File.ReadAllText(portPath).Trim(), out var appPort))
                {
                    Info?.Invoke("[PROBE] 端口文件内容无效，跳过 App 端口测试");
                    return;
                }
                foreach (var delayMs in new[] { 0, 5000, 15000 })
                {
                    if (delayMs > 0) Thread.Sleep(delayMs);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        using (var c = new System.Net.Sockets.Socket(
                            System.Net.Sockets.AddressFamily.InterNetwork,
                            System.Net.Sockets.SocketType.Stream,
                            System.Net.Sockets.ProtocolType.Tcp))
                        {
                            c.SendTimeout = 3000;
                            c.ReceiveTimeout = 3000;
                            c.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, appPort));
                        }
                        Info?.Invoke($"[PROBE] 连接 App 端口 {appPort} 成功，用时 {sw.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        Info?.Invoke($"[PROBE] 连接 App 端口 {appPort} 失败（{sw.ElapsedMilliseconds} ms）：{ex.GetType().Name}：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Info?.Invoke($"[PROBE] 探针本身异常：{ex.GetType().Name}：{ex.Message}");
            }
        })
        { IsBackground = true, Name = "UT-TCP-Probe" };
        worker.Start();
    }
    public void Send(RequestMessage request) => _outbound.Enqueue(JsonUtility.ToJson(request));
    public void SendOcr(OcrFrameMessage frame) => _outbound.Enqueue(JsonUtility.ToJson(frame));

    /// <summary>
    /// 回一条作弊消息。两端共用不依赖游戏自带 JSON 程序集的 CheatProtocol。
    /// </summary>
    public void SendCheat(string kind, string requestId, string sessionId, int processId, string payload)
    {
        var message = new CheatWireMessage
        {
            Version = 1,
            Kind = kind,
            RequestId = requestId,
            SessionId = sessionId,
            ProcessId = processId,
            Payload = payload ?? string.Empty
        };
        _outbound.Enqueue(CheatProtocol.Default.WriteMessage(message));
    }

    /// <summary>管道层的诊断输出（由 Plugin 接到 BepInEx 日志）。</summary>
    public static Action<string>? Info { get; set; }

    /// <summary>
    /// 读一下**真正的** IsConnected。Mono 上这个属性并不可靠：老版本可能恒为 false 或直接抛。
    /// 而写入循环以前正是拿它当循环条件 —— 一旦它为 false，循环立刻退出、
    /// **发送队列再也不被消费**，可插件自己的 _connected 标志仍是 true，
    /// 于是日志看上去"连接正常、请求发得好好的"，对面却一个字都收不到。
    /// （实测 Yidhra / Unity 2020.1.2f1 就是这个症状。）
    /// </summary>
    private static string ProbeConnected(NamedPipeClientStream pipe)
    {
        try { return pipe.IsConnected ? "true" : "false"; }
        catch (Exception ex) { return "抛异常:" + ex.GetType().Name; }
    }

    /// <summary>
    /// 命名管道的名字。默认就是 <c>UnityTranslator</c>（**产品行为不变**）。
    ///
    /// 之所以加这个环境变量覆盖，是因为实测到一个会**污染全部隔离验证**的坑：
    /// 用户自己的正式版 App 一旦在跑，就占住了这个固定管道名，
    /// 于是隔离宿主**永远抢不到管道**（`ERROR_PIPE_BUSY` 无限重试，见 B85），
    /// 而测试里的插件其实连的是**用户那个 App** —— 译文是它给的、它的日志里却没有，
    /// 于是"埋点没数据"被误判成"埋点没生效"。给隔离测试一个专属名字才能真正隔离。
    /// </summary>
    private static readonly string PipeName =
        Environment.GetEnvironmentVariable("UT_PIPE_NAME") is { Length: > 0 } custom
            ? custom
            : "UnityTranslator";

    /// <summary>
    /// 回退链：**命名管道 → TCP 环回 → 文件投递**。
    /// 管道写失败（老版本 Mono 的管道客户端不可用）时置 <see cref="_useTcp"/>；
    /// TCP 也接不上时才置 <see cref="_useFile"/>。
    /// 两个环境变量只用于验证：分别把回退链**强制**从第 2 / 第 3 级开始，
    /// 否则"管道好好的"就永远走不到那条分支、也就无从证明它可用。
    /// </summary>
    private bool _useTcp = Environment.GetEnvironmentVariable("UT_TCP_TRANSPORT") == "1";

    /// <summary>最近一次 TCP 会话是否真的接上过，用来决定要不要继续往下降级。</summary>
    private volatile bool _tcpOk;

    /// <summary>管道写不进去时改用文件投递；目录与桌面端约定一致。</summary>
    private bool _useFile = Environment.GetEnvironmentVariable("UT_FILE_TRANSPORT") == "1";

    // ── 常驻 TCP 泵线程 ────────────────────────────────────────────────────────
    // **必须在 Unity 主线程上创建**（见 StartTcpPumpThread 的注释）。
    private readonly AutoResetEvent _pumpSignal = new(false);
    private readonly ConcurrentQueue<string> _pumpOut = new();
    private readonly ConcurrentQueue<string> _pumpIn = new();
    private volatile bool _pumpAlive;       // 泵线程真的跑起来了
    private volatile bool _pumpWanted;      // 主线程要求开始/维持一次会话
    private volatile bool _pumpConnected;   // 本次会话已接上
    private volatile bool _pumpDone;        // 本次会话已结束
    private int _pumpPort;

    // ── 主线程驱动的 TCP 回退（第 84 轮，当前生效的实现） ──────────────────────
    // 为什么必须放在主线程：实测 Unity 2020.1.2f1 上，进程**启动完成之后**
    // 在非主线程做 socket 操作会永久停住 —— 线程池线程里 `new Thread` 的线程体不执行、
    // 直接同步 `Connect` 永不返回、连主线程上创建的常驻线程都进不去含 socket 的方法体。
    // 只有启动瞬间跑得通（探针）。所以 TCP 这一级改由 `Plugin.Update()`
    // （每帧、Unity 主线程）用**非阻塞** API 驱动：`Poll(0, …)` 先判可读可写，
    // 每帧限量收发，`Connect` 失败立刻返回，绝不卡帧。
    private System.Net.Sockets.Socket? _tcpSocket;   // 只在主线程访问
    private int _tcpConnectAttempts;
    private volatile bool _tcpGaveUp;
    private readonly StringBuilder _tcpPending = new();
    private readonly byte[] _tcpBuffer = new byte[8192];

    /// <summary>
    /// 给 <c>TrimEnd</c> 用的单字符数组。
    ///
    /// **千万不要写成 <c>TrimEnd('\r')</c>**：那个 <c>TrimEnd(char)</c> 重载是
    /// .NET Standard 2.1 / .NET Core 才有的，而我们把插件编译到 netstandard2.1、
    /// 却要跑在 Unity 2020.1.2f1 的老 Mono 上 —— 那里只有 <c>TrimEnd(params char[])</c>。
    /// 实测就是在 Yidhra 上抛 `MissingMethodException: string string.TrimEnd(char)`，
    /// 把整个 TCP 主线程驱动在第一次调用时就打掉了。
    /// 传数组形式两种运行时都有。
    /// </summary>
    private static readonly char[] CrOnly = { '\r' };
    /// <summary>
    /// 泵线程当前走到哪一步。**故意不靠日志**：泵线程的日志调用本身也可能被阻塞，
    /// 所以阶段用普通字段记录，由主线程在超时信息里打出来 —— 这样能区分
    /// "socket 操作挂住" 和 "日志调用挂住"。
    /// </summary>
    private volatile string _pumpStage = "未开始";
    private volatile string? _pumpError;
    private int _pumpSent;
    private int _pumpRecv;


    /// <summary>
    /// 文件投递通道的一次会话：请求写到 <c>&lt;游戏目录&gt;\.unity-translator-io\req-&lt;id&gt;.json</c>，
    /// 回复从 <c>res-&lt;id&gt;.json</c> 取回。**完全不碰网络栈** ——
    /// 这个运行时里命名管道写不进去、TCP 又会阻塞线程，只剩文件这条路。
    /// 请求先写 .tmp 再改名，避免桌面端读到半截文件。
    /// </summary>
    private async Task RunFileOnce()
    {
        var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".unity-translator-io");
        System.IO.Directory.CreateDirectory(dir);
        Info?.Invoke($"[文件] 已启用文件投递通道：{dir}");
        _connected = true;
        var sent = 0;
        while (!_stop.IsCancellationRequested)
        {
            while (_outbound.TryPeek(out var request))
            {
                var id = Guid.NewGuid().ToString("N");
                var tmp = System.IO.Path.Combine(dir, "req-" + id + ".json.tmp");
                var dst = System.IO.Path.Combine(dir, "req-" + id + ".json");
                System.IO.File.WriteAllText(tmp, request);
                System.IO.File.Move(tmp, dst);
                _outbound.TryDequeue(out _);
                sent++;
                if (sent == 1) Info?.Invoke("[文件] 第一条请求已写出");
            }

            foreach (var res in System.IO.Directory.GetFiles(dir, "res-*.json"))
            {
                try
                {
                    var text = System.IO.File.ReadAllText(res);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var reply = JsonUtility.FromJson<ReplyMessage>(text);
                        EnqueueReply(reply);
                    }
                    System.IO.File.Delete(res);
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException) { }
            }
            await Task.Delay(100, _stop.Token);
        }
    }

    /// <summary>
    /// TCP 回退通道的一次会话。协议与命名管道完全一致（按行 JSON + 同一个调度器）。
    ///
    /// **为什么不能用 async/await 写这段（这是之前"TCP 一进去就再也出不来"的真正原因）**：
    /// <c>TcpClient.ConnectAsync</c> 是在 **Unity 主线程**上调用的，Mono 会把它的续体
    /// 绑定到 Unity 的 <c>SynchronizationContext</c>；随后 <c>Wait(5000)</c> 把主线程一堵，
    /// 续体永远排不上队 —— **连超时都轮不到，直接死锁**。
    /// 所以这里改成：**socket 的全部 I/O 放在独立后台线程上做纯同步调用**
    /// （<c>BeginConnect</c> + <c>WaitOne</c> 限时，之后 <c>Poll</c>/<c>Receive</c>/<c>Send</c>），
    /// 主线程这一侧只用 <c>await Task.Delay</c> 做搬运和 JSON 解析。
    /// <c>_outbound</c> 里存的本来就是主线程序列化好的 JSON 行，后台线程不碰任何 Unity API。
    /// </summary>
    /// <summary>读端口文件；拿不到有效端口就返回 0（并说明原因）。</summary>
    private static int ReadTcpPort()
    {
        var portPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".unity-translator-port");
        if (!System.IO.File.Exists(portPath))
        {
            Info?.Invoke("[TCP] 找不到端口文件 .unity-translator-port，暂时无法回退（等桌面端写入）");
            return 0;
        }
        var text = System.IO.File.ReadAllText(portPath).Trim();
        if (!int.TryParse(text, out var port) || port <= 0 || port > 65535)
        {
            Info?.Invoke($"[TCP] 端口文件内容无效：\"{text}\"");
            return 0;
        }
        Info?.Invoke($"[TCP] 端口文件 {portPath} → {port}");
        return port;
    }

    /// <summary>
    /// 【已废弃 —— 保留仅作记录，不再有任何调用点】
    ///
    /// 这是第 82 轮的"在 Unity 主线程上创建常驻泵线程"方案。它证明了
    /// **主线程创建线程这一步是有效的**（泵线程确实启动并收到了会话请求），
    /// 但随后卡在进入 `RunPumpSession` 的方法调用上 ——
    /// `_pumpStage` 停在 `收到会话请求`、始终不前进到 `创建 socket 前`。
    ///
    /// 三轮实验（泵线程 / 线程池同步 Connect / 主线程常驻线程）全部停住，
    /// 而**启动瞬间**同一个探针却能 1 ms 连上 —— 结论是这个运行时启动完成后
    /// 非主线程的 socket 路径不可用。**当前生效的实现是 `TickTcp()`（主线程非阻塞）。**
    /// </summary>
    private void StartTcpPumpThread()
    {
        try
        {
            var pump = new Thread(TcpPumpLoop) { IsBackground = true, Name = "UT-TCP-Pump" };
            pump.Start();
        }
        catch (Exception ex)
        {
            Info?.Invoke($"[TCP] 泵线程创建失败：{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 常驻泵线程的主循环：等主线程发来会话请求，跑完一次会话再回来等。
    /// **只搬字节，绝不调用任何 Unity API。**
    /// </summary>
    private void TcpPumpLoop()
    {
        _pumpAlive = true;
        _pumpStage = "泵线程已启动，等待会话请求";
        while (!_stop.IsCancellationRequested)
        {
            bool signaled;
            try { signaled = _pumpSignal.WaitOne(200); }
            catch (Exception) { continue; }
            if (_stop.IsCancellationRequested) break;
            if (!signaled || !_pumpWanted) continue;
            _pumpStage = "收到会话请求";
            RunPumpSession(_pumpPort);
            _pumpStage = "会话已结束，等待下次请求";
        }
    }

    /// <summary>
    /// 一次传输会话：连接、把 <see cref="_pumpOut"/> 里的行发出去、
    /// 把收到的行放进 <see cref="_pumpIn"/> 交给主线程解析。
    /// </summary>
    private void RunPumpSession(int port)
    {
        System.Net.Sockets.Socket? socket = null;
        try
        {
            _pumpStage = "创建 socket 前";
            socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            _pumpStage = "socket 已创建，准备 Connect";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            socket.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
            _pumpStage = $"Connect 已返回（{sw.ElapsedMilliseconds} ms）";
            // 超时属性放在连接之后再设：某些 Mono 版本在未连接的 socket 上设它会抛。
            try { socket.SendTimeout = 2000; socket.ReceiveTimeout = 2000; } catch (Exception) { }
            _pumpStage = "传输中";
            _pumpConnected = true;

            var buffer = new byte[8192];
            var pending = new StringBuilder();
            while (!_stop.IsCancellationRequested && _pumpWanted)
            {
                while (_pumpOut.TryDequeue(out var line))
                {
                    socket.Send(Encoding.UTF8.GetBytes(line + "\n"));
                    _pumpSent++;
                }
                while (socket.Poll(0, System.Net.Sockets.SelectMode.SelectRead))
                {
                    var n = socket.Receive(buffer);
                    if (n <= 0) { _pumpStage = "对端已关闭"; return; }
                    _pumpRecv += n;
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    int idx;
                    while ((idx = IndexOfNewline(pending)) >= 0)
                    {
                        var line = pending.ToString(0, idx).TrimEnd(CrOnly);
                        pending.Remove(0, idx + 1);
                        if (line.Length > 0 && line.Length <= 65536) _pumpIn.Enqueue(line);
                    }
                }
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            // **不能在这里记日志**：泵线程的日志调用本身会阻塞（实测），
            // 把原因存进字段、由主线程打出来。
            _pumpError = ex.GetType().Name + "：" + ex.Message;
        }
        finally
        {
            try { socket?.Close(); } catch (Exception) { }
            if (_pumpError is null) _pumpStage = "会话正常结束";
            _pumpConnected = false;
            _pumpDone = true;
        }
    }

    /// <summary>
    /// `Run()` 这一侧（线程池线程）的入口：请泵线程开一次会话，
    /// 然后把 <c>_outbound</c> 搬进 <c>_pumpOut</c>、把 <c>_pumpIn</c> 解析进 <c>_inbound</c>。
    ///
    /// **关键：这里绝不直接碰 socket** —— 只做队列搬运和 JSON 解析，
    /// 所以即使泵线程卡住，本循环也不会被拖死，仍能继续降级到文件投递。
    /// 这正是上一版最严重的问题：`Connect` 挂住把整个传输循环一起卡死了。
    /// </summary>
    private async Task RunTcpOnce()
    {
        var port = ReadTcpPort();
        if (port <= 0) return;
        if (!_pumpAlive)
        {
            Info?.Invoke("[TCP] 泵线程未能启动，无法使用 TCP 回退");
            return;
        }

        _pumpPort = port;
        _pumpConnected = false;
        _pumpDone = false;
        _pumpError = null;
        _pumpSent = 0;
        _pumpRecv = 0;
        _pumpWanted = true;
        try { _pumpSignal.Set(); }
        catch (Exception ex) { Info?.Invoke($"[TCP] 唤醒泵线程失败：{ex.GetType().Name}：{ex.Message}"); }

        var waited = 0;
        while (waited < 10000 && !_pumpConnected && !_pumpDone && !_stop.IsCancellationRequested)
        {
            try { await Task.Delay(100, _stop.Token); }
            catch (OperationCanceledException) { break; }
            waited += 100;
        }
        if (!_pumpConnected)
        {
            Info?.Invoke($"[TCP] 连接 127.0.0.1:{port} 未成功（等待 {waited} ms，泵已结束={_pumpDone}，"
                + $"泵阶段=\"{_pumpStage}\"，泵错误={_pumpError ?? "无"}）");
            _pumpWanted = false;
            return;
        }
        Info?.Invoke($"[TCP] 已接入 127.0.0.1:{port}（泵阶段=\"{_pumpStage}\"）");

        _connected = true;
        _tcpOk = true;
        var moved = 0;
        while (!_stop.IsCancellationRequested && !_pumpDone)
        {
            // 先 Peek 再转投、成功才出队 —— 和管道那条路径同一套"不丢请求"的写法。
            while (_outbound.TryPeek(out var request))
            {
                _pumpOut.Enqueue(request);
                _outbound.TryDequeue(out _);
                moved++;
            }
            while (_pumpIn.TryDequeue(out var line))
            {
                try
                {
                    var reply = JsonUtility.FromJson<ReplyMessage>(line);
                    EnqueueReply(reply);
                }
                catch (ArgumentException) { }
            }
            try { await Task.Delay(50, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
        _pumpWanted = false;
        Info?.Invoke($"[TCP] 本次会话结束：转投 {moved} 条请求，泵发出 {_pumpSent} 条、收到 {_pumpRecv} 字节，泵阶段=\"{_pumpStage}\"，泵错误={_pumpError ?? "无"}");
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }


    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            // 回退链第 2 级：TCP 环回。App 侧会把端口写进游戏目录的 .unity-translator-port。
            // 泵线程在 Start() 里（Unity 主线程上）就已建好并常驻；这里只投递任务、收结果，
            // **绝不直接碰 socket**，所以泵即使卡住也不会把传输循环拖死。
            if (_useTcp && !_useFile)
            {
                // TCP 完全由主线程的 TickTcp() 驱动（见其注释：这个运行时上
                // 在本线程碰 socket 会永久停住）。这里只负责在它放弃时降级。
                if (_tcpGaveUp)
                {
                    _useFile = true;
                    Info?.Invoke("[TCP] 回退未成功，继续降级到文件投递通道");
                }
                try { await Task.Delay(200, _stop.Token); } catch (Exception) { }
                continue;
            }

            // 回退链第 3 级（最后一级）：文件投递。
            if (_useFile)
            {
                try { await RunFileOnce(); }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException ||
                                               ex is InvalidOperationException || ex is ArgumentException ||
                                               ex is UnauthorizedAccessException)
                {
                    Info?.Invoke($"[文件] 通道出错，稍后重试：{ex.GetType().Name}：{ex.Message}");
                }
                finally { _connected = false; }
                try { await Task.Delay(1000, _stop.Token); } catch (Exception) { }
                continue;
            }
            try
            {
                // 必须用 PipeOptions.Asynchronous。两次实验证明同步管道行不通：
                // 同步 + StreamWriter → Leticia `Pipe is broken`；
                // 同步 + 原始阻塞写   → Leticia 依旧 `Pipe is broken`、Yidhra 依旧 232。
                // 已回退。Yidhra 的 `Win32 IO returned 232` 见进展记录 B19/B20。
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(1000);
                    _connected = true;
                    Info?.Invoke($"[管道] 已连接；真正的 IsConnected={ProbeConnected(pipe)}（插件自己的标志=true）");
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
                                    if (line.Contains("\"Kind\":\"OcrOverlay\""))
                                    {
                                        if (OcrOverlayWire.TryRead(line, out var wire) && wire != null)
                                        {
                                            var boxes = new OcrOverlayBox[wire.Boxes.Length];
                                            for (var index = 0; index < boxes.Length; index++)
                                            {
                                                var box = wire.Boxes[index];
                                                boxes[index] = new OcrOverlayBox { X = box.X, Y = box.Y,
                                                    Width = box.Width, Height = box.Height, Translated = box.Translated };
                                            }
                                            _ocrInbound.Enqueue(new OcrOverlayMessage { Version = wire.Version, Kind = wire.Kind,
                                                FrameId = wire.FrameId, Width = wire.Width, Height = wire.Height, Boxes = boxes });
                                        }
                                        continue;
                                    }
                                    if (line.Contains("\"Kind\":\"Cheat"))
                                    {
                                        // 作弊命令交给主线程处理，读取循环不做反射或写入。
                                        var command = ParseCheatCommand(line);
                                        if (command != null) _cheatInbound.Enqueue(command);
                                        continue;
                                    }
                                    var reply = JsonUtility.FromJson<ReplyMessage>(line);
                                    EnqueueReply(reply);
                                }
                                catch (ArgumentException) { }
                            }
                        });
                        var written = 0;
                        var writeFailed = false;
                        // 条件里不用 pipe.IsConnected：Mono 上它不可靠，读到 false 就再也不消费队列。
                        // 断连改由写失败的异常来发现。
                        while (!_stop.IsCancellationRequested && !reading.IsCompleted)
                        {
                            try
                            {
                                // **先 Peek 再写，写成功才出队**。
                                // 以前是 TryDequeue 之后才写，一旦写失败这条请求已经出队、
                                // 就此永久丢失，既不重发也没人知道（Yidhra 的 6 条请求就是这么没的）。
                                while (_outbound.TryPeek(out var request))
                                {
                                    // 直接往管道写原始字节，**绕开 StreamWriter**。
                                    // Mono 上 StreamWriter + 命名管道 + AutoFlush 是已知的脆弱组合：
                                    // 实测在 Yidhra（Unity 2020.1.2f1）上每次写都抛
                                    // `IOException: Win32 IO returned 232`（ERROR_NO_DATA），
                                    // 而同一份代码在 Leticia（2022.3.62f2）上第一条就写成功。
                                    var payload = Encoding.UTF8.GetBytes(request + "\n");
                                    await pipe.WriteAsync(payload, 0, payload.Length);
                                    await pipe.FlushAsync();
                                    _outbound.TryDequeue(out _);
                                    written++;
                                    if (written == 1) Info?.Invoke("[管道] 第一条请求已真正写出（原始字节路径）");
                                }
                            }
                            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                            {
                                // 断线交给外层重连；未写出的那条仍在队列里，连上后会重发。
                                // 若失败原因是"管道写不进去"（老版本 Mono 的管道客户端不可用），
                                // 下一轮起改走 TCP 回退通道 —— 端口由桌面端写在游戏目录里。
                                if (written == 0) _useTcp = true;
                                writeFailed = true;
                                Info?.Invoke($"[管道] 写出失败，准备重连并重发（队列里还有 {_outbound.Count} 条）："
                                    + $"{ex.GetType().Name}：{ex.Message}"
                                    + (written == 0 ? "；下次改走 TCP 回退通道" : string.Empty));
                                break;
                            }
                            await Task.Delay(50, _stop.Token);
                        }
                        // 写失败时**不能**等 reading：它要等管道断开才结束，
                        // 会把外层循环卡死，TCP 回退永远轮不上（实测踩到过）。
                        if (!writeFailed) await reading;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // The game remains usable while the desktop translator is offline.
            }
            finally
            {
                _connected = false;
                // 这里以前会把发送队列整个倒空。断线重连后待发的请求就被悄悄丢掉了，
                // 而标题界面这类不再变化的文字永远不会再触发一次发送 —— 译文就此永久缺失。
                // 现在保留队列，等下一次连上继续发。
            }
            try { await Task.Delay(1000, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>只接受版本、类型、请求编号与会话编号都合法的作弊命令。</summary>
    internal static CheatCommandMessage? ParseCheatCommand(string line)
    {
        if (!CheatProtocol.Default.TryReadMessage(line, out var message) || message is null) return null;
        var kind = message.Kind;
        if (kind != "CheatFieldQuery" && kind != "CheatFieldWrite") return null;
        return new CheatCommandMessage
        {
            Version = 1,
            Kind = kind,
            RequestId = message.RequestId,
            SessionId = message.SessionId,
            ProcessId = message.ProcessId,
            Payload = message.Payload
        };
    }

    public void Dispose()
    {
        _connected = false;
        _stop.Cancel();
    }
}

/// <summary>桌面端下发、需要在 Unity 主线程执行的作弊命令。</summary>
public sealed class CheatCommandMessage
{
    public int Version;
    public string Kind = string.Empty;
    public string RequestId = string.Empty;
    public string SessionId = string.Empty;
    public int ProcessId;
    public string Payload = string.Empty;
}
