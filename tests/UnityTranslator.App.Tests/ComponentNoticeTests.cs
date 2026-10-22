using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

/// <summary>
/// 组件更新的提示文案。
///
/// 这些断言守的是**用户能不能看懂**：只说"请更新"用户不知道为什么；
/// 而"自己改过文件"这种情况必须单独提醒，否则更新会把用户的改动覆盖掉。
/// </summary>
public sealed class ComponentNoticeTests
{
    private static ComponentCheck Needs(UpdateReason reason, string? installed = "0.4") =>
        new(ComponentState.NeedsUpdate, reason, installed, "0.5.1");

    /// <summary>旧清单（没有版本号）也要能给出可读的提示，不能显示成空白或 null。</summary>
    [Fact]
    public void UnknownInstalledVersionIsExplainedInPlainWords()
    {
        var (title, body) = ComponentNotice.UpdateNotice(Needs(UpdateReason.UnknownVersion, installed: null));

        Assert.Equal("组件需要更新", title);
        Assert.Contains("未知", body);
        Assert.Contains("0.5.1", body);          // 当前版本要写出来，用户才知道升到哪
        Assert.DoesNotContain("null", body);
    }

    /// <summary>提示里要写清"已装版本 → 当前版本"，否则用户无法判断该不该更新。</summary>
    [Fact]
    public void BothVersionsAppearInTheBody()
    {
        var (_, body) = ComponentNotice.UpdateNotice(Needs(UpdateReason.OlderVersion));

        Assert.Contains("0.4", body);
        Assert.Contains("0.5.1", body);
    }

    /// <summary>
    /// 文件被改过时必须单独提醒 —— 这是"别把用户自己的改动当成旧版覆盖掉"的唯一防线。
    /// </summary>
    [Fact]
    public void ModifiedFileGetsItsOwnWarning()
    {
        var (_, body) = ComponentNotice.UpdateNotice(
            Needs(UpdateReason.OlderVersion | UpdateReason.FileModifiedSinceInstall));

        Assert.Contains("改动", body);
        Assert.Contains("备份", body);
    }

    /// <summary>没被改过时不该出现"改动/备份"这类吓人的话。</summary>
    [Fact]
    public void UnmodifiedFileDoesNotMentionBackup()
    {
        var (_, body) = ComponentNotice.UpdateNotice(Needs(UpdateReason.OlderVersion));

        Assert.DoesNotContain("备份", body);
    }

    /// <summary>卡片状态文字：需要更新时给一句短提示，其它状态不抢原有文案。</summary>
    [Fact]
    public void StatusTextOnlyAppearsWhenUpdateIsNeeded()
    {
        Assert.Equal("组件需要更新", ComponentNotice.StatusText(Needs(UpdateReason.OlderVersion)));
        Assert.Null(ComponentNotice.StatusText(
            new ComponentCheck(ComponentState.UpToDate, UpdateReason.None, "0.5.1", "0.5.1")));
        Assert.Null(ComponentNotice.StatusText(
            new ComponentCheck(ComponentState.NotInstalled, UpdateReason.None, null, "0.5.1")));
    }

    /// <summary>版本号取自程序集信息，必须是可以比较的纯数字形式（不带 V 前缀）。</summary>
    [Fact]
    public void CurrentVersionHasNoPrefix()
    {
        Assert.DoesNotContain("V", ComponentNotice.CurrentVersion);
        Assert.Matches(@"^\d+(\.\d+)*$", ComponentNotice.CurrentVersion);
    }
}
