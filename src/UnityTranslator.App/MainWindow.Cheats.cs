using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App;

/// <summary>
/// 游戏工作台：游戏与翻译、数值修改、游戏数值推荐三个页面。
///
/// 关键约束：
/// - 作弊功能不依赖联网翻译服务，也不要求填写密钥；
/// - 所有查找、修改、锁定都绑定当前选中的游戏进程；
/// - 游戏退出或切换游戏时清空结果并停止锁定；
/// - 自动推荐只是候选，必须由玩家确认后才写入。
/// </summary>
public partial class MainWindow
{
    private readonly MemoryScanner _scanner = new MemoryScanner();
    private MemoryScanWorkspace _scanWorkspace = new();
    private MemoryAddressTable? _addressTable;
    private readonly ObservableCollection<CheatTableEntry> _addressEntries = [];
    private DispatcherTimer? _addressRefreshTimer;
    private long _totalScanHits;
    private readonly ObservableCollection<CheatPreset> _cheatPresets = new ObservableCollection<CheatPreset>();
    private readonly ObservableCollection<UnityFieldCandidate> _unityFields = new ObservableCollection<UnityFieldCandidate>();
    private readonly ObservableCollection<RankedCandidateView> _rankedFields = new ObservableCollection<RankedCandidateView>();
    private readonly ObservableCollection<ScanHitView> _cheatHits = new ObservableCollection<ScanHitView>();

    private CheatPresetStore? _presetStore;
    private IProcessMemory? _processMemory;
    private CheatSession? _cheatSession;
    private CancellationTokenSource? _scanStop;
    private ScanHit? _frozenHit;
    private GameProfile? _activeGame;

    /// <summary>备用 TCP 通道（只绑环回）。部分老版本 Mono 的命名管道客户端写不进去时由插件回退使用。</summary>
    private TcpTranslationHost? _tcpHost;

    /// <summary>备用文件投递通道。TCP 在该运行时也会阻塞线程，所以再留一条不碰网络栈的路。</summary>
    private FileDropTranslationHost? _fileHost;
    private int _uid;
    private IReadOnlyList<CheatPreset> _loadedPresets = Array.Empty<CheatPreset>();
    private readonly Dictionary<string, CheatFieldWriteTarget> _fieldTargets =
        new Dictionary<string, CheatFieldWriteTarget>(StringComparer.Ordinal);
    private ScanHitView? _selectedHit;
    /// <summary>本次状态是明确设置的（例如“游戏已退出”），刷新时不要用通用提示覆盖它。</summary>
    private bool _cheatStatusExplicit;
    /// <summary>这次会话里做过“首次查找”没有。</summary>
    private bool _firstScanAttempted;
    /// <summary>这次运行里建立过游戏会话没有：用来区分“还没启动过”和“会话已断开”。</summary>
    private bool _hadGameSession;
    /// <summary>跟踪的游戏进程已经退出（正在等新进程接管）：此时不能再修改或锁定。</summary>
    private bool _processLost;
    /// <summary>“再次查找”用过没有（用它区分“还没查找”和“筛出 0 项”）。</summary>
    private bool _hasRefined;
    /// <summary>上一次“再次查找”筛出多少项。</summary>
    private int _lastRefineCount;
    /// <summary>窗口正在关闭：不要再尝试接管新进程。</summary>
    private bool _closing;
    /// <summary>游戏进程退出后等待新进程出现的定时器（Unity 游戏常会重启自己）。</summary>
    private DispatcherTimer? _takeoverTimer;
    private int _takeoverAttempts;
    private int _lastAttachedProcessId;
    /// <summary>测试替身：只认这些“正在运行的进程”，不真的去枚举系统进程。</summary>
    internal Func<GameProfile, IReadOnlyList<GameProcessInfo>>? RunningProcessesForTest { get; set; }

    /// <summary>测试替身：接管/连接新进程时用它代替真实进程内存（测试里没有真实游戏进程）。</summary>
    internal Func<GameProcessInfo, IProcessMemory?>? ProcessMemoryForTest { get; set; }

    /// <summary>游戏退出后等新进程出现的总时长与轮询间隔。</summary>
    private static readonly TimeSpan TakeoverWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TakeoverInterval = TimeSpan.FromSeconds(0.6);

    /// <summary>不联网的翻译调度器：翻译关闭时作弊功能也必须能启动。</summary>
    private sealed class DisabledTranslation : ITranslationDispatcher
    {
        public Task<TranslateReply?> TranslateAsync(TranslateRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<TranslateReply?>(null);
    }

    public ObservableCollection<CheatPreset> CheatPresets => _cheatPresets;

    public ObservableCollection<UnityFieldCandidate> UnityFields => _unityFields;

    public ObservableCollection<ScanHitView> CheatHits => _cheatHits;

    /// <summary>页签标题，顺序固定为 游戏与翻译 / 数值修改 / 游戏数值推荐。</summary>
    public IReadOnlyList<string> WorkspacePageTitles => WorkspaceTabs.Items
        .OfType<TabItem>()
        .Select(item => item.Header?.ToString() ?? string.Empty)
        .ToArray();

    /// <summary>作弊入口不因缺少翻译服务地址或密钥而被禁用。</summary>
    public bool CanStartCheatsWithoutTranslation => true;

    public GameProfile? ActiveGame => _activeGame;

    public bool HasGameSession => _cheatSession is not null;

    public string? CheatSessionId { get; private set; }

    public int CheatProcessId { get; private set; }

    /// <summary>进程退出后不能再修改或锁定。</summary>
    public bool CanWriteCheatValue => _cheatSession is not null && _selectedHit is not null && !_processLost;

    public bool CanFreezeCheatValue => _cheatSession is not null && _selectedHit is not null && !_processLost;

    /// <summary>进程没了的等待期内不能开新查找：那时查的是已经不存在的进程。</summary>
    public bool CanScan => _cheatSession is not null && !_processLost;

    public bool HasCheatHits => _cheatHits.Count > 0;

    public bool IsFreezing => _cheatSession?.IsFreezing ?? false;

    /// <summary>Unity 字段查询是否成功收到过回复。</summary>
    public bool UnityFieldsAvailable { get; private set; }

    private CheatPresetStore PresetStore => _presetStore ??= new CheatPresetStore(
        Path.Combine(_appDataDirectory, "cheat-presets.json"));

    // ================= 会话 =================

    /// <summary>
    /// 启动游戏并建立作弊会话。翻译调度器由调用方决定：翻译关闭时使用
    /// <see cref="DisabledTranslation"/>，查询与修改照常可用。
    /// </summary>
    private void StartGameSession(GameProfile game, ITranslationDispatcher dispatcher, string statusText)
    {
        EndGameSession();
        EnsureHost(dispatcher);
        CheatSessionId = Guid.NewGuid().ToString("N");
        _host!.CheatSessionId = CheatSessionId;
        _host.CheatProcessId = 0;
        Log($"管道：已启动，会话 {CheatSessionId}，管道名 UnityTranslator");

        // 记在 try 外面：启动成功后要用它当"只看这次启动写的日志"的时间闸。
        var launchStamp = DateTime.UtcNow;

        try
        {
            // `UT_GAME_ARGS` 只给**隔离验证**用：某些游戏的行为（例如界面语言）由命令行
            // 参数决定，要复现"用户在日文界面下看到的缺陷"就必须能把它传进去。
            // 不设置时**默认行为完全不变**（等于不传任何参数）。
            //
            // 为什么走环境变量而不是改界面：这是验证手段，不是产品功能；
            // 而且宿主是由 `ut-installui` 启动的，环境变量是最省事、最不容易漏的传递方式。
            var extraArgs = Environment.GetEnvironmentVariable("UT_GAME_ARGS");
            var psi = new ProcessStartInfo(game.ExePath)
            {
                WorkingDirectory = game.RootPath,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(extraArgs))
            {
                psi.Arguments = extraArgs;
                Log($"启动游戏：透传额外参数（验证用）：{extraArgs}");
            }
            // ★ 预加载器日志是**追加**的：必须记下"启动前"这个时刻，
            //   后面只认比它新的日志行，否则会把上一轮的失败当成这一轮的。
            launchStamp = DateTime.UtcNow;
            _gameProcess = Process.Start(psi) ?? throw new IOException("游戏进程未能启动");
        }
        catch
        {
            // 启动失败时把会话收干净，避免留下一个没有游戏的“已连接”状态。
            EndGameSession();
            throw;
        }
        _gameProcess.EnableRaisingEvents = true;
        _gameProcess.Exited += (_, _) => Dispatcher.BeginInvoke(OnGameProcessExited);

        CheatProcessId = _gameProcess.Id;
        _host.CheatProcessId = CheatProcessId;
        _activeGame = game;
        PublishOcrSessionCapability(game);
        _hadGameSession = true;
        _processLost = false;
        WriteTransportPortFile(game);
        Log($"启动游戏：{game.DisplayName}（exe={game.ExePath}，目录={game.RootPath}，"
            + $"Unity={game.UnityVersion}，后端={game.Backend}，架构={game.Architecture}）");
        Log($"会话：会话 ID={CheatSessionId}，跟踪进程 PID={CheatProcessId}（{DescribeProcess(_gameProcess)}）");
        AttachMemory(_gameProcess);
        SetRunStatus(statusText, RunState.Ok);
        if (dispatcher is TranslationCoordinator coordinator) BindTranslationOcrSession(game, coordinator);
        LoadPresetsForActiveGame(game);
        RefreshWorkspaceState();

        // ★ 启动之后还要再看一眼：预加载器**崩在加载任何插件之前**，
        //   插件自己一行日志都写不出来（实测 cambion 就是这么崩的），
        //   所以"游戏起来了但翻译一直没反应"这件事只能在宿主这边发现。
        //   发现之后自动换成 BepInEx 5 并记住，用户只需要再点一次启动。
        WatchPreloaderAfterLaunch(game, _gameProcess.Id, launchStamp);
    }

    /// <summary>
    /// 游戏启动后盯一段时间的预加载器日志，发现"预加载器整体崩了"就自动回退到 BepInEx 5。
    ///
    /// 两条判据缺一不可，少一条就会误判成"该回退"（那比不回退更坏 —— 会把
    /// 一个本来能用的装置换掉）：
    ///   ① 日志是**本次启动之后**写的（<c>launchStamp</c>）—— 排除历史失败记录。
    ///      这一条同时顶掉了"日志读不到就不判"：没有新日志 = 读不到 = 不判。
    ///   ② 仍在跟踪**同一个** PID —— 用户可能已经关掉又启动了别的游戏。
    ///
    /// ★ 第 100 轮改掉的一条旧判据（原来还要求"游戏进程还活着"）：
    ///   实测 cambion 上预加载器崩掉时，**游戏进程是一起退掉的**
    ///   （`TypeInitializationException` 发生在 Unity 起来之前，
    ///   玩家看到的是"点了启动，游戏闪一下就没了"）。
    ///   那时候要求"程序还活着"才判，等于在最该回退的场景里永远不回退。
    ///   现在改成：只要这次启动留下了失败证据就判 —— 依据是**日志**，不是进程存活。
    ///   安全性不靠存活性保证，而靠 <see cref="PreloaderFailureDetector.IsPreloaderFailure"/>
    ///   那条"必须同时出现致命标志且没有 finished 标志"的严格判据。
    /// </summary>
    private void WatchPreloaderAfterLaunch(GameProfile game, int processId, DateTime launchStamp)
    {
        // ★ 这条路径**完全静默**：不发日志、不改界面，只是在后台悄悄看一眼。
        //   第 100 轮验证自动回退时，正是"它一声不吭"让我分不清
        //   "看门狗没装上 / 装了但没判出来 / 判出来了但换失败"三种情况，白跑两轮。
        //   所以加一条落盘追踪，默认关闭（不设 UT_FALLBACK_TRACE 就一行都不写）。
        void Trace(string message) => LoaderFallbackTrace(message);

        Trace($"看门狗：请求安装（游戏={game.DisplayName} PID={processId} 偏好="
            + $"{(string.IsNullOrEmpty(game.PreferredLoader) ? "(自动)" : game.PreferredLoader)}）");
        if (game.PrefersBepInEx5)
        {
            Trace("看门狗：跳过 —— 已经在最后一级（BepInEx 5）上了");
            return;
        }
        if (PackageCatalog.FallbackPackageName(game, LoaderFamily.Auto) is null)
        {
            Trace("看门狗：跳过 —— 这款游戏没有可退的档（非 Mono）");
            return;
        }
        if (!_autoFallbackAttempted.Add(game.GameId))
        {
            Trace("看门狗：跳过 —— 这一局已经试过一次了");
            return;
        }
        Trace($"看门狗：已装上，时间闸={launchStamp:HH:mm:ss.fff}Z，最多盯 120 秒");

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                try
                {
                    // 不再跟踪这个 PID（＝已经换成一局新的了）就停手。
                    if (!IsStillTracked(processId))
                    {
                        Trace($"看门狗：第 {(i + 1) * 3} 秒停手 —— 跟踪的 PID 已经变成 {CheatProcessId}");
                        return;
                    }
                    var text = PreloaderFailureDetector.ReadLogText(game.RootPath, launchStamp);
                    var failed = PreloaderFailureDetector.IsPreloaderFailure(text);
                    Trace($"看门狗：第 {(i + 1) * 3} 秒 日志={(text is null ? "读不到" : text.Length + " 字符")}"
                        + $" 判定失败={failed}");
                    if (!failed) continue;
                    await Dispatcher.InvokeAsync(() => BeginLoaderFallback(game));
                    Trace("看门狗：已请求界面线程执行回退");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Trace($"看门狗：第 {(i + 1) * 3} 秒出错 {ex.GetType().Name} {ex.Message}");
                }
            }
            Trace("看门狗：盯满 120 秒没发现失败，收工");
        });
    }

    /// <summary>当前跟踪的进程还是不是当初那一个（而不是用户后来启动的另一个游戏）。</summary>
    private bool IsStillTracked(int processId) => CheatProcessId == processId;

    /// <summary>
    /// 用户已经把游戏退掉了 —— 这时候可以安全地把 BepInEx 6 换成 BepInEx 5。
    /// 换完只提示一句"再启动一次"，因为已经在跑的那个进程**换不掉**
    /// （预加载器只在进程启动时执行一次，改文件对当前进程没有任何作用）。
    /// </summary>
    private void BeginLoaderFallback(GameProfile game)
    {
        // ★ 这一段原先在异常面前是**完全静默**的：第 100 轮做无人值守验证时，
        //   看门狗明明判出了失败、也派发到了界面线程，但界面上什么都没发生，
        //   而日志里连一行都没有 —— 完全看不出是哪一步断的。
        //   自动路径出问题时用户没有任何线索，所以这里每一步都留痕，异常也说出来。
        LoaderFallbackTrace($"进入回退：游戏={game.DisplayName}");
        try
        {
            if (_gameProcess is { HasExited: false })
            {
                LoaderFallbackTrace($"游戏还在跑（PID={_gameProcess.Id}），等它退出再换");
                SetRunStatus($"{game.DisplayName}：翻译组件用的加载器起不来，退出游戏后会自动换掉。",
                    RunState.Warning, "退出游戏即可，不用做别的；换好之后需要重新启动一次游戏。");
                if (!_pendingLoaderFallback.Contains(game.GameId))
                {
                    _pendingLoaderFallback.Add(game.GameId);
                    var pending = _gameProcess;
                    pending.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
                    {
                        LoaderFallbackTrace("游戏已退出，开始换加载器");
                        _pendingLoaderFallback.Remove(game.GameId);
                        TryLoaderFallback(game);
                    });
                    pending.EnableRaisingEvents = true;
                }
                return;
            }
            LoaderFallbackTrace("游戏已经不在跑，直接换");
            TryLoaderFallback(game);
        }
        catch (Exception ex)
        {
            // 不允许静默失败：回退没做成，用户至少要知道"这件事失败了"。
            LoaderFallbackTrace($"回退入口抛异常：{ex.GetType().Name} {ex.Message}");
            SetRunStatus($"{game.DisplayName}：自动更换加载器时出错。", RunState.Error,
                "请手动点“安装翻译组件”；仍不行就把这条状态记下来反馈。",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>加载器回退的落盘追踪：只在设了 <c>UT_FALLBACK_TRACE</c> 时写（默认一行都不写）。</summary>
    private static void LoaderFallbackTrace(string message)
    {
        var path = Environment.GetEnvironmentVariable("UT_FALLBACK_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException) { }
    }

    /// <summary>
    /// 把组件换成这款游戏实测能跑的加载器（BepInEx 5），并把结果记进游戏列表。
    ///
    /// 顺序是**先换文件、后记偏好**：换失败就不该留下"已回退"的记录，
    /// 否则下次启动会拿一个根本没装上的包去自检。
    /// </summary>
    private void TryLoaderFallback(GameProfile game)
    {
        LoaderFallbackTrace($"开始换：游戏目录={game.RootPath}，程序目录={AppContext.BaseDirectory}");
        var package = PackageCatalog.Load(AppContext.BaseDirectory, game, LoaderFamily.BepInEx5);
        if (package is null || package.Count == 0)
        {
            // 说清楚"在哪里找过"：两种情况用户要做的事完全不同 ——
            // 一种是包缺了（重新解压），另一种是把 exe 单独挪出来跑了（放回去）。
            var expected = Path.Combine(AppContext.BaseDirectory, "packages", "unity2020-mono");
            LoaderFallbackTrace($"没有找到 BepInEx 5 的组件包，放弃（找过 {expected}）");
            Log($"加载器回退：没有找到 BepInEx 5 的组件包（找过 {expected}），跳过");
            SetRunStatus($"{game.DisplayName}：BepInEx 6 起不来，但没找到 BepInEx 5 的组件包。", RunState.Warning,
                $"在本工具所在目录的 packages 下没找到。请把 packages 目录和本程序放在一起（或重新解压一份完整产品包），再点“安装翻译组件”。",
                $"期望路径：{expected}");
            return;
        }
        LoaderFallbackTrace($"已载入 BepInEx 5 包（{package.Count} 个文件）");

        try
        {
            var plan = InstallPlanner.Plan(game, package, ComponentNotice.CurrentVersion);
            // ★ 必须带上"要清掉旧加载器残留"：两个 BepInEx 大版本的 core 文件名大面积重名、
            //   内容不同，还有对方版本独有的文件（详见 InstallPlanner.RemoveStaleFiles 的说明）。
            //   只覆盖不清理 = 留下一个两代混装的目录，正是本次要修的那个故障的形状。
            ApplyInstall(plan, game, package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or InvalidOperationException)
        {
            Log($"加载器回退：安装失败：{ex.GetType().Name} {ex.Message}");
            SetRunStatus($"{game.DisplayName}：自动换成 BepInEx 5 时失败了。", RunState.Error,
                "请手动点“安装翻译组件”，仍不行就把这条状态记下来反馈。");
            return;
        }

        var remembered = _library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader);
        // 列表里的对象换了（with 表达式生成新实例），界面上的卡片也要跟着刷，
        // 否则下次点"安装/自检"还是拿旧偏好去算包。
        RefreshGameList();
        LoaderFallbackTrace($"完成：已记住偏好={remembered}，界面卡片已刷新");
        Log($"加载器回退：{game.DisplayName} 已改用 BepInEx 5（记住偏好={remembered}），"
            + "下次启动生效");
        SetRunStatus($"{game.DisplayName}：BepInEx 6 起不来，已自动改成 BepInEx 5。", RunState.Ok,
            "请重新启动一次游戏，翻译就会跟着起来。");
        // 走 ShowMessage（而不是直接弹对话框）：这条路径是**自动**发生的，
        // 必须能被无人值守验证跑到，否则"回退到底会不会自己走完"就只能靠人工点。
        ShowMessage(
            $"{game.DisplayName} 上用 BepInEx 6 起不来（游戏自带运行时缺一个入口点），\n"
            + "已经把翻译组件换成 BepInEx 5 并记住了，以后不会再换回去。\n\n"
            + "请重新启动一次游戏。",
            "已自动改用 BepInEx 5", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 确保通信通道已经建好。作弊功能不依赖联网翻译，所以没有传入调度器时
    /// 用 <see cref="DisabledTranslation"/>：查询与修改照常可用。
    /// “连接到正在运行的游戏”也走这里，保证那条路不依赖“先启动过一次游戏”。
    /// </summary>
    private void EnsureHost(ITranslationDispatcher? dispatcher = null)
    {
        if (_host is not null) return;
        var resolved = dispatcher ?? new DisabledTranslation();
        // 管道名默认仍是 "UnityTranslator"（产品行为不变）。
        // `UT_PIPE_NAME` 只给隔离验证用：用户正式版 App 在跑时会占住固定名字，
        // 隔离宿主就永远抢不到（ERROR_PIPE_BUSY 无限重试），测量数据全是脏的。
        var pipeName = Environment.GetEnvironmentVariable("UT_PIPE_NAME") is { Length: > 0 } custom
            ? custom
            : "UnityTranslator";
        _host = new NamedPipeHost(pipeName, resolved);
        _host.CheatResponder = HandleCheatCommandAsync;
        _host.CheatMessageRejected += message => Dispatcher.BeginInvoke(() => SetCheatWarning(message));
        // 被丢弃的管道行一定要留痕：否则"插件说发了、桌面端没反应"这种事根本没法查。
        _host.LineRejected += line => Log("管道：丢弃了一行无法解析的消息："
            + (line.Length <= 200 ? line : line.Substring(0, 200) + "…"));
        _host.ConnectionChanged += message => Log("管道：" + message);
        AttachOcrHandlers(_host);
        _host.Start();

        // 备用通道：只绑环回的 TCP。某些老版本 Mono 的命名管道客户端写不进去
        // （Unity 2020.1.2f1 实测每次写都 ERROR_NO_DATA），插件会回退到这里。
        // 命名管道仍是首选，这条通道建着不用也不影响任何现有游戏。
        _tcpHost = new TcpTranslationHost(resolved);
        _tcpHost.Diagnostics += message => Log(message);
        _tcpHost.Start();
        Log($"TCP 备用通道：已监听 127.0.0.1:{_tcpHost.Port}");

        _fileHost = new FileDropTranslationHost(resolved);
        _fileHost.Diagnostics += message => Log(message);
    }

    /// <summary>
    /// 把 TCP 备用通道的端口写进游戏目录，供插件在命名管道写不进去时回退使用。
    /// 只写一个端口号；失败不影响主管道。
    /// </summary>
    private void WriteTransportPortFile(GameProfile game)
    {
        try
        {
            if (_tcpHost is null || _tcpHost.Port <= 0) return;
            var path = Path.Combine(game.RootPath, ".unity-translator-port");
            File.WriteAllText(path, _tcpHost.Port.ToString());
            Log($"TCP 备用通道：端口已写入 {path}（127.0.0.1:{_tcpHost.Port}）");
            _fileHost?.Start(game.RootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"TCP 备用通道：端口文件写入失败（不影响管道通道）：{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>连接游戏内存。失败时给出“能看懂 + 有下一步”的提示，并写进 app.log。</summary>
    private bool AttachMemory(Process process)
    {
        try
        {
            _processMemory = new Win32ProcessMemory(process);
            _cheatSession = new CheatSession(_processMemory);
            StartAddressTable();
            Log($"内存连接：成功，PID={process.Id}，可读写进程内存");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException or
                                       InvalidOperationException)
        {
            _processMemory = null;
            _cheatSession = null;
            var explained = UiText.Explain(ex, "连接游戏内存");
            Log($"内存连接：失败，PID={process.Id}，{ex.GetType().Name}：{ex.Message}");
            SetCheatWarning(explained.Message + " " + UiText.MemoryUnavailableAdvice, explained.Technical);
            return false;
        }
    }

    /// <summary>把进程的启动时间也记进日志：判断“是不是同一个进程”时它比 PID 更可靠。</summary>
    private static string DescribeProcess(Process? process)
    {
        if (process is null) return "进程未知";
        try { return "启动于 " + process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "启动时间读不到（" + ex.GetType().Name + "）";
        }
    }

    /// <summary>
    /// 游戏进程退出。
    ///
    /// 这里**不再立刻清空会话**：实测中玩家看到的是“软件显示未启动、游戏却还在跑”，
    /// 最可能的原因就是 Unity 游戏改设置/切章节时重启了自己 ——
    /// 老进程退出，新进程起来，而软件只看到“退出”，于是把游戏选择和结果全清了
    /// （截图里侧栏“还没有选择游戏”、连接区“未启动游戏”、首次查找按钮变灰，
    /// 都指向 _activeGame 和 _cheatSession 同时为 null）。
    ///
    /// 所以现在先给它一段时间等新进程出现：找到就自动接管并保留游戏选择与结果；
    /// 确实没有新进程了才清空。
    /// </summary>
    private void OnGameProcessExited()
    {
        if (_closing) return;
        if (_gameProcess is { HasExited: false }) return;
        var exitedId = CheatProcessId;
        // 进程已经没了：即使会话对象还在，也不能再修改或锁定。
        _processLost = true;
        InvalidateOcrRequests();
        _externalOcrController?.Invalidate(ExternalOcr.ExternalOcrStopReason.ProcessLost);
        ResetBuiltInTools();
        _cheatSession?.StopFreeze();
        // 内存地址随进程一起失效：立刻清掉结果与选中项。
        // 留着它们只会让界面自相矛盾（明明进程都没了，还显示"目前还剩 N 项"），
        // 也会让空状态判定把"有旧结果"误当成"不需要提示"。
        // 注意：这里**不动 `_cheatSession`** —— 它代表"游戏还选着"，等接管还要用它；
        // 真正拦住写入的是 `_processLost`（见 CanWriteCheatValue / CanScan 与各入口的判断）。
        if (_processMemory is IDisposable disposableMemory) disposableMemory.Dispose();
        _processMemory = null;
        _frozenHit = null;
        _selectedHit = null;
        _cheatHits.Clear();
        _unityFields.Clear();
        _rankedFields.Clear();
        _fieldTargets.Clear();
        UnityFieldsAvailable = false;
        Log($"游戏进程退出：PID={exitedId}，标记为“进程已退出”，清空失效的内存结果，开始等待同款游戏的新进程"
            + $"（最长 {TakeoverWindow.TotalSeconds:0.#} 秒）");
        if (_activeGame is null)
        {
            FinishGameSession("游戏已退出，可以再次启动。", "游戏已退出，查找结果和锁定都已清空。");
            return;
        }
        RefreshWorkspaceState();
        BeginTakeoverWait(exitedId);
    }

    /// <summary>等待期结束仍没等到新进程：按“游戏已退出”收尾。</summary>
    private void FinishGameSession(string runStatus, string cheatStatus)
    {
        StopTakeoverWait();
        EndGameSession();
        SetRunStatus(runStatus, RunState.Idle);
        SetCheatStatus(cheatStatus + UiText.MemoryAddressCaveat);
        RefreshWorkspaceState();
    }

    // ================= 游戏重启后的接管 =================

    /// <summary>
    /// 游戏重启自己时自动接管新进程，并**保留游戏选择**。
    /// 只有确实找不到该游戏的新进程时才清空会话。
    /// </summary>
    internal bool TryTakeOverRunningGame()
    {
        var game = _activeGame;
        if (game is null)
        {
            Log("接管：没有选中的游戏，放弃接管");
            return false;
        }
        var candidates = RunningProcesses(game);
        Log($"接管：查找 {game.DisplayName} 的进程，找到 {candidates.Count} 个候选"
            + (candidates.Count > 0 ? "（PID " + string.Join(",", candidates.Select(item => item.ProcessId)) + "）" : string.Empty)
            + $"，上一个已连接的 PID={_lastAttachedProcessId}");
        GameProcessInfo? target = null;
        foreach (var candidate in candidates)
        {
            if (candidate.ProcessId == _lastAttachedProcessId) continue;
            target = candidate;
            break;
        }
        if (target is null)
        {
            if (candidates.Count > 0) Log("接管：找到的进程就是刚才那个，不当作新进程");
            return false;
        }
        SwitchToProcess(game, target);
        return true;
    }

    /// <summary>
    /// 把会话切到另一个进程上：保留游戏选择与快捷项，清空地址类结果。
    ///
    /// 内存地址在新进程里必然无效，所以结果与锁定一定清空；
    /// 但“玩家选的是哪款游戏”没有变，必须保留 —— 这正是本次要修的地方。
    /// </summary>
    private void SwitchToProcess(GameProfile game, GameProcessInfo target)
    {
        ResetBuiltInTools();
        _scanStop?.Cancel();
        _scanStop = null;
        try { _cheatSession?.Stop(); } catch (InvalidOperationException) { }
        _cheatSession?.Dispose();
        // IProcessMemory 接口本身不带 Dispose：真实实现（Win32ProcessMemory）要释放进程句柄，
        // 测试替身没有句柄可放，所以按可释放来判断。
        if (_processMemory is IDisposable disposableMemory) disposableMemory.Dispose();
        _processMemory = null;
        _cheatSession = null;
        _frozenHit = null;
        _selectedHit = null;
        _unityFields.Clear();
        _rankedFields.Clear();
        _cheatHits.Clear();
        _fieldTargets.Clear();
        UnityFieldsAvailable = false;
        _firstScanAttempted = false;
        _hasRefined = false;
        _lastRefineCount = 0;
        UiThreadSafe(() => { ScanFreezeButton.Content = "锁定这个数值"; });

        var process = target.Process;
        _gameProcess?.Dispose();
        _gameProcess = process;
        CheatProcessId = target.ProcessId;
        _lastAttachedProcessId = target.ProcessId;
        _cheatStatusExplicit = false;
        // 已经接到新进程上了：必须清掉“进程已退出”，否则查找、修改、锁定会一直是禁用的。
        _processLost = false;
        // Process 只有“本进程启动的”或“枚举系统进程拿到的”才有；测试里的候选没有句柄。
        PublishOcrSessionCapability(game);
        if (process is not null)
        {
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Dispatcher.BeginInvoke(OnGameProcessExited);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log("接管：无法监听新进程的退出事件：" + ex.GetType().Name + "：" + ex.Message);
            }
        }
        if (_host is not null)
        {
            _host.CheatProcessId = CheatProcessId;
            _host.CheatSessionId = CheatSessionId;
        }
        // 真机走 AttachMemory；测试用注入的内存实现，避免去开一个不存在的进程。
        var injected = ProcessMemoryForTest?.Invoke(target);
        if (injected is not null)
        {
            _processMemory = injected;
            _cheatSession = new CheatSession(injected);
            StartAddressTable();
            Log($"接管：已用测试内存实现连上新进程 PID={CheatProcessId}");
        }
        else if (process is not null)
        {
            AttachMemory(process);
        }
        Log($"接管：已连接到 {game.DisplayName} 的新进程 PID={CheatProcessId}（{DescribeProcess(process)}）；"
            + "游戏选择与快捷项已保留，内存地址类结果已清空");
        SetRunStatus($"游戏已重新启动，已自动重新连接到新进程（进程 {CheatProcessId}）。"
            + "之前查到的内存地址已经失效，需要重新查找；游戏选择保持不变。", RunState.Warning);
        SetCheatStatus($"游戏重新启动过，已自动连上新进程（进程 {CheatProcessId}）。"
            + "旧的内存地址全部作废，请重新做“首次查找”。" + UiText.MemoryAddressCaveat);
        if (_activeTranslationCoordinator is { } coordinator) BindTranslationOcrSession(game, coordinator);
        LoadPresetsForActiveGame(game);
        RefreshWorkspaceState();
    }

    private void BeginTakeoverWait(int exitedId)
    {
        StopTakeoverWait();
        _takeoverAttempts = 0;
        SetRunStatus("游戏进程结束了，正在确认是不是它重启了自己…", RunState.Warning);
        SetCheatStatus("刚才那个游戏进程已经退出。"
            + "如果游戏是重启自己（改设置、切章节时很常见），本工具会自动接到新进程上，"
            + "并保留你选的游戏；确实退出了才会清空结果。");
        RefreshWorkspaceState();
        if (TryTakeOverRunningGame())
        {
            Log($"接管：进程 {exitedId} 退出后立刻找到了新进程，已接管");
            return;
        }
        _takeoverTimer = new DispatcherTimer { Interval = TakeoverInterval };
        _takeoverTimer.Tick += OnTakeoverTick;
        _takeoverTimer.Start();
        Log($"接管：进程 {exitedId} 退出，开始轮询等待新进程");
    }

    private void OnTakeoverTick(object? sender, EventArgs e)
    {
        _takeoverAttempts++;
        if (TryTakeOverRunningGame())
        {
            Log($"接管：第 {_takeoverAttempts} 次轮询找到新进程并接管成功");
            StopTakeoverWait();
            return;
        }
        if (_takeoverAttempts * TakeoverInterval < TakeoverWindow) return;
        Log($"接管：等了 {TakeoverWindow.TotalSeconds:0.#} 秒没有出现新进程，按“游戏已退出”收尾");
        FinishGameSession("游戏已退出，可以再次启动。", "游戏已退出，查找结果和锁定都已清空。");
    }

    private void StopTakeoverWait()
    {
        _takeoverTimer?.Stop();
        if (_takeoverTimer is not null) _takeoverTimer.Tick -= OnTakeoverTick;
        _takeoverTimer = null;
        _takeoverAttempts = 0;
    }

    /// <summary>窗口关闭时调用：收尾等待定时器，避免关窗后还在轮询。</summary>
    internal void StopTakeOverWaitOnClose() => StopTakeoverWait();

    /// <summary>是不是这段“等新进程”的窗口期：界面提示用它区分“已退出”和“正在重连”。</summary>
    internal bool IsWaitingForTakeOver => _takeoverTimer is not null;

    /// <summary>当前有没有该游戏的进程在运行（生产实现：按同目录同名 exe 匹配）。</summary>
    private IReadOnlyList<GameProcessInfo> RunningProcesses(GameProfile game) =>
        RunningProcessesForTest is not null ? RunningProcessesForTest(game) : FindRunningProcesses(game);

    private static IReadOnlyList<GameProcessInfo> FindRunningProcesses(GameProfile game)
    {
        var running = new List<GameProcessInfo>();
        var expectedDirectory = Normalize(game.RootPath);
        var expectedExecutable = Path.GetFileName(game.ExePath);
        Process[] candidates;
        try { candidates = Process.GetProcesses(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return running;
        }
        foreach (var candidate in candidates)
        {
            try
            {
                if (string.IsNullOrEmpty(candidate.MainModule?.FileName)) continue;
                var mainModulePath = candidate.MainModule!.FileName!;
                if (!string.Equals(Path.GetFileName(mainModulePath), expectedExecutable,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Normalize(Path.GetDirectoryName(mainModulePath) ?? string.Empty),
                        expectedDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                running.Add(new GameProcessInfo(candidate.Id, mainModulePath, candidate));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                           NotSupportedException)
            {
                // 有些系统进程读不到 MainModule；它们不可能是我们要找的游戏，跳过即可。
            }
        }
        return running;
    }

    private static string Normalize(string path) => path.TrimEnd('\\', '/');

    /// <summary>让界面更新跑在 Dispatcher 线程上；已经在 Dispatcher 线程时直接执行。</summary>
    private void UiThreadSafe(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void EndGameSession()
    {
        _activeTranslationCoordinator = null;
        if (!_externalOcrDrained) _externalOcrController?.Invalidate(ExternalOcr.ExternalOcrStopReason.SessionEnded);
        System.Threading.Volatile.Write(ref _ocrSessionCapability, null);
        // OnLaunch prepares the next service before StartGameSession calls this.
        // Invalidate replies without clearing that newly prepared service.
        InvalidateOcrRequests();
        ResetBuiltInTools();
        // 注意：这里不能停“等待接管”的定时器。
        // 等新进程期间会话本来就是断的，而玩家点“启动游戏/连接”时也会先走 EndGameSession；
        // 一停就会把正在进行的等待悄悄取消掉。
        // 等待期由 BeginTakeoverWait / FinishGameSession / StopTakeoverWait 自己收尾。
        _scanStop?.Cancel();
        _scanStop = null;
        try { _cheatSession?.Stop(); } catch (InvalidOperationException) { }
        _cheatSession?.Dispose();
        _cheatSession = null;
        if (_processMemory is IDisposable disposableMemory) disposableMemory.Dispose();
        _processMemory = null;
        _frozenHit = null;
        _selectedHit = null;
        UnityFieldsAvailable = false;
        _firstScanAttempted = false;
        _hasRefined = false;
        _lastRefineCount = 0;
        _fieldTargets.Clear();
        _unityFields.Clear();
        _rankedFields.Clear();
        _cheatHits.Clear();
        CheatSessionId = null;
        CheatProcessId = 0;
        _lastAttachedProcessId = 0;
        _activeGame = null;
        _processLost = false;
        if (_host is not null)
        {
            _host.CheatResponder = null;
            _host.CheatSessionId = null;
            _host.CheatProcessId = 0;
        }
    }

    /// <summary>
    /// 测试钩子：模拟游戏进程退出。
    /// 刻意与真实退出走同一段收尾逻辑，避免测试验证的路径和实际路径不一致。
    /// 注意：现在退出的第一步是“等新进程”，所以这个钩子之后 _activeGame 仍在，
    /// 要用 <see cref="TakeOverImmediatelyForTest"/> 或
    /// <see cref="FinishTakeOverWaitForTest"/> 明确走完下一步。
    /// </summary>
    internal void NotifyGameExitedForTest()
    {
        _gameProcess = null;
        OnGameProcessExited();
    }

    /// <summary>测试钩子：立刻判定能不能接管新进程（不真的等定时器）。</summary>
    internal bool TakeOverImmediatelyForTest() => TryTakeOverRunningGame();

    internal bool HasTakeOverWaitForTest() => IsWaitingForTakeOver;

    /// <summary>测试诊断用：当前会被认为可接管的候选进程号。</summary>
    internal IReadOnlyList<int> RunningProcessesDebugForTest() =>
        _activeGame is null
            ? Array.Empty<int>()
            : RunningProcesses(_activeGame).Select(item => item.ProcessId).ToArray();

    internal int LastAttachedProcessIdForTest() => _lastAttachedProcessId;

    /// <summary>测试钩子：等待期结束仍没等到新进程时的收尾（走真实方法）。</summary>
    internal void FinishTakeOverWaitForTest() =>
        FinishGameSession("游戏已退出，可以再次启动。", "游戏已退出，查找结果和锁定都已清空。");

    /// <summary>测试钩子：直接连上一个已在运行的进程（走真实入口）。</summary>
    internal void ConnectSpecificProcessForTest(GameProfile game, int processId, IProcessMemory memory) =>
        AttachToRunning(game, new GameProcessInfo(processId, game.ExePath, null), memory);

    /// <summary>测试钩子：标记窗口正在关闭，确认关闭时不会再尝试接管。</summary>
    internal void MarkClosingForTest() => _closing = true;

    private void AttachOcrHandlers(NamedPipeHost host)
    {
        host.OcrFrameReceived = async (frame, cancellationToken) =>
        {
            // The game owns this capture: ValidateOverlay reads it again before
            // accepting the reply. It releases the file after validation, or on
            // cancellation, scene changes, timeout and disconnect. Do not delete
            // it here, including on an empty/error reply, before that validation.
            var service = _ocrService;
            var session = CheatSessionId;
            var revision = System.Threading.Volatile.Read(ref _ocrStateRevision);
            if (service is null || !CanProcessSessionOcr(host, service, session, revision))
                return Array.Empty<OcrOverlayItem>();
            var boxes = await service.ProcessAsync(frame, cancellationToken);
            if (!CanProcessSessionOcr(host, service, session, revision))
                return Array.Empty<OcrOverlayItem>();
            if (boxes.Count > 0)
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (CanProcessSessionOcr(host, service, session, revision))
                        SetRunStatus($"已准备 {boxes.Count} 处图片译文，等待游戏校验。", RunState.Ok);
                });
            return boxes;
        };
        host.Translated += (request, reply) => Dispatcher.BeginInvoke(() =>
        {
            _lastRequest = request;
            if (reply is not null) CorrectionBox.Text = reply.Translated;
            SetRunStatus(reply is null
                ? "这次没有拿到译文，游戏继续显示原文。"
                : $"翻译成功：{Shorten(request.Source)} → {reply.Translated}", RunState.Ok);
        });
    }

    private static string Shorten(string text) => text.Length <= 18 ? text : text[..18] + "…";

    // ================= 连接到正在运行的游戏 =================

    /// <summary>
    /// 连接到“已经在运行”的游戏：给从软件外启动游戏的情况一条明确出路，
    /// 而不是只留一张空表和“未启动游戏”。
    /// </summary>
    private void OnConnectToRunningGame(object sender, RoutedEventArgs e)
    {
        if (HasGameSession)
        {
            SetCheatStatus($"已经连接着游戏（进程 {CheatProcessId}），不需要再连一次。");
            return;
        }
        var found = new List<(GameProfile Game, GameProcessInfo Process)>();
        foreach (var game in Games)
            foreach (var process in RunningProcesses(game))
                found.Add((game, process));
        Log($"连接已运行的游戏：扫描到 {found.Count} 个候选进程（已登记游戏 {Games.Count} 款）");

        if (found.Count == 0)
        {
            SetCheatStatus("没有找到正在运行的游戏。请确认游戏已经启动，"
                + "并且它和这里列出的游戏是同一个 exe；也可以在“游戏与翻译”页点“启动游戏”。");
            return;
        }

        // 只能挂一个进程：多个候选时让玩家自己选，避免连错游戏改错数值。
        var labels = found
            .Select(item => $"{item.Game.DisplayName}（进程 {item.Process.ProcessId}）")
            .ToArray();
        var chosen = AskWhichProcess(labels);
        if (chosen is null || chosen < 0 || chosen >= found.Count)
        {
            Log($"连接已运行的游戏：玩家取消（候选 {string.Join(" / ", labels)}）");
            SetCheatStatus("已取消连接。需要时再点“连接到正在运行的游戏”。");
            return;
        }
        var target = found[chosen.Value];
        AttachToRunning(target.Game, target.Process, null);
    }

    /// <summary>
    /// 有多个候选进程时问玩家连哪一个；只有一个就直接连。
    /// 返回下标，null 表示取消。
    /// </summary>
    internal int? AskWhichProcess(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0) return null;
        if (labels.Count == 1) return 0;
        var answer = AskYesNoForTest is not null
            ? AskYesNoForTest(labels[0])
            : MessageBox.Show(this,
                $"发现多个正在运行的游戏进程：\n\n{string.Join("\n", labels)}\n\n"
                + $"要连接到第一个（{labels[0]}）吗？\n"
                + "选“否”可以取消，然后先关掉不需要的那个游戏再试。",
                "要连接哪一个游戏？", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes ? 0 : null;
    }

    /// <summary>测试替身：代替“要连接哪一个游戏”的确认框。</summary>
    internal Func<string, MessageBoxResult>? AskYesNoForTest { get; set; }

    /// <summary>
    /// 连上一个已经在运行的进程。与“启动游戏”共用同一套会话状态更新，
    /// 区别只是进程不是这里启动的，所以退出事件监听要单独挂。
    /// </summary>
    private void AttachToRunning(GameProfile game, GameProcessInfo target, IProcessMemory? memory)
    {
        EndGameSession();
        // A newly attached process must not inherit another game's translator.
        _ocrService = null;
        EnsureHost();
        CheatSessionId = Guid.NewGuid().ToString("N");
        _host!.CheatSessionId = CheatSessionId;
        CheatProcessId = target.ProcessId;
        _host.CheatProcessId = CheatProcessId;
        _lastAttachedProcessId = target.ProcessId;
        _activeGame = game;
        PublishOcrSessionCapability(game);
        _hadGameSession = true;
        _processLost = false;
        var process = target.Process;
        if (process is not null)
        {
            _gameProcess?.Dispose();
            _gameProcess = process;
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Dispatcher.BeginInvoke(OnGameProcessExited);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log("连接已运行的游戏：无法监听退出事件：" + ex.GetType().Name + "：" + ex.Message);
            }
        }
        Log($"连接已运行的游戏：{game.DisplayName}（exe={game.ExePath}），PID={target.ProcessId}，会话 {CheatSessionId}");
        // 测试注入的内存实现优先（测试里没有真实游戏进程）。
        var injectedMemory = memory ?? ProcessMemoryForTest?.Invoke(target);
        var memoryReady = injectedMemory is not null;
        if (injectedMemory is not null)
        {
            _processMemory = injectedMemory;
            _cheatSession = new CheatSession(injectedMemory);
            StartAddressTable();
            Log($"内存连接：成功（注入的内存实现），PID={target.ProcessId}");
        }
        else if (process is not null)
        {
            memoryReady = AttachMemory(process);
        }
        else
        {
            Log($"内存连接：拿不到进程句柄，PID={target.ProcessId}，本次只建立了会话");
        }
        if (!memoryReady)
        {
            SetRunStatus($"已经连上「{game.DisplayName}」（进程 {target.ProcessId}），但读不到它的内存。",
                RunState.Warning, UiText.MemoryUnavailableAdvice);
            LoadPresetsForActiveGame(game);
            RefreshWorkspaceState();
            return;
        }
        SetRunStatus($"已经连上正在运行的「{game.DisplayName}」（进程 {target.ProcessId}）。"
            + "现在可以直接查找数值了。", RunState.Ok,
            "如果游戏是自己启动的，本工具不会去重启或关闭它。");
        LoadPresetsForActiveGame(game);
        RefreshWorkspaceState();
    }

    /// <summary>刷新所有与“有没有游戏会话”相关的控件状态与提示。</summary>
    private void RefreshWorkspaceState()
    {
        var connected = HasGameSession;
        ScanStartButton.IsEnabled = CanScan && _scanStop is null;
        ScanRefineButton.IsEnabled = CanScan && _scanStop is null && (_scanWorkspace.HasScan || _cheatHits.Count > 0);
        // 没有候选时“再次查找”无意义，先禁用避免玩家以为程序没反应。
        ScanCancelButton.IsEnabled = _scanStop is not null;
        ScanWriteButton.IsEnabled = CanWriteCheatValue;
        ScanFreezeButton.IsEnabled = CanFreezeCheatValue;
        ScanUndoButton.IsEnabled = connected;
        UnityQueryButton.IsEnabled = false;
        UnityWriteButton.IsEnabled = false;

        UpdateEmptyStates(connected);
        UpdateConnectionIndicator(connected);
        UpdateGameCardHint();
        // 只有没有明确状态时才用通用提示，避免把“游戏已退出”这类关键信息覆盖掉。
        if (!_cheatStatusExplicit)
            SetCheatStatus(connected
                ? $"已连上游戏（进程 {CheatProcessId}）。按页面上的步骤操作即可。"
                : "还没有启动游戏。先到“游戏与翻译”页启动，再回来查找数值。");

        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanWriteCheatValue));
        OnPropertyChanged(nameof(CanFreezeCheatValue));
        OnPropertyChanged(nameof(HasCheatHits));
        OnPropertyChanged(nameof(IsFreezing));
    }

    private void UpdateGameCardHint()
    {
        GameListEmptyHint.Visibility = Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLibraryPresentation();
    }

    /// <summary>结果表为空时该怎么解释。四种情况必须分开，不能都写“请先做首次查找”。</summary>
    internal enum ScanResultEmptyKind
    {
        /// <summary>还没查找过（包括还没启动游戏）。</summary>
        NotStarted,

        /// <summary>查找/筛选做过了，但当前一项都没有。</summary>
        NoResults,

        /// <summary>这次会话里游戏退出过，会话已经断开。</summary>
        SessionEnded
    }

    /// <summary>
    /// 结果表为空时的文案。
    ///
    /// 修的是这个实测问题：再次查找筛出 0 项之后，玩家看到的仍然是
    /// “还没有结果时，请先做首次查找”——和实际发生的事不符，于是以为程序坏了。
    /// 现在三种状态文案分开，“筛出 0 项”会说明可能的原因和下一步。
    /// </summary>
    internal const string ScanHintNotStarted = "结果会显示在这里。还没有结果时，请先做“首次查找”。";
    internal const string ScanHintNoResults =
        "现在没有匹配的结果。可能这个数值不是我们要找的那个，或者游戏已经把它重新计算过。"
        + "回到第 1 步换一个更好认的数值重新查找即可。";
    internal const string ScanHintSessionEnded =
        "游戏会话已经结束，结果和锁定都已清空。先启动游戏（或点上面的“连接到正在运行的游戏”），再重新查找。";

    /// <summary>
    /// 当前结果为空属于哪一种情况。
    ///
    /// 注意顺序，以及"有结果时"的处理：
    /// - 有结果的时候这个枚举没有意义（结果区显示的是「目前还剩 N 项」），返回 NotStarted 表示
    ///   "不需要用空状态文案"。真机验证时就是靠这一点发现过一次自相矛盾的输出：
    ///   明明「目前还剩 252 项」，空状态却报 NoResults。
    /// - 「做过再次查找却一项都没有」优先于「第一次也做过」，
    ///   因为前者才需要说明"没有匹配结果"而不是"还没查找"。
    /// </summary>
    internal ScanResultEmptyKind EmptyResultKindForTest()
    {
        if (_cheatHits.Count > 0) return ScanResultEmptyKind.NotStarted;
        if (_hasRefined && _lastRefineCount == 0) return ScanResultEmptyKind.NoResults;
        if (_firstScanAttempted) return ScanResultEmptyKind.NoResults;
        // 会话已经断开（这次会话里游戏退出过）。
        if (_processLost || (!HasGameSession && _hadGameSession)) return ScanResultEmptyKind.SessionEnded;
        return ScanResultEmptyKind.NotStarted;
    }

    internal static string ScanHintFor(ScanResultEmptyKind kind) => kind switch
    {
        ScanResultEmptyKind.NoResults => ScanHintNoResults,
        ScanResultEmptyKind.SessionEnded => ScanHintSessionEnded,
        _ => ScanHintNotStarted
    };

    /// <summary>结果表下方那两行提示当前显示的文字（第一行是主提示，第二行是下一步）。</summary>
    internal string ScanResultHintForTest() => ScanResultHint.Text;

    internal string ScanResultEmptyHintForTest() => ScanResultEmptyHint.Text;

    /// <summary>未选游戏 / 未启动 / 没有结果，都要有明确的下一步。</summary>
    private void UpdateEmptyStates(bool connected)
    {
        // “进程退出了但还在等它重启”时，会话对象还在，这时要按“已断开”提示，
        // 而不是让玩家以为一切正常。
        var linked = connected && !_processLost;
        ScanEmptyState.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
        ScanEmptyTitle.Text = _activeGame is null
            ? "还没有启动游戏"
            : IsWaitingForTakeOver ? "游戏正在重新启动，正在等它" : "游戏会话已结束";
        ScanEmptyAdvice.Text = _activeGame is null
            ? "先到“游戏与翻译”页启动游戏，再回来这里查找数值。数值查找必须有正在运行的游戏。"
            : "游戏进程已经退出。如果是它自己重启（改设置、切章节时常见），本工具会自动接到新进程上；"
              + "长时间没接上就用下面的按钮重新连接。";
        ScanConnectRunningButton.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;

        UnityEmptyState.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
        UnityEmptyTitle.Text = _activeGame is null ? "还没有启动游戏" : "游戏会话已结束";
        UnityEmptyAdvice.Text = "先到“游戏与翻译”页安装并启动游戏，装好组件才能读到游戏里的数值。";

        var emptyKind = EmptyResultKindForTest();
        var hasHits = _cheatHits.Count > 0;
        ScanResultEmptyHint.Visibility = hasHits ? Visibility.Collapsed : Visibility.Visible;
        ScanResultHint.Visibility = hasHits ? Visibility.Collapsed : Visibility.Visible;
        ScanResultEmptyHint.Text = ScanHintFor(emptyKind);
        // 第二行只在不重复第一行结论时出现，避免同类提示连着说两遍。
        ScanResultHint.Text = emptyKind switch
        {
            ScanResultEmptyKind.NotStarted => "第 1 步填游戏里现在显示的数值，“首次查找”会把这个数值在内存里出现的位置全部列出来。",
            ScanResultEmptyKind.NoResults => "也可以点“取消查找”后换个数值重来；已经改过的数值可以用“撤销上次修改”还原。",
            _ => "结果不会保留：游戏退出或重启后，内存地址就失效了。"
        };
        ScanResultSummary.Text = hasHits ? $"目前还剩 {Math.Max(_totalScanHits,_cheatHits.Count)} 项" +
            (_totalScanHits > _cheatHits.Count ? $"，显示前 {_cheatHits.Count} 项；全部候选参与再次查找" : "") : string.Empty;

        UnityFieldEmptyHint.Visibility = _rankedFields.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetEmptyHint.Visibility = _cheatPresets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanGoToGameButton.IsEnabled = !connected;
    }

    private void UpdateConnectionIndicator(bool connected)
    {
        ConnectionDot.SetResourceReference(Border.BackgroundProperty, connected ? "Success" : "TextMuted");
        ConnectionText.Text = connected ? "已连接" : "未启动游戏";
        ConnectionDetail.Text = connected
            ? $"游戏 {_activeGame?.DisplayName}（进程 {CheatProcessId}）"
            : "在“游戏与翻译”页启动";

        SidebarGameName.Text = _activeGame?.DisplayName ?? "还没有选择游戏";
        SidebarGameDot.SetResourceReference(Border.BackgroundProperty, connected ? "Success" : "TextMuted");
        SidebarGameStateText.Text = connected ? "运行中" : _activeGame is null ? "未选择" : "未启动";
        SidebarGameStateText.SetResourceReference(TextBlock.ForegroundProperty, connected ? "Success" : "TextSecondary");
        TopGameText.Text = _activeGame?.DisplayName ?? "未选择游戏";

        StatusDot.SetResourceReference(Border.BackgroundProperty, connected ? "Success" : "TextMuted");
        CheatStatusDot.SetResourceReference(Border.BackgroundProperty, connected ? "Success" : "TextMuted");
    }

    // ================= 状态显示 =================

    private enum RunState { Idle, Ok, Warning, Error }

    /// <summary>顶部“运行状态”：先说发生了什么，再给建议，最后才是原始错误。</summary>
    private void SetRunStatus(string message, RunState state, string? advice = null, string? technical = null)
    {
        ServiceStatus.Text = message;
        StatusDot.SetResourceReference(Border.BackgroundProperty, state switch
        {
            RunState.Ok => "Success",
            RunState.Warning => "Warning",
            RunState.Error => "Danger",
            _ => "TextMuted"
        });
        ShowOptional(StatusSuggestion, advice);
        ShowOptional(StatusTechnical, technical, asTechnical: true);
        TopNextStep.Text = advice ?? string.Empty;
    }

    private void SetCheatStatus(string message)
    {
        _cheatStatusExplicit = true;
        CheatStatus.Text = message;
        ShowOptional(CheatStatusAdvice, null);
        ShowOptional(CheatStatusTechnical, null, asTechnical: true);
    }

    private void SetCheatWarning(string message, string? technical = null)
    {
        _cheatStatusExplicit = true;
        CheatStatus.Text = message;
        ShowOptional(CheatStatusAdvice, null);
        ShowOptional(CheatStatusTechnical, technical, asTechnical: true);
        CheatStatusDot.SetResourceReference(Border.BackgroundProperty, "Warning");
    }

    private void SetCheatError(string message, string advice, string? technical = null)
    {
        _cheatStatusExplicit = true;
        CheatStatus.Text = message;
        ShowOptional(CheatStatusAdvice, advice);
        ShowOptional(CheatStatusTechnical, technical, asTechnical: true);
        CheatStatusDot.SetResourceReference(Border.BackgroundProperty, "Danger");
    }

    private void SetUnityStatus(string message, string? advice = null, string? technical = null)
    {
        UnityStatus.Text = message;
        ShowOptional(UnityStatusAdvice, advice);
        ShowOptional(UnityStatusTechnical, technical, asTechnical: true);
    }

    private static void ShowOptional(TextBlock target, string? text, bool asTechnical = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            target.Visibility = Visibility.Collapsed;
            target.Text = string.Empty;
            return;
        }
        target.Text = asTechnical ? "技术详情：" + text : text;
        target.Visibility = Visibility.Visible;
    }


    // ================= 危险操作确认 =================

    private Action? _pendingConfirm;

    /// <summary>卸载等危险操作先弹确认，默认焦点在“取消”上。</summary>
    private void AskConfirm(string title, string message, string detail, Action onConfirm,
        string acceptText = "确认")
    {
        if (MessageBoxHandler is not null)
        {
            // None 让界面测试检查实际的确认浮层；其它结果模拟用户选择。
            var result = MessageBoxHandler(message);
            if (result != MessageBoxResult.None)
            {
                if (result is MessageBoxResult.OK or MessageBoxResult.Yes) onConfirm();
                return;
            }
        }
        _pendingConfirm = onConfirm;
        ConfirmTitle.Text = title;
        ConfirmMessage.Text = message;
        ShowOptional(ConfirmDetail, detail, asTechnical: true);
        ConfirmOkButton.Content = acceptText;
        ConfirmOverlay.Visibility = Visibility.Visible;
        ConfirmCancelButton.Focus();
    }

    private void OnConfirmCancel(object sender, RoutedEventArgs e)
    {
        _pendingConfirm = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmAccept(object sender, RoutedEventArgs e)
    {
        var action = _pendingConfirm;
        _pendingConfirm = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        action?.Invoke();
    }

    // ================= 数值查找 =================

    private async void OnFirstScan(object sender, RoutedEventArgs e) => await RunFirstScanAsync();

    /// <summary>
    /// 首次查找的核心逻辑。做成返回 Task 的方法而不是 async void：
    /// 事件处理器无法被等待，而测试需要确定地等它结束。
    /// </summary>
    private async Task RunFirstScanAsync()
    {
        if (_processLost)
        {
            SetCheatError("游戏进程已经退出了，不能开始查找。",
                "等新进程接上（或点“连接到正在运行的游戏”）之后再查找；旧的内存地址已经失效。");
            return;
        }
        if (_cheatSession is null || _processMemory is null)
        {
            SetCheatError("还没有连上游戏，不能开始查找。", "先到“游戏与翻译”页启动游戏。");
            return;
        }
        var kind = UiText.KindAt(ScanKindBox.SelectedIndex);
        ScanTypeHint.Text = UiText.KindHint(kind);
        var mode = (InitialScanMode)Math.Max(0,ScanInitialModeBox.SelectedIndex);
        if (mode != InitialScanMode.Unknown && !NumericValue.TryParse(ScanValueBox.Text, kind, out _))
        {
            SetCheatError($"“{ScanValueBox.Text}”不是有效的{UiText.KindName(kind)}。",
                kind is NumericKind.Float32 or NumericKind.Float64
                    ? "小数请用小数点，例如 12.5。不要填汉字或空格。"
                    : "整数只能填数字，例如 1280，不要带小数点或汉字。",
                UiText.KindTechnical(kind));
            return;
        }
        _cheatHits.Clear();
        _selectedHit = null;
        _firstScanAttempted = true;
        _totalScanHits = 0;
        var memory = _processMemory;
        var maxHits = ReadMaxHits();
        Log($"首次查找：开始，类型={kind}，输入值=「{ScanValueBox.Text}」，列表显示上限={maxHits}（完整候选保留），进程 {CheatProcessId}");
        var progress = new Progress<ScanProgress>(report =>
        {
            if (!ReferenceEquals(memory,_processMemory) || _processLost || _scanStop is null) return;
            var done = report.BytesTotal == 0 ? 0 : (int)(report.BytesRead * 100 / report.BytesTotal);
            ScanProgressText.Text = $"正在查找… 已查看 {done}%，暂时找到 {report.Hits} 项";
            ScanProgressBar.Visibility = Visibility.Visible;
            ScanProgressBar.Value = done;
        });
        var stop = new CancellationTokenSource();
        _scanStop = stop;
        SetCheatStatus("正在查找，请稍等。这一步可能要几秒到几十秒，可以随时点“取消查找”。");
        RefreshWorkspaceState();
        try
        {
            var startAddress = ParseScanAddress(ScanStartAddressBox.Text,0);
            var stopAddress = ParseScanAddress(ScanStopAddressBox.Text,0x7FFFFFFFFFFF);
            _scanWorkspace.Clear();
            var result = await _scanWorkspace.FirstAsync(memory,kind,mode,ScanValueBox.Text,ScanUpperValueBox.Text,
                maxHits,progress,stop.Token,startAddress,stopAddress,ScanAlignedBox.IsChecked == true).ConfigureAwait(true);
            if (!ReferenceEquals(memory,_processMemory) || _processLost) return;
            var hits = result.Hits;
            _totalScanHits = result.TotalCount;
            AddHits(hits);
            Log($"首次查找：结束，命中 {hits.Count} 项（类型={kind}，输入值=「{ScanValueBox.Text}」）");
            SetCheatStatus(hits.Count == 0
                ? "没有找到这个数值。可以换一种“数值类型”，或者换一个更好认的数值再试。"
                : $"找到了 {result.TotalCount} 项。现在回游戏让这个数值变化，再回来点“再次查找”。" +
                    (result.TotalCount > hits.Count ? $" 当前显示前 {hits.Count} 项，全部候选仍会参与筛选。" : ""));
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(memory,_processMemory) || _processLost) return;
            _cheatHits.Clear();
            Log("首次查找：已取消");
            SetCheatStatus("查找已取消，结果已清空。可以改好数值后重新查找。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (!ReferenceEquals(memory,_processMemory) || _processLost) return;
            Log($"首次查找：失败，{ex.GetType().Name}：{ex.Message}");
            SetCheatError(ex.Message, "可以缩小扫描地址范围，或改好数值和类型后重新查找。", ex.GetType().Name);
        }
        finally
        {
            stop.Dispose();
            if (ReferenceEquals(_scanStop,stop))
            {
                _scanStop = null;
                ScanProgressBar.Visibility = Visibility.Collapsed;
                ScanProgressText.Text = string.Empty;
            }
            RefreshWorkspaceState();
        }
    }

    private async void OnRefineScan(object sender, RoutedEventArgs e) => await RunRefineScanAsync();

    /// <summary>再次查找的核心逻辑：用玩家刚填的新数值做精确筛选。</summary>
    private async Task RunRefineScanAsync()
    {
        if (_processLost)
        {
            SetCheatError("游戏进程已经退出了，不能继续筛选。",
                "等新进程接上之后重新做“首次查找”；旧的内存地址已经失效。");
            return;
        }
        if (_cheatSession is null || _processMemory is null || _cheatHits.Count == 0)
        {
            SetCheatError("还没有可缩小的结果。", "请先做一次“首次查找”。");
            return;
        }
        // 默认精确值；未知初始值也可以通过相对上次快照的变化来逐步缩小。
        var filter = (ScanFilter)Math.Max(0,ScanFilterBox.SelectedIndex);
        var previousHits = _cheatHits.ToArray();
        var kind = _scanWorkspace.HasScan ? _scanWorkspace.Kind : previousHits[0].Hit.Kind;
        byte[]? exact = null;
        if (filter == ScanFilter.Exact && (!NumericValue.TryParse(ScanNewValueBox.Text, kind, out exact) || exact is null))
        {
            SetCheatError($"“{ScanNewValueBox.Text}”不是有效的{UiText.KindName(kind)}。",
                $"在“游戏现在显示的新数值”里填一个{UiText.KindName(kind)}再点“再次查找”。" +
                (kind is NumericKind.Float32 or NumericKind.Float64 ? "小数请用小数点，例如 12.5。" : "整数只填数字。"),
                UiText.KindTechnical(kind));
            return;
        }
        var previous = previousHits.Select(view => view.Hit).ToArray();
        var memory = _processMemory;
        var stop = new CancellationTokenSource();
        _scanStop = stop;
        // 先记下“这次做过再次查找”，这样即使筛出 0 项，结果区的提示也会说“没有匹配结果”，
        // 而不是退回“请先做首次查找”。
        _hasRefined = true;
        Log($"再次查找：开始，类型={kind}，输入新值=「{ScanNewValueBox.Text}」，筛选前候选 {previous.Length} 项，进程 {CheatProcessId}");
        SetCheatStatus(filter == ScanFilter.Exact ? $"正在筛选等于 {ScanNewValueBox.Text} 的结果…" : "正在按相对上次扫描的变化筛选…");
        RefreshWorkspaceState();
        try
        {
            IReadOnlyList<ScanHit> hits;
            long totalHits;
            if (_scanWorkspace.HasScan)
            {
                var result = await _scanWorkspace.RefineAsync(memory,filter,ScanNewValueBox.Text,ReadMaxHits(),null,stop.Token).ConfigureAwait(true);
                hits = result.Hits; totalHits = result.TotalCount;
            }
            else
            {
                hits = await _scanner.RefineAsync(memory,previous,filter,exact,stop.Token).ConfigureAwait(true);
                totalHits = hits.Count;
            }
            if (!ReferenceEquals(memory,_processMemory) || _processLost) return;
            _totalScanHits = totalHits;
            _cheatHits.Clear();
            _selectedHit = null;
            AddHits(hits);
            _lastRefineCount = hits.Count;
            Log($"再次查找：结束，筛选前 {previous.Length} 项 → 筛选后 {hits.Count} 项（输入新值=「{ScanNewValueBox.Text}」）");
            SetCheatStatus(hits.Count == 0
                ? $"已经没有等于 {ScanNewValueBox.Text} 的结果了。可能这个数值不是我们要找的那个，或者游戏已经把它重新计算。回到第 1 步重新开始即可。"
                : $"目前剩 {_totalScanHits} 项。" + (filter == ScanFilter.Exact ? $"结果等于 {ScanNewValueBox.Text}。" : "已按上次扫描的变化筛选。") +
                    (_totalScanHits == 1 ? "只剩一项，到第 3 步试着修改它。" : "回游戏让数值变化后继续筛选，直到只剩一两项。"));
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(memory,_processMemory) || _processLost) return;
            Log($"再次查找：已取消（筛选前 {previous.Length} 项，结果保持不变）");
            SetCheatStatus("缩小结果已取消，结果保持不变。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (!ReferenceEquals(memory,_processMemory) || _processLost) return;
            Log($"再次查找：失败，{ex.GetType().Name}：{ex.Message}");
            var explained = UiText.Explain(ex, "缩小结果");
            SetCheatError(explained.Message, explained.Advice, explained.Technical);
        }
        finally
        {
            stop.Dispose();
            if (ReferenceEquals(_scanStop,stop)) _scanStop = null;
            RefreshWorkspaceState();
        }
    }

    private void AddHits(IReadOnlyList<ScanHit> hits)
    {
        foreach (var hit in hits)
            _cheatHits.Add(new ScanHitView(hit, NumericValue.Format(hit.LastBytes, hit.Kind),
                "0x" + hit.Address.ToString("X", CultureInfo.InvariantCulture)));
        RefreshWorkspaceState();
    }

    private void OnCancelScan(object sender, RoutedEventArgs e)
    {
        _scanStop?.Cancel();
        SetCheatStatus("已请求取消，正在停止…");
    }

    private void OnScanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var previous = _selectedHit;
        _selectedHit = ScanResultList.SelectedItem as ScanHitView;
        // 只在真的换了选中项时才覆盖“改成”，否则玩家自己填进去的值会被悄悄改掉。
        if (_selectedHit is not null && !ReferenceEquals(previous, _selectedHit))
            ScanWriteValueBox.Text = _selectedHit.CurrentValue;
        RefreshWorkspaceState();
    }

    private void OnWriteScannedValue(object sender, RoutedEventArgs e)
    {
        if (_processLost)
        {
            SetCheatError("游戏进程已经退出了，这次修改没有执行。",
                "等游戏重新启动后，本工具会自动接上新进程（也可以点上面的“连接到正在运行的游戏”）。"
                + "内存地址在重启后会失效，接上之后请重新做“首次查找”。");
            return;
        }
        if (_cheatSession is null || _selectedHit is null)
        {
            SetCheatError("还没有选中要修改的那一项。", "先在上面的结果列表里点一行。");
            return;
        }
        var kind = _selectedHit.Hit.Kind;
        if (!NumericValue.TryParse(ScanWriteValueBox.Text, kind, out var replacement))
        {
            SetCheatError($"“{ScanWriteValueBox.Text}”不是有效的{UiText.KindName(kind)}。",
                "只填数字。小数请用小数点，例如 9999 或 12.5。");
            return;
        }
        var result = _cheatSession.TryWrite(_selectedHit.Hit, _selectedHit.Hit.LastBytes, replacement);
        Log($"数值修改：地址 0x{_selectedHit.Hit.Address:X}，类型={kind}，"
            + $"「{_selectedHit.CurrentValue}」→「{ScanWriteValueBox.Text}」，结果={result.Status}");
        if (result.Status == WriteStatus.Ok)
        {
            _selectedHit.Hit = _selectedHit.Hit with { LastBytes = replacement };
            _selectedHit.Refresh();
            SetCheatStatus($"已改成 {ScanWriteValueBox.Text}。切回游戏看看有没有生效；没变化就说明选错了，点“撤销上次修改”再换一项。");
            return;
        }
        SetCheatError(ExplainWrite(result), WriteAdvice(result));
    }

    private static string ExplainWrite(WriteResult result) => result.Status switch
    {
        WriteStatus.ValueChanged => "这个数值在你确认之前又变了，所以没有修改，以免改错。",
        WriteStatus.Unreadable => "这个地址已经读不到了，可能游戏已经重新分配了内存。",
        WriteStatus.WrongProcess => "游戏已经退出或重新启动，这次修改没有执行。",
        WriteStatus.WriteFailed => "系统没有接受这次写入。",
        _ => "修改失败。"
    };

    private static string WriteAdvice(WriteResult result) => result.Status switch
    {
        WriteStatus.ValueChanged => "回到游戏看现在的数值，重新“缩小结果”之后再修改。",
        WriteStatus.Unreadable => "重新做一次“首次查找”。" + UiText.MemoryAddressCaveat,
        WriteStatus.WrongProcess => "重新启动游戏，然后重新查找数值。",
        _ => "可以重新查找或核对地址与数值类型，再选择其他候选试试。"
    };

    private void OnToggleFreeze(object sender, RoutedEventArgs e)
    {
        if (_processLost)
        {
            SetCheatError("游戏进程已经退出了，锁定无法进行。",
                "等游戏重新启动并自动接管之后，重新做“首次查找”再锁定。");
            return;
        }
        if (_cheatSession is null || _selectedHit is null)
        {
            SetCheatError("还没有选中要锁定的那一项。", "先在上面的结果列表里点一行。");
            return;
        }
        if (IsFreezing)
        {
            _cheatSession.StopFreeze();
            _frozenHit = null;
            ScanFreezeButton.Content = "锁定这个数值";
            SetCheatStatus("已停止锁定。这个数值可以被游戏正常改变了。");
            RefreshWorkspaceState();
            return;
        }
        if (!NumericValue.TryParse(ScanWriteValueBox.Text, _selectedHit.Hit.Kind, out var value))
        {
            SetCheatError("锁定需要一个有效的数值。", "在“改成”里填一个数字，再点锁定。");
            return;
        }
        try
        {
            _cheatSession.StartFreeze(_selectedHit.Hit, value, TimeSpan.FromMilliseconds(60));
            _frozenHit = _selectedHit.Hit;
            ScanFreezeButton.Content = "停止锁定";
            SetCheatStatus($"已锁定为 {ScanWriteValueBox.Text}。游戏会一直保持这个数值；再点一次“停止锁定”即可解除。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException)
        {
            var explained = UiText.Explain(ex, "锁定数值");
            SetCheatError(explained.Message, explained.Advice, explained.Technical);
        }
        RefreshWorkspaceState();
    }

    private void OnUndoCheatWrite(object sender, RoutedEventArgs e)
    {
        if (_cheatSession is null)
        {
            SetCheatError("还没有连上游戏。", "先到“游戏与翻译”页启动游戏。");
            return;
        }
        var result = _cheatSession.UndoLast(out var restored);
        if (result.Status == WriteStatus.Ok)
        {
            if (restored is not null)
                foreach (var row in _cheatHits.Where(row => row.Hit.Address == restored.Address && row.Hit.Kind == restored.Kind))
                {
                    row.Hit = restored with { LastBytes = restored.LastBytes.ToArray() };
                    row.Refresh();
                }
            SetCheatStatus("已撤销上一次修改，数值恢复成原来的样子。");
            return;
        }
        SetCheatError(result.Message, result.Status switch
        {
            WriteStatus.ValueChanged => "回到游戏确认现在的数值，重新查找后再修改。",
            WriteStatus.WrongProcess => "重新启动或连接游戏，然后重新查找数值。",
            WriteStatus.Unreadable => "重新做一次“首次查找”。" + UiText.MemoryAddressCaveat,
            _ => "请确认本次会话有成功的修改，且目标地址仍然可读写。"
        });
    }

    // ================= 游戏数值推荐 =================

    private async void OnQueryUnityFields(object sender, RoutedEventArgs e)
    {
        if (_host is null || CheatSessionId is null)
        {
            SetUnityStatus("还没有连上游戏。", "先到“游戏与翻译”页启动游戏。");
            return;
        }
        try
        {
            var filter = UnityFieldFilterBox.Text ?? string.Empty;
            Log($"游戏数值查询：发出请求，进程 {CheatProcessId}，过滤词=「{filter}」");
            await _host.SendFieldQueryAsync(NextRequestId(), CheatSessionId, CheatProcessId, filter).ConfigureAwait(true);
            SetUnityStatus("已向游戏查询，正在等它回复。第一次查询通常几秒内完成。");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            var explained = UiText.Explain(ex, "查询游戏数值");
            SetUnityStatus(explained.Message, explained.Advice, explained.Technical);
        }
    }

    /// <summary>管道收到作弊消息时调用。查询与写入都在这里应答，不依赖联网翻译。</summary>
    private Task<CheatWireMessage?> HandleCheatCommandAsync(CheatWireMessage command, CancellationToken cancellationToken)
    {
        if (command.Kind == CheatMessageKinds.FieldReply)
        {
            if (CheatProtocol.Default.TryDecodeFieldReply(command.Payload, out var reply) && reply is not null)
                _ = Dispatcher.BeginInvoke(() => ApplyFieldReply(reply));
            return Task.FromResult<CheatWireMessage?>(null);
        }
        return Task.FromResult<CheatWireMessage?>(BuildAnswer(command));
    }

    private CheatWireMessage? BuildAnswer(CheatWireMessage command) => new CheatWireMessage
    {
        Version = CheatWire.Version,
        Kind = CheatMessageKinds.Error,
        RequestId = command.RequestId,
        SessionId = command.SessionId,
        ProcessId = command.ProcessId,
        Payload = CheatProtocol.Default.WriteErrorPayload("桌面端不处理这种作弊命令。")
    };

    private void ApplyFieldReply(CheatFieldReply reply)
    {
        Log($"游戏数值查询：收到回复，字段 {reply.Fields.Count} 个，来自 {reply.ScannedObjects} 个组件"
            + (reply.Truncated ? "（结果过多已截断）" : string.Empty));
        UnityFieldsAvailable = true;
        _unityFields.Clear();
        _rankedFields.Clear();
        _fieldTargets.Clear();
        var displayed = ScanValueBox.Text;
        foreach (var field in reply.Fields)
        {
            var candidate = new FieldCandidate(field.ObjectPath, field.ComponentType, field.FieldName, field.Kind,
                field.DisplayValue);
            _unityFields.Add(new UnityFieldCandidate(field, candidate));
            _fieldTargets[field.Token] = new CheatFieldWriteTarget(field.ObjectPath, field.ComponentType,
                field.FieldName, field.Kind);
        }
        var ranked = _unityFields
            .Select(item => CandidateRanker.Rank(item.Candidate, displayed))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Field.ComponentType, StringComparer.Ordinal)
            .ThenBy(item => item.Field.FieldName, StringComparer.Ordinal)
            .ToArray();
        var index = 0;
        foreach (var item in ranked)
            _rankedFields.Add(new RankedCandidateView(item, TokenOf(item.Field) ?? string.Empty, index++));
        UnityFieldList.ItemsSource = _rankedFields;
        SetUnityStatus(reply.Fields.Count == 0
                ? "没有找到可修改的数值。"
                : $"找到 {reply.Fields.Count} 个数值，来自 {reply.ScannedObjects} 个游戏组件。",
            reply.Fields.Count == 0
                ? "可能这款游戏没有把数值放在游戏组件里，或者它用了加密、实时计算。可以到“数值修改”页自己查找。"
                : null,
            reply.Truncated ? "结果过多，只显示了一部分。用上面的搜索框缩小范围。" : null);
        RefreshWorkspaceState();
    }

    private void OnUnityFieldSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UnityFieldList.SelectedItem is RankedCandidateView view)
        {
            UnityFieldValueBox.Text = view.Field.DisplayValue;
            UnityFieldEvidence.Text = view.ReasonSummary;
            UnityFieldTechnical.Text = view.Technical;
        }
        else
        {
            UnityFieldEvidence.Text = "选中一行后，这里会说明推荐理由。";
            UnityFieldTechnical.Text = "（选中一行后显示）";
        }
        RefreshWorkspaceState();
    }

    private void OnWriteUnityField(object sender, RoutedEventArgs e)
    {
        if (_host is null || CheatSessionId is null || UnityFieldList.SelectedItem is not RankedCandidateView view)
        {
            SetUnityStatus("还没有选中要修改的数值。", "先点“开始查询”，再在结果里选一行。");
            return;
        }
        if (string.IsNullOrEmpty(view.Token) || !_fieldTargets.TryGetValue(view.Token, out var target))
        {
            SetUnityStatus("这条结果已经过期。", "请重新点一次“开始查询”。");
            return;
        }
        var newValue = UnityFieldValueBox.Text ?? string.Empty;
        if (!NumericValue.TryParse(newValue, target.Kind, out _))
        {
            SetUnityStatus($"“{newValue}”不是有效的{UiText.KindName(target.Kind)}。",
                "只填数字，不要带汉字或空格。");
            return;
        }
        var detail = $"{view.OwnerSummary}｜{target.FieldName}｜原值 {view.Field.DisplayValue} → 新值 {newValue}";
        AskConfirm("确认修改游戏数值",
            $"即将把“{target.FieldName}”从 {view.Field.DisplayValue} 改成 {newValue}。\n\n" +
            UiText.CandidateCaveat + "\n改完如果游戏里没变化，重新查询即可；不会破坏游戏文件。",
            detail,
            () => SendFieldWrite(view, target, newValue));
    }

    private async void SendFieldWrite(RankedCandidateView view, CheatFieldWriteTarget target, string newValue)
    {
        if (_host is null || CheatSessionId is null) return;
        try
        {
            await _host.SendFieldWriteAsync(NextRequestId(), CheatSessionId, CheatProcessId, target,
                view.Field.DisplayValue, newValue).ConfigureAwait(true);
            Log($"游戏数值修改：发出请求，{target.ComponentType}.{target.FieldName}，"
                + $"「{view.Field.DisplayValue}」→「{newValue}」，进程 {CheatProcessId}");
            SetUnityStatus("已发出修改请求，正在等游戏确认…");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            var explained = UiText.Explain(ex, "修改游戏数值");
            SetUnityStatus(explained.Message, explained.Advice, explained.Technical);
        }
    }

    private string? TokenOf(FieldCandidate candidate)
    {
        foreach (var item in _unityFields)
            if (ReferenceEquals(item.Candidate, candidate) ||
                (string.Equals(item.Candidate.ObjectPath, candidate.ObjectPath, StringComparison.Ordinal) &&
                 string.Equals(item.Candidate.ComponentType, candidate.ComponentType, StringComparison.Ordinal) &&
                 string.Equals(item.Candidate.FieldName, candidate.FieldName, StringComparison.Ordinal) &&
                 item.Candidate.Kind == candidate.Kind))
                return item.Snapshot.Token;
        return null;
    }

    // ================= 快捷项 =================

    private void LoadPresetsForActiveGame(GameProfile game)
    {
        _cheatPresets.Clear();
        try
        {
            _loadedPresets = PresetStore.List(game.GameId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadedPresets = Array.Empty<CheatPreset>();
            var explained = UiText.Explain(ex, "读取快捷项");
            SetCheatWarning(explained.Message, explained.Technical);
        }
        foreach (var preset in _loadedPresets) _cheatPresets.Add(preset);
        PresetList.ItemsSource = _cheatPresets;
    }

    private void OnSaveCheatPreset(object sender, RoutedEventArgs e)
    {
        if (_activeGame is null)
        {
            SetCheatError("还没有启动游戏，不能保存快捷项。", "先到“游戏与翻译”页启动游戏。");
            return;
        }
        var name = (PresetNameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            SetCheatError("请先给这个快捷项起个名字。", "例如“金钱”“生命上限”，方便下次认出来。");
            return;
        }
        try
        {
            CheatPreset preset;
            if (UnityFieldList.SelectedItem is RankedCandidateView view)
            {
                preset = CheatPreset.Field(_activeGame.GameId, _activeGame.UnityVersion, name,
                    view.Field.ObjectPath, view.Field.ComponentType, view.Field.FieldName, view.Field.Kind);
            }
            else if (_selectedHit is not null)
            {
                // 普通内存地址不保存：只记录搜索条件，重启后必须重新查找。
                preset = CheatPreset.Memory(_activeGame.GameId, _activeGame.UnityVersion, name,
                    _selectedHit.Hit.Address > int.MaxValue ? int.MaxValue : (int)_selectedHit.Hit.Address,
                    ScanNewValueBox.Text ?? string.Empty);
            }
            else
            {
                SetCheatError("没有可保存的选择。", "先在“游戏数值推荐”里选一个数值，或在“数值修改”里选一条结果。");
                return;
            }
            PresetStore.Save(preset);
            LoadPresetsForActiveGame(_activeGame);
            SetCheatStatus($"快捷项“{name}”已保存。" + (preset.Source == CheatPresetSource.MemorySearch
                ? "它只记住搜索条件，游戏重启后仍要重新查找。"
                : "下次启动游戏会自动核对这个数值是否还在。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var explained = UiText.Explain(ex, "保存快捷项");
            SetCheatError(explained.Message, explained.Advice, explained.Technical);
        }
    }

    private void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedItem is not CheatPreset preset) return;
        if (preset.Source == CheatPresetSource.MemorySearch)
        {
            SetCheatStatus($"“{preset.Name}”是内存快捷项。它只保存了搜索条件，游戏重启后必须重新查找。");
            return;
        }
        var resolution = PresetStore.Resolve(preset, _unityFields.Select(item => item.Candidate).ToArray(),
            _activeGame?.UnityVersion ?? string.Empty);
        SetCheatStatus(resolution.Status switch
        {
            PresetStatus.Ready => $"快捷项“{preset.Name}”已核对成功，可以直接修改。",
            PresetStatus.RescanRequired => $"快捷项“{preset.Name}”需要重新查找：{resolution.Reason}",
            _ => $"快捷项“{preset.Name}”需要重新确认：{resolution.Reason} 为避免改错，程序不会自动修改。"
        });
    }

    // ================= 导航 =================

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, NavList)) return;
        if (NavList.SelectedIndex < 0) return;
        if (WorkspaceTabs.SelectedIndex != NavList.SelectedIndex)
            WorkspaceTabs.SelectedIndex = NavList.SelectedIndex;
    }

    private void OnWorkspaceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles from combo boxes and the game library too.
        // Only a tab selection owns navigation (including during BAML loading).
        if (!ReferenceEquals(e.OriginalSource, WorkspaceTabs)) return;
        if (NavList.SelectedIndex != WorkspaceTabs.SelectedIndex)
            NavList.SelectedIndex = WorkspaceTabs.SelectedIndex;
        TopPageTitle.Text = WorkspaceTabs.SelectedIndex >= 0 && WorkspaceTabs.SelectedIndex < WorkspacePageTitles.Count
            ? WorkspacePageTitles[WorkspaceTabs.SelectedIndex]
            : string.Empty;
    }

    private void OnGoToGamePage(object sender, RoutedEventArgs e)
    {
        WorkspaceTabs.SelectedIndex = 0;
        NavList.SelectedIndex = 0;
    }

    // ================= 测试钩子 =================

    /// <summary>
    /// 测试钩子：建立一个作弊会话，走的是“连接到正在运行的游戏”同一段真实逻辑
    /// （AttachToRunning），这样测试验证的会话状态和真实路径一致。
    /// </summary>
    internal void StartCheatSessionForTest(GameProfile game, IProcessMemory memory) =>
        AttachToRunning(game, new GameProcessInfo(memory.ProcessId, game.ExePath, null), memory);

    /// <summary>测试钩子：当前选中的游戏（会话断开后也应该保留，用于“接管后游戏选择不变”）。</summary>
    internal GameProfile? ActiveGameForTest() => _activeGame;

    /// <summary>测试钩子：点“连接到正在运行的游戏”按钮，走真实入口。</summary>
    internal void ConnectToRunningForTest() => OnConnectToRunningGame(this, new RoutedEventArgs());

    internal void ApplyFieldReplyForTest(CheatFieldReply reply) => ApplyFieldReply(reply);

    internal CheatSession? CheatSessionForTest() => _cheatSession;

    /// <summary>
    /// 测试钩子：往结果表里塞一条命中。
    /// 必须和真实查找走同一段收尾逻辑（AddHits），否则按钮状态不会更新，
    /// “再次查找”会一直是禁用状态，等待它的测试就会超时。
    /// </summary>
    internal void SeedHitForTest(ScanHit hit) => AddHits(new[] { hit });

    internal IReadOnlyList<RankedCandidateView> RankedFieldsForTest() => _rankedFields.ToArray();

    internal int ScoreOfForTest(FieldCandidate candidate)
    {
        foreach (var item in _rankedFields)
            if (string.Equals(item.Field.FieldName, candidate.FieldName, StringComparison.Ordinal))
                return item.Ranked.Score;
        return -1;
    }

    internal void SaveMemoryPresetForTest() => OnSaveCheatPreset(this, new RoutedEventArgs());

    /// <summary>测试钩子：按指定类型与数值触发一次首次查找并等它结束（同步，原因同上）。</summary>
    internal Task ScanForTest(NumericKind kind, string value)
    {
        ScanKindBox.SelectedIndex = kind switch
        {
            NumericKind.Int64 => 1,
            NumericKind.Float32 => 2,
            NumericKind.Float64 => 3,
            _ => 0
        };
        ScanValueBox.Text = value;
        return RunFirstScanAsync();
    }

    /// <summary>
    /// 测试钩子：在第 2 步的“游戏现在显示的新数值”里填值并点“再次查找”，等它结束。
    /// 走的是和真实按钮完全相同的入口（OnRefineScan），因此这里验证的就是玩家实际走的路径。
    ///
    /// 刻意做成同步：界面测试在 STA 线程上用 GetResult() 阻塞等待，
    /// 如果这里用 await ConfigureAwait(true)，续体要回到同一个 Dispatcher，
    /// 而那个 Dispatcher 正被测试阻塞——会直接死锁。
    /// </summary>
    internal Task RefineWithValueForTest(string newValue)
    {
        ScanNewValueBox.Text = newValue;
        return RunRefineScanAsync();
    }

    /// <summary>测试钩子：点第 3 步的“修改”，走真实写入路径。</summary>
    internal void WriteSelectedForTest() => OnWriteScannedValue(this, new RoutedEventArgs());
    /// <summary>测试钩子：当前结果表里的数值文本，用来验证筛选结果。</summary>
    internal IReadOnlyList<string> HitValuesForTest() => _cheatHits.Select(view => view.CurrentValue).ToArray();

    /// <summary>测试钩子：第 2 步的“目前还剩 N 项”提示。</summary>
    internal string ResultSummaryForTest() => ScanResultSummary.Text;
    /// <summary>测试/验证钩子：当前结果表里第一条命中的地址，用来独立读回内存核对。</summary>
    internal ulong FirstHitAddressForTest() => _cheatHits.Count == 0 ? 0UL : _cheatHits[0].Hit.Address;

    /// <summary>测试/验证钩子：点“撤销上次修改”，走真实入口。</summary>
    internal void UndoForTest() => OnUndoCheatWrite(this, new RoutedEventArgs());

    /// <summary>截图/测试用：等待一个查找任务（在当前线程上等待，不阻塞 Dispatcher）。</summary>
    internal void WaitForScanTask(Task task)
    {
        var started = DateTime.UtcNow;
        while (!task.IsCompleted && DateTime.UtcNow - started < TimeSpan.FromSeconds(30))
        {
            var frame = new DispatcherFrame();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        if (task.IsFaulted) task.GetAwaiter().GetResult();
    }

    /// <summary>截图用：直接设置状态文字，方便展示某个阶段的界面。</summary>
    internal void SetScanStatusForSnapshot(string message) => SetCheatStatus(message);
    internal int HitCountForTest() => _cheatHits.Count;

    internal void SelectFirstHitForTest()
    {
        if (_cheatHits.Count == 0) return;
        // 同时做两件事：
        // 1) 走真实的选中变更，验证玩家点一行时的实际行为；
        // 2) 直接落定 _selectedHit，因为未 Show 出来的 DataGrid 不一定会生成行、
        //    也就不一定触发 SelectionChanged。
        ScanResultList.SelectedIndex = -1;
        ScanResultList.SelectedIndex = 0;
        var previous = _selectedHit;
        _selectedHit = _cheatHits[0];
        if (!ReferenceEquals(previous, _selectedHit)) ScanWriteValueBox.Text = _selectedHit.CurrentValue;
        RefreshWorkspaceState();
    }

    /// <summary>
    /// 测试钩子：把三页当前会显示的文字汇总成一份文本。
    ///
    /// 用遍历逻辑树的方式而不是可视树：窗口没有 Show 出来时 WPF 不生成模板子元素，
    /// 可视树是空的。返回的是“页面上会显示什么”，供文案测试检查用词。
    /// </summary>
    internal string VisibleTextDumpForTest()
    {
        var builder = new System.Text.StringBuilder();
        var originalPage = WorkspaceTabs.SelectedIndex;
        for (var page = 0; page < WorkspaceTabs.Items.Count; page++)
        {
            WorkspaceTabs.SelectedIndex = page;
            builder.AppendLine("### PAGE " + page);
            AppendText(WorkspaceTabs, builder, 0);
        }
        WorkspaceTabs.SelectedIndex = originalPage;
        return builder.ToString();
    }

    private static void AppendText(DependencyObject node, System.Text.StringBuilder builder, int depth)
    {
        if (depth > 80) return;
        if (node is UIElement { Visibility: not Visibility.Visible }) return;
        if (node is Expander { IsExpanded: false }) return;

        switch (node)
        {
            case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                builder.AppendLine(text.Text);
                break;
            case ContentControl { Content: string label } when !string.IsNullOrWhiteSpace(label):
                builder.AppendLine(label);
                break;
            case ComboBox combo:
                foreach (var item in combo.Items)
                    if (item is ComboBoxItem { Content: string option }) builder.AppendLine(option);
                break;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < childCount; index++)
            AppendText(VisualTreeHelper.GetChild(node, index), builder, depth + 1);

        if (childCount > 0) return;
        if (node is not FrameworkElement element) return;
        foreach (var child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject childNode) AppendText(childNode, builder, depth + 1);
    }
    /// <summary>
    /// 测试钩子：为界面测试自己的 STA 线程准备一个 Dispatcher 和
    /// DispatcherSynchronizationContext。
    ///
    /// 只建 Dispatcher 不够：OnFirstScan / OnRefineScan 里的
    /// await ... ConfigureAwait(true) 依赖 SynchronizationContext.Current 才能把续体
    /// 送回来。没有它，续体会在线程池线程上继续跑，去碰控件就抛
    /// “调用线程无法访问此对象”。
    /// </summary>
    internal static void PrepareTestDispatcherForTest()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
    }
    /// <summary>测试钩子：作弊状态下面的“怎么办”提示行。</summary>
    internal string CheatAdviceForTest() => CheatStatusAdvice.Visibility == Visibility.Visible
        ? CheatStatusAdvice.Text
        : string.Empty;
    internal string CheatStatusTextForTest() => CheatStatus.Text;

    internal string RunStatusTextForTest() => ServiceStatus.Text;

    internal bool ScanEmptyStateVisibleForTest() => ScanEmptyState.Visibility == Visibility.Visible;

    /// <summary>列表显示上限固定 5000；完整候选保留在快照中供再次筛选。</summary>
    private static int ReadMaxHits() => 5000;

    private string NextRequestId() => "r" + Interlocked.Increment(ref _uid).ToString(CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>结果表里的一行。数值以界面文本形式保存，刷新时重新格式化。</summary>
public sealed class ScanHitView : INotifyPropertyChanged
{
    public ScanHitView(ScanHit hit, string currentValue, string addressText)
    {
        Hit = hit;
        CurrentValue = currentValue;
        AddressText = addressText;
    }

    public ScanHit Hit { get; set; }

    public string AddressText { get; }

    public string KindText => UiText.KindName(Hit.Kind);

    public string CurrentValue { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        CurrentValue = NumericValue.Format(Hit.LastBytes, Hit.Kind);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue)));
    }
}

/// <summary>推荐字段的协议快照与排序输入。</summary>
public sealed class UnityFieldCandidate
{
    public UnityFieldCandidate(FieldSnapshot snapshot, FieldCandidate candidate)
    {
        Snapshot = snapshot;
        Candidate = candidate;
    }

    public FieldSnapshot Snapshot { get; }

    public FieldCandidate Candidate { get; }
}

/// <summary>
/// 一个“正在运行的候选进程”：接管游戏重启、连接已运行的游戏都要用到它。
///
/// 带上 <see cref="Process"/> 是为了给会话挂退出事件与内存句柄；
/// 测试里没有真实进程，所以它是可空的，此时 <see cref="ProcessId"/> 仍然可用。
/// </summary>
public sealed record GameProcessInfo(int ProcessId, string ExecutablePath, Process? Process);

