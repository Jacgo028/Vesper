using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class InstallPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-install-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 真实场景：游戏目录里已经自带一份 BepInEx 汉化（像 DECOY），
    /// 大部分文件和我们的一模一样，少数几个不同。
    /// 以前那种"有同名文件就整个拒绝安装"会让用户永远装不上。
    /// </summary>
    [Fact]
    public void FilesIdenticalToTheOnesAlreadyThereAreSkippedInsteadOfBlocking()
    {
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "core"));
        var same = Path.Combine(_root, "BepInEx", "core", "shared.dll");
        File.WriteAllBytes(same, [1, 2, 3]);
        var different = Path.Combine(_root, "winhttp.dll");
        File.WriteAllBytes(different, [9, 9, 9]);
        var mine = Profile();

        var plan = InstallPlanner.Plan(mine,
        [
            new PackageFile("BepInEx/core/shared.dll", [1, 2, 3]),
            new PackageFile("winhttp.dll", [4, 5, 6]),
            new PackageFile("BepInEx/plugins/translator.dll", [7])
        ]);

        Assert.Equal(2, plan.Files.Count);                       // 相同的那份不再重复写
        Assert.Equal(1, plan.AddedCount);
        Assert.Single(plan.Identical);
        Assert.Single(plan.Replaced);
        Assert.Empty(plan.Upgraded);
    }

    /// <summary>覆盖游戏原有文件之前必须先备份，卸载时把原件还原回去。</summary>
    [Fact]
    public void ReplacedGameFileIsBackedUpAndRestoredOnUninstall()
    {
        Directory.CreateDirectory(_root);
        var mine = Path.Combine(_root, "winhttp.dll");
        byte[] original = [9, 9, 9];
        File.WriteAllBytes(mine, original);

        var plan = InstallPlanner.Plan(Profile(), [new PackageFile("winhttp.dll", [4, 5, 6])]);
        InstallPlanner.Apply(plan);

        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(mine));

        var report = InstallPlanner.Uninstall(plan.ManifestPath);

        Assert.Empty(report.Conflicts);
        Assert.Single(report.Restored);
        Assert.Equal(original, File.ReadAllBytes(mine));         // 原件回来了，而不是留下一个空洞
        Assert.False(Directory.Exists(Path.Combine(_root, ".unity-translator-backup")));
    }

    /// <summary>更新本工具自己以前装的旧版本，不需要备份（那本来就是我们的文件）。</summary>
    [Fact]
    public void UpgradingOurOwnEarlierInstallNeedsNoBackup()
    {
        Directory.CreateDirectory(_root);
        var first = InstallPlanner.Plan(Profile(), [new PackageFile("BepInEx/plugins/translator.dll", [1])]);
        InstallPlanner.Apply(first);

        var second = InstallPlanner.Plan(Profile(), [new PackageFile("BepInEx/plugins/translator.dll", [2])]);
        Assert.Empty(second.Replaced);
        Assert.Single(second.Upgraded);
        InstallPlanner.Apply(second);

        Assert.False(Directory.Exists(Path.Combine(_root, ".unity-translator-backup")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(_root, "BepInEx", "plugins", "translator.dll")));

        var report = InstallPlanner.Uninstall(second.ManifestPath);
        Assert.Empty(report.Restored);
        Assert.False(File.Exists(Path.Combine(_root, "BepInEx", "plugins", "translator.dll")));
    }

    /// <summary>装完之后再装一次：什么都不同写，也不会报错。</summary>
    [Fact]
    public void InstallingTwiceWritesNothingTheSecondTime()
    {
        Directory.CreateDirectory(_root);
        var package = new[]
        {
            new PackageFile("BepInEx/core/example.dll", [1, 2]),
            new PackageFile("BepInEx/plugins/translator.dll", [3, 4])
        };
        InstallPlanner.Apply(InstallPlanner.Plan(Profile(), package));

        var again = InstallPlanner.Plan(Profile(), package);
        InstallPlanner.Apply(again);

        Assert.Empty(again.Files);
        Assert.Equal(2, again.Identical.Count);
    }

    /// <summary>卸载只删没被动过的、本工具自己的文件；用户改过的要留下。</summary>
    [Fact]
    public void UninstallRemovesOnlyUnchangedOwnedFiles()
    {
        Directory.CreateDirectory(_root);
        var plan = InstallPlanner.Plan(Profile(),
        [
            new PackageFile("BepInEx/core/example.dll", [1, 2]),
            new PackageFile("BepInEx/plugins/translator.dll", [3, 4])
        ]);
        InstallPlanner.Apply(plan);
        var edited = Path.Combine(_root, "BepInEx", "plugins", "translator.dll");
        File.WriteAllBytes(edited, [9, 9]);

        var report = InstallPlanner.Uninstall(plan.ManifestPath);

        Assert.False(File.Exists(Path.Combine(_root, "BepInEx", "core", "example.dll")));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(edited));
        Assert.Contains(report.Conflicts, p => p.EndsWith("translator.dll"));
    }

    /// <summary>安装中途失败必须整体回滚：新写的删掉，覆盖过的还原。</summary>
    [Fact]
    public void FailedInstallRestoresEverythingItTouched()
    {
        Directory.CreateDirectory(_root);
        var overwritten = Path.Combine(_root, "winhttp.dll");
        byte[] original = [9, 9, 9];
        File.WriteAllBytes(overwritten, original);
        // 用一个同名目录把最后一个文件的写入顶失败（File.Move 到目录上会抛异常）。
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins", "translator.dll"));

        var plan = InstallPlanner.Plan(Profile(),
        [
            new PackageFile("BepInEx/core/fresh.dll", [1]),
            new PackageFile("winhttp.dll", [4, 5, 6]),
            new PackageFile("BepInEx/plugins/translator.dll", [7])
        ]);

        var failure = Record.Exception(() => InstallPlanner.Apply(plan));

        Assert.NotNull(failure);
        Assert.False(File.Exists(Path.Combine(_root, "BepInEx", "core", "fresh.dll")));
        Assert.Equal(original, File.ReadAllBytes(overwritten));
        Assert.False(File.Exists(Path.Combine(_root, ".unity-translator-install.json")));
    }

    /// <summary>游戏目录里已有别的汉化组件时要说出来，不能悄悄替换掉。</summary>
    [Fact]
    public void ExistingTranslationComponentIsReported()
    {
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins", "XUnity.AutoTranslator"));
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "config"));
        File.WriteAllText(Path.Combine(_root, "BepInEx", "config", "AutoTranslatorConfig.ini"), string.Empty);

        var plan = InstallPlanner.Plan(Profile(), [new PackageFile("BepInEx/plugins/translator.dll", [1])]);

        Assert.True(plan.HasOtherTranslationComponent);
        Assert.Contains("XUnity.AutoTranslator", plan.ExistingTranslationComponents);
    }

    [Fact]
    public void PackageCannotEscapeGameRootOrModifyGameAssets()
    {
        Directory.CreateDirectory(_root);
        var game = Profile();

        Assert.Throws<ArgumentException>(() => InstallPlanner.Plan(game, [new PackageFile("../other.dll", [1])]));
        Assert.Throws<ArgumentException>(() => InstallPlanner.Plan(game, [new PackageFile("Game_Data/level0", [1])]));
    }

    private GameProfile Profile() => new("test", _root, Path.Combine(_root, "Game.exe"), "6000.0.68f1", "Mono", "x64", true, "");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
