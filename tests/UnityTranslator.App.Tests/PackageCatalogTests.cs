using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class PackageCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-package-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void VerifiedUnitySixGameLoadsOnlyItsPackageFiles()
    {
        var packageFolder = Path.Combine(_root, "packages", "unity6-mono", "BepInEx", "plugins");
        Directory.CreateDirectory(packageFolder);
        File.WriteAllBytes(Path.Combine(packageFolder, "UnityTranslator.Plugin.dll"), [1, 2, 3]);
        var game = new GameProfile("a", "C:\\Game", "C:\\Game\\Kurea Struggle.exe", "6000.0.68f1 (hash)", "Mono", "x64", true, "");

        var package = PackageCatalog.Load(_root, game);
        Assert.NotNull(package);

        var file = Assert.Single(package.Where(file =>
            file.RelativePath.Replace('\\', '/') == "BepInEx/plugins/UnityTranslator.Plugin.dll"));
        Assert.Equal("BepInEx/plugins/UnityTranslator.Plugin.dll", file.RelativePath.Replace('\\', '/'));
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Content);
        Assert.Contains(package, file =>
            file.RelativePath.Replace('\\', '/') == "BepInEx/config/BepInEx.cfg");
    }

    [Fact]
    public void GameOfAnyNameGetsAPackageBasedOnItsUnityVersion()
    {
        // 用户要求支持更多游戏：同样技术与 Unity 版本的游戏，不该因为文件名不同就装不了
        var sameEngine = new GameProfile("b", "D:\\Other", "D:\\Other\\Some Other Game.exe",
            "6000.0.68f1 (e1e9baaf294b)", "Mono", "x64", true, "");

        Assert.Equal("unity6-mono", PackageCatalog.ResolvePackageName(sameEngine));
    }

    [Theory]
    [InlineData("6000.0.68f1 (e1e9baaf294b)", "unity6-mono")]
    [InlineData("6000.1.2f1", "unity6-mono")]
    [InlineData("2022.3.62f2 (7670c08855a9)", "unity2022-mono")]
    [InlineData("2021.3.16f1", "unity2021-mono")]
    [InlineData("2020.1.2f1", "unity2020-mono")]
    [InlineData("2019.4.40f1", "unity2020-mono")]
    [InlineData("5.6.7f1", "legacy-mono")]
    // 版本号取不到时也要给一个包：用 BepInEx 5，官方支持 Unity 4~2022，覆盖面最广
    [InlineData("Unknown", "legacy-mono")]
    public void UnityVersionDecidesWhichLoaderPackageIsUsed(string unityVersion, string expected)
    {
        var game = new GameProfile("c", "D:\\G", "D:\\G\\G.exe", unityVersion, "Mono", "x64", true, "");

        Assert.Equal(expected, PackageCatalog.ResolvePackageName(game));
    }

    [Fact]
    public void Il2CppGameGetsTheIl2CppPackage()
    {
        // IL2CPP 现在有专门的组件包（BepInEx 6 + Il2CppInterop），已在 4 款游戏上实机验证。
        // 关键是不能把 Mono 的包塞给 IL2CPP 游戏 —— 那会让插件根本加载不起来。
        var il2cpp = new GameProfile("d", "D:\\I", "D:\\I\\I.exe",
            "2022.3.62f2 (7670c08855a9)", "IL2CPP", "x64", true, "");

        Assert.Equal("il2cpp-bepinex6", PackageCatalog.ResolvePackageName(il2cpp));
    }

    [Fact]
    public void IncompatibleGameHasNoPackage()
    {
        var broken = new GameProfile("e", "D:\\B", "D:\\B\\B.exe",
            "6000.0.68f1", "Mono", "x64", false, "缺少 UnityPlayer.dll");

        Assert.Null(PackageCatalog.ResolvePackageName(broken));
    }

    [Fact]
    public void UnverifiedGameHasNoInstallPackage()
    {
        // 兼容但没有对应包目录时，仍然返回 null（而不是空列表）
        var game = new GameProfile("f", "C:\\Other", "C:\\Other\\Other.exe", "6000.0.68f1", "Mono", "x64", true, "");

        Assert.Null(PackageCatalog.Load(_root, game));
    }

    [Fact]
    public void BepInEx5FallbackPackageIncludesTheSafeDelayedFontEntrypoint()
    {
        // Regression: BepInEx 5's default Application entrypoint can load Awake without
        // ever registering Update. The package must select MonoBehaviour; the plugin
        // defers FontEngine work until the update loop is live.
        var packageFolder = Path.Combine(_root, "packages", "unity2022-mono-bepinex5", "BepInEx", "plugins");
        Directory.CreateDirectory(packageFolder);
        File.WriteAllBytes(Path.Combine(packageFolder, "UnityTranslator.Plugin.dll"), [1, 2, 3]);
        var game = new GameProfile("fallback", "C:\\Game", "C:\\Game\\Game.exe",
            "2022.3.62f2", "Mono", "x64", true, "");

        var package = PackageCatalog.Load(_root, game, LoaderFamily.BepInEx5);

        var config = Assert.Single(package!.Where(file =>
            file.RelativePath.Replace('\\', '/') == "BepInEx/config/BepInEx.cfg"));
        var text = System.Text.Encoding.UTF8.GetString(config.Content);
        Assert.Contains("[Preloader.Entrypoint]", text);
        Assert.Contains("Assembly = UnityEngine.CoreModule.dll", text);
        Assert.Contains("Type = MonoBehaviour", text);
        Assert.Contains("Method = .cctor", text);
    }

    [Fact]
    public void LegacyMonoPackageAlsoIncludesTheSafeDelayedFontEntrypoint()
    {
        // Regression: legacy-mono is BepInEx 5 as well. Leaving it on the default
        // Application entrypoint recreates the Awake-without-Update failure.
        var packageFolder = Path.Combine(_root, "packages", "legacy-mono", "BepInEx", "plugins");
        Directory.CreateDirectory(packageFolder);
        File.WriteAllBytes(Path.Combine(packageFolder, "UnityTranslator.Plugin.dll"), [1, 2, 3]);
        var game = new GameProfile("legacy", "C:\\Game", "C:\\Game\\Game.exe",
            "5.6.7f1", "Mono", "x64", true, "");

        var package = PackageCatalog.Load(_root, game);

        var config = Assert.Single(package!.Where(file =>
            file.RelativePath.Replace('\\', '/') == "BepInEx/config/BepInEx.cfg"));
        var text = System.Text.Encoding.UTF8.GetString(config.Content);
        Assert.Contains("Assembly = UnityEngine.dll", text);
        Assert.Contains("Type = MonoBehaviour", text);
    }

    [Theory]
    [InlineData("2020.1.2f1", "unity2020-mono", 20)]
    [InlineData("2021.3.16f1", "unity2021-mono", 21)]
    [InlineData("2022.3.62f2", "unity2022-mono-bepinex5", 22)]
    public void ForcedBepInEx5StillLoadsThePluginsMatchingTheGameRuntime(string version, string directory, byte marker)
    {
        foreach (var entry in new[] { ("unity2020-mono", (byte)20), ("unity2021-mono", (byte)21),
                     ("unity2022-mono-bepinex5", (byte)22), ("legacy-mono", (byte)5) })
        {
            var folder = Path.Combine(_root, "packages", entry.Item1, "BepInEx", "plugins");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "UnityTranslator.Plugin.dll"), [entry.Item2]);
        }
        var game = new GameProfile("runtime", "C:\\Game", "C:\\Game\\Game.exe", version, "Mono", "x64", true, "");

        var files = PackageCatalog.Load(_root, game, LoaderFamily.BepInEx5);

        Assert.Equal(directory, PackageCatalog.ResolvePackageName(game, LoaderFamily.BepInEx5));
        var plugin = Assert.Single(files!.Where(file => file.RelativePath.EndsWith("UnityTranslator.Plugin.dll")));
        Assert.Equal(new[] { marker }, plugin.Content);
    }

    [Fact]
    public void ExplicitBepInEx6PreferenceDoesNotSelectTheBepInEx5Fallback()
    {
        var game = new GameProfile("runtime", "C:\\Game", "C:\\Game\\Game.exe", "2022.3.62f2", "Mono", "x64", true, "");
        Assert.Equal("unity2022-mono", PackageCatalog.ResolvePackageName(game, LoaderFamily.BepInEx6));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
