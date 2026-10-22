using UnityTranslator.Core;
using Xunit;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 预加载器失败判据的测试（第 100 轮）。
///
/// 这些测试盯的是"要不要把用户已装好的组件换掉"这个决定 ——
/// 判错两个方向都有代价：该换没换 = 游戏起不来；不该换却换 = 越修越坏。
/// 所以下面既测"能认出来"，也**重点测"不会误判"**。
/// </summary>
public class PreloaderFailureDetectorTests
{
    /// <summary>实机原文（cambion，Unity 2022.3.62f2 / Mono）。</summary>
    private const string CambionFailure = """
        [Message: Preloader] BepInEx 6.0.0-be.788 - cambion (2026/9/20 12:50:30)
        [Info   : Preloader] Running under Unity 2022.3.62f2
        [Message: Preloader] Preloader started
        [Fatal  : Preloader] Could not run preloader!
        [Fatal  : Preloader] System.TypeInitializationException: The type initializer for
        'BepInEx.Unity.Mono.Preloader.Utils.MonoAssemblyHelper' threw an exception.
          ---> System.EntryPointNotFoundException: No matching entry point found for imageOpen
        """;

    /// <summary>Leticia 上同一套装置成功时的日志（BepInEx 6）。</summary>
    private const string SuccessBepInEx6 = """
        [Message: Preloader] BepInEx 6.0.0-be.788 - Novice Sister Leticia
        [Message: Preloader] Preloader started
        [Message: Preloader] Preloader finished
        [Message:   BepInEx] Chainloader startup complete
        """;

    /// <summary>cambion 换成 BepInEx 5 之后成功时的日志。</summary>
    private const string SuccessBepInEx5 = """
        [Message:   BepInEx] BepInEx 5.4.23.3 - cambion (2026/9/20 12:50:30)
        [Message:   BepInEx] Preloader started
        [Message:   BepInEx] Preloader finished
        [Info   :   BepInEx] Loading [Unity Translator 0.5.0]
        [Message:   BepInEx] Chainloader startup complete
        """;

    [Fact]
    public void RecognisesTheRealFailure()
    {
        Assert.True(PreloaderFailureDetector.IsPreloaderFailure(CambionFailure));
    }

    // ── 下面几条是"不许误判"，比上面那条更重要 ────────────────────────────────

    [Fact]
    public void SuccessfulBepInEx6IsNotAFailure()
    {
        Assert.False(PreloaderFailureDetector.IsPreloaderFailure(SuccessBepInEx6));
    }

    [Fact]
    public void SuccessfulBepInEx5IsNotAFailure()
    {
        Assert.False(PreloaderFailureDetector.IsPreloaderFailure(SuccessBepInEx5));
    }

    [Fact]
    public void EmptyOrMissingLogIsNotAFailure()
    {
        // 关键：**日志还没生成**不能被当成"失败" ——
        // 否则每次点启动、在日志写出来之前去看一眼，都会误判成失败并乱换装置。
        Assert.False(PreloaderFailureDetector.IsPreloaderFailure(null));
        Assert.False(PreloaderFailureDetector.IsPreloaderFailure(string.Empty));
        Assert.False(PreloaderFailureDetector.IsPreloaderFailure("   "));
    }

    [Fact]
    public void FailureFollowedByLaterSuccessIsNotAFailure()
    {
        // 同一个目录里可能同时留着旧的失败日志和新的成功日志（我们不清理旧文件）。
        // 只要出现过 "Preloader finished"，就说明至少有一次是成功的 —— 不能据此把装置换掉。
        var mixed = CambionFailure + "\n" + SuccessBepInEx5;
        Assert.False(PreloaderFailureDetector.IsPreloaderFailure(mixed));
    }

    [Fact]
    public void PlainGameLogWithoutBepInExIsNotAFailure()
    {
        // 游戏自己的 Player.log 之类：既没有致命标志也没有 finished 标志 → 不判定为失败。
        Assert.False(PreloaderFailureDetector.IsPreloaderFailure(
            "Initialize engine version: 2022.3.62f2\nLoading player data..."));
    }

    [Fact]
    public void MissingDirectoryReturnsNullInsteadOfThrowing()
    {
        // 目录不存在时必须安静地返回 null：这个判定只是"锦上添花"，不能影响启动。
        Assert.Null(PreloaderFailureDetector.ReadLogText(Path.Combine(Path.GetTempPath(), "ut-no-such-dir-9f3a")));
        Assert.Null(PreloaderFailureDetector.ReadLogText(string.Empty));
    }

    [Fact]
    public void ReadsBothPreloaderAndOutputLogs()
    {
        // 两个来源都要读：预加载器早期崩溃时**只有** preloader_*.log；
        // 正常启动时只有 LogOutput.log。少读一个就会漏判或误判。
        var root = Path.Combine(Path.GetTempPath(), "ut-prefail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 只有 preloader 日志（＝崩溃那一局的形状）
            File.WriteAllText(Path.Combine(root, "preloader_20260928_012939_487.log"), CambionFailure);
            var text = PreloaderFailureDetector.ReadLogText(root);
            Assert.NotNull(text);
            Assert.True(PreloaderFailureDetector.IsPreloaderFailure(text));

            // 再加上一份成功的 LogOutput.log（＝后来又换对了装置的形状）
            Directory.CreateDirectory(Path.Combine(root, "BepInEx"));
            File.WriteAllText(Path.Combine(root, "BepInEx", "LogOutput.log"), SuccessBepInEx5);
            var both = PreloaderFailureDetector.ReadLogText(root);
            Assert.NotNull(both);
            Assert.False(PreloaderFailureDetector.IsPreloaderFailure(both));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }

    // ── 时间闸：只看本次启动之后写的日志 ─────────────────────────────────────
    //    没有它就会"每次启动都把上一轮的失败当成这一轮的"，于是反复自动换装置。

    [Fact]
    public void AStaleFailureLogIsNotReadAtAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "ut-prefail-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 上一次启动留下的失败日志：文件时间是"现在"，但时间闸设在它之后
            // （＝这次启动比它更晚发生）。
            var log = Path.Combine(root, "preloader_20260928_012939_487.log");
            File.WriteAllText(log, CambionFailure);
            var afterLaunch = File.GetLastWriteTimeUtc(log).AddSeconds(5);

            Assert.Null(PreloaderFailureDetector.ReadLogText(root, afterLaunch));

            // 不限时间时仍然读得到（人工排查、旧调用方行为不变）
            Assert.NotNull(PreloaderFailureDetector.ReadLogText(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public void AFreshFailureLogIsRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "ut-prefail-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = Path.Combine(root, "preloader_20260928_012939_487.log");
            var launchStamp = DateTime.UtcNow.AddSeconds(-1);   // 启动发生在写入之前
            File.WriteAllText(log, CambionFailure);

            var text = PreloaderFailureDetector.ReadLogText(root, launchStamp);

            Assert.NotNull(text);
            Assert.True(PreloaderFailureDetector.IsPreloaderFailure(text));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public void TheTimeGateAlsoAppliesToTheOutputLog()
    {
        // 只给 preloader_*.log 加时间闸、忘了 LogOutput.log 是很容易犯的错：
        // 后果是上一轮"成功"的日志让这一轮的失败**永远判不出来**（回退永不触发）。
        var root = Path.Combine(Path.GetTempPath(), "ut-prefail-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "BepInEx"));
            var output = Path.Combine(root, "BepInEx", "LogOutput.log");
            File.WriteAllText(output, SuccessBepInEx6);
            var afterLaunch = File.GetLastWriteTimeUtc(output).AddSeconds(5);

            Assert.Null(PreloaderFailureDetector.ReadLogText(root, afterLaunch));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }
}
