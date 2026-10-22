using System.Windows;
using System.Windows.Controls;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

/// <summary>
/// 界面文案与可用性测试。
///
/// 这些测试直接读取界面上真正显示的文字，防止以后又把 Int32 / Exact / API 之类的
/// 技术词放回主要操作路径，也防止在文案里把“候选”说成“保证正确”。
/// </summary>
[Collection(WpfUiCollection.Name)]
public sealed class UiWordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-wording-tests", Guid.NewGuid().ToString("N"));

    /// <summary>主要操作路径上不应该出现的英文技术词。</summary>
    private static readonly string[] Jargon =
    [
        "Int32", "Int64", "Float32", "Float64", "Exact", "Increased", "Decreased",
        "Unchanged", "Changed", "Backend", "SessionId", "Session ID", "Mono", "x64"
    ];

    private MainWindow NewWindow() => new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
    /// <summary>三页上会显示给玩家的全部文字。</summary>
    private static string PageText(MainWindow window) => window.VisibleTextDumpForTest();
    [Fact]
    public void ScanTypeOptionsUsePlainChinese()
    {
        var kinds = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                return window.ScanKindBox.Items.OfType<ComboBoxItem>()
                    .Select(item => item.Content?.ToString() ?? string.Empty).ToArray();
            }
            finally { window.Close(); }
        });

        // 类型：解释成整数/小数，而不是直接甩出引擎类型名。
        Assert.Equal(4, kinds.Length);
        Assert.Contains("整数", kinds[0], StringComparison.Ordinal);
        Assert.Contains("小数", kinds[2], StringComparison.Ordinal);
        Assert.All(kinds, option => Assert.DoesNotContain("Int32", option, StringComparison.Ordinal));
        Assert.All(kinds, option => Assert.DoesNotContain("Float", option, StringComparison.Ordinal));
    }

    [Fact]
    public void KindOptionOrderMatchesTheMappingUsedByTheScan()
    {
        var kinds = new[]
        {
            (Index: 0, Expected: NumericKind.Int32),
            (Index: 1, Expected: NumericKind.Int64),
            (Index: 2, Expected: NumericKind.Float32),
            (Index: 3, Expected: NumericKind.Float64)
        };
        foreach (var item in kinds)
            Assert.Equal(item.Expected, UiText.KindAt(item.Index));
    }

    /// <summary>
    /// 主流程不再让玩家选“变大了 / 变小了”。界面上不能出现筛选下拉框，
    /// 页面文案里也不能再出现这些说法，否则等于把判断变化方向的负担又推回给玩家。
    /// </summary>
    [Fact]
    public void RefineDefaultsToExactAndOffersPlainChineseComparisons()
    {
        var dump = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.WorkspaceTabs.SelectedIndex = 1;
                return (Text: PageText(window),
                        HasRefineBox: window.ScanNewValueBox is not null,
                        WriteBox: window.ScanWriteValueBox is not null);
            }
            finally { window.Close(); }
        });

        Assert.True(dump.HasRefineBox);
        Assert.True(dump.WriteBox);
        foreach (var direction in new[] { "精确等于新数值", "比上次增加", "比上次减少", "与上次不同", "与上次相同" })
            Assert.Contains(direction, dump.Text, StringComparison.Ordinal);
        // 取而代之的是清楚标注的新数值输入框。
        Assert.Contains("游戏现在显示的新数值", dump.Text, StringComparison.Ordinal);
        Assert.Contains("再次查找", dump.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTechnicalJargonOnTheMainPages()
    {
        var dump = OnUiThread(() =>
        {
            var window = NewWindow();
            try { return PageText(window); }
            finally { window.Close(); }
        });

        Assert.NotEmpty(dump);
        foreach (var term in Jargon)
        {
            var offender = dump
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Contains(term, StringComparison.OrdinalIgnoreCase));
            Assert.True(offender is null, $"主要路径上出现了技术词“{term}”：{offender}");
        }
    }

    [Fact]
    public void TranslationKeyIsExplainedWhereItIsNeeded()
    {
        var joined = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.ApiSettingsButton.IsChecked = true;
                return PageText(window);
            }
            finally { window.Close(); }
        });
        // 中文说法，并且说明“什么时候需要”。
        Assert.Contains("翻译服务密钥", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("API 密钥", joined, StringComparison.Ordinal);
        Assert.Contains("你需要自己的翻译服务密钥", joined, StringComparison.Ordinal);
        // 联网会把文字发出去，必须明确告知。
        Assert.Contains("会发送到本页填写的翻译服务", joined, StringComparison.Ordinal);
        Assert.Contains("关闭后不发送联网翻译请求", joined, StringComparison.Ordinal);
        // 关闭翻译仍可用数值工具。
        Assert.Contains("数值工具不受影响", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void HonestCaveatsArePresent()
    {
        var joined = OnUiThread(() =>
        {
            var window = NewWindow();
            try { return PageText(window); }
            finally { window.Close(); }
        });

        // 自动推荐只是候选，不能暗示保证正确。
        Assert.Contains("推荐只是", joined, StringComparison.Ordinal);
        Assert.Contains("不代表它一定就是你想要的那个数值", joined, StringComparison.Ordinal);
        // IL2CPP 当前不支持，必须写清楚。
        Assert.Contains("IL2CPP 打包的游戏暂时读不到游戏内数值", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryAddressCaveatAppearsAfterProcessExit()
    {
        var status = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.NotifyGameExitedForTest();
                return window.CheatStatusTextForTest();
            }
            finally { window.Close(); }
        });

        Assert.Contains("游戏已退出", status, StringComparison.Ordinal);
        Assert.Contains("重新查找", status, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStatesTellThePlayerWhatToDoNext()
    {
        var state = OnUiThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.WorkspaceTabs.SelectedIndex = 1;
                window.UpdateLayout();
                var scanVisible = window.ScanEmptyState.Visibility == Visibility.Visible;
                var scanAdvice = window.ScanEmptyAdvice.Text;
                window.WorkspaceTabs.SelectedIndex = 2;
                window.UpdateLayout();
                return (scanVisible, scanAdvice,
                        UnityVisible: window.UnityEmptyState.Visibility == Visibility.Visible,
                        UnityAdvice: window.UnityEmptyAdvice.Text,
                        ResultHint: window.ScanResultEmptyHint.Text);
            }
            finally { window.Close(); }
        });

        Assert.True(state.scanVisible);
        Assert.Contains("启动游戏", state.scanAdvice, StringComparison.Ordinal);
        Assert.True(state.UnityVisible);
        Assert.Contains("启动游戏", state.UnityAdvice, StringComparison.Ordinal);
        Assert.Contains("首次查找", state.ResultHint, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorsLeadWithWhatHappenedAndKeepTechnicalDetail()
    {
        var permission = UiText.Explain(new UnauthorizedAccessException("Access to the path 'x' is denied."), "安装");
        Assert.Contains("安装失败", permission.Message, StringComparison.Ordinal);
        Assert.Contains("没有权限", permission.Message, StringComparison.Ordinal);
        Assert.Contains("管理员", permission.Advice, StringComparison.Ordinal);
        // 原始异常信息保留在技术详情里，方便排查。
        Assert.Contains("Access to the path", permission.Technical, StringComparison.Ordinal);
        Assert.Contains("UnauthorizedAccessException", permission.Technical, StringComparison.Ordinal);

        var io = UiText.Explain(new IOException("The process cannot access the file because it is being used by another process."), "安装");
        Assert.Contains("占用", io.Message, StringComparison.Ordinal);
        Assert.Contains("关掉游戏", io.Advice, StringComparison.Ordinal);

        var generic = UiText.Explain(new Exception("boom"), "保存");
        Assert.Contains("没有预料到", generic.Message, StringComparison.Ordinal);
        Assert.Contains("技术详情", generic.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void GameCardsLeadWithSupportAndNextStep()
    {
        var compatible = new GameProfile("g1", @"C:\games\Kurea", @"C:\games\Kurea\Kurea Struggle.exe",
            "6000.0.68f1", "Mono", "x64", true, string.Empty);
        // 手动数值工具不依赖托管后端，IL2CPP 也可以使用。
        var il2cpp = new GameProfile("g2", @"C:\games\Other", @"C:\games\Other\Other.exe",
            "2022.3.62f2", "IL2CPP", "x64", true, string.Empty);

        Assert.Equal("Kurea Struggle", compatible.DisplayName);
        Assert.Contains("可以翻译", compatible.SupportSummary, StringComparison.Ordinal);
        Assert.Contains("安装翻译组件", compatible.NextStepHint, StringComparison.Ordinal);
        // 两种后端的卡片都应说明翻译与内置数值工具可用。
        Assert.Contains("可以翻译", il2cpp.SupportSummary, StringComparison.Ordinal);
        Assert.Contains("内置数值工具", il2cpp.SupportSummary, StringComparison.Ordinal);

        // 技术资料只出现在技术详情里。
        Assert.Contains("Mono", compatible.TechnicalSummary, StringComparison.Ordinal);
        Assert.Contains("x64", compatible.TechnicalSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendedCandidatesDoNotClaimCertainty()
    {
        var field = new FieldCandidate("Main/Player/Stats", "PlayerStats", "gold", NumericKind.Int32, "1280");
        var view = new RankedCandidateView(CandidateRanker.Rank(field, "1280"), "tok", 0);

        // 不给玩家看裸分数，而是给可解释的推荐程度。
        Assert.Equal("很像", view.MatchLabel);
        Assert.DoesNotContain("100", view.MatchLabel, StringComparison.Ordinal);
        Assert.Contains("gold", view.ReasonSummary, StringComparison.Ordinal);
        Assert.Contains("当前数值和你输入的数值一致", view.ReasonSummary, StringComparison.Ordinal);
        // 技术详情里保留原始评分与字段路径。
        Assert.Contains("内部评分", view.Technical, StringComparison.Ordinal);
        Assert.Contains("Int32", view.Technical, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmatchedCandidateIsLabelledAsMerelyListed()
    {
        var field = new FieldCandidate("Main/Player/Stats", "PlayerStats", "frameCounter", NumericKind.Int32, "50");
        var view = new RankedCandidateView(CandidateRanker.Rank(field, "9999"), "tok", 1);

        Assert.Equal("仅列出", view.MatchLabel);
        Assert.Contains("只是把它列出来", view.ReasonSummary, StringComparison.Ordinal);
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
