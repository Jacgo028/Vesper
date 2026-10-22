using System.Windows;
using System.Windows.Controls;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

/// <summary>
/// V0.2 修的两个实测问题的回归测试。
///
/// 问题一（截图里看到的现象）：再次查找筛出 0 项之后，结果区仍然写着
/// “还没有结果时，请先做首次查找”，与实际发生的事不符；
/// 以及游戏重启自己之后软件显示“未启动游戏”，游戏却还在跑。
///
/// 这里断言的都是**玩家真正看得到的文字与控件状态**，
/// 并且尽量走真实入口（按钮事件、真实的收尾方法），避免只测到一个平行实现。
/// </summary>
[Collection(WpfUiCollection.Name)]
public sealed class EmptyStateAndTakeOverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-emptystate-tests", Guid.NewGuid().ToString("N"));

    private static GameProfile TestGame(string? root = null) => new GameProfile(
        "game-" + Guid.NewGuid().ToString("N")[..8],
        root ?? Path.Combine(Path.GetTempPath(), "g"),
        Path.Combine(root ?? Path.Combine(Path.GetTempPath(), "g"), "Game.exe"),
        "2022.3.62f2", "Mono", "x64", true, string.Empty);

    private MainWindow NewWindow() => new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });

    // ==================== 问题一：三种空结果状态 ====================

    /// <summary>还没查找过：文案要请玩家做“首次查找”，并且别说成“会话已结束”。</summary>
    [Fact]
    public void FreshWindowAsksForTheFirstScan()
    {
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.WorkspaceTabs.SelectedIndex = 1;
                return (Kind: window.EmptyResultKindForTest(),
                        Main: window.ScanResultEmptyHintForTest(),
                        Next: window.ScanResultHintForTest());
            }
            finally { window.Close(); }
        });

        Assert.Equal(MainWindow.ScanResultEmptyKind.NotStarted, state.Kind);
        Assert.Contains("首次查找", state.Main, StringComparison.Ordinal);
        Assert.DoesNotContain("会话已经结束", state.Main, StringComparison.Ordinal);
        Assert.Contains("首次查找", state.Next, StringComparison.Ordinal);
    }

    /// <summary>
    /// 核心回归：首次查找有候选 → 再让它们全都变化 → 再次查找筛出 0 项。
    /// 这时结果区必须说“没有匹配的结果”，**绝不能**再出现“请先做首次查找”。
    /// </summary>
    [Fact]
    public void RefineYieldingNothingExplainsThereAreNoMatches()
    {
        var memory = new FakeMemory((0x1000, 130), (0x2000, 500), (0x3000, 999));
        ResultText state = default;

        OnWindow(async window =>
        {
            window.StartCheatSessionForTest(TestGame(), memory);
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
            window.SeedHitForTest(new ScanHit(0x2000, NumericKind.Int32, BitConverter.GetBytes(500)));
            window.SeedHitForTest(new ScanHit(0x3000, NumericKind.Int32, BitConverter.GetBytes(999)));

            // 游戏把三个数值全改了，玩家填一个已经不存在的数值再筛。
            memory.SetInt32(0x1000, 7);
            memory.SetInt32(0x2000, 8);
            memory.SetInt32(0x3000, 9);
            await window.RefineWithValueForTest("12345");

            state = new ResultText(window.EmptyResultKindForTest(), window.ScanResultEmptyHintForTest(),
                window.ScanResultHintForTest(), window.HitCountForTest(),
                window.CheatStatusTextForTest());
        });

        Assert.Equal(0, state.Hits);
        Assert.Equal(MainWindow.ScanResultEmptyKind.NoResults, state.Kind);
        // 这是本次要修的原始缺陷：0 项时不能再让玩家看到“请先做首次查找”。
        Assert.DoesNotContain("请先做“首次查找”", state.Main, StringComparison.Ordinal);
        Assert.Contains("没有匹配的结果", state.Main, StringComparison.Ordinal);
        // 下一步要具体：说清可能的原因。
        Assert.Contains("重新计算", state.Main, StringComparison.Ordinal);
        // 状态栏也要说“已经没有等于 … 的结果了”，和结果区一致。
        Assert.Contains("已经没有等于", state.CheatStatus, StringComparison.Ordinal);
    }

    /// <summary>“还没查找”和“筛出 0 项”必须是两段不同的文字，不能共用一句。</summary>
    [Fact]
    public void TheTwoEmptyMessagesAreDifferentStrings()
    {
        Assert.NotEqual(MainWindow.ScanHintNotStarted, MainWindow.ScanHintNoResults);
        Assert.NotEqual(MainWindow.ScanHintNoResults, MainWindow.ScanHintSessionEnded);
        Assert.NotEqual(MainWindow.ScanHintNotStarted, MainWindow.ScanHintSessionEnded);

        Assert.Equal(MainWindow.ScanHintNotStarted,
            MainWindow.ScanHintFor(MainWindow.ScanResultEmptyKind.NotStarted));
        Assert.Equal(MainWindow.ScanHintNoResults,
            MainWindow.ScanHintFor(MainWindow.ScanResultEmptyKind.NoResults));
        Assert.Equal(MainWindow.ScanHintSessionEnded,
            MainWindow.ScanHintFor(MainWindow.ScanResultEmptyKind.SessionEnded));
    }

    /// <summary>会话断开（游戏真退出）之后：提示要说“会话已结束”，并给出下一步。</summary>
    [Fact]
    public void SessionEndedExplainsHowToGetBack()
    {
        var result = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                using var memory = new FakeMemory((0x1000, 130));
                window.StartCheatSessionForTest(TestGame(), memory);
                window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
                // 进程退出 -> 等新进程 -> 等不到 -> 收尾
                window.NotifyGameExitedForTest();
                window.FinishTakeOverWaitForTest();
                return (Kind: window.EmptyResultKindForTest(),
                        Main: window.ScanResultEmptyHintForTest(),
                        Hits: window.HitCountForTest(),
                        HasSession: window.HasGameSession,
                        ConnectVisible: window.ScanConnectRunningButton.Visibility);
            }
            finally { window.Close(); }
        });

        Assert.Equal(0, result.Hits);
        Assert.False(result.HasSession);
        Assert.Equal(MainWindow.ScanResultEmptyKind.SessionEnded, result.Kind);
        Assert.Contains("游戏会话已经结束", result.Main, StringComparison.Ordinal);
        Assert.Contains("连接到正在运行的游戏", result.Main, StringComparison.Ordinal);
        // 断开之后要能重新连上，所以“连接到正在运行的游戏”按钮必须露出来。
        Assert.Equal(Visibility.Visible, result.ConnectVisible);
    }

    /// <summary>
    /// 有结果的时候不能用空状态文案。
    /// 真机验证时发现过自相矛盾的输出：明明「目前还剩 252 项」，空状态却报 NoResults。
    /// </summary>
    [Fact]
    public void EmptyKindIsNotReportedWhileThereAreHits()
    {
        var memory = new FakeMemory((0x1000, 130));
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.StartCheatSessionForTest(TestGame(), memory);
                var before = (Hits: window.HitCountForTest(), Kind: window.EmptyResultKindForTest(),
                    HintVisible: window.ScanResultEmptyHint.Visibility);
                window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
                var after = (Hits: window.HitCountForTest(), Kind: window.EmptyResultKindForTest(),
                    HintVisible: window.ScanResultEmptyHint.Visibility,
                    HintText: window.ScanResultEmptyHintForTest());
                return (before, after);
            }
            finally { window.Close(); }
        });

        Assert.Equal(0, state.before.Hits);
        Assert.Equal(MainWindow.ScanResultEmptyKind.NotStarted, state.before.Kind);
        Assert.Equal(Visibility.Visible, state.before.HintVisible);

        Assert.Equal(1, state.after.Hits);
        // 有结果时不该被判成任何一种"空状态"。
        Assert.Equal(MainWindow.ScanResultEmptyKind.NotStarted, state.after.Kind);
        Assert.Equal(Visibility.Collapsed, state.after.HintVisible);
    }

    // ==================== 问题一：游戏重启自己后的接管 ====================

    /// <summary>
    /// 核心回归：游戏重启自己（老进程退出、新进程起来）之后，
    /// 软件必须**自动接管**新进程，并且保留玩家选的游戏；
    /// 只有确实没有新进程时才会清空。
    /// </summary>
    [Fact]
    public void GameRestartingItselfIsTakenOverAndTheGameSelectionKept()
    {
        var game = TestGame();
        // 必须和“上一个已连接的进程”不同，否则接管会被正确地跳过。
        // 上一个进程号来自 FakeMemory（就是当前测试进程），所以这里用一个足够远、不冲突的值。
        var newProcessId = Environment.ProcessId + 100000;
        TakeOverState state = default;

        OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                using var memory = new FakeMemory((0x1000, 130));
                window.StartCheatSessionForTest(game, memory);
                window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));

                // 新进程起来了（同一个 exe、同一个目录），老进程已经退出。
                IReadOnlyList<GameProcessInfo> running = new[]
                {
                    new GameProcessInfo(newProcessId, game.ExePath, null)
                };
                window.RunningProcessesForTest = _ => running;
                // 测试里没有真实进程，用假内存代替：接管逻辑本身才是被测对象。
                var newMemory = new FakeMemory((0x1000, 130));
                window.ProcessMemoryForTest = _ => newMemory;

                window.NotifyGameExitedForTest();
                // 退出瞬间就会先试一次接管；只有那一次没成才需要等定时器。
                var autoProcessId = window.CheatProcessId;
                var autoGame = window.ActiveGameForTest()?.GameId;
                var autoHits = window.HitCountForTest();
                var autoWaiting = window.HasTakeOverWaitForTest();

                state = new TakeOverState(autoProcessId, autoGame, autoHits, autoWaiting,
                    window.HasGameSession, window.CanScan,
                    window.RunStatusTextForTest(), window.CheatStatusTextForTest());
                return Task.CompletedTask;
            }
            finally { window.Close(); }
        });

        // 退出那一刻就应该已经自动接管到新进程（不需要等定时器）。
        Assert.Equal(newProcessId, state.ProcessId);
        // 这是本次要修的核心：游戏选择不能丢。
        Assert.Equal(game.GameId, state.GameId);
        Assert.True(state.HasSession, "接管后必须仍有会话；诊断：pid=" + state.ProcessId
            + " hits=" + state.Hits + " canScan=" + state.CanScan + " waiting=" + state.WaitingForTakeOver
            + " | Run=" + state.RunStatus + " | Cheat=" + state.CheatStatus);
        Assert.True(state.CanScan);
        // 接管成功就不该再继续等。
        Assert.False(state.WaitingForTakeOver);
        // 但内存地址在新进程里无效，结果必须清空，并且要明确告诉玩家。
        Assert.Equal(0, state.Hits);
        Assert.Contains("重新启动", state.RunStatus, StringComparison.Ordinal);
        Assert.Contains(newProcessId.ToString(), state.RunStatus, StringComparison.Ordinal);
        Assert.Contains("重新做“首次查找”", state.CheatStatus, StringComparison.Ordinal);
    }

    /// <summary>确实没有新进程时才清空会话，并给出“可以再次启动”。</summary>
    [Fact]
    public void NoNewProcessMeansTheSessionIsCleared()
    {
        var game = TestGame();
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                using var memory = new FakeMemory((0x1000, 130));
                window.StartCheatSessionForTest(game, memory);
                window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
                // 没有任何正在运行的进程。
                window.RunningProcessesForTest = _ => Array.Empty<GameProcessInfo>();

                window.NotifyGameExitedForTest();
                var tookOver = window.TakeOverImmediatelyForTest();
                window.FinishTakeOverWaitForTest();
                return (TookOver: tookOver, window.HasGameSession, Hits: window.HitCountForTest(),
                        Game: window.ActiveGameForTest(), Kind: window.EmptyResultKindForTest(),
                        RunStatus: window.RunStatusTextForTest(), ConnectVisible: window.ScanConnectRunningButton.Visibility);
            }
            finally { window.Close(); }
        });

        Assert.False(state.TookOver);
        Assert.False(state.HasGameSession);
        Assert.Null(state.Game);
        Assert.Equal(0, state.Hits);
        Assert.Contains("游戏已退出", state.RunStatus, StringComparison.Ordinal);
        Assert.Equal(MainWindow.ScanResultEmptyKind.SessionEnded, state.Kind);
        // 退出后仍要能连回正在运行的游戏或重新启动。
        Assert.Equal(Visibility.Visible, state.ConnectVisible);
    }

    // ==================== 问题一：连接到正在运行的游戏 ====================

    /// <summary>已经有候选时点“连接到正在运行的游戏”：直接连上，并给出进程号。</summary>
    [Fact]
    public void ConnectButtonAttachesToTheRunningGame()
    {
        var game = TestGame();
        var running = new[] { new GameProcessInfo(4321, game.ExePath, null) };
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.Games.Add(game);
                window.RunningProcessesForTest = _ => running;
                var runningMemory = new FakeMemory((0x1000, 130));
                window.ProcessMemoryForTest = _ => runningMemory;
                using var memory = new FakeMemory((0x1000, 130));
                // 先建一次会话拿到管道，再断开，模拟“软件开着、游戏是玩家自己启动的”。
                window.StartCheatSessionForTest(game, memory);
                window.NotifyGameExitedForTest();
                window.FinishTakeOverWaitForTest();

                window.ConnectToRunningForTest();
                return (Game: window.ActiveGameForTest()?.GameId, ProcessId: window.CheatProcessId,
                        HasSession: window.HasGameSession, Status: window.RunStatusTextForTest(),
                        Cheat: window.CheatStatusTextForTest());
            }
            finally { window.Close(); }
        });

        Assert.Equal(game.GameId, state.Game);
        Assert.Equal(4321, state.ProcessId);
        Assert.True(state.HasSession, "连接后必须建立会话；诊断：Status=" + state.Status + " | Cheat=" + state.Cheat);
        var text = state.Status + " " + state.Cheat;
        Assert.Contains("4321", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 组件没装但游戏已经在跑：不该弹“还没有安装组件”把人挡在门外，
    /// 而应该直接连上正在运行的进程（实测踩到的顺序问题）。
    /// </summary>
    [Fact]
    public void LaunchingWithoutInstallStillAttachesWhenTheGameIsRunning()
    {
        var game = TestGame();
        var messages = new List<string>();
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                // 故意不建 .unity-translator-install.json：模拟“没装组件”。
                window.MessageBoxHandler = message => { messages.Add(message); return MessageBoxResult.OK; };
                window.RunningProcessesForTest = _ => new[] { new GameProcessInfo(555, game.ExePath, null) };
                var runningMemory = new FakeMemory((0x1000, 130));
                window.ProcessMemoryForTest = _ => runningMemory;
                window.Games.Add(game);
                window.LaunchForTest(new Button { Tag = game });
                return (Game: window.ActiveGameForTest()?.GameId, ProcessId: window.CheatProcessId,
                        Status: window.RunStatusTextForTest());
            }
            finally { window.Close(); }
        });

        Assert.Empty(messages);
        Assert.Equal(game.GameId, state.Game);
        Assert.Equal(555, state.ProcessId);
        Assert.Contains("555", state.Status, StringComparison.Ordinal);
    }

    /// <summary>一个候选都没有时要明确说“没找到正在运行的游戏”，并提示下一步。</summary>
    [Fact]
    public void ConnectButtonExplainsWhenNothingIsRunning()
    {
        var status = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.Games.Add(TestGame());
                window.RunningProcessesForTest = _ => Array.Empty<GameProcessInfo>();
                window.ConnectToRunningForTest();
                return window.CheatStatusTextForTest();
            }
            finally { window.Close(); }
        });

        Assert.Contains("没有找到正在运行的游戏", status, StringComparison.Ordinal);
        Assert.Contains("启动游戏", status, StringComparison.Ordinal);
    }

    /// <summary>多个候选进程时让玩家确认，取消就不连。</summary>
    [Fact]
    public void MultipleRunningGamesRequirePlayerConfirmation()
    {
        var asked = new List<string>();
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                var game = TestGame();
                window.Games.Add(game);
                window.RunningProcessesForTest = _ => new[]
                {
                    new GameProcessInfo(111, game.ExePath, null),
                    new GameProcessInfo(222, game.ExePath, null)
                };
                window.AskYesNoForTest = label => { asked.Add(label); return MessageBoxResult.No; };
                window.ConnectToRunningForTest();
                return (window.HasGameSession, Status: window.CheatStatusTextForTest());
            }
            finally { window.Close(); }
        });

        Assert.Single(asked);
        Assert.False(state.HasGameSession);
        Assert.Contains("已取消连接", state.Status, StringComparison.Ordinal);
    }

    /// <summary>游戏已经在跑时点“启动游戏”不能再起第二个进程（会改错进程的数值）。</summary>
    [Fact]
    public void LaunchingWhenTheGameIsAlreadyRunningAttachesInstead()
    {
        var game = TestGame();
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.RunningProcessesForTest = _ => new[] { new GameProcessInfo(987, game.ExePath, null) };
                window.ProcessMemoryForTest = _ => new FakeMemory((0x1000, 130));
                // 直接走“启动游戏”的入口，验证它不会去启动第二个进程。
                var button = new Button { Tag = game };
                window.Games.Add(game);
                window.LaunchForTest(button);
                return (Game: window.ActiveGameForTest()?.GameId, Status: window.RunStatusTextForTest(),
                        Cheat: window.CheatStatusTextForTest());
            }
            finally { window.Close(); }
        });

        Assert.Equal(game.GameId, state.Game);
        // 不能启动第二个进程：这条路必须走“连上去”，并显示进程号。
        var text = state.Status + " " + state.Cheat;
        Assert.Contains("987", text, StringComparison.Ordinal);
    }

    // ==================== app.log 诊断 ====================

    /// <summary>
    /// 版本号一致性：界面左下角读的是程序集信息，必须与 Version.props 一致。
    /// 发布新版时忘了改 Version.props，这条测试就会红。
    /// </summary>
    [Fact]
    public void DisplayedVersionComesFromTheSingleVersionSource()
    {
        // ★ 不要在这里硬编码版本号 —— 每次升版都会让这条用例失败，
        //   而它想守的其实是"界面显示的版本与唯一版本源一致"。
        //   改成从 Version.props 读出版本再比对，升版时不必再改测试。
        var props = FindVersionProps();
        Assert.NotNull(props);

        var match = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(props!), "<AppVersion>([^<]+)</AppVersion>");
        Assert.True(match.Success, "Version.props 里没有 AppVersion");

        Assert.Equal("V" + match.Groups[1].Value.Trim(), UnityTranslator.App.AppVersion.Display);
    }

    /// <summary>向上找到仓库根的 Version.props。</summary>
    private static string? FindVersionProps()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Version.props");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 诊断缺口（本次排查困难的根本原因）：app.log 里必须能查到
    /// 会话、进程号与查找命中数，否则下次仍然查不出问题。
    /// </summary>
    [Fact]
    public void AppLogRecordsSessionProcessAndScanCounts()
    {
        var game = TestGame();
        var dataDirectory = Path.Combine(_root, "appdata");
        var logPath = Path.Combine(dataDirectory, "app.log");

        OnWindow(async window =>
        {
            using var memory = new FakeMemory((0x1000, 130), (0x2000, 500));
            window.StartCheatSessionForTest(game, memory);
            // 走真实的首次查找与再次查找（不是 SeedHit 捷径），日志才代表玩家真实操作。
            await window.ScanForTest(NumericKind.Int32, "130");
            await window.RefineWithValueForTest("500");
            // 再走一次真实的进程退出：日志必须能看出“软件在跟的进程没了、有没有接管”。
            window.RunningProcessesForTest = _ => Array.Empty<GameProcessInfo>();
            window.NotifyGameExitedForTest();
            window.FinishTakeOverWaitForTest();
        });

        Assert.True(File.Exists(logPath), "app.log 必须被创建：" + logPath);
        var log = File.ReadAllText(logPath);

        // 会话与进程身份：判断“软件到底在跟哪个进程”全靠它。
        Assert.Contains("会话", log, StringComparison.Ordinal);
        Assert.Contains("PID=", log, StringComparison.Ordinal);
        Assert.Contains("连接", log, StringComparison.Ordinal);
        // 查找：命中数前后都要有。
        Assert.Contains("首次查找", log, StringComparison.Ordinal);
        Assert.Contains("再次查找", log, StringComparison.Ordinal);
        Assert.Contains("筛选前", log, StringComparison.Ordinal);
        Assert.Contains("筛选后", log, StringComparison.Ordinal);
        // 进程退出与接管尝试。
        Assert.Contains("进程退出", log, StringComparison.Ordinal);
        Assert.Contains("接管", log, StringComparison.Ordinal);
    }

    // ==================== 测试基础设施 ====================

    private readonly record struct ResultText(MainWindow.ScanResultEmptyKind Kind, string Main, string Next,
        int Hits, string CheatStatus);

    private readonly record struct TakeOverState(int ProcessId, string? GameId, int Hits, bool WaitingForTakeOver,
        bool HasSession, bool CanScan, string RunStatus, string CheatStatus);

    /// <summary>受控进程内存替身：数值可读可写，且一直“活着”。</summary>
    private sealed class FakeMemory : IProcessMemory, IDisposable
    {
        private readonly Dictionary<ulong, byte[]> _values = new();

        public FakeMemory(params (ulong Address, int Value)[] initial)
        {
            foreach (var item in initial) SetInt32(item.Address, item.Value);
            ProcessId = Environment.ProcessId;
            StartedUtcTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        }

        public int ProcessId { get; }
        public long StartedUtcTicks { get; }
        public bool IsAlive => true;

        public void SetInt32(ulong address, int value)
        {
            lock (_values) _values[address] = BitConverter.GetBytes(value);
        }

        public IReadOnlyList<MemoryRegion> Regions() =>
            _values.Keys.Select(address => new MemoryRegion(address, 8, true, true)).ToArray();

        public bool TryRead(ulong address, Span<byte> destination)
        {
            lock (_values)
            {
                if (!_values.TryGetValue(address, out var bytes)) return false;
                bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length)).CopyTo(destination);
                return true;
            }
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            lock (_values)
            {
                if (!_values.ContainsKey(address)) return false;
                _values[address] = source.ToArray();
                return true;
            }
        }

        public void Dispose() { }
    }

    /// <summary>在 STA 线程上建窗口、跑测试体、抽送消息循环。</summary>
    private void OnWindow(Func<MainWindow, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                MainWindow.PrepareTestDispatcherForTest();
                window = NewWindow();
                var task = body(window);
                var started = DateTime.UtcNow;
                while (!task.IsCompleted && DateTime.UtcNow - started < TimeSpan.FromSeconds(60))
                {
                    PumpOnce();
                    Thread.Sleep(2);
                }
                if (!task.IsCompleted) throw new TimeoutException("界面测试体 60 秒内没有结束");
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { window?.Close(); } catch (Exception) { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "界面线程超时未结束");
        if (failure is not null) throw new InvalidOperationException("界面测试失败", failure);
    }

    private static T OnUiThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                MainWindow.PrepareTestDispatcherForTest();
                result = body();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "界面线程超时未结束");
        if (failure is not null)
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ut-emptystate-diag.txt"),
                DateTime.Now.ToString("HH:mm:ss") + " " + failure + Environment.NewLine);
            throw new InvalidOperationException("界面测试失败：" + failure, failure);
        }
        return result;
    }

    private static void PumpOnce()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
