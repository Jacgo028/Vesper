using System.Security.Cryptography;
using System.Text.Json;

namespace UnityTranslator.Core;

public static class InstallPlanner
{
    private const string ManifestFileName = ".unity-translator-install.json";

    /// <summary>
    /// 安装清单的文件名。公开出来是给"回退加载器"那条路径用的 ——
    /// 它必须在安装新包**之前**先按清单清理旧包的残留文件，
    /// 手写一遍这个字面量迟早会写歪一次（写歪了就是静默地不清理）。
    /// </summary>
    public const string ManifestName = ManifestFileName;

    /// <summary>被覆盖的游戏原文件备份在这里；卸载时从这里还原。</summary>
    private const string BackupDirectoryName = ".unity-translator-backup";

    /// <summary>插件 DLL 的相对路径（自检时用它比对内容）。两种后端各有一个文件名。</summary>
    private static readonly string[] PluginRelativePaths =
    {
        @"BepInEx\plugins\UnityTranslator.Plugin.dll",
        @"BepInEx\plugins\UnityTranslator.Plugin.Il2Cpp.dll"
    };

    /// <summary>
    /// **组件自检**：判断游戏里装着的组件是不是当前这一版。
    ///
    /// 为什么需要它（用户实测需求）：机器上装的是**旧版组件**时，软件原先什么都不提示 ——
    /// 表现是"翻译有毛病但看不出原因"，只有手动「卸载组件 → 安装组件」之后才恢复正常。
    /// 现在把这件事变成软件自己会说的话。
    ///
    /// 判定顺序与理由：
    ///   1. 没有清单            → <see cref="ComponentState.NotInstalled"/>；
    ///   2. 清单版本 == 当前版本，且插件内容一致 → 最新；
    ///   3. 清单版本比当前旧      → 需要更新（<see cref="UpdateReason.OlderVersion"/>）；
    ///   4. 清单**没有版本号**（老清单）→ 需要更新（<see cref="UpdateReason.UnknownVersion"/>）；
    ///   5. 版本号相同但插件内容不同 → 需要更新（<see cref="UpdateReason.DifferentContent"/>）；
    ///      若此时磁盘上的文件又与清单记录的哈希不符，额外标
    ///      <see cref="UpdateReason.FileModifiedSinceInstall"/> —— 说明这个文件被改过，
    ///      提示要区别于"单纯版本旧"，免得把用户自己的改动当成旧版覆盖掉。
    /// </summary>
    public static ComponentCheck Check(string gameRoot, IReadOnlyList<PackageFile> package, string currentVersion)
    {
        var root = Path.GetFullPath(gameRoot);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
            return new ComponentCheck(ComponentState.NotInstalled, UpdateReason.None, null, currentVersion);

        InstallManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(manifestPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 清单读不出来 = 无法证明它是最新的；按需要更新处理，让用户重装一次即可。
            return new ComponentCheck(ComponentState.NeedsUpdate, UpdateReason.UnknownVersion, null, currentVersion);
        }
        if (manifest is null || manifest.Files is null)
            return new ComponentCheck(ComponentState.NeedsUpdate, UpdateReason.UnknownVersion, null, currentVersion);

        var installedVersion = manifest.Version;
        var reason = UpdateReason.None;

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            // 老清单：没有版本号，无法证明是最新的 → 提示更新。
            reason |= UpdateReason.UnknownVersion;
        }
        else if (!string.Equals(installedVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            reason |= UpdateReason.OlderVersion;
        }

        // 版本号相同也可能内容不同（例如拿旧 DLL 覆盖过，或包本身变了但忘了升版本号），
        // 所以再逐字节比一次插件文件。这一步是"内容为准"，比只看版本号可靠。
        var plugin = package.FirstOrDefault(file =>
            PluginRelativePaths.Any(known => string.Equals(
                file.RelativePath.Replace('/', '\\'), known, StringComparison.OrdinalIgnoreCase)));
        if (plugin is not null)
        {
            var destination = Path.Combine(root, plugin.RelativePath);
            byte[] onDisk;
            try { onDisk = File.ReadAllBytes(destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onDisk = []; }

            // A current manifest cannot prove the component is usable when its
            // plugin is missing, unreadable or empty. Reuse the update flow.
            if (onDisk.Length == 0 || !string.Equals(Hash(onDisk), Hash(plugin.Content), StringComparison.OrdinalIgnoreCase))
                reason |= UpdateReason.DifferentContent;

            var recorded = manifest.Files.FirstOrDefault(entry =>
                string.Equals(entry.RelativePath.Replace('/', '\\'),
                    plugin.RelativePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
            if (recorded is not null && onDisk.Length > 0 &&
                !string.Equals(Hash(onDisk), recorded.Sha256, StringComparison.OrdinalIgnoreCase))
                reason |= UpdateReason.FileModifiedSinceInstall;
        }

        return new ComponentCheck(
            reason == UpdateReason.None ? ComponentState.UpToDate : ComponentState.NeedsUpdate,
            reason, installedVersion, currentVersion);
    }

    public static InstallPlan Plan(GameProfile game, IReadOnlyList<PackageFile> package, string? version = null)
    {        if (!game.IsCompatible) throw new ArgumentException(game.CompatibilityReason, nameof(game));
        var root = Path.GetFullPath(game.RootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var requested = package
            .Select(file => new PackageFile(ValidateRelativePath(root, file.RelativePath), file.Content))
            .ToArray();
        if (requested.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            throw new ArgumentException("安装包中有重复文件", nameof(package));

        // 清单里记着、而且没被改过的文件是本工具以前装的，更新它不算“覆盖别人的东西”。
        var owned = OwnedRelativePaths(root);

        var files = new List<PackageFile>();
        var replaced = new List<string>();
        var upgraded = new List<string>();
        var identical = new List<string>();
        foreach (var file in requested)
        {
            var destination = Path.Combine(root, file.RelativePath);
            if (!File.Exists(destination))
            {
                files.Add(file);
                continue;
            }

            // 内容一样就没必要写：既省时间，也不会让用户看到"要装 230 个文件"。
            if (SameContent(destination, file.Content))
            {
                identical.Add(destination);
                continue;
            }

            files.Add(file);
            if (owned.Contains(file.RelativePath)) upgraded.Add(destination);
            else replaced.Add(destination);
        }

        return new InstallPlan(root, Path.Combine(root, ManifestFileName), files, replaced, upgraded, identical,
            ExistingTranslationComponents(root), version);
    }

    public static void Apply(InstallPlan plan)
    {
        var stagingRoot = Path.Combine(plan.GameRoot, ".unity-translator-stage-" + Guid.NewGuid().ToString("N"));
        var backupRoot = Path.Combine(plan.GameRoot, BackupDirectoryName);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var written = new List<(string Destination, string? Backup)>();
        try
        {
            Directory.CreateDirectory(stagingRoot);
            foreach (var file in plan.Files)
            {
                var staged = Path.Combine(stagingRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.WriteAllBytes(staged, file.Content);
            }

            // 先把要覆盖的原文件整份备份，再动任何目标文件。
            foreach (var path in plan.Replaced)
            {
                var relative = Path.GetRelativePath(plan.GameRoot, path);
                var backup = Path.Combine(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(path, backup, overwrite: true);
                backups[path] = backup;
            }

            foreach (var file in plan.Files)
            {
                var destination = Path.Combine(plan.GameRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(Path.Combine(stagingRoot, file.RelativePath), destination, overwrite: true);
                written.Add((destination, backups.TryGetValue(destination, out var backup) ? backup : null));
            }

            WriteManifest(plan, backups);
        }
        catch
        {
            // 半途失败要还原成安装前的样子：覆盖过的从备份拷回来，新写的直接删掉。
            foreach (var (destination, backup) in written)
            {
                try
                {
                    if (backup is not null && File.Exists(backup)) File.Copy(backup, destination, overwrite: true);
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var backup in backups.Values)
            {
                try { if (File.Exists(backup)) File.Delete(backup); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            PruneEmptyDirectories(backupRoot);
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        }
    }

    public static UninstallReport Uninstall(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path))
            ?? throw new InvalidDataException("安装清单无效");
        var root = Path.GetFullPath(manifest.GameRoot);
        if (!string.Equals(path, Path.Combine(root, ManifestFileName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装清单位置与游戏目录不匹配");

        var backupRoot = Path.Combine(root, BackupDirectoryName);
        var conflicts = new List<string>();
        var restored = new List<string>();
        var remaining = new List<InstalledFile>();
        foreach (var entry in manifest.Files)
        {
            var destination = Path.Combine(root, ValidateRelativePath(root, entry.RelativePath));
            if (!File.Exists(destination)) continue;

            // 内容被改过：这是用户自己的东西了，保留下来不删。
            if (!string.Equals(Hash(File.ReadAllBytes(destination)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(destination);
                remaining.Add(entry);
                continue;
            }

            if (entry.HadBackup)
            {
                var backup = Path.Combine(backupRoot, ValidateRelativePath(root, entry.RelativePath));
                if (File.Exists(backup))
                {
                    // 安装时覆盖了游戏原有的文件：把原件放回去，而不是删掉留个空洞。
                    File.Copy(backup, destination, overwrite: true);
                    File.Delete(backup);
                    restored.Add(destination);
                    continue;
                }
            }

            File.Delete(destination);
        }

        if (remaining.Count == 0) File.Delete(path);
        else File.WriteAllText(path, JsonSerializer.Serialize(new InstallManifest(root, remaining, manifest.Version)));
        PruneEmptyDirectories(backupRoot);
        return new UninstallReport(conflicts, restored);
    }

    private static void WriteManifest(InstallPlan plan, IReadOnlyDictionary<string, string> backups)
    {
        var merged = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ReadManifestFiles(plan.ManifestPath)) merged[entry.RelativePath] = entry;
        foreach (var file in plan.Files)
        {
            var hadBackup = backups.ContainsKey(Path.Combine(plan.GameRoot, file.RelativePath)) ||
                            (merged.TryGetValue(file.RelativePath, out var previous) && previous.HadBackup);
            merged[file.RelativePath] = new InstalledFile(file.RelativePath, Hash(file.Content), hadBackup);
        }
        // 写入本次安装所用的软件版本 —— 下次启动时的自检就靠它判断"是不是旧组件"。
        // 旧清单没有这个字段，读出来是 null，自检会按"需要更新"处理（见 Check 的说明）。
        File.WriteAllText(plan.ManifestPath,
            JsonSerializer.Serialize(new InstallManifest(plan.GameRoot, merged.Values.ToArray(), plan.Version)));
    }

    /// <summary>
    /// 把"以前装过、但**新包里没有**"的文件清掉 —— 换加载器（BepInEx 6 → BepInEx 5）时必须先做这一步。
    ///
    /// ★ 为什么非清不可（本轮实测对比两个包的文件清单才发现，之前一直以为"覆盖安装"就够）：
    ///   两个 BepInEx 大版本的 `BepInEx\core` 文件名大面积重名、内容却不同：
    ///     同名不同内容：`0Harmony.dll`、`MonoMod.RuntimeDetour.dll`、`MonoMod.Utils.dll`
    ///     只在 BepInEx 6 里有：`BepInEx.Core.dll`、`BepInEx.Preloader.Core.dll`、
    ///                          `BepInEx.Unity.Common.dll`、`BepInEx.Unity.Mono.dll`、
    ///                          `BepInEx.Unity.Mono.Preloader.dll`、`SemanticVersioning.dll` …
    ///     只在 BepInEx 5 里有：`BepInEx.dll`、`BepInEx.Preloader.dll`、`0Harmony20.dll` …
    ///   而且两者的 `doorstop_config.ini` **互相指向对方的预加载器**
    ///   （6 → `BepInEx\core\BepInEx.Unity.Mono.Preloader.dll`，5 → `BepInEx\core\BepInEx.Preloader.dll`）。
    ///   所以"只覆盖、不清理"会留下一个两代混装的目录：这恰恰是我们要修的那个故障
    ///   （预加载器起不来）最容易被再次踩到的形状。
    ///
    /// ★ 两条安全线（判错就得删用户的东西，所以宁可不删）：
    ///   1. **只删清单记着、并且内容没被改过的文件**。清单是"本工具自己装进去的"的唯一凭据；
    ///      磁盘上的哈希与清单记录不符 = 用户改过它了，那是用户的东西，留着。
    ///      完全不在清单里的文件一律不碰 —— 那是用户的汉化、Mod、存档。
    ///   2. **新包里有的文件不删**，直接由随后的安装覆盖。
    ///
    /// 覆盖过的游戏原文件会从备份里还原（与 <see cref="Uninstall"/> 同样的规则），
    /// 不会因为"换了加载器"就把游戏自带的东西弄丢。
    /// </summary>
    /// <returns>实际处理掉的文件绝对路径（供日志/测试核对）。</returns>
    public static IReadOnlyList<string> RemoveStaleFiles(string gameRoot, IReadOnlyList<PackageFile> package)
    {
        var root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root)) return [];

        var keep = new HashSet<string>(
            package.Select(file => file.RelativePath.Replace('/', '\\')),
            StringComparer.OrdinalIgnoreCase);

        var backupRoot = Path.Combine(root, BackupDirectoryName);
        var handled = new List<string>();
        foreach (var entry in ReadManifestFiles(Path.Combine(root, ManifestFileName)))
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath)) continue;
            if (keep.Contains(entry.RelativePath.Replace('/', '\\'))) continue;

            string destination;
            try { destination = Path.Combine(root, ValidateRelativePath(root, entry.RelativePath)); }
            catch (ArgumentException) { continue; }   // 清单里有非法路径：跳过而不是抛
            if (!File.Exists(destination)) continue;

            byte[] onDisk;
            try { onDisk = File.ReadAllBytes(destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            // 内容对不上 = 不是我们装的那一份了 → 保留。
            if (!string.Equals(Hash(onDisk), entry.Sha256, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var backup = Path.Combine(backupRoot, ValidateRelativePath(root, entry.RelativePath));
                if (entry.HadBackup && File.Exists(backup))
                {
                    // 装的时候覆盖了游戏原有的文件：把原件放回去，别留下一个空洞。
                    File.Copy(backup, destination, overwrite: true);
                    File.Delete(backup);
                }
                else
                {
                    File.Delete(destination);
                }
                handled.Add(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就算了：多留几个文件远好过中断这次回退。
            }
        }

        // 备份目录里被搬空的层级要收掉 —— 卸载后不该在游戏目录留下空文件夹。
        // 必须**等所有还原都做完**再收，否则先收掉的目录会让后面几个文件还原失败。
        PruneEmptyDirectories(backupRoot);
        return handled;
    }

    private static IReadOnlyList<InstalledFile> ReadManifestFiles(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return [];
            var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(manifestPath));
            return manifest?.Files ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 旧清单读不出来时按“没装过”处理：最多是卸载时多留几个文件，不会误删。
            return [];
        }
    }

    /// <summary>本工具以前装进去、清单里记着的文件（相对路径）。</summary>
    private static HashSet<string> OwnedRelativePaths(string root)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ReadManifestFiles(Path.Combine(root, ManifestFileName)))
            owned.Add(entry.RelativePath);
        return owned;
    }

    /// <summary>
    /// 游戏目录里本来就有的别的汉化组件。安装前要告诉用户，免得他以为自己的汉化被无声换掉了。
    /// </summary>
    private static IReadOnlyList<string> ExistingTranslationComponents(string root)
    {
        var found = new List<string>();
        var plugins = Path.Combine(root, "BepInEx", "plugins");
        if (Directory.Exists(plugins))
        {
            if (Directory.Exists(Path.Combine(plugins, "XUnity.AutoTranslator")) ||
                HasFile(plugins, "XUnity.AutoTranslator.Plugin.Core.dll"))
                found.Add("XUnity.AutoTranslator");
            if (Directory.Exists(Path.Combine(plugins, "XUnity.ResourceRedirector")))
                found.Add("XUnity.ResourceRedirector");
        }
        if (File.Exists(Path.Combine(root, "BepInEx", "config", "AutoTranslatorConfig.ini")))
            found.Add("AutoTranslator 汉化配置");
        return found;
    }

    private static bool HasFile(string directory, string pattern)
    {
        try { return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SameContent(string path, byte[] content)
    {
        var existing = new FileInfo(path);
        if (existing.Length != content.Length) return false;
        return Hash(File.ReadAllBytes(path)) == Hash(content);
    }

    private static void PruneEmptyDirectories(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var child in Directory.GetDirectories(directory)) PruneEmptyDirectories(child);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ValidateRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("安装路径必须是游戏目录内的相对路径", nameof(relativePath));
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("安装路径不能离开游戏目录或修改游戏资源", nameof(relativePath));
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("安装路径越过游戏目录", nameof(relativePath));
        return Path.GetRelativePath(root, fullPath);
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
}
