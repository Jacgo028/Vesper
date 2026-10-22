namespace UnityTranslator.Core;

public sealed record GameProfile(
    string GameId,
    string RootPath,
    string ExePath,
    string UnityVersion,
    string Backend,
    string Architecture,
    bool IsCompatible,
    string CompatibilityReason)
{
    /// <summary>玩家自定义的卡片名称；空值表示使用程序名，兼容旧游戏列表。</summary>
    public string CustomName { get; init; } = string.Empty;

    /// <summary>游戏卡片标题：用程序名，而不是完整路径。</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName.Trim();
            var name = string.IsNullOrEmpty(ExePath)
                ? System.IO.Path.GetFileName(RootPath)
                : System.IO.Path.GetFileNameWithoutExtension(ExePath);
            return string.IsNullOrWhiteSpace(name) ? "未命名游戏" : name;
        }
    }

    /// <summary>玩家能看懂的支持结论，不出现 Mono / IL2CPP / x64 等词。</summary>
    public string SupportSummary => IsCompatible
        // 手动内存扫描不依赖 Unity 的托管后端，两类游戏都可以使用。
        ? "可以翻译，也可以使用内置数值工具"
        : "这台电脑上暂时装不了";

    /// <summary>这张卡片现在该做什么。</summary>
    public string NextStepHint => IsCompatible
        ? "下一步：点右边的“安装翻译组件”。装好后按钮会变成“启动游戏”。"
        : "原因：" + CompatibilityReason;

    /// <summary>技术原文，折叠在“技术详情”里，方便排查问题时查看。</summary>
    public string TechnicalSummary =>
        $"Unity {UnityVersion} · 脚本后端 {Backend} · 架构 {Architecture}";

    /// <summary>
    /// 这款游戏**实测**该用哪一档加载器（空 = 按 Unity 版本自动判断）。
    ///
    /// ★ 为什么必须按游戏记（第 100 轮实测）：
    ///   实测 cambion（Unity 2022.3.62f2 / Mono / x64）上 BepInEx 6 的预加载器整体崩掉，
    ///   而同版本的 Leticia 用同一套包完全正常 —— 两款游戏的 Unity 版本、Mono 运行时文件、
    ///   BepInEx core **逐字节相同**。**所以光看版本号判断不出来**，只能在实机上发现一次、
    ///   然后把结论记在这个游戏身上，之后它就一直用对的那一档。
    ///
    /// 取值：`""`（自动）/ `"BepInEx5"`（实测 BepInEx 6 起不来，强制用 BepInEx 5）。
    /// 用字符串而不是枚举：旧 `games.json`（没有这个字段）读出来就是 `""`，
    /// **不需要任何迁移**，也不会因为反序列化失败而丢掉整份游戏列表。
    /// </summary>
    public string PreferredLoader { get; init; } = string.Empty;

    /// <summary>是否已被实测标记为"BepInEx 6 在这款游戏上起不来"。</summary>
    public bool PrefersBepInEx5 =>
        string.Equals(PreferredLoader, BepInEx5Loader, System.StringComparison.Ordinal);

    /// <summary>
    /// `PreferredLoader` 里唯一有含义的取值。抽成常量是因为它现在写在**三个**地方
    /// （App 的写入方、App 的读取方、这里的判断方），散落的字面量只要有一处写错，
    /// 表现就是"回退没生效"或"回退完又自己换回去"，而且全程不报错。
    /// </summary>
    public const string BepInEx5Loader = "BepInEx5";
}
