using UnityTranslator.Core;
using Xunit;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 换加载器（BepInEx 6 → BepInEx 5）时的安装/清理测试（第 100 轮）。
///
/// ★ 为什么专门为这件事写一组测试：
///   回退是**自动**发生的（用户没点任何按钮），所以它一旦出错，
///   用户看到的是"游戏还是起不来"或者"我的东西被删了"，而且没有任何线索。
///   这里用两个**结构逼真**的假包（重名不同内容 + 各自独有文件 + doorstop 互相指向）
///   把真实包清单里那个坑钉住。
///
/// 判据取自实际发布包（`packages\unity2022-mono` 是 BepInEx 6，`packages\unity2020-mono` 是 BepInEx 5）：
///   · 两个版本都有、内容不同：`BepInEx\core\0Harmony.dll`
///   · 只在 BepInEx 6 有：`BepInEx\core\BepInEx.Core.dll`、`BepInEx\core\BepInEx.Unity.Mono.Preloader.dll`
///   · 只在 BepInEx 5 有：`BepInEx\core\BepInEx.Preloader.dll`
///   · `doorstop_config.ini` 的 `target_assembly` 互相指向对方版本的预加载器
/// </summary>
public sealed class LoaderSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ut-loader-switch-" + Guid.NewGuid().ToString("N"));

    private GameProfile Game => new(
        "cambion", _root, Path.Combine(_root, "cambion.exe"),
        "2022.3.62f2 (7670c08855a9)", "Mono", "x64", true, "");

    private const string Doorstop6 =
        "[General]\ntarget_assembly = BepInEx\\core\\BepInEx.Unity.Mono.Preloader.dll\n";

    private const string Doorstop5 =
        "[General]\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n";

    /// <summary>BepInEx 6 包：cambion 上预加载器起不来的那一档。</summary>
    private static PackageFile[] PackageBepInEx6() =>
    [
        new("doorstop_config.ini", Bytes(Doorstop6)),
        new("winhttp.dll", [0x60, 0x60]),
        new(@"BepInEx\core\0Harmony.dll", [0x06, 0x06]),
        new(@"BepInEx\core\BepInEx.Core.dll", [0x06, 0x01]),
        new(@"BepInEx\core\BepInEx.Preloader.Core.dll", [0x06, 0x02]),
        new(@"BepInEx\core\BepInEx.Unity.Mono.Preloader.dll", [0x06, 0x03]),
        new(@"BepInEx\plugins\UnityTranslator.Plugin.dll", [0x06, 0x99]),
    ];

    /// <summary>BepInEx 5 包：实测能起来的那一档。</summary>
    private static PackageFile[] PackageBepInEx5() =>
    [
        new("doorstop_config.ini", Bytes(Doorstop5)),
        new("winhttp.dll", [0x50, 0x50]),
        new(@"BepInEx\core\0Harmony.dll", [0x05, 0x05]),
        new(@"BepInEx\core\BepInEx.dll", [0x05, 0x01]),
        new(@"BepInEx\core\BepInEx.Preloader.dll", [0x05, 0x02]),
        new(@"BepInEx\plugins\UnityTranslator.Plugin.dll", [0x05, 0x99]),
    ];

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private string Path_(string relative) => Path.Combine(_root, relative);

    private void Install(PackageFile[] package)
    {
        Directory.CreateDirectory(_root);
        InstallPlanner.Apply(InstallPlanner.Plan(Game, package, "0.5.3"));
    }

    // ── 主路径：换过去、换干净 ─────────────────────────────────────────────

    [Fact]
    public void SwitchingLoaderRemovesTheOldLoadersOwnFiles()
    {
        Install(PackageBepInEx6());

        var removed = InstallPlanner.RemoveStaleFiles(_root, PackageBepInEx5());

        // 三个"只属于 BepInEx 6"的文件都必须被清掉 ——
        // 尤其 BepInEx.Unity.Mono.Preloader.dll：它在新配置里已经不被加载，
        // 但留在目录里就是下次"两代混装"故障的种子。
        Assert.Contains(Path_(@"BepInEx\core\BepInEx.Core.dll"), removed);
        Assert.Contains(Path_(@"BepInEx\core\BepInEx.Preloader.Core.dll"), removed);
        Assert.Contains(Path_(@"BepInEx\core\BepInEx.Unity.Mono.Preloader.dll"), removed);
        Assert.False(File.Exists(Path_(@"BepInEx\core\BepInEx.Unity.Mono.Preloader.dll")));
        Assert.False(File.Exists(Path_(@"BepInEx\core\BepInEx.Core.dll")));

        // "两个包都有"的文件不能在这一步被删 —— 它们由随后的安装覆盖。
        Assert.True(File.Exists(Path_(@"BepInEx\core\0Harmony.dll")));
        Assert.True(File.Exists(Path_(@"BepInEx\plugins\UnityTranslator.Plugin.dll")));
    }

    [Fact]
    public void AfterSwitchTheInstallMatchesTheNewPackageExactly()
    {
        Install(PackageBepInEx6());
        var package5 = PackageBepInEx5();

        InstallPlanner.RemoveStaleFiles(_root, package5);
        InstallPlanner.Apply(InstallPlanner.Plan(Game, package5, "0.5.3"));

        // ① 新包的每个文件都在，且内容就是新包的
        foreach (var file in package5)
        {
            var path = Path.Combine(_root, file.RelativePath);
            Assert.True(File.Exists(path), $"缺文件：{file.RelativePath}");
            Assert.Equal(file.Content, File.ReadAllBytes(path));
        }

        // ② 旧包独有、新包没有的文件，一个都不能剩下
        var expected = package5
            .Select(file => file.RelativePath.Replace('/', '\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftovers = PackageBepInEx6()
            .Select(file => file.RelativePath.Replace('/', '\\'))
            .Where(path => !expected.Contains(path))
            .Where(path => File.Exists(Path.Combine(_root, path)))
            .ToArray();
        Assert.Empty(leftovers);

        // ③ 门禁配置指向的是 BepInEx 5 的预加载器（混装时最容易错的就是这里）
        var doorstop = File.ReadAllText(Path_("doorstop_config.ini"));
        Assert.Contains(@"BepInEx\core\BepInEx.Preloader.dll", doorstop);
        Assert.DoesNotContain("BepInEx.Unity.Mono.Preloader", doorstop);
    }

    // ── 安全线：绝不删不属于自己的东西 ─────────────────────────────────────

    [Fact]
    public void FilesOutsideTheManifestAreNeverTouched()
    {
        Install(PackageBepInEx6());

        // 用户自己放的汉化 / Mod / 存档 —— 清单里没有，任何情况下都不能碰。
        var userFile = Path_(@"BepInEx\plugins\XUnity.AutoTranslator\Translator.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllBytes(userFile, [1, 2, 3]);
        var userSave = Path_(@"BepInEx\config\AutoTranslatorConfig.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(userSave)!);
        File.WriteAllText(userSave, "keep me");

        var removed = InstallPlanner.RemoveStaleFiles(_root, PackageBepInEx5());

        Assert.DoesNotContain(userFile, removed);
        Assert.DoesNotContain(userSave, removed);
        Assert.True(File.Exists(userFile));
        Assert.True(File.Exists(userSave));
    }

    [Fact]
    public void AFileTheUserReplacedIsKept()
    {
        Install(PackageBepInEx6());

        // 用户把 BepInEx 6 的组件拆了，换成自己的（或改了其中一个）：
        // 内容与清单记录不符 → 那是用户的东西，保留。
        var edited = Path_(@"BepInEx\core\BepInEx.Core.dll");
        File.WriteAllBytes(edited, [0xAA, 0xBB]);

        var removed = InstallPlanner.RemoveStaleFiles(_root, PackageBepInEx5());

        Assert.DoesNotContain(edited, removed);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, File.ReadAllBytes(edited));
    }

    [Fact]
    public void AFileTheGameProvidedIsRestoredNotDeleted()
    {
        // 游戏目录里**本来就有**一个同名文件（别的汉化/Mod 装的），安装时被备份下来。
        // 换加载器把这一份换成"新包没有"的文件时，不能一删了事 —— 得把游戏原件放回去。
        Directory.CreateDirectory(_root);
        var shared = Path_(@"BepInEx\core\Stale.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllBytes(shared, [0x11, 0x11, 0x11]);

        var oldPackage = new PackageFile[] { new(@"BepInEx\core\Stale.dll", [0x06, 0x06]) };
        InstallPlanner.Apply(InstallPlanner.Plan(Game, oldPackage, "0.5.3"));
        Assert.Equal(new byte[] { 0x06, 0x06 }, File.ReadAllBytes(shared));

        // 新包里没有 Stale.dll → 换包时它属于"旧文件"
        var newPackage = new PackageFile[] { new(@"BepInEx\core\BepInEx.Preloader.dll", [0x05, 0x02]) };
        var removed = InstallPlanner.RemoveStaleFiles(_root, newPackage);

        Assert.Contains(shared, removed);
        Assert.True(File.Exists(shared), "游戏原有的文件必须还原，不能删掉留个空洞");
        Assert.Equal(new byte[] { 0x11, 0x11, 0x11 }, File.ReadAllBytes(shared));
    }

    [Fact]
    public void ReinstallingTheSamePackageRemovesNothing()
    {
        Install(PackageBepInEx6());

        // 同一个包重装（最常见的情况）：清理必须是空操作，
        // 否则"每次启动都自检并重装"的那条路径会把自己的文件删掉。
        var removed = InstallPlanner.RemoveStaleFiles(_root, PackageBepInEx6());

        Assert.Empty(removed);
        Assert.True(File.Exists(Path_(@"BepInEx\core\BepInEx.Core.dll")));
        Assert.True(File.Exists(Path_(@"BepInEx\core\BepInEx.Unity.Mono.Preloader.dll")));
    }

    [Fact]
    public void UninstallAfterASwitchLeavesNothingBehind()
    {
        Install(PackageBepInEx6());
        var package5 = PackageBepInEx5();
        InstallPlanner.RemoveStaleFiles(_root, package5);
        InstallPlanner.Apply(InstallPlanner.Plan(Game, package5, "0.5.3"));

        // 换过加载器之后再卸载：BepInEx 6 的清单一并作废，一个文件都不该留下。
        InstallPlanner.Uninstall(Path_(InstallPlanner.ManifestName));

        foreach (var file in PackageBepInEx6().Concat(package5))
        {
            Assert.False(File.Exists(Path.Combine(_root, file.RelativePath)),
                $"卸载后仍残留：{file.RelativePath}");
        }
    }

    [Fact]
    public void StaleRemovalOnAMissingDirectoryIsHarmless()
    {
        // 游戏目录不存在（用户刚删过）时不能抛：这条路径跑在启动流程里。
        var missing = Path.Combine(Path.GetTempPath(), "ut-no-such-game-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(InstallPlanner.RemoveStaleFiles(missing, PackageBepInEx5()));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception) { }
    }
}
