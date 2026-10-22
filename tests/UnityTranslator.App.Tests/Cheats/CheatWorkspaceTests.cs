using System.Windows;
using System.Windows.Controls;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

/// <summary>
/// 工作台测试：原有页面与翻译服务页、无翻译密钥也能启动作弊、进程退出后禁止写入、
/// 切换游戏清空结果，以及 Unity 字段候选必须先选中才能修改。
///
/// 这里使用真实进程内存（测试进程自己）驱动扫描与写入，避免启动 Unity 游戏。
/// </summary>
[Collection(WpfUiCollection.Name)]
public sealed class CheatWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-workspace-tests", Guid.NewGuid().ToString("N"));

    private GameProfile TestGame() => new GameProfile("game-" + Guid.NewGuid().ToString("N")[..8],
        Path.Combine(_root, "game"), Path.Combine(_root, "game", "Game.exe"), "6000.0.58f2", "Mono", "x64", true, string.Empty);

    [Fact]
    public void WorkspacePreservesExistingPageOrderAndAddsTranslationServices()
    {
        var titles = OnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            try { return window.WorkspacePageTitles; }
            finally { window.Close(); }
        });

        Assert.Equal(new[] { "游戏翻译", "数值修改", "游戏数值推荐", "翻译服务" }, titles);
    }

    [Fact]
    public void CheatControlsNeedNoTranslationServiceOrApiKey()
    {
        var state = OnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            try
            {
                // 完全没有填写接口地址与密钥。
                window.EndpointBox.Text = string.Empty;
                window.ApiKeyBox.Password = string.Empty;
                return (window.CanStartCheatsWithoutTranslation,
                        ScanEnabledBeforeSession: window.ScanStartButton.IsEnabled,
                        window.CanScan);
            }
            finally { window.Close(); }
        });

        Assert.True(state.CanStartCheatsWithoutTranslation);
        // 还没有游戏会话时不能扫描；但原因与翻译服务无关。
        Assert.False(state.ScanEnabledBeforeSession);
        Assert.False(state.CanScan);
    }

    [Fact]
    public void ScanWriteAndUndoWorkThroughRealProcessMemory()
    {
        var target = new MutableInt32(4242);
        const ulong address = 0x1000;

        var result = OnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            try
            {
                using var memory = new SingleAddressMemory(address, () => target.Value, v => target.Value = v);
                window.StartCheatSessionForTest(TestGame(), memory);
                Assert.True(window.HasGameSession);
                Assert.Equal(4242, target.Value);

                var session = window.CheatSessionForTest()!;
                var hit = new ScanHit(address, NumericKind.Int32, BitConverter.GetBytes(4242));
                var write = session.TryWrite(hit, BitConverter.GetBytes(4242), BitConverter.GetBytes(9999));
                Assert.Equal(WriteStatus.Ok, write.Status);
                Assert.Equal(9999, target.Value);

                var undo = session.UndoLast();
                Assert.Equal(WriteStatus.Ok, undo.Status);
                Assert.Equal(4242, target.Value);
                return true;
            }
            finally { window.Close(); }
        });

        Assert.True(result);
    }

    /// <summary>
    /// 进程退出分两步，两步都要验证：
    /// 1. 退出瞬间**不能立刻清空**：Unity 游戏常会重启自己，这一段是“等新进程”的窗口期，
    ///    游戏选择与结果都还在（否则玩家看到的就是“软件说没启动、游戏其实在跑”）；
    /// 2. 确认没有新进程之后才真正收尾：会话、结果、锁定全部清空，不能再写入。
    /// </summary>
    [Fact]
    public void ProcessExitWaitsForATakeOverBeforeClearingEverything()
    {
        var target = new MutableInt32(7);
        const ulong address = 0x2000;
        var state = OnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            try
            {
                using var memory = new SingleAddressMemory(address, () => target.Value, v => target.Value = v);
                var game = TestGame();
                window.StartCheatSessionForTest(game, memory);
                var setupGame = window.ActiveGameForTest()?.GameId;
                var setupSession = window.HasGameSession;
                var setupHits = window.HitCountForTest();
                var session = window.CheatSessionForTest()!;
                var hit = new ScanHit(address, NumericKind.Int32, BitConverter.GetBytes(7));
                window.SeedHitForTest(hit);
                window.SelectFirstHitForTest();

                var before = (window.CanWriteCheatValue, window.CanFreezeCheatValue, window.HitCountForTest(),
                    setupGame, setupSession, setupHits);

                // 退出瞬间：进入等新进程的窗口期，结果与游戏选择都还在，但已经不能再写。
                window.NotifyGameExitedForTest();
                var waiting = (Waiting: window.HasTakeOverWaitForTest(),
                    window.HasGameSession, window.CanWriteCheatValue,
                    Game: window.ActiveGameForTest()?.GameId, Hits: window.HitCountForTest(),
                    Kind: window.EmptyResultKindForTest());

                // 进程退出后玩家再点“修改”必须被挡住，数值不许动。
                // 这里刻意走真实的界面入口（WriteSelectedForTest），而不是直接调 CheatSession：
                // 玩家能碰到的就是按钮，能被挡住才有意义。
                window.WriteSelectedForTest();
                var blocked = (Status: window.CheatStatusTextForTest() + " " + window.CheatAdviceForTest(),
                    Value: target.Value, Hits: window.HitCountForTest());

                // 等待期结束：这才真正清空。
                window.FinishTakeOverWaitForTest();
                var after = (window.CanWriteCheatValue, window.CanFreezeCheatValue, window.HitCountForTest(),
                    window.HasGameSession, window.CanScan, window.HasTakeOverWaitForTest(),
                    window.EmptyResultKindForTest());
                return (before, waiting, blocked, after, Value: target.Value, game.GameId);
            }
            finally { window.Close(); }
        });

        Assert.Equal((true, true, 1, state.GameId, true, 0), state.before);
        // 等待接管期间：会话对象还在（游戏选择保留），但内存结果必须已经清掉
        // （地址随进程失效，留着只会让界面自相矛盾），并且不能再写、不能开新查找。
        Assert.True(state.waiting.Waiting, "退出后应该进入等待接管的窗口期");
        Assert.True(state.waiting.HasGameSession);
        Assert.False(state.waiting.CanWriteCheatValue);
        Assert.Equal(state.GameId, state.waiting.Game);
        Assert.Equal(0, state.waiting.Hits);
        // 进程没了的时候，结果区必须说“会话已结束”，不能还说“请先做首次查找”。
        Assert.Equal(MainWindow.ScanResultEmptyKind.SessionEnded, state.waiting.Kind);
        // 想改也改不动：数值保持原样，并且给出明确原因。
        Assert.Equal(7, state.blocked.Value);
        Assert.Equal(0, state.blocked.Hits);
        Assert.Contains("退出", state.blocked.Status, StringComparison.Ordinal);
        // 等待期结束：全部清空，并且空状态仍然是“会话已结束”。
        Assert.Equal((false, false, 0, false, false, false, MainWindow.ScanResultEmptyKind.SessionEnded), state.after);
        Assert.Equal(7, state.Value);
    }

    [Fact]
    public void UnityFieldCandidatesRankAndRequireSelectionBeforeWriting()
    {
        var ranking = OnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            try
            {
                window.ScanValueBox.Text = "50";
                window.ApplyFieldReplyForTest(new CheatFieldReply(new[]
                {
                    new FieldSnapshot("t1", "Main/Player", "Stats", "gold", NumericKind.Int32, "50"),
                    new FieldSnapshot("t2", "Main/Player", "Stats", "frameCounter", NumericKind.Int32, "50")
                }, false, 5));

                var goldRank = window.UnityFields.First(item => item.Candidate.FieldName == "gold");
                var frameRank = window.UnityFields.First(item => item.Candidate.FieldName == "frameCounter");
                return (Ranked: window.RankedFieldsForTest().ToArray(),
                        GoldScore: window.ScoreOfForTest(goldRank.Candidate),
                        FrameScore: window.ScoreOfForTest(frameRank.Candidate),
                        Count: window.UnityFields.Count);
            }
            finally { window.Close(); }
        });

        Assert.Equal(2, ranking.Count);
        // 命中字段名并且当前值与界面输入一致，分数必然高于无关字段。
        Assert.True(ranking.GoldScore > ranking.FrameScore,
            $"gold={ranking.GoldScore} frameCounter={ranking.FrameScore}");
        Assert.Equal("gold", ranking.Ranked[0].Field.FieldName);
        Assert.Contains(ranking.Ranked[0].Ranked.Reasons, reason => reason.Contains("gold", StringComparison.Ordinal));
        // 界面上给出的是可读的推荐程度，而不是内部分数。
        Assert.Equal("很像", ranking.Ranked[0].MatchLabel);
        Assert.Contains("gold", ranking.Ranked[0].ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnityWriteIsRefusedWithoutAGameSession()
    {
        var enabled = OnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            try
            {
                window.ApplyFieldReplyForTest(new CheatFieldReply(new[]
                {
                    new FieldSnapshot("t1", "Main/Player", "Stats", "gold", NumericKind.Int32, "50")
                }, false, 1));
                return (Write: window.UnityWriteButton.IsEnabled, Query: window.UnityQueryButton.IsEnabled);
            }
            finally { window.Close(); }
        });

        // 没有游戏会话：不能查询，也不能写入。
        Assert.False(enabled.Query);
        Assert.False(enabled.Write);
    }

    [Fact]
    public void MemoryPresetIsSavedAsSearchConditionNotAddress()
    {
        var appData = Path.Combine(_root, "appdata");
        var presets = OnUiThread(() =>
        {
            var window = new MainWindow(appData, new RecordingExternalFactory { Available = false });
            try
            {
                var game = TestGame();
                using var memory = new SingleAddressMemory(0, () => 0, _ => { });
                window.StartCheatSessionForTest(game, memory);
                // 内存快捷项保存第 2 步“游戏现在显示的新数值”输入框里的搜索条件。
                window.ScanNewValueBox.Text = "8888";
                window.PresetNameBox.Text = "金钱扫描";
                // 内存快捷项保存的是搜索条件，因此必须先选中一条扫描结果。
                window.SeedHitForTest(new ScanHit(0x3000, NumericKind.Int32, BitConverter.GetBytes(8888)));
                window.SelectFirstHitForTest();
                window.SaveMemoryPresetForTest();
                return window.CheatPresets.ToArray();
            }
            finally { window.Close(); }
        });

        var saved = Assert.Single(presets);
        Assert.Equal(CheatPresetSource.MemorySearch, saved.Source);
        // 保存的是搜索条件（界面输入的数值），不是地址。
        Assert.Equal("8888", saved.Note);

        // 预设文件必须落在注入的数据目录里：既覆盖真实路径，也保证测试不写用户数据。
        var file = Path.Combine(appData, "cheat-presets.json");
        Assert.True(File.Exists(file), "预设文件没有写进注入的数据目录：" + file);
        var text = File.ReadAllText(file);
        Assert.Contains("8888", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0x3000", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Address", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>WPF 控件必须在 STA 线程上创建。</summary>
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
        if (failure is not null) throw new InvalidOperationException("界面测试失败", failure);
        return result;
    }

    private sealed class MutableInt32
    {
        public MutableInt32(int value) => Value = value;
        public int Value;
    }

    /// <summary>
    /// 只暴露一个地址的进程内存替身。仍使用真实的进程身份（当前进程），
    /// 因此 CheatSession 的进程校验、写前核对与撤销路径都被真实执行。
    /// </summary>
    private sealed class SingleAddressMemory : IProcessMemory, IDisposable
    {
        private readonly ulong _address;
        private readonly Func<int> _read;
        private readonly Action<int> _write;

        public SingleAddressMemory(ulong address, Func<int> read, Action<int> write)
        {
            _address = address;
            _read = read;
            _write = write;
            ProcessId = Environment.ProcessId;
            StartedUtcTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        }

        public int ProcessId { get; }
        public long StartedUtcTicks { get; }
        public bool IsAlive => true;

        public IReadOnlyList<MemoryRegion> Regions() =>
            new[] { new MemoryRegion(_address, 4, true, true) };

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address != _address || destination.Length < 4) return false;
            BitConverter.GetBytes(_read()).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address != _address || source.Length < 4) return false;
            _write(BitConverter.ToInt32(source));
            return true;
        }

        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
