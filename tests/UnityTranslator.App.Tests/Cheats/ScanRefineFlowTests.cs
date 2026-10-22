using System.Windows;
using System.Windows.Threading;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

/// <summary>
/// 数值查找流程的回归测试。
///
/// 玩家要走的路径是：输入当前数值 → 首次查找 → 回游戏让数值改变 → 填上新的数值 → 再次查找。
/// 这里验证“再次查找”真的按新数值做了精确筛选，而不是只换了按钮文案；
/// 同时验证整个流程不需要玩家选择“变大了 / 变小了”这类变化方向。
///
/// 线程约定（踩过坑，写在这里避免以后又踩）：
/// 窗口和它的 async 事件处理器都绑定在创建它的 STA 线程上。测试体必须
/// **在同一个 STA 线程上跑完并持续抽送消息循环**，await 的结果才会回到这个线程。
/// 在 STA 线程上 GetResult() 阻塞会把续体堵死（实测 90 秒超时）。
/// </summary>
[Collection(WpfUiCollection.Name)]
public sealed class ScanRefineFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-scanflow-tests", Guid.NewGuid().ToString("N"));

    private static GameProfile TestGame() => new GameProfile("game-flow", Path.Combine(Path.GetTempPath(), "g"),
        Path.Combine(Path.GetTempPath(), "g", "g.exe"), "6000.0.68f1", "Mono", "x64", true, string.Empty);

    /// <summary>
    /// 受控进程内存替身。
    ///
    /// 关键点：“内存里当前的值”和“上次记录在候选表里的值”是两回事。
    /// MemoryScanner.RefineAsync 会重新读取内存再和玩家输入的新数值比较，
    /// 所以测试必须能表达“内存里的值已经变了，候选表里还是上一次的快照”。
    /// </summary>
    private sealed class FakeMemory : IProcessMemory, IDisposable
    {
        private readonly Dictionary<ulong, byte[]> _values = new();
        private readonly object _gate = new();

        public FakeMemory(params (ulong Address, int Value)[] initial)
        {
            foreach (var item in initial) SetInt32(item.Address, item.Value);
            ProcessId = Environment.ProcessId;
            StartedUtcTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        }

        public int ProcessId { get; }
        public long StartedUtcTicks { get; }
        public bool IsAlive => true;

        /// <summary>模拟游戏把某个地址上的数值改成了新值。</summary>
        public void SetInt32(ulong address, int value)
        {
            lock (_gate) _values[address] = BitConverter.GetBytes(value);
        }

        public void SetFloat(ulong address, float value)
        {
            lock (_gate) _values[address] = BitConverter.GetBytes(value);
        }

        public IReadOnlyList<MemoryRegion> Regions() =>
            _values.Keys.Select(address => new MemoryRegion(address, 8, true, true)).ToArray();

        public bool TryRead(ulong address, Span<byte> destination)
        {
            lock (_gate)
            {
                if (!_values.TryGetValue(address, out var bytes)) return false;
                bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length)).CopyTo(destination);
                return true;
            }
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (!_values.ContainsKey(address)) return false;
                var copy = source.ToArray();
                _values[address] = copy;
                return true;
            }
        }

        public void Dispose() { }
    }
    /// <summary>
    /// 在一个新的 STA 线程上创建窗口，运行 <paramref name="body"/>（可以是 async），
    /// 期间持续抽送消息循环，结束后关闭窗口。
    /// </summary>
    private void OnWindow(Func<MainWindow, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                MainWindow.PrepareTestDispatcherForTest();
                window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
                var task = body(window);
                // 抽送消息循环直到测试体结束：续体是被 Post 回这个线程的，
                // 不抽送就永远不会执行。
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
        if (failure is not null)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ut-scanflow-diag.txt"),
                failure.ToString(), new System.Text.UTF8Encoding(false));
            throw new InvalidOperationException("界面测试失败", failure);
        }
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // ==================== 核心回归 ====================

    /// <summary>
    /// 核心回归：首次以一个数值查找，模拟游戏数值改变，输入新数值后再次查找，
    /// 结果只应保留等于新数值的候选，且全程不需要选择变化方向。
    /// </summary>
    [Fact]
    public void RefineKeepsOnlyCandidatesEqualToTheNewValue()
    {
        string[] first = [], second = [];
        var status = string.Empty;

        var memory = new FakeMemory((0x1000, 130), (0x2000, 500), (0x3000, 999));
        OnWindow(async window =>
        {
            window.StartCheatSessionForTest(TestGame(), memory);

            // 首次查找的等价场景：三个候选，值分别是 130 / 500 / 999。
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
            window.SeedHitForTest(new ScanHit(0x2000, NumericKind.Int32, BitConverter.GetBytes(500)));
            window.SeedHitForTest(new ScanHit(0x3000, NumericKind.Int32, BitConverter.GetBytes(999)));
            first = window.HitValuesForTest().ToArray();

            // 玩家在游戏里把 0x1000 上的数值从 130 改成了 500（和 0x2000 撞值）。
            memory.SetInt32(0x1000, 500);

            // 回软件，把游戏现在显示的 500 填进去再查找。
            await window.RefineWithValueForTest("500");
            second = window.HitValuesForTest().ToArray();
            status = window.CheatStatusTextForTest() + " " + window.CheatAdviceForTest();
        });

        Assert.Equal(new[] { "130", "500", "999" }, first);
        // 0x1000 从 130 变成 500 后，两个地址都是 500：只保留这两项，
        // 999 那一项被筛掉。这证明筛选是按新数值做精确比较，而不是原样返回。
        Assert.Equal(new[] { "500", "500" }, second);
        Assert.Contains("500", status, StringComparison.Ordinal);
    }

    /// <summary>可以反复“再次查找”，结果逐步收敛。</summary>
    [Fact]
    public void RefineCanBeRepeatedAndNarrowsTheCandidates()
    {
        var start = 0;
        string[] afterFirst = [], afterSecond = [];

        var memory = new FakeMemory((0x1000, 7), (0x2000, 7), (0x3000, 7));
        OnWindow(async window =>
        {
            window.StartCheatSessionForTest(TestGame(), memory);
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(7)));
            window.SeedHitForTest(new ScanHit(0x2000, NumericKind.Int32, BitConverter.GetBytes(7)));
            window.SeedHitForTest(new ScanHit(0x3000, NumericKind.Int32, BitConverter.GetBytes(7)));
            start = window.HitCountForTest();

            // 三个候选里有两个变成了 9，第三个没动。
            memory.SetInt32(0x1000, 9);
            memory.SetInt32(0x2000, 9);
            await window.RefineWithValueForTest("9");
            afterFirst = window.HitValuesForTest().ToArray();

            // 剩下的继续变化，再筛一次。候选值会被更新成新值，所以可以反复筛选。
            memory.SetInt32(0x1000, 11);
            await window.RefineWithValueForTest("11");
            afterSecond = window.HitValuesForTest().ToArray();
        });

        Assert.Equal(3, start);
        Assert.Equal(2, afterFirst.Length);
        Assert.Single(afterSecond);
    }

    /// <summary>当新数值真的命中某个候选时，只保留命中的那一个。</summary>
    [Fact]
    public void RefineKeepsTheMatchingCandidateOnly()
    {
        string[] values = [];
        var status = string.Empty;
        var summary = string.Empty;

        var memory = new FakeMemory((0x1000, 130), (0x2000, 500), (0x3000, 999));
        OnWindow(async window =>
        {
            window.StartCheatSessionForTest(TestGame(), memory);
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
            window.SeedHitForTest(new ScanHit(0x2000, NumericKind.Int32, BitConverter.GetBytes(500)));
            window.SeedHitForTest(new ScanHit(0x3000, NumericKind.Int32, BitConverter.GetBytes(999)));

            await window.RefineWithValueForTest("500");
            values = window.HitValuesForTest().ToArray();
            summary = window.ResultSummaryForTest();
            status = window.CheatStatusTextForTest();
        });

        Assert.Equal(new[] { "500" }, values);
        Assert.Contains("1", summary, StringComparison.Ordinal);
        Assert.Contains("只剩一项", status, StringComparison.Ordinal);
    }

    /// <summary>类型沿用首次查找的选择：小数候选要用小数筛选。</summary>
    [Fact]
    public void RefineUsesTheSameNumericKindAsTheFirstScan()
    {
        string[] matched = [], afterInvalid = [];
        var status = string.Empty;

        var memory = new FakeMemory();
        memory.SetFloat(0x1000, 12.5f);
        memory.SetFloat(0x2000, 31.25f);
        OnWindow(async window =>
        {
            window.StartCheatSessionForTest(TestGame(), memory);
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Float32, BitConverter.GetBytes(12.5f)));
            window.SeedHitForTest(new ScanHit(0x2000, NumericKind.Float32, BitConverter.GetBytes(31.25f)));

            await window.RefineWithValueForTest("31.25");
            matched = window.HitValuesForTest().ToArray();

            // 类型仍按小数处理：非数字文本必须被拒绝，而不是被当成整数。
            await window.RefineWithValueForTest("abc");
            afterInvalid = window.HitValuesForTest().ToArray();
            status = window.CheatStatusTextForTest();
        });

        Assert.Equal(new[] { "31.25" }, matched);
        Assert.Equal(new[] { "31.25" }, afterInvalid);
        Assert.Contains("小数", status, StringComparison.Ordinal);
    }

    /// <summary>新数值不是合法数字时必须给出清楚的提示，并且不动已有结果。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public void InvalidNewValueIsRejectedWithoutTouchingTheResults(string invalid)
    {
        string[] values = [];
        var status = string.Empty;
        var advice = string.Empty;

        var memory = new FakeMemory((0x1000, 130), (0x2000, 500));
        OnWindow(async window =>
        {
            window.StartCheatSessionForTest(TestGame(), memory);
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
            window.SeedHitForTest(new ScanHit(0x2000, NumericKind.Int32, BitConverter.GetBytes(500)));

            await window.RefineWithValueForTest(invalid);
            values = window.HitValuesForTest().ToArray();
            status = window.CheatStatusTextForTest();
            advice = window.CheatAdviceForTest();
        });

        // 结果保持原样，并且提示里说明要填什么。
        Assert.Equal(new[] { "130", "500" }, values);
        Assert.Contains("不是有效", status, StringComparison.Ordinal);
        Assert.Contains("游戏现在显示的新数值", advice, StringComparison.Ordinal);
    }

    /// <summary>没有候选时“再次查找”不可用，避免玩家以为程序没反应。</summary>
    [Fact]
    public void RefineIsDisabledWithoutCandidates()
    {
        var before = false;
        var noHits = false;
        var withHits = false;

        OnWindow(window =>
        {
            before = window.ScanRefineButton.IsEnabled;
            window.StartCheatSessionForTest(TestGame(), new FakeMemory((0x1000, 130)));
            noHits = window.ScanRefineButton.IsEnabled;
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(130)));
            window.SelectFirstHitForTest();
            withHits = window.ScanRefineButton.IsEnabled;
            return Task.CompletedTask;
        });

        Assert.False(before);
        Assert.False(noHits);
        Assert.True(withHits);
    }

    /// <summary>
    /// 改完之后仍能修改数值：写入用第 3 步的“改成”框，而且第 2 步的新数值框不会影响它。
    /// </summary>
    [Fact]
    public void WriteStillUsesItsOwnInputBox()
    {
        // 注意：内存里的当前值必须和候选表里记录的值一致，否则 CheatSession
        // 的“写前核对”会（正确地）拒绝写入。这里两边都是 130。
        const int original = 130;
        var memory = new FakeMemory((0x1000, original));
        var afterSelect = string.Empty;
        var afterTypingRefine = string.Empty;
        var written = 0;

        OnWindow(window =>
        {
            window.StartCheatSessionForTest(TestGame(), memory);
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(original)));
            window.SelectFirstHitForTest();

            afterSelect = window.ScanWriteValueBox.Text;
            // 改第 2 步的输入框不应该波及第 3 步的输入框。
            window.ScanNewValueBox.Text = "175";
            afterTypingRefine = window.ScanWriteValueBox.Text;

            window.ScanWriteValueBox.Text = "777";
            window.WriteSelectedForTest();

            // 从内存里读回写入结果：证明修改真的落到了目标地址。
            var readback = new byte[4];
            memory.TryRead(0x1000, readback);
            written = BitConverter.ToInt32(readback, 0);
            return Task.CompletedTask;
        });

        Assert.Equal("130", afterSelect);
        Assert.Equal("130", afterTypingRefine);
        Assert.Equal(777, written);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
