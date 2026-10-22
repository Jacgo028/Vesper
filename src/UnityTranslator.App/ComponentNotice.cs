using UnityTranslator.Core;

namespace UnityTranslator.App;

/// <summary>
/// 把组件自检结果翻译成给用户看的话。
///
/// 单独拆出来是为了能进单元测试：判断逻辑（<see cref="InstallPlanner.Check"/>）与
/// 措辞规则都不该埋在界面代码里 —— 那样每次改一句话都得开一次真机。
/// </summary>
internal static class ComponentNotice
{
    /// <summary>当前软件自带的组件版本（与 Version.props 的 AppVersion 同源）。</summary>
    public static string CurrentVersion => AppVersion.Display.TrimStart('V', 'v');

    /// <summary>游戏一旦装了组件，卡片上该显示的状态文字；没装时返回 null（由原来的提示负责）。</summary>
    public static string? StatusText(ComponentCheck check) => check.State switch
    {
        ComponentState.NeedsUpdate => "组件需要更新",
        _ => null
    };

    /// <summary>
    /// 需要更新时的完整提示（标题、正文）。
    ///
    /// 措辞要点：
    ///   * **说清楚为什么**要更新 —— 只说"请更新"用户不知道为什么；
    ///   * 旧版组件会导致"翻译有毛病但看不出原因"，这句是用户真实遇到过的，写出来最有说服力；
    ///   * 如果这个文件在装完之后被改过，要**单独提醒**，不要说得像是纯版本旧。
    /// </summary>
    public static (string Title, string Body) UpdateNotice(ComponentCheck check)
    {
        var version = string.IsNullOrWhiteSpace(check.InstalledVersion)
            ? "未知（是很早以前装的组件，安装记录里没有版本号）"
            : check.InstalledVersion!;

        var body = "这款游戏里装的翻译组件不是当前版本：\n\n"
            + $"　已装版本：{version}\n"
            + $"　当前版本：{check.CurrentVersion}\n\n";

        if (check.Reason.HasFlag(UpdateReason.FileModifiedSinceInstall))
            body += "另外，组件文件在装好之后被改动过（和安装记录对不上）。\n"
                + "如果你自己改过这个文件，请先备份，再决定要不要更新。\n\n";

        body += "旧版组件可能出现「翻译有问题但看不出原因」的情况。\n"
            + "选择「一键更新并启动」会先更新并检查组件，成功后才启动游戏；取消则不会启动。";

        return ("组件需要更新", body);
    }
}
