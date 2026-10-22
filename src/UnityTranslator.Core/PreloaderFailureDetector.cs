namespace UnityTranslator.Core;

/// <summary>
/// 判断"这个游戏的 BepInEx 预加载器是不是起不来"。
///
/// ★ 为什么需要它（第 100 轮实测）：
///   实测 cambion（Unity 2022.3.62f2 / Mono / x64）上，BepInEx 6 的预加载器在
///   **加载任何插件之前**就整体崩掉：
///     [Message: Preloader] Preloader started
///     [Fatal  : Preloader] Could not run preloader!
///     System.TypeInitializationException: … MonoAssemblyHelper …
///       ---> System.EntryPointNotFoundException: No matching entry point found for imageOpen
///   而同一款包在 Leticia 上是好的；两款游戏的 Unity 版本、Mono 运行时文件、
///   BepInEx core **逐字节相同**。所以**光看版本号判断不出来**，只能在实机上发现，
///   发现之后换成 BepInEx 5（实测可用）。
///
/// ★ 为什么抽成纯函数（放在 Core）：
///   这是"要不要把用户已装好的东西换掉"的判据。判错两个方向都有代价：
///     * 该回退却没回退 → 用户看到游戏起不来（就是本轮这个报错）；
///     * 不该回退却回退 → 把一个本来能用的装置换掉，越修越坏。
///   插件项目带 UnityEngine 引用、App 项目带 WPF，都测不了；放这里能被 xunit 钉住。
/// </summary>
public static class PreloaderFailureDetector
{
    /// <summary>BepInEx 6 预加载器起不来时会写下的标志串（实测原文）。</summary>
    public const string FatalMarker = "Could not run preloader!";

    /// <summary>预加载器"跑完了"的标志（BepInEx 5 与 6 都会写）。</summary>
    public const string FinishedMarker = "Preloader finished";

    /// <summary>
    /// 从日志内容判断预加载器是否失败。
    ///
    /// 判据刻意收得很紧：**必须**同时出现
    ///   ① `Could not run preloader!`（BepInEx 自己宣布致命失败），且
    ///   ② 全文**没有** `Preloader finished`。
    /// 只用①会误判（同一份日志里可能既有旧的失败又有后来的成功）；
    /// 加上②之后，"成功启动的日志"永远返回 false —— 这是最重要的一条不变式。
    /// </summary>
    public static bool IsPreloaderFailure(string? logText)
    {
        if (string.IsNullOrEmpty(logText)) return false;
        if (logText.Contains(FinishedMarker, StringComparison.Ordinal)) return false;
        return logText.Contains(FatalMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在游戏根目录里找 BepInEx 写下的日志内容（找不到返回 null）。
    ///
    /// 两个来源都要看，少一个就会漏判：
    ///   * `preloader_*.log` —— 预加载器**崩在早期**时只写这个（本轮 cambion 就是），
    ///     那时 `BepInEx\LogOutput.log` 根本还没被创建；
    ///   * `BepInEx\LogOutput.log` —— 正常启动（含 BepInEx 5）的日志在这里，
    ///     用它来确认"其实成功了"，避免误判。
    ///
    /// 只读、不抛异常：判定失败也不该影响启动流程。
    /// </summary>
    public static string? ReadLogText(string gameRoot) => ReadLogText(gameRoot, null);

    /// <summary>
    /// 同上，但只读 <paramref name="notBeforeUtc"/> **之后**被写过的日志。
    ///
    /// ★ 为什么需要这个时间闸（第 100 轮差点又踩一次）：
    ///   `preloader_*.log` 是**追加**的，上一次失败的记录会永远留在那里。
    ///   启动后不看时间就读，会把"上一轮的失败"当成"这一轮的失败"，
    ///   于是每次启动都触发一次回退 —— 把一个本来好好的装置越换越坏。
    ///   传 null 表示不限时间（人工排查、单元测试用）。
    /// </summary>
    public static string? ReadLogText(string gameRoot, DateTime? notBeforeUtc)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) return null;
        var parts = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(gameRoot, "preloader_*.log", SearchOption.TopDirectoryOnly))
            {
                if (!IsFresh(path, notBeforeUtc)) continue;
                parts.Add(SafeRead(path));
            }
        }
        catch (Exception) { /* 目录读不到就当没有 */ }

        var output = Path.Combine(gameRoot, "BepInEx", "LogOutput.log");
        if (File.Exists(output) && IsFresh(output, notBeforeUtc)) parts.Add(SafeRead(output));

        if (parts.Count == 0) return null;
        return string.Join("\n", parts);
    }

    /// <summary>
    /// 日志的**最后写入时间**是否晚于时间闸。文件刚被创建、但内容还没落盘时
    /// 长度可能是 0 —— 那也算"新鲜"，只是内容为空，由调用方循环重试。
    /// 取不到时间时按"不新鲜"处理：宁可漏判（下次启动再判）也不误判。
    /// </summary>
    private static bool IsFresh(string path, DateTime? notBeforeUtc)
    {
        if (notBeforeUtc is not { } floor) return true;
        try { return File.GetLastWriteTimeUtc(path) >= floor; }
        catch (Exception) { return false; }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception) { return string.Empty; }
    }
}
