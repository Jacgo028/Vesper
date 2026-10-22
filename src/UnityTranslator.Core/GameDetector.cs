using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UnityTranslator.Core;

public static class GameDetector
{
    public static GameProfile Detect(string droppedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(droppedPath);
        var fullPath = Path.GetFullPath(droppedPath);
        var isExe = File.Exists(fullPath) && string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase);
        var root = Path.TrimEndingDirectorySeparator(isExe ? Path.GetDirectoryName(fullPath)! : fullPath);
        var exe = isExe ? fullPath : FindGameExe(root);
        var gameId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
        var unityPlayer = Path.Combine(root, "UnityPlayer.dll");
        var version = File.Exists(unityPlayer)
            ? FileVersionInfo.GetVersionInfo(unityPlayer).ProductVersion ?? "Unknown"
            : "Unknown";
        var dataRoot = exe.Length == 0 ? string.Empty : Path.Combine(root, Path.GetFileNameWithoutExtension(exe) + "_Data");
        var mono = dataRoot.Length != 0 && File.Exists(Path.Combine(dataRoot, "Managed", "Assembly-CSharp.dll"));
        var il2cpp = File.Exists(Path.Combine(root, "GameAssembly.dll")) ||
                     (dataRoot.Length != 0 && Directory.Exists(Path.Combine(dataRoot, "il2cpp_data")));
        var backend = mono ? "Mono" : il2cpp ? "IL2CPP" : "Unknown";
        var architecture = exe.Length == 0 ? "Unknown" : ReadArchitecture(exe);

        // 这些文字会直接显示给玩家，所以先说人话，需要时再带上技术文件名。
        var reason = !Directory.Exists(root) ? "找不到这个游戏文件夹，可能已被移动或删除。" :
            exe.Length == 0 ? "在这个文件夹里没找到游戏程序。请选择游戏的 .exe，或包含它的文件夹。" :
            !File.Exists(unityPlayer) ? "这个游戏缺少必要文件 UnityPlayer.dll，可能没有下载完整。" :
            dataRoot.Length == 0 || !Directory.Exists(dataRoot) ? "这个游戏缺少数据文件夹，可能没有下载完整。" :
            // ★ IL2CPP 现在**支持翻译**了（BepInEx 6 + Il2CppInterop 包，已在 4 款游戏上实机验证）。
            //   原先这里直接判"不支持"，导致交付的界面根本不给 IL2CPP 游戏装组件的机会 ——
            //   插件能力做出来了，用户在 UI 上却够不到。这里放行，选包交给 PackageCatalog。
            //   （数值工具仍只支持 Mono，那一项由 UiText.Il2CppCaveat 单独说明，两者不要混为一谈。）
            backend != "Mono" && backend != "IL2CPP" ? "认不出这个游戏用的是哪种 Unity，暂时无法处理。" :
            architecture != "x64" ? "只支持 64 位 Windows 游戏，这个游戏不是。" :
            string.Empty;

        return new GameProfile(gameId, root, exe, version, backend, architecture, reason.Length == 0, reason);
    }

    private static string FindGameExe(string root)
    {
        if (!Directory.Exists(root)) return string.Empty;
        return Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(path => Directory.Exists(Path.Combine(root, Path.GetFileNameWithoutExtension(path) + "_Data")))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string ReadArchitecture(string exe)
    {
        try
        {
            using var stream = File.OpenRead(exe);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return "Unknown";
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6) return "Unknown";
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return "Unknown";
            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                _ => "Unknown"
            };
        }
        catch (IOException)
        {
            return "Unknown";
        }
    }
}
