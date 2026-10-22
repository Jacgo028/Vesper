using System.Text.Json;
using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 组件自检（`InstallPlanner.Check`）。
///
/// 起因是用户实测：机器上装的是**旧版组件**时软件什么提示都没有，
/// 表现成"翻译有毛病但看不出原因"，只有手动「卸载 → 安装」之后才恢复。
/// 这些测试把"什么情况该提示更新"钉死，避免以后再退回去。
/// </summary>
public sealed class ComponentVersionCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-version-tests", Guid.NewGuid().ToString("N"));
    private static readonly string PluginPath = "BepInEx/plugins/UnityTranslator.Plugin.dll";

    private static GameProfile Profile(string root) =>
        new("test", root, Path.Combine(root, "Game.exe"), "2022.3.62f2", "Mono", "x64", true, string.Empty);

    private static byte[] PluginBytes(byte marker) => [0x4D, 0x5A, marker, marker, marker];

    private void WriteManifest(string? version, byte[]? recordedPluginHashSource = null)
    {
        var installed = recordedPluginHashSource ?? PluginBytes(1);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installed));
        var manifest = new InstallManifest(_root, [new InstalledFile(PluginPath.Replace('/', '\\'), sha, false)], version);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".unity-translator-install.json"), JsonSerializer.Serialize(manifest));
    }

    /// <summary>没装过：既不是"最新"也不是"需要更新"，而是未安装。</summary>
    [Fact]
    public void NotInstalledWhenThereIsNoManifest()
    {
        Directory.CreateDirectory(_root);
        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, PluginBytes(1))], "0.5.1");

        Assert.Equal(ComponentState.NotInstalled, check.State);
        Assert.False(check.IsInstalled);
        Assert.False(check.NeedsUpdate);
    }

    /// <summary>装的就是当前这一版 → 不提示。</summary>
    [Fact]
    public void UpToDateWhenVersionMatchesAndPluginContentMatches()
    {
        var bytes = PluginBytes(1);
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins"));
        File.WriteAllBytes(Path.Combine(_root, PluginPath.Replace('/', '\\')), bytes);
        WriteManifest("0.5.1", bytes);

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, bytes)], "0.5.1");

        Assert.Equal(ComponentState.UpToDate, check.State);
        Assert.False(check.NeedsUpdate);
        Assert.Equal("0.5.1", check.InstalledVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentManifestWithMissingOrEmptyPluginNeedsUpdate(bool emptyFile)
    {
        var bytes = PluginBytes(1);
        WriteManifest("0.5.1", bytes);
        if (emptyFile)
        {
            Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins"));
            File.WriteAllBytes(Path.Combine(_root, PluginPath), []);
        }

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, bytes)], "0.5.1");

        Assert.Equal(ComponentState.NeedsUpdate, check.State);
        Assert.True(check.NeedsUpdate);
        Assert.True(check.Reason.HasFlag(UpdateReason.DifferentContent));
    }

    [Theory]
    [InlineData("{\"Version\":\"0.5.1\"}")]
    [InlineData("{\"Version\":\"0.5.1\",\"Files\":null}")]
    public void ManifestWithoutFileListNeedsUpdateInsteadOfThrowing(string manifest)
    {
        var bytes = PluginBytes(1);
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins"));
        File.WriteAllBytes(Path.Combine(_root, PluginPath), bytes);
        File.WriteAllText(Path.Combine(_root, ".unity-translator-install.json"), manifest);

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, bytes)], "0.5.1");

        Assert.Equal(ComponentState.NeedsUpdate, check.State);
        Assert.True(check.Reason.HasFlag(UpdateReason.UnknownVersion));
    }

    /// <summary>
    /// ★ 这条对应用户实测的那个场景：**老清单没有版本号**，无法证明它是最新的 → 必须提示更新。
    /// </summary>
    [Fact]
    public void OldManifestWithoutVersionNeedsUpdate()
    {
        var bytes = PluginBytes(1);
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins"));
        File.WriteAllBytes(Path.Combine(_root, PluginPath.Replace('/', '\\')), bytes);
        WriteManifest(null, bytes);          // 旧清单：没有 Version 字段

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, bytes)], "0.5.1");

        Assert.Equal(ComponentState.NeedsUpdate, check.State);
        Assert.True(check.Reason.HasFlag(UpdateReason.UnknownVersion));
        Assert.Null(check.InstalledVersion);
        // 内容明明一致，仍然要提示 —— 因为"没有版本号"就等于无法证明是最新的
        Assert.False(check.Reason.HasFlag(UpdateReason.DifferentContent));
    }

    /// <summary>清单版本比当前旧 → 提示更新，理由是"版本较旧"。</summary>
    [Fact]
    public void OlderVersionNeedsUpdate()
    {
        var bytes = PluginBytes(1);
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins"));
        File.WriteAllBytes(Path.Combine(_root, PluginPath.Replace('/', '\\')), bytes);
        WriteManifest("0.4", bytes);

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, bytes)], "0.5.1");

        Assert.Equal(ComponentState.NeedsUpdate, check.State);
        Assert.True(check.Reason.HasFlag(UpdateReason.OlderVersion));
        Assert.Equal("0.4", check.InstalledVersion);
    }

    /// <summary>版本号相同但插件内容不一样（例如拿旧 DLL 覆盖过）→ 也要提示，理由是"内容不一致"。</summary>
    [Fact]
    public void SameVersionButDifferentPluginContentNeedsUpdate()
    {
        var oldBytes = PluginBytes(1);
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins"));
        File.WriteAllBytes(Path.Combine(_root, PluginPath.Replace('/', '\\')), oldBytes);
        WriteManifest("0.5.1", oldBytes);

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, PluginBytes(2))], "0.5.1");

        Assert.Equal(ComponentState.NeedsUpdate, check.State);
        Assert.True(check.Reason.HasFlag(UpdateReason.DifferentContent));
    }

    /// <summary>
    /// 磁盘上的文件与清单记录的哈希不符 = 装完之后被人改过。
    /// 仍然提示更新，但**额外**标出这一条，让界面区别于"单纯版本旧"，
    /// 免得把用户自己的改动当成旧版悄悄覆盖掉。
    /// </summary>
    [Fact]
    public void LocallyModifiedPluginIsFlaggedSeparately()
    {
        var installedBytes = PluginBytes(1);
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "plugins"));
        // 磁盘上是改过的内容，清单记的是没改过的
        File.WriteAllBytes(Path.Combine(_root, PluginPath.Replace('/', '\\')), PluginBytes(9));
        WriteManifest("0.5.1", installedBytes);

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, PluginBytes(2))], "0.5.1");

        Assert.Equal(ComponentState.NeedsUpdate, check.State);
        Assert.True(check.Reason.HasFlag(UpdateReason.FileModifiedSinceInstall));
    }

    /// <summary>清单损坏读不出来时，按"需要更新"处理（而不是崩掉或当成最新）。</summary>
    [Fact]
    public void CorruptManifestIsTreatedAsNeedingUpdate()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".unity-translator-install.json"), "{ this is not json");

        var check = InstallPlanner.Check(_root, [new PackageFile(PluginPath, PluginBytes(1))], "0.5.1");

        Assert.Equal(ComponentState.NeedsUpdate, check.State);
        Assert.True(check.Reason.HasFlag(UpdateReason.UnknownVersion));
    }

    /// <summary>安装时要把版本号写进清单，否则下次自检又只能靠"猜"。</summary>
    [Fact]
    public void ApplyWritesTheVersionIntoTheManifest()
    {
        Directory.CreateDirectory(_root);
        var package = new[] { new PackageFile(PluginPath, PluginBytes(3)) };

        var plan = InstallPlanner.Plan(Profile(_root), package, "0.5.1");
        InstallPlanner.Apply(plan);

        var written = JsonSerializer.Deserialize<InstallManifest>(
            File.ReadAllText(Path.Combine(_root, ".unity-translator-install.json")));
        Assert.NotNull(written);
        Assert.Equal("0.5.1", written!.Version);

        // 装完立刻自检应当是"最新"，不应当再提示更新
        var check = InstallPlanner.Check(_root, package, "0.5.1");
        Assert.Equal(ComponentState.UpToDate, check.State);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
