namespace UnityTranslator.Core;

/// <summary>游戏里当前装着的翻译组件处于什么状态。</summary>
public enum ComponentState
{
    /// <summary>游戏目录里没有本工具的安装清单 —— 没装过。</summary>
    NotInstalled,

    /// <summary>装的就是当前这一版组件，不用动。</summary>
    UpToDate,

    /// <summary>装的是旧版/内容对不上的组件 —— 应当提示用户更新。</summary>
    NeedsUpdate
}

/// <summary>需要更新的原因。给界面用，让提示能说清"为什么"。可组合。</summary>
[Flags]
public enum UpdateReason
{
    None = 0,

    /// <summary>
    /// 清单里**没有版本号** —— 是加版本号之前的老清单装的，无法证明它是最新的。
    /// 实测（Kurea Struggle）：用户机器上装的正是这种旧组件，界面却什么都不提示，
    /// 只有手动「卸载 → 安装」之后问题才消失。这条就是为那个场景加的。
    /// </summary>
    UnknownVersion = 1,

    /// <summary>清单记的版本比当前软件自带的组件包旧。</summary>
    OlderVersion = 2,

    /// <summary>版本号相同，但**插件文件内容**与当前组件包不一致（例如拿旧的 DLL 覆盖过）。</summary>
    DifferentContent = 4,

    /// <summary>
    /// 插件文件当前内容与清单里记录的哈希不符 —— 像是被用户自己改过。
    /// 这种情况仍然提示更新，但**措辞要区别于"版本旧"**：不能悄悄把用户的改动覆盖掉。
    /// </summary>
    FileModifiedSinceInstall = 8
}

/// <summary>自检结果。</summary>
public sealed record ComponentCheck(
    ComponentState State,
    UpdateReason Reason,
    string? InstalledVersion,
    string CurrentVersion)
{
    public bool NeedsUpdate => State == ComponentState.NeedsUpdate;
    public bool IsInstalled => State != ComponentState.NotInstalled;
}
