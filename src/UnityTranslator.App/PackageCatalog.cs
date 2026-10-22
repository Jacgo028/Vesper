using System.IO;
using System.Text;
using UnityTranslator.Core;

namespace UnityTranslator.App;

/// <summary>
/// 加载器家族。同一款游戏可能两个都能用，也可能只有一个能用 ——
/// 这取决于**游戏自己的 Mono 运行时**，而不是 Unity 版本号（实测：cambion 与 Leticia 的
/// Unity 版本、Mono 运行时文件、BepInEx core 全部逐字节相同，但 cambion 上 BepInEx 6
/// 的预加载器直接崩）。所以需要记住"这款游戏上次实测哪个能跑"。
/// </summary>
public enum LoaderFamily
{
    /// <summary>按 Unity 版本自动判断（默认行为）。</summary>
    Auto,
    /// <summary>BepInEx 6（预加载器较新，但要求 Mono 运行时导出 mono_dlopen）。</summary>
    BepInEx6,
    /// <summary>BepInEx 5（覆盖面更宽，实测在 BepInEx 6 起不来的游戏上可用）。</summary>
    BepInEx5,
}

/// <summary>
/// 按"后端 + Unity 版本"挑选要安装的组件包，而不是按游戏名写死白名单，
/// 这样未逐一验证过的同类游戏也能安装。
///
/// 已知的限制（来自实测，不能想当然推广）：
///   Unity 6 上 BepInEx 6 会在预加载阶段失败（找不到 imageOpen 入口点），必须用 BepInEx 5。
///   Unity 2022 上 BepInEx 6 **通常**可用 —— **但并非总是**：
///     实测 cambion（Unity 2022.3.62f2 / Mono / x64）上 BepInEx 6 的预加载器同样
///     报 "No matching entry point found for imageOpen" 而整体崩掉，
///     而它的 Unity 版本、Mono 运行时文件、BepInEx core 与能正常工作的 Leticia **逐字节相同**。
///     → 结论：**能不能用不能只看版本号**，必须在实机上发现失败后回退。
/// </summary>
public static class PackageCatalog
{
    /// <summary>Only full Mono packages contain the embedded capture/overlay path.</summary>
    public static bool SupportsEmbeddedOcr(GameProfile game, LoaderFamily preferred) =>
        ResolvePackageName(game, preferred) is "unity6-mono" or "unity2022-mono"
            or "unity2022-mono-bepinex5" or "unity2021-mono" or "unity2020-mono";

    /// <summary>按游戏特征得到组件包目录名；无法判断时返回 null。</summary>
    public static string? ResolvePackageName(GameProfile game) =>
        ResolvePackageName(game, LoaderFamily.Auto);

    /// <summary>
    /// 带上"加载器家族"偏好。`Auto` 时行为与旧版完全一致（保证既有游戏不受影响）；
    /// `BepInEx5` 时强制走 BepInEx 5 那一档 —— 这是预加载器失败后的回退路径。
    /// </summary>
    public static string? ResolvePackageName(GameProfile game, LoaderFamily preferred)
    {
        if (!game.IsCompatible) return null;

        // IL2CPP 只有一个包（BepInEx 6 + Il2CppInterop），没有回退档 —— 这里也不做假设。
        if (game.Backend.Equals("IL2CPP", StringComparison.OrdinalIgnoreCase)) return "il2cpp-bepinex6";

        if (!game.Backend.Equals("Mono", StringComparison.OrdinalIgnoreCase)) return null;

        // 实测发现 BepInEx 6 起不来时，强制用 BepInEx 5。
        if (preferred == LoaderFamily.BepInEx5) return BepInEx5Package(game.UnityVersion);

        var major = UnityMajorVersion(game.UnityVersion);
        return major switch
        {
            // Unity 6：BepInEx 6 实测在预加载阶段失败（找不到 imageOpen 入口点），必须用 BepInEx 5
            >= 6000 => "unity6-mono",
            // Unity 2022：实测**多数**游戏 BepInEx 6 可用（Leticia/DECOY 等），
            // 但 cambion 不可以 —— 那种情况由运行期检测改成 BepInEx5 偏好。
            >= 2022 => "unity2022-mono",
            2021 => "unity2021-mono",
            >= 2019 => "unity2020-mono",
            // 老 CLR 游戏使用独立的 net35/uGUI 插件，不能加载 Unity 6 的程序集。
            _ => "legacy-mono",
        };
    }

    /// <summary>
    /// 加载器相同不代表运行时引用相同。按游戏版本分别构建 TMP/TextCore 与目标框架。
    /// </summary>
    public static string BepInEx5Package(string unityVersion) => UnityMajorVersion(unityVersion) switch
    {
        >= 6000 => "unity6-mono",
        >= 2022 => "unity2022-mono-bepinex5",
        2021 => "unity2021-mono",
        >= 2019 => "unity2020-mono",
        _ => "legacy-mono",
    };

    /// <summary>
    /// 回退目标（Auto/BepInEx6 失败后该换成哪个包）；IL2CPP 与未知后端没有回退档，返回 null。
    /// 抽成独立方法是为了能被单元测试钉住 —— 回退方向搞反的代价是"越修越坏"。
    /// </summary>
    public static string? FallbackPackageName(GameProfile game, LoaderFamily current)
    {
        if (!game.IsCompatible) return null;
        if (!game.Backend.Equals("Mono", StringComparison.OrdinalIgnoreCase)) return null;
        if (current == LoaderFamily.BepInEx5) return null;   // 已经是最后一级了
        return BepInEx5Package(game.UnityVersion);
    }

    public static IReadOnlyList<PackageFile>? Load(string appDirectory, GameProfile game) =>
        Load(appDirectory, game, LoaderFamily.Auto);

    public static IReadOnlyList<PackageFile>? Load(string appDirectory, GameProfile game, LoaderFamily preferred)
    {
        var packageName = ResolvePackageName(game, preferred);
        if (packageName is null) return null;

        var packageRoot = Path.Combine(appDirectory, "packages", packageName);
        if (!Directory.Exists(packageRoot)) return null;
        var files = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => new PackageFile(Path.GetRelativePath(packageRoot, path), File.ReadAllBytes(path)))
            .ToList();

        // The default BepInEx 5 Application entrypoint can call Awake without
        // registering Update. The plugin now delays FontEngine work until Update,
        // so this documented MonoBehaviour entrypoint is safe for these packages.
        if (packageName is "unity2020-mono" or "unity2021-mono" or "unity2022-mono-bepinex5"
            or "unity6-mono" or "legacy-mono")
        {
            const string configPath = "BepInEx/config/BepInEx.cfg";
            files.RemoveAll(file => string.Equals(file.RelativePath.Replace('\\', '/'), configPath,
                StringComparison.OrdinalIgnoreCase));
            files.Add(new PackageFile(configPath, Encoding.UTF8.GetBytes(
                "[Preloader.Entrypoint]\n" +
                "Assembly = " + (packageName == "legacy-mono" ? "UnityEngine.dll" : "UnityEngine.CoreModule.dll") + "\n" +
                "Type = MonoBehaviour\n" +
                "Method = .cctor\n")));
        }

        return files;
    }

    /// <summary>从 "6000.0.68f1 (hash)" 这类字符串里取主版本号；取不到返回 -1。</summary>
    private static int UnityMajorVersion(string unityVersion)
    {
        if (string.IsNullOrWhiteSpace(unityVersion)) return -1;
        var span = unityVersion.AsSpan().TrimStart();
        var length = 0;
        while (length < span.Length && char.IsAsciiDigit(span[length])) length++;
        if (length == 0) return -1;
        return int.TryParse(span[..length], out var major) ? major : -1;
    }
}
