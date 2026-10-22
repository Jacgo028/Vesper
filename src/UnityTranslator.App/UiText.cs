using System.IO;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App;

/// <summary>
/// 所有直接显示给玩家的文字，集中放在这里，方便统一口径和检查用词。
/// 规则：主路径上说人话；技术原文只在“技术详情”里出现。
/// </summary>
internal static class UiText
{
    // ---------- 数值类型 ----------

    public static NumericKind KindAt(int index) => index switch
    {
        1 => NumericKind.Int64,
        2 => NumericKind.Float32,
        3 => NumericKind.Float64,
        _ => NumericKind.Int32
    };

    public static string KindName(NumericKind kind) => kind switch
    {
        NumericKind.Int32 => "整数",
        NumericKind.Int64 => "很大的整数",
        NumericKind.Float32 => "小数",
        NumericKind.Float64 => "精确小数",
        _ => "整数"
    };

    /// <summary>类型选择框下面的建议。区分“给玩家看的说法”和“引擎里的类型名”。</summary>
    public static string KindHint(NumericKind kind) => kind switch
    {
        NumericKind.Int32 => "先按“整数”试：金币、等级、数量大多是这种。适合 0 到约 21 亿之间的数。",
        NumericKind.Int64 => "金额特别大的游戏用这种（超过 21 亿）。多数游戏用不到。",
        NumericKind.Float32 => "小数用它，例如攻击力 12.5、进度 0.75。",
        NumericKind.Float64 => "需要很精确的小数时用它。很少用到，先试“小数”。",
        _ => string.Empty
    };

    /// <summary>游戏内部类型名，只在技术详情里显示。</summary>
    public static string KindTechnical(NumericKind kind) => kind switch
    {
        NumericKind.Int32 => "引擎内类型名 Int32（4 字节有符号整数）",
        NumericKind.Int64 => "引擎内类型名 Int64（8 字节有符号整数）",
        NumericKind.Float32 => "引擎内类型名 Float32 / Single（4 字节浮点）",
        NumericKind.Float64 => "引擎内类型名 Float64 / Double（8 字节浮点）",
        _ => string.Empty
    };

    // ---------- 状态与错误 ----------

    /// <summary>把异常翻译成“发生了什么 + 能怎么办”，原始信息另存到技术详情。</summary>
    public static (string Message, string Advice, string Technical) Explain(Exception ex, string action)
    {
        var technical = ex.GetType().Name + "：" + ex.Message;

        if (ex is UnauthorizedAccessException)
            return ($"{action}失败：没有权限访问这个文件。",
                "换个位置放游戏，或以管理员身份重新打开本程序再试。", technical);

        if (ex is System.ComponentModel.Win32Exception win32)
            return ($"{action}失败：系统拒绝了这次操作。",
                "如果游戏正在运行请先退出；仍然失败就以管理员身份重新打开本程序。", technical);

        if (ex is IOException io)
        {
            var busy = io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
                       io.Message.Contains("正由另一进程使用", StringComparison.Ordinal) ||
                       io.Message.Contains("另一个程序", StringComparison.Ordinal);
            return busy
                ? ($"{action}失败：有文件正被其他程序占用。",
                    "先关掉游戏、杀毒软件的扫描窗口，再试一次。", technical)
                : ($"{action}失败：读写文件时出错。",
                    "确认游戏文件夹还在、磁盘空间足够，然后重试。", technical);
        }

        if (ex is ArgumentException)
            return ($"{action}失败：填的内容格式不对。",
                "检查刚填写的地址或数值，改成正确的格式再试。", technical);

        if (ex is InvalidOperationException)
            return ($"{action}失败：当前状态下不能做这一步。",
                "先退出游戏重新启动一次，再按页面上的顺序操作。", technical);

        return ($"{action}失败：出现了没有预料到的问题。",
            "可以先重试一次；仍然失败请把“技术详情”里的内容发给开发者。", technical);
    }

    /// <summary>作弊会话不可用时的玩家说明。</summary>
    public const string MemoryUnavailableAdvice =
        "可能是权限不足或游戏刚启动还没准备好。可以退出游戏重新启动一次，或改用“游戏数值推荐”页。";

    /// <summary>重申关键事实，避免玩家误解。</summary>
    public const string MemoryAddressCaveat = "普通内存地址在游戏重启后会失效，必须重新查找。";

    public const string CandidateCaveat = "推荐只是根据名字和当前值猜出来的候选，不代表一定正确。改之前请先备份存档。";

    public const string Il2CppCaveat = "目前只支持一部分 Unity 游戏。IL2CPP 打包的游戏暂时读不到游戏内数值，这个页面会查不到东西。";
}
