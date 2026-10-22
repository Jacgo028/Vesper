using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class GameDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExeAndFolderShareCanonicalIdentity()
    {
        var exe = CreateMonoGame();

        var byExe = GameDetector.Detect(exe);
        var byFolder = GameDetector.Detect(_root);
        var byFolderWithSlash = GameDetector.Detect(_root + Path.DirectorySeparatorChar);

        Assert.Equal(byExe.GameId, byFolder.GameId);
        Assert.Equal(byExe.GameId, byFolderWithSlash.GameId);
        Assert.Equal(byExe.RootPath, byFolderWithSlash.RootPath);
        Assert.Equal("Mono", byExe.Backend);
        Assert.True(byExe.IsCompatible);
        Assert.Equal(Path.GetFullPath(_root), byExe.RootPath);
    }

    [Fact]
    public void MissingUnityPlayerIsIncompatible()
    {
        var exe = CreateMonoGame();
        File.Delete(Path.Combine(_root, "UnityPlayer.dll"));

        var result = GameDetector.Detect(exe);

        Assert.False(result.IsCompatible);
        Assert.Contains("UnityPlayer.dll", result.CompatibilityReason);
    }

    [Fact]
    public void Il2CppGameIsDetectedAndSupportedForTranslation()
    {
        var exe = CreateMonoGame();
        File.Delete(Path.Combine(_root, "Game_Data", "Managed", "Assembly-CSharp.dll"));
        File.WriteAllBytes(Path.Combine(_root, "GameAssembly.dll"), [1]);

        var result = GameDetector.Detect(exe);

        Assert.Equal("IL2CPP", result.Backend);
        // IL2CPP 现在支持翻译（BepInEx 6 + Il2CppInterop 包，已在 4 款游戏实机验证）。
        // 之前这里断言的是"不支持"，正是那个判断让交付的界面不给 IL2CPP 游戏装组件的机会。
        Assert.True(result.IsCompatible);
        Assert.Empty(result.CompatibilityReason);
    }

    [Fact]
    public void PlayerFacingTextAvoidsRawTechnicalJargon()
    {
        var exe = CreateMonoGame();

        var ok = GameDetector.Detect(exe);
        var il2cppExe = CreateMonoGame();
        File.Delete(Path.Combine(_root, "Game_Data", "Managed", "Assembly-CSharp.dll"));
        File.WriteAllBytes(Path.Combine(_root, "GameAssembly.dll"), new byte[] { 1 });
        var il2cpp = GameDetector.Detect(il2cppExe);

        // 卡片上直接显示的结论不应该出现这些词。
        foreach (var jargon in new[] { "IL2CPP", "Mono", "x64", "Backend" })
        {
            Assert.DoesNotContain(jargon, ok.SupportSummary, StringComparison.Ordinal);
            Assert.DoesNotContain(jargon, ok.NextStepHint, StringComparison.Ordinal);
        }
        Assert.Contains("可以翻译", ok.SupportSummary, StringComparison.Ordinal);
        // 手动扫描直接使用进程内存，IL2CPP 与 Mono 都有这个入口。
        Assert.Contains("可以翻译", il2cpp.SupportSummary, StringComparison.Ordinal);
        Assert.Contains("内置数值工具", il2cpp.SupportSummary, StringComparison.Ordinal);
        // 技术原文仍然保留在折叠区。
        Assert.Contains("Mono", ok.TechnicalSummary, StringComparison.Ordinal);
    }

    private string CreateMonoGame()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Game_Data", "Managed"));
        var exe = Path.Combine(_root, "Game.exe");
        var pe = new byte[256];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3c);
        pe[0x80] = (byte)'P';
        pe[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84);
        File.WriteAllBytes(exe, pe);
        File.WriteAllBytes(Path.Combine(_root, "UnityPlayer.dll"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "Game_Data", "Managed", "Assembly-CSharp.dll"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "Game_Data", "Managed", "Unity.TextMeshPro.dll"), [1]);
        return exe;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
