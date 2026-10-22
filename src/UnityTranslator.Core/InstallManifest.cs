namespace UnityTranslator.Core;

public sealed record PackageFile(string RelativePath, byte[] Content);

/// <summary>
/// 安装计划。
///
/// 这里刻意不再把"游戏目录里已有同名文件"一律当成错误拒绝安装：
/// 很多游戏（尤其是自带汉化包的）目录里已经有 BepInEx 和别的汉化工具，
/// 直接拒绝会让用户完全装不上。改成按文件分类处理：
///   * 内容一模一样      -> Identical，跳过不写；
///   * 内容不同、但不是本工具装的 -> Replaced，先备份再替换，卸载时还原；
///   * 内容不同、是本工具装的旧版本 -> Upgraded，直接更新，不用备份。
/// </summary>
public sealed record InstallPlan(
    string GameRoot,
    string ManifestPath,
    IReadOnlyList<PackageFile> Files,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Upgraded,
    IReadOnlyList<string> Identical,
    IReadOnlyList<string> ExistingTranslationComponents,
    string? Version = null)
{
    /// <summary>全新写入、游戏目录里原本没有的文件数。</summary>
    public int AddedCount => Files.Count - Replaced.Count - Upgraded.Count;

    /// <summary>游戏目录里本来就有别的汉化组件时，安装界面要把这件事说清楚。</summary>
    public bool HasOtherTranslationComponent => ExistingTranslationComponents.Count > 0;
}

public sealed record UninstallReport(IReadOnlyList<string> Conflicts, IReadOnlyList<string> Restored);

/// <summary>
/// HadBackup 表示安装时覆盖了游戏原有的同名文件、已经把原件备份下来。
/// 卸载时要把备份还原回去，而不是简单删掉。
/// 旧版本清单没有这个字段，反序列化时默认 false，行为与以前一致。
/// </summary>
public sealed record InstalledFile(string RelativePath, string Sha256, bool HadBackup = false);

/// <summary>
/// 安装清单。
///
/// <para><b>Version</b> 是"装这份组件时用的是哪一版软件"。
/// 它存在的唯一目的是**自检**：没有它就无法区分
/// 「装的是旧版组件」和「装的是最新版但用户自己改过文件」——
/// 前者应当直接提示更新，后者应当先提醒、别悄悄覆盖。
/// **旧清单没有这个字段**（反序列化得到 null），那种情况一律按"无法证明是最新的"处理，
/// 也就是会提示更新 —— 这正是用户实测需要的那个行为
/// （Kurea Struggle：机器上装的旧组件，界面什么提示都没有）。</para>
/// </summary>
public sealed record InstallManifest(string GameRoot, IReadOnlyList<InstalledFile> Files, string? Version = null);
