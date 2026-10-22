using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityTranslator.Core;

namespace UnityTranslator.Plugin.Il2Cpp;

/// <summary>
/// BepInEx 6 IL2CPP 插件。与 Mono 版共用同一套协议与判定逻辑，
/// 但通过反射访问 Unity 类型，所以编译期不依赖任何 interop 程序集，
/// 一份 dll 适用于所有 IL2CPP 游戏。
/// </summary>
[BepInPlugin(PluginGuid, "Unity Translator (IL2CPP)", "0.5.7")]
public sealed class Il2CppPlugin : BasePlugin
{
    public const string PluginGuid = "org.codex.unitytranslator.il2cpp";

    /// <summary>默认管道名。**产品行为**，不要改。</summary>
    public const string DefaultPipeName = "UnityTranslator";

    /// <summary>
    /// 真正使用的管道名。默认就是 <see cref="DefaultPipeName"/>，产品行为不变。
    ///
    /// `UT_PIPE_NAME` 只给**隔离验证**用：用户自己的正式版 App 在跑时会占住默认名字，
    /// 隔离宿主于是永远抢不到管道（`ERROR_PIPE_BUSY` 无限重试），而测试里的插件会连到
    /// **用户那个 App** —— 译文是它给的、它的日志里没有新埋点，测量数据全是脏的。
    /// 第 93 轮（B86）在 Mono 侧踩过这个坑，IL2CPP 侧当时**没有**对应的开关，
    /// 所以做多字符串并发测试前必须先补上，否则测的还是用户那个 App。
    /// </summary>
    public static readonly string PipeName =
        Environment.GetEnvironmentVariable("UT_PIPE_NAME") is { Length: > 0 } custom
            ? custom
            : DefaultPipeName;

    internal static ManualLogSource? Log2;

    public override void Load()
    {
        Log2 = Log;
        Log.LogInfo("Unity Translator (IL2CPP) loading");

        // 让运行时能找到 BepInEx 为这款游戏生成的互操作程序集
        UnityReflectionBridge.Install();
        Log.LogInfo("Unity Translator (IL2CPP): assembly resolver installed");

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<TranslatorBehaviour>();
            AddComponent<TranslatorBehaviour>();
            Log.LogInfo("Unity Translator (IL2CPP): behaviour added");
        }
        catch (Exception ex)
        {
            Log.LogError("Unity Translator (IL2CPP): failed to add behaviour: " + ex);
        }
    }
}

/// <summary>把程序集解析接到 BepInEx 的目录布局上。</summary>
internal static class UnityReflectionBridge
{
    private static UnityReflection? _reflection;

    public static UnityReflection Reflection => _reflection ??= Create();

    private static UnityReflection Create()
    {
        var root = Paths.BepInExRootPath;
        var directories = new[]
        {
            Path.Combine(root, "core"),
            Path.Combine(root, "interop"),
            Path.Combine(root, "unity-libs"),
            Path.Combine(root, "plugins"),
        };
        var lookup = new DirectoryLookup(directories);
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var requested = new AssemblyName(args.Name).Name;
            return string.IsNullOrEmpty(requested) ? null : lookup.Find(requested);
        };
        return new UnityReflection(lookup);
    }

    public static void Install() => _ = Reflection;

    private sealed class DirectoryLookup : IAssemblyLookup
    {
        private readonly string[] _directories;

        /// <summary>
        /// 正在解析中的程序集名。**重入守卫，缺了它就会栈溢出。**
        ///
        /// ★★★ 实测（本轮定位到的致命 bug）：
        ///   <c>Assembly.Load</c> / <c>Assembly.LoadFrom</c> 在解析不到时会**触发
        ///   AppDomain.AssemblyResolve 事件**，而那个事件正是接到本类的 <c>Find</c> 上 ——
        ///   于是 Find → Load → 解析事件 → Find → … 无限递归，
        ///   最终 <c>Stack overflow</c>（BepInEx 的 ErrorLog.log 里是完整堆栈），
        ///   插件加载直接死掉，日志只剩 "assembly resolver installed"。
        ///
        ///   守卫的作用：同一个名字正在解析时再次被请求，一律返回 null 交给运行时自己处理，
        ///   绝不递归。
        /// </summary>
        private readonly HashSet<string> _resolving = new HashSet<string>(StringComparer.Ordinal);

        public DirectoryLookup(string[] directories) => _directories = directories;

        public Assembly? Find(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;

            lock (_resolving)
            {
                if (!_resolving.Add(simpleName)) return null;   // 重入 → 直接放弃
                try
                {
                    // 先看默认上下文里**已经加载**的那一份：复用同一实例才不会出现
                    // "同一 dll 两个 Assembly、类型标识不匹配"（那会让 PropertyInfo.GetValue
                    // 抛 Object does not match target type）。
                    // 注意这里只查、不触发加载，避免再次进入解析事件。
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.Ordinal))
                                return loaded;
                        }
                        catch (Exception) { }
                    }

                    // 再按目录找文件，用 LoadFromAssemblyPath 直接装进默认上下文 ——
                    // 它不会为**同一个**程序集再触发一次解析事件。
                    foreach (var directory in _directories)
                    {
                        try
                        {
                            var candidate = Path.Combine(directory, simpleName + ".dll");
                            if (File.Exists(candidate))
                                return System.Runtime.Loader.AssemblyLoadContext.Default
                                    .LoadFromAssemblyPath(Path.GetFullPath(candidate));
                        }
                        catch (Exception ex) when (ex is IOException or BadImageFormatException
                                                       or FileLoadException or FileNotFoundException) { }
                    }
                    return null;
                }
                finally
                {
                    lock (_resolving) _resolving.Remove(simpleName);
                }
            }
        }
    }
}

/// <summary>
/// 一个被跟踪的文字组件。
///
/// 必须是**顶层**类型：Il2CppInterop 的类型注入（ClassInjector）不处理嵌套类型，
/// 嵌套类型会被静默跳过并留下警告，注入出来的托管类在原生侧没有对应类型。
/// </summary>
public sealed class TrackedTextState
{
    public string Source = string.Empty;
    public string? Translated;
    public int Generation;
    public string RequestId = string.Empty;
    public float LastSentAt;
    public int Attempts;
    public object Component = null!;
    public ReflectionTextLayout? Layout;
    public int LayoutLogCount;

    // -- request debounce (round 100: "slow / unstable / inaccurate after returning to menu") --
    //
    // MEASURED ROOT CAUSE this exists for. The game types dialogue out one character at a time,
    // so the component text changes every few frames:
    //     黒岩市が -> 黒岩市が全域封鎖されてから半月が過 -> ... -> the whole sentence
    // Every intermediate value is a DIFFERENT string, so the plugin asked the online service to
    // translate EACH PREFIX separately. 63 uncached requests were observed for roughly eight
    // sentences, and the queue pushed the API tail to P95=5468ms / max=**17349ms** for a single
    // character ("エ"). It also explains the accuracy drop: half-sentences were translated and
    // written into the translation database on their own.
    //
    // So: only ask for a translation once the text has STOPPED changing.
    public string Pending = string.Empty;
    public float PendingSince;
    public float PendingFirstSeen;
    public bool PendingSent;

    // -- end-to-end timing (round 100) -------------------------------------------------
    // The user reported that on NORMAL text the translation still feels slow. Every number
    // we had was App-side (API 1.7s) or scan-side (<1ms) -- there was NO end-to-end figure,
    // so "slow" could not be split into "we were late to notice" vs "the service was slow".
    // These three stamps make that split visible. Values come from a single process-wide
    // Stopwatch, so the differences are meaningful (no cross-process clock alignment).
    public long SentMs;
    public long RecvMs;
    public long WrittenMs;
    public long DiscoveredMs;
    public bool TimingLogged;
}

/// <summary>
/// 一句原文"正在飞"的请求 —— 同原文的其他组件搭它的车，不再重复联网。
///
/// 必须是**顶层**类型（与 <see cref="TrackedTextState"/> 同一个理由）：
/// Il2CppInterop 的 ClassInjector 不处理嵌套类型。
/// </summary>
public sealed class InFlightRequest
{
    /// <summary>这句话的原文（注销登记时用它当键，不必再去字典里反查）。</summary>
    public string Source = string.Empty;

    /// <summary>发请求的那个组件（答复按它的 ComponentId 回来）。</summary>
    public long Primary;

    /// <summary>发出时刻（<c>Time.unscaledTime</c>），用于超时判断。</summary>
    public float SinceUnscaledTime;

    /// <summary>挂在这条请求下面等答复的其他组件。</summary>
    public readonly List<long> Dependents = new List<long>();
}

/// <summary>
/// 插件的全部可变状态。
///
/// ★★★ 为什么状态必须放在这个**非注入的静态类**里（本轮定位到的 IL2CPP 致命 bug）：
///
///   <c>ClassInjector.RegisterTypeInIl2Cpp&lt;TranslatorBehaviour&gt;()</c> 会把该类型
///   **所有字段的类型逐个重写**（<c>RewriteType</c> → <c>Type.GetType(程序集限定名)</c>）。
///   我们原来把 <c>Dictionary&lt;long, TrackedTextState&gt;</c>、
///   <c>ConcurrentQueue&lt;ReplyMessage&gt;</c>、<c>PipeClient</c> 直接放在 MonoBehaviour 上，
///   Il2CppInterop 解析不了这些自定义/泛型类型，注册当场抛异常 ——
///   插件加载**停在这一步**，日志只剩 "assembly resolver installed"，
///   <c>TranslatorBehaviour</c> 从未注册成功。完整堆栈见 BepInEx 的 ErrorLog.log：
///     ClassInjector.RewriteType → System.Type.GetType → RegisterTypeInIl2Cpp → Il2CppPlugin.Load
///
///   所以：**注入类型只允许基元字段**，其余状态一律放静态类。
/// </summary>
internal static class TranslatorState
{
    public static readonly Dictionary<long, TrackedTextState> States = new Dictionary<long, TrackedTextState>();
    public static readonly ConcurrentQueue<ReplyMessage> Replies = new ConcurrentQueue<ReplyMessage>();
    public static PipeClient? Pipe;
    public static int NextGeneration;

    // ── 写回统计：按组件类型分开计数 ────────────────────────────────
    //
    // 为什么需要：写回日志原来用 `_appliedLogged++ < 10` 封顶，第 10 条之后**全部静默**。
    // 实测就被这一点误导过一轮 —— 日志里只有 8 条 UI.Text 的写回、
    // 一条 TMP 的都没有，看上去像"8 个 TMP 组件从没被处理过"，
    // 其实可能只是被日志上限截掉了，或者它们的原文本来就不需要翻译。
    // 分开计数 + 按类型各自限流，才能把这件事说清楚。
    public static int UiTextApplied;
    public static int UiTextFailed;
    public static int TmpApplied;
    public static int TmpFailed;

    // ── 写回与字体分支的**按组件**日志（第 100 轮改）────────────────────────────
    //
    // 原来这里是 `UiTextLogShown` / `TmpLogShown` 两个**全局计数器**，上限 12：
    //   实测 112 条写回里只有最早 12 条留下日志，**真正出问题的那组组件一条都没有**，
    //   于是"屏幕上还是日文"到底是没写进去还是写进去了画不出来，完全无法判断。
    // 改成按组件 id 去重：每个组件各留一条，配合字体分支日志用 id 一对就能定案。
    public static readonly HashSet<long> UiWriteLogged = new HashSet<long>();
    public static readonly HashSet<long> TmpWriteLogged = new HashSet<long>();
    public static readonly HashSet<long> FontBranchLogged = new HashSet<long>();
    /// <summary>已经报过「答复被丢弃」的组件 id（按组件去重，不刷屏）。</summary>
    public static readonly HashSet<long> DropLogged = new HashSet<long>();
    /// <summary>每个组件被我们写回了几次（判定「我们写一次、游戏改回去一次」的拉锯）。</summary>
    public static readonly Dictionary<long, int> WriteAttempts = new Dictionary<long, int>();

    // -- per-component translation memory (round 100, the dialogue bug) ----------------
    // WHY: one component can cycle through MANY different lines. The real evidence from the
    // log -- the bottom dialogue box is a SINGLE component that showed, in turn:
    //   "隔壁房间里也许有幸存者……" / "首先去帮助旁边的幸存者吧。" / "在周围找找，确认有没有其他路吧。"
    // The guard could only re-write when `current == state.Source`, i.e. when the SAME line
    // came back -- but the box was showing a DIFFERENT line, so the guard never fired and we
    // fell back to a fresh round trip. Remember every (line -> translation) we have seen for
    // this component and the box can be filled instantly, with no network at all.
    public static readonly Dictionary<long, Dictionary<string, string>> SeenByComponent =
        new Dictionary<long, Dictionary<string, string>>();
    public static int TmLogShown;

    // -- prewarm (round 100, user request) ---------------------------------------------
    // WHAT IT DOES: send translation requests for text we have DISCOVERED but not yet asked
    // about, at a PACED rate, so they are already in the local translation database by the
    // time the player opens the screen that shows them.
    //
    // WHY IT IS WORTH DOING (measured, not assumed):
    //   opening the inventory asks for ~26 item descriptions AT THE SAME INSTANT; the provider
    //   queues them, which is where the 5.7s tail came from (median stayed ~1.7s).
    //   A paced prewarm spreads that burst over time instead of firing it in one frame.
    // WHAT IT CANNOT DO: make a single NEW line faster than the model itself (1.7s).
    // ★★ 默认 **0 = 不限流**（第 100 轮实测教训）。
    //
    // 我最初把它默认设成 4，想法是"把同屏爆发摊开"。实机结果**是一场灾难**：
    //   [端到端] 原文出现→译文可见=21976ms（其中 发现→发送=21961ms 发送→回包=13ms）
    //   163 个组件抢"每秒 4 条"，标题画面那几行排到几十条后面 —— **10~22 秒才轮到**。
    //   而它想解决的问题（同屏 26 条的尾巴 5.7 秒）完全不成比例。
    //
    // 所以：**默认关掉**，需要时用 UT_PREWARM_PER_SEC=<正数> 打开。
    // 教训记在进展记录 B119：**"平摊"是有代价的** —— 它把"某一条很晚"变成"每一条都很晚"。
    public static readonly int PrewarmPerSecond = (int)ReadFloatEnv("UT_PREWARM_PER_SEC", 0f);
    public static readonly int PrewarmMaxPerSession = (int)ReadFloatEnv("UT_PREWARM_MAX", 400f);
    public static bool PrewarmDisabled = Environment.GetEnvironmentVariable("UT_PREWARM") == "0";
    public static int PrewarmSent;
    public static int PrewarmLogged;
    public static float PrewarmWindowStart;
    public static int PrewarmInWindow;
    /// <summary>预热只在这些文字"已经被游戏加载进内存"时才有意义 —— 也就是被扫描到的组件。</summary>
    public static readonly HashSet<long> Prewarmed = new HashSet<long>();
    /// <summary>「守回译文」日志已打了几条（限流用）。</summary>
    public static int GuardLogShown;

    // -- monotonic clock + end-to-end stats (round 100) --------------------------------
    public static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public static long NowMs() => Clock.ElapsedMilliseconds;
    public static readonly List<long> TotalSamples = new List<long>();
    public static readonly List<long> IpcSamples = new List<long>();
    public static readonly List<long> WaitSamples = new List<long>();
    public static readonly List<long> DiscoverToSendSamples = new List<long>();
    public static int TimingLogShown;
    public static int WriteLogTotal;
    public static int TmpFontLogShown;

    // ── TMP 中文回退字体资产 ──────────────────────────────────────
    //
    // 实测（DECOY 截图）：游戏自带的 TMP 字体是日文字体 Honoka_Shin_Mincho_L SDF，
    // 汉字里"日文也有"的字（角色/意/工坊/返回/冒）能显示，
    // 而简体独有的字（编/辑/设/置/选/项/创/继/续/险/标/题）全是**空心方框**。
    // 所以必须给 TMP 挂一个带简体字形的回退字体资产。
    public static object? CjkTmpFontAsset;
    public static bool CjkTmpFontAttempted;
    public static bool TmpGlobalFallbackInstalled;
    public static int TmpFallbackLogShown;
    public static int TmpGlyphLogShown;
    public static bool FontEngineProbed;

    // ── 同原文在飞去重（用户新增需求：「减少请求数」，第 99 轮）─────────────────
    //
    // **为什么做**：第 95 轮在这款游戏上实测拿到硬数据 —— 103 条请求里
    //   **32 条是重复原文**（71 条唯一），重复率 **31%**：
    //     `Sell Out` ×12、`Stock：0` ×12、`Back`/`Settings`/`SavePoint` 各 ×3 …
    //   API 单次 2.1~4.7 秒、并发时尾部还会被拉长，所以这 31% 是**纯浪费**。
    //
    // **只是"在飞"登记，不是缓存**：拿到答复立刻注销。同一句话之后再出现，
    //   照旧走一次 IPC —— App 侧的 `UserCorrected` 与 `TextSafety.LooksLikeChatReply`
    //   校验必须留在链路上（第 97 轮已量化：热命中端到端只要 56ms，省不出东西，
    //   而插件本地缓存会绕过校验，等于把修好的正确性缺陷重新引进来）。
    public static readonly Dictionary<string, InFlightRequest> InFlight =
        new Dictionary<string, InFlightRequest>(StringComparer.Ordinal);
    public static int DedupSkipped;
    public static int DedupAttached;
    public static bool DedupSummaryLogged;

    /// <summary>在飞登记的最长有效时间（秒）：超过就让搭车的自己重发，避免永久不动。</summary>
    public const float InFlightTimeoutSeconds = 5f;

    /// <summary>去重明细日志（`UT_DEDUP_TRACE=1` 打开）。</summary>
    public static readonly bool DedupTrace =
        Environment.GetEnvironmentVariable("UT_DEDUP_TRACE") == "1";

    // -- request debounce (round 100) -------------------------------------------------
    //
    // How long a piece of text must stay UNCHANGED before we ask for a translation.
    // 0 disables debouncing entirely (the pre-round-100 behaviour), kept so the fix can be
    // A/B compared on the same scene instead of merely argued about.
    public static readonly float DebounceSeconds = ReadFloatEnv("UT_DEBOUNCE_SEC", 0.0f);

    /// <summary>
    /// 全场景扫描周期（秒）。用户要求"再次加快"，默认从 0.4 秒提到 0.1 秒。
    /// 可以用 UT_SCAN_SEC 覆盖，方便实机对照（改坏了不用重新编译）。
    /// ⚠️ 一轮要反射遍历 213 个组件，0.1 秒一轮＝每帧都在扫；
    ///    所以必须同时盯着 [扫描耗时] 的数，确认没有反噬帧率。
    /// </summary>
    public static readonly float ScanIntervalSeconds = ReadFloatEnv("UT_SCAN_SEC", 0.05f);

    /// <summary>
    /// 守回译文 / 词典直写的节奏（秒），与全场景扫描**分开**。
    ///
    /// 用户要求"策略激进一点"：从 0.1 秒压到 0.03 秒。
    /// 依据：写回本身 0~3ms、扫描平均 &lt;1ms，这条路径很便宜；
    /// 而它盯的是"组件被游戏还原成原文"这种时刻 —— 越快补回来，用户越看不到闪回。
    /// </summary>
    public static readonly float GuardIntervalSeconds = ReadFloatEnv("UT_GUARD_SEC", 0.03f);

    /// <summary>扫描耗时的累计统计（毫秒），用于判断 0.1 秒一轮是否可行。</summary>
    public static long ScanMsTotal;
    public static long ScanMsMax;
    public static int ScanCount;
    public static int ScanLogShown;

    /// <summary>Upper bound: send anyway once the text has existed this long.</summary>
    public static readonly float DebounceMaxWaitSeconds = ReadFloatEnv("UT_DEBOUNCE_MAX_SEC", 2.0f);

    public static readonly bool DebounceTrace =
        Environment.GetEnvironmentVariable("UT_DEBOUNCE_TRACE") == "1";

    public static int DebounceHeld;
    public static int DebounceSent;

    private static float ReadFloatEnv(string name, float fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(raw)) return fallback;
        float value;
        if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value)) return fallback;
        return value < 0f ? 0f : value;
    }
}

/// <summary>
/// 每 0.4 秒扫一遍 TMP 文字，把新出现的送去翻译，收到回复后写回。
///
/// ★ 字段只允许基元类型（见 <see cref="TranslatorState"/> 的说明）。
/// </summary>
public sealed class TranslatorBehaviour : MonoBehaviour
{
    private float _nextScan;
    /// <summary>守回译文自己的节奏（0.1 秒），与 0.4 秒的扫描分开 —— 见 GuardAppliedTranslations。</summary>
    private float _nextGuard;
    private int _scannedLogged;
    private int _updateErrors;

    /// <summary>写回统计总结的限流（上次打印时的总数 / 时间）。</summary>
    private int _lastSummaryTotal;
    private float _lastSummaryTime;

    /// <summary>重新包装结果的日志计数。</summary>
    private int _rewrapLogged;

    /// <summary>扫描类型日志计数。</summary>
    private int _scanTypeLogged;

    public TranslatorBehaviour(IntPtr pointer) : base(pointer) { }

    private static void Say(string message) => Il2CppPlugin.Log2?.LogInfo(message);

    private void Start()
    {
        TranslatorState.Pipe = new PipeClient(Il2CppPlugin.PipeName, TranslatorState.Replies);
        TranslatorState.Pipe.Start();
        Say("IL2CPP translator started; pipe=" + Il2CppPlugin.PipeName);
    }

    /// <summary>
    /// 每**帧**把已经收到的回复全部处理掉（第 94 轮在 Mono 侧验证过的优化，这里补进 IL2CPP）。
    ///
    /// 为什么必须每帧做、不能挂在 0.4 秒的扫描后面：
    ///   实测（Mono 侧 Leticia 冷启动，真实 API）「回包→写回」里**排队占 52~281ms**，
    ///   而写回本身只要 0~3ms —— 那 52~281ms 纯粹是"回包已经在队列里，但要等下一次扫描
    ///   才被取走"的固定成本，与缓存冷热无关。
    ///   文字**发现**确实需要扫描（要遍历场景），但"把已经拿到的译文写回去"只需要
    ///   一次字典查找 + 一次赋值，完全可以每帧做。改完排队从 52~281ms 降到 1~29ms。
    ///
    /// 参数只用 <see cref="ReplyMessage"/>（BepInEx 的类型，不是我们注入的类型），
    /// 与 <see cref="WriteBack"/> 同一个约束：**不要在这里引入自定义托管类型参数**。
    /// </summary>
    private void DrainReplies()
    {
        while (TranslatorState.Replies.TryDequeue(out var reply))
        {
            if (reply == null) continue;
            // end-to-end timing: the moment this reply was taken off the queue.
            if (TranslatorState.States.TryGetValue(reply.ComponentId, out var recvState)) recvState.RecvMs = TranslatorState.NowMs();

            // ★ 先把"这句话的在飞登记"取出来（它会带出搭车的组件列表），再处理主组件。
            //   即使主组件这一条因为校验不过被跳过，登记也必须注销 ——
            //   否则搭车的组件会一直等一个不会来的信号（要等 30 秒超时才自愈）。
            InFlightRequest? flight = null;
            if (TranslatorState.States.TryGetValue(reply.ComponentId, out var primaryState))
                TranslatorState.InFlight.TryGetValue(primaryState.Source, out flight);

            if (WriteBack(reply)) continue;   // 主组件写回成功 → 不用再补给搭车的

            // 主组件这一条没写成（它自己已经拿到过译文、原文变了、或组件没了）：
            // 同一句译文仍然对**其他同原文组件**有效，所以照样补给它们。
            if (flight is not null && flight.Primary == reply.ComponentId)
                FlushDedup(flight, reply.Translated);
        }
    }

    /// <summary>
    /// ★★★ 守住已经写好的译文：每轮扫描检查一次，**凡是"我们译过、但组件现在又显示原文"的，
    /// 就把手上已有的译文再写回去**。
    ///
    /// 为什么光靠"答复到达时补写一次"不够（第 100 轮实机证明）：
    ///   `[重新写回]` 已经证实我们**确实**把译文写进去了四次（Esc 菜单四个按钮，
    ///   内容都对），但用户屏幕上仍是原文 —— 也就是"**我们写一次、游戏改回去一次**"的拉锯。
    ///   拉锯里我们只在"答复到达"那一个瞬间补写，而游戏每次开菜单都会重置文字，
    ///   时机对不上就白补。
    ///
    /// 这个守门逻辑不猜时机，而是**直接看事实**：组件现在显示的是不是原文？
    ///   是 → 手上又有译文 → 就写。 每 0.4 秒一遍，最坏 0.4 秒内收敛。
    ///
    /// 代价：只对 `Translated != null`（＝我们确实写成功过）的组件做，
    ///   一次字符串读 + 一次比较，没有它时为 0；有它时也只在"被还原"那一刻才真写。
    /// </summary>
    private void GuardAppliedTranslations()
    {
        var reflection = UnityReflectionBridge.Reflection;
        foreach (var pair in TranslatorState.States)
        {
            var state = pair.Value;
            if (state.Translated == null) continue;
            string? current;
            try { current = reflection.ReadText(state.Component); }
            catch (Exception) { continue; }
            // 只有"组件回到原文"才动手 —— 组件显示别的（游戏换了内容/正常显示我们的译文）都不碰。
            if (!ReplacementPolicy.ShouldReapply(state.Source, current, state.Translated)) continue;
            bool ok;
            try { ok = TryApplyTranslation(pair.Key, state.Translated); }
            catch (Exception) { continue; }
            NoteWriteAttempt(pair.Key, ok, state.Translated);
            if (ok && TranslatorState.GuardLogShown < 60)
            {
                TranslatorState.GuardLogShown++;
                Say("[守回译文] id=" + pair.Key + " 组件又变回原文了，已把已有译文再写一次（零联网）：「"
                    + OneLine(state.Source) + "」");
            }
        }
    }

    private void Update()
    {
        try
        {
            // ★★ 先每帧收包 —— 必须在下面 `_nextScan` 的早退**之前**。
            //   放在扫描之后的话，这一帧就跳过了收包，"回包→写回"要多等 0~400ms。
            //   这一条是第 94 轮实测抓到的两个真实延迟来源之一（见 DrainReplies 的说明）。
            DrainReplies();

            // ★★ 守回译文**单独一个更快的节奏**（0.1 秒），不跟着 0.4 秒的扫描走。
            //
            //   为什么分开：扫描一轮要遍历 213 个组件（贵），而"守住已经写好的译文"
            //   只需要对**已经译过**的组件各读一次字符串（便宜）。
            //   挂在一起的话，游戏一旦把文字还原成原文，最坏要等 0.4 秒才补回来 ——
            //   用户会看到"文字闪回日文再变中文"。分开之后最坏等待降到 0.1 秒。
            //
            //   注意：它**同样必须在 `_nextScan` 早退之前**，否则又变成 0.4 秒一轮。
            if (Time.unscaledTime >= _nextGuard)
            {
                _nextGuard = Time.unscaledTime + TranslatorState.GuardIntervalSeconds;
                GuardAppliedTranslations();
                // 词典直写：组件换回我们译过的句子就立刻写回（用户的判断：策略太保守）
                ApplyFromMemory();
            }

            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + TranslatorState.ScanIntervalSeconds;

            // 去重登记也要有寿命：主组件没了、或答复等太久时丢掉登记，
            // 让搭车的组件自己重发一次（App 侧有 SQLite 缓存，这一趟很便宜）。
            PruneInFlight();
            // ★ 给扫描计时：0.1 秒一轮是用户要求的激进值，必须用数据确认它可行。
            var scanSw = System.Diagnostics.Stopwatch.StartNew();
            Scan();
            scanSw.Stop();
            TranslatorState.ScanMsTotal += scanSw.ElapsedMilliseconds;
            TranslatorState.ScanCount++;
            if (scanSw.ElapsedMilliseconds > TranslatorState.ScanMsMax) TranslatorState.ScanMsMax = scanSw.ElapsedMilliseconds;
            // 每 100 轮报一次：平均 / 最大 / 轮数。平均接近 100ms 就说明 0.1 秒一轮撑不住。
            if (TranslatorState.ScanCount % 100 == 0 && TranslatorState.ScanLogShown++ < 40)
            {
                Say("[扫描耗时] 轮数=" + TranslatorState.ScanCount
                    + " 平均=" + (TranslatorState.ScanMsTotal / TranslatorState.ScanCount) + "ms"
                    + " 最大=" + TranslatorState.ScanMsMax + "ms"
                    + " 跟踪中=" + TranslatorState.States.Count
                    + " 周期=" + TranslatorState.ScanIntervalSeconds + "s"
                    + " 去抖=" + TranslatorState.DebounceSeconds + "s");
                TranslatorState.ScanMsMax = 0;   // 每个窗口重置一次，看的是"最近最坏"
            }

            LogWriteBackSummary();
        }
        catch (Exception ex)
        {
            // 必须打完整堆栈：只报 ex.Message 时，"Object does not match target type"
            // 这类异常完全看不出是哪一步抛的（实测为此多花了一轮实机测试）。
            if (_updateErrors++ < 5)
                Say("IL2CPP translator Update failed: " + ex);
        }
    }

    /// <summary>
    /// 把一条答复写给**它自己那个组件**。返回 true 表示确实写进去了。
    ///
    /// 参数只用 <c>ReplyMessage</c>（BepInEx 的类型，不是我们注入的类型），
    /// 与原来内联时保持同一套约束（见 <see cref="Update"/> 顶部的说明）。
    /// </summary>
    private bool WriteBack(ReplyMessage reply)
    {
        // ★★ 第 100 轮：这四条早退原来**全是静默的** —— 一条答复被丢掉时日志里一个字都没有，
        //    于是"我们发过请求、服务也翻成功了、屏幕上却还是原文"这种现象**完全无法归因**。
        //    实机症状就是这样：对话框那一行 `API=1033ms 结果=成功`，但屏幕上一直日文，
        //    而我手上没有任何一行日志能说明它是在这四条里的哪一条被丢掉的。
        //    现在每条早退都留下带组件 id 的原因（按组件去重，不刷屏）。
        if (!TranslatorState.States.TryGetValue(reply.ComponentId, out var state))
        {
            LogDrop(reply.ComponentId, "组件不在跟踪表里(States 里没有它)");
            return false;
        }
        if (state.Translated != null)
        {
            // ★★★ 第 100 轮实机抓到的真 bug。
            //     用户原话：「点 esc 返回菜单了之后再返回游戏再返回菜单，翻译就又消失了」。
            //
            //   日志里的直接证据 —— 这四个 id 正是 Esc 菜单的四个按钮，
            //   而它们**刚刚才被成功翻译过**：
            //     [丢弃答复] id=1987544260880 原因：这个组件已经写过了(state.Translated 非空)  (ロード)
            //     [丢弃答复] id=1986351270288 原因：这个组件已经写过了(state.Translated 非空)  (設定)
            //     [丢弃答复] id=1986351970704 原因：这个组件已经写过了(state.Translated 非空)  (メインメニュー)
            //     [丢弃答复] id=1987543842816 原因：这个组件已经写过了(state.Translated 非空)  (ゲームに戻る)
            //
            //   链条：游戏把菜单文字**还原成原文** → 下一轮扫描看到 text==Source 且 RetryDue 到点
            //   → **重发请求** → App 命中缓存秒回 → 到这里却因为"这个组件已经写过了"**被丢掉**
            //   → 译文再也回不到组件上。**我们手里本来就有译文，却因为这条早退而不写。**
            //
            //   修法：译文就在手上，而组件现在显示的是原文 → **直接再写一次**（零成本、零联网）。
            //   返回 true 表示"这条答复已处理"，调用方不必再去喂搭车的组件。
            var textNow = UnityReflectionBridge.Reflection.ReadText(state.Component);
            if (ReplacementPolicy.ShouldReapply(state.Source, textNow, state.Translated))
            {
                var reapplied = TryApplyTranslation(reply.ComponentId, state.Translated);
                Say("[重新写回] id=" + reply.ComponentId
                    + (reapplied
                        ? " 游戏把文字还原成原文了，已把手上已有的译文再写一次（零联网）：\""
                        : " 游戏把文字还原成原文了，但重写失败：\"")
                    + OneLine(state.Source) + "\" -> \"" + OneLine(state.Translated) + "\"");
                NoteWriteAttempt(reply.ComponentId, reapplied, state.Translated);
                if (reapplied) return true;
            }
            else
            {
                LogDrop(reply.ComponentId, "这个组件已经写过了，且当前文字不需要重写"
                    + " 现=" + (textNow is null ? "(null)" : "\"" + OneLine(textNow) + "\""));
            }
            return false;
        }
        if (string.IsNullOrWhiteSpace(reply.Translated))
        {
            LogDrop(reply.ComponentId, "答复里的译文是空的");
            return false;
        }
        if (!ReplacementPolicy.ReplyMatches(state.Source, state.Source, state.Generation,
                state.RequestId, reply.Generation, reply.RequestId))
        {
            LogDrop(reply.ComponentId, "答复编号对不上(Generation/RequestId 不匹配，多半是组件被重建过)");
            return false;
        }

        var reflection = UnityReflectionBridge.Reflection;
        var current = reflection.ReadText(state.Component);
        if (current != state.Source)
        {
            // 逗号后面把"我们以为的原文"与"组件现在的文字"都打出来 ——
            // 这两者不一致正是"游戏把组件改掉了"的直接证据（打字机逐字覆盖就是这个样子）。
            LogDrop(reply.ComponentId, "组件当前文字已经不等于我们记录的原文(游戏改过它)"
                + " 记录=\"" + OneLine(state.Source) + "\" 现=" + (current is null ? "(null)" : "\"" + OneLine(current) + "\""));
            return false;   // 画面已经变了，放弃这次回复
        }

        // ★★ 写回前先保证字体能显示这段译文 —— IL2CPP 的中文字体路径。
        //
        //   为什么必须做：插件把中文写进组件后，如果组件当前字体没有这些汉字字形，
        //   画面就是一片方框。Mono 侧靠运行时装配 TMP SDF 字体解决，
        //   但那条路要遍历 FontEngine/TextCore 的一大堆类型，在 IL2CPP 反射下代价很高。
        //
        //   旧版 UnityEngine.UI.Text 有更简单的路：
        //   `Font.CreateDynamicFontFromOSFont("Microsoft YaHei", size)`
        //   直接拿到一个能用系统字体渲染的动态字体，赋给 Text.font 即可 ——
        //   这条 API 在 Mono 侧的实机日志里已证明本机可用。
        // 按组件实际类型分流：TMP 与旧版 UI.Text 的字体路径完全不同，
        // 混在一个计数器里就分不清是谁成功了、谁在被静默跳过。
        var componentType = state.Component.GetType().FullName ?? "?";
        var isTmp = componentType.IndexOf("TMPro.", StringComparison.Ordinal) == 0;

        // 字体必须在写回**之前**准备好：先写字再补字体，玩家会先看到一屏方框。
        EnsureFontCanRender(state.Component, reply.Translated);

        var written = TryApplyTranslation(reply.ComponentId, reply.Translated);
        if (written)
        {
            state.Translated = reply.Translated;
        }

        if (written)
        {
            RememberTranslation(reply.ComponentId, state.Source, reply.Translated);
            state.WrittenMs = TranslatorState.NowMs();
            LogEndToEnd(reply.ComponentId, state);
        }
        NoteWriteAttempt(reply.ComponentId, written, reply.Translated);
        RecordWriteBack(isTmp, written, componentType, reply.ComponentId, state.Source, reply.Translated);
        return written;
    }

    /// <summary>
    /// 主请求的答复到了 → 注销在飞登记，并把同一句译文补给**所有搭车组件**。
    ///
    /// ★ 每条搭车组件都**逐个走 <see cref="ReplacementPolicy.ReplyMatches"/> 校验**，
    ///   不是无脑照抄：搭车期间它可能已被销毁、或原文被游戏改掉，那就不该写。
    /// ★ 这里没有把答复塞回队列"假装是它自己的回包"，而是直接复用 <see cref="WriteBack"/> ——
    ///   `WriteBack` 的校验全部基于**状态自己的** Generation/RequestId（搭车时已对齐到主请求），
    ///   所以复用不会引入跨组件的错误写入；参数也仍然只有 BepInEx 类型。
    /// </summary>
    private void FlushDedup(InFlightRequest flight, string translated)
    {
        TranslatorState.InFlight.Remove(flight.Source);
        if (flight.Dependents.Count == 0) return;

        var dependents = flight.Dependents.ToArray();
        var applied = 0;
        foreach (var dependentId in dependents)
        {
            if (!TranslatorState.States.TryGetValue(dependentId, out var dependent)) continue;
            if (dependent.Translated != null) continue;
            var echo = new ReplyMessage
            {
                Version = 1,
                RequestId = dependent.RequestId,
                Translated = translated,
                ComponentId = dependentId,
                Generation = dependent.Generation,
            };
            if (WriteBack(echo)) applied++;
        }
        if (applied > 0 && TranslatorState.DedupAttached <= 20)
            Say("[去重] 同原文的译文已补给 " + applied + " 个搭车组件（省下 "
                + applied + " 次联网翻译）");
        LogDedupSummary();
    }

    /// <summary>去重汇总：只在真的省下过请求时打一行，让用户能从日志里看出效果。</summary>
    private void LogDedupSummary()
    {
        if (TranslatorState.DedupSummaryLogged || TranslatorState.DedupSkipped < 5) return;
        TranslatorState.DedupSummaryLogged = true;
        Say("[去重] 已省下 " + TranslatorState.DedupSkipped
            + " 条重复请求（同原文在飞时搭车，翻译仍由 App 做并走它的校验）");
    }

    /// <summary>
    /// 分类记录一次写回结果。
    ///
    /// ★★ 第 100 轮改：日志从"**前 12 条**之后静默"改成"**每个组件各一条**"。
    ///
    /// 为什么必须改（我埋点上一个真实缺口，害我查不出根因）：
    ///   原来是 `if (UiTextLogShown < 12)` —— 全局计数器，第 12 条之后**一个字都不打**。
    ///   实测 112 条写回里只看得到最早那 12 条（都是设置菜单的 Voice/Back/Reload），
    ///   而**真正出问题的那组组件**（装備/装備品/アクセサリ設備…，以及底部对话框那一行）
    ///   **一条日志都没留下**：既不知道它们走了哪条字体分支，也不知道是被静默跳过还是写进去了。
    ///   于是"屏幕上还是日文"这个现象，无法判断是渲染问题还是写回问题。
    ///
    /// 这正是本项目方法论第 5 条踩的第 6 次：**会静默失败的检查比没有检查更危险**。
    ///   按组件去重后，每个组件都会留下自己的一行（含组件 id），
    ///   再配合 EnsureFontCanRender 里同样按组件记录的字体分支，两边用 id 一对就能定案。
    ///   仍保留 800 行安全上限，避免异常情况刷爆日志
    ///   （B85 就是因为日志被刷爆，把"埋点没生效"误判成"没有请求"）。
    /// </summary>
    private static void RecordWriteBack(bool isTmp, bool written, string componentType,
        long componentId, string source, string translated)
    {
        var tag = isTmp ? "[TMP]" : "[UI.Text]";
        if (isTmp)
        {
            if (written) TranslatorState.TmpApplied++; else TranslatorState.TmpFailed++;
        }
        else
        {
            if (written) TranslatorState.UiTextApplied++; else TranslatorState.UiTextFailed++;
        }

        var seen = isTmp ? TranslatorState.TmpWriteLogged : TranslatorState.UiWriteLogged;
        if (!seen.Add(componentId)) return;                  // 这个组件已经报过一条了
        if (TranslatorState.WriteLogTotal >= 800) return;    // 安全上限，防刷屏
        TranslatorState.WriteLogTotal++;

        // 组件 id 必须在行里：它是把这一行与"该组件走了哪条字体分支"对起来的唯一钥匙。
        Say((written ? "写回成功" : "写回失败（组件拒绝写入）") + " " + tag
            + " id=" + componentId + " " + componentType
            + ": \"" + OneLine(source) + "\" -> \"" + OneLine(translated) + "\"");
    }

    /// <summary>把多行原文压成一行，否则日志会被换行撑散、难以比对。</summary>
    private static string OneLine(string text) =>
        text is null ? "" : text.Replace("\r", " ").Replace("\n", " ");

    /// <summary>
    /// 周期性打印写回统计（每 10 秒最多一次，且计数有变化时才打）。
    /// 有了它，"这一轮到底写回了多少、两类各自失败多少"不必再靠日志行数去猜。
    /// </summary>
    private void LogWriteBackSummary()
    {
        var total = TranslatorState.UiTextApplied + TranslatorState.TmpApplied
                    + TranslatorState.UiTextFailed + TranslatorState.TmpFailed;
        if (total == _lastSummaryTotal) return;
        if (Time.unscaledTime - _lastSummaryTime < 10f) return;

        _lastSummaryTotal = total;
        _lastSummaryTime = Time.unscaledTime;
        Say("写回统计：UI.Text 成功=" + TranslatorState.UiTextApplied
            + " 失败=" + TranslatorState.UiTextFailed
            + "；TMP 成功=" + TranslatorState.TmpApplied
            + " 失败=" + TranslatorState.TmpFailed);
    }

    private void Scan()
    {
        var reflection = UnityReflectionBridge.Reflection;
        var textType = reflection.TextComponentType;
        if (textType == null)
        {
            if (_scannedLogged++ < 1) Say("找不到 TMP 文字类型，跳过扫描");
            return;
        }

        var resources = reflection.ResolveType("UnityEngine.CoreModule", "UnityEngine.Resources");
        if (resources == null) return;

        // Resources.FindObjectsOfTypeAll 的参数是 Il2CppSystem.Type，不是 System.Type。
        // 使用官方非泛型 From(System.Type) 重载，避免对 Of<T>() 做反射泛型实例化。
        var il2cppTypeFactory = reflection.ResolveType("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.Il2CppType");
        if (il2cppTypeFactory == null) { Say("找不到 Il2CppInterop.Runtime.Il2CppType"); return; }
        var fromSystemType = UnityReflection.ResolveIl2CppTypeFromSystemType(il2cppTypeFactory);
        if (fromSystemType == null) { Say("找不到 Il2CppType.From(System.Type)"); return; }


        // ★★ 用**多个候选类型**分别查询再取并集。
        //
        //   实测教训（第二款 IL2CPP 游戏 赤莲忍法帖）：只查 TMPro.TextMeshProUGUI 时 `组件=0`。
        //   原因是 UnityReflection.TextComponentType 一旦解析出 TextMeshProUGUI 就**不会**再退回
        //   基类 TMP_Text，于是游戏自有的 TMP 子类、或 3D 版 TextMeshPro 组件全都扫不到。
        //   Unity 的 FindObjectsOfTypeAll(Type) 语义是"所有可赋值给该类型的对象"，
        //   所以查基类 TMP_Text 覆盖面最大；这里两个都查、按指针去重。
        var scanTypes = new List<Type>();
        var baseTextType = reflection.ResolveType("Unity.TextMeshPro", "TMPro.TMP_Text");
        if (baseTextType is not null) scanTypes.Add(baseTextType);
        if (!scanTypes.Contains(textType)) scanTypes.Add(textType);

        // ★★ 旧版 UnityEngine.UI.Text 也要扫。
        //   实测（赤莲忍法帖 / IL2CPP）：两种 TMP 类型都查到 0 个 —— 这款游戏根本没用 TMP，
        //   用的是旧版 UnityEngine.UI.Text。只扫 TMP 的话这类游戏永远是"组件=0"。
        //   Mono 侧早就有旧版 Text 的中文路径（Font.CreateDynamicFontFromOSFont），
        //   这里把扫描面补齐；读写仍走同一套 text 反射（旧版 Text 也有 text 属性）。
        var legacyTextType = reflection.ResolveType("UnityEngine.UIModule", "UnityEngine.UI.Text")
                             ?? reflection.ResolveType("UnityEngine.UI", "UnityEngine.UI.Text");
        if (legacyTextType is not null && !scanTypes.Contains(legacyTextType)) scanTypes.Add(legacyTextType);

        var rawComponents = new List<KeyValuePair<object, Type>>();
        var seenPointers = new HashSet<long>();
        foreach (var scanType in scanTypes)
        {
            object? scanIl2CppType;
            try { scanIl2CppType = fromSystemType.Invoke(null, new object[] { scanType }); }
            catch (Exception) { continue; }
            if (scanIl2CppType is null) continue;

            var findMethod = resources.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    return m.Name == "FindObjectsOfTypeAll" && parameters.Length == 1 &&
                        parameters[0].ParameterType == fromSystemType.ReturnType;
                });
            if (findMethod is null) { Say("找不到 FindObjectsOfTypeAll(Type)"); return; }

            object? found;
            try { found = findMethod.Invoke(null, new[] { scanIl2CppType }); }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException)
            {
                if (_scannedLogged++ < 3) Say("FindObjectsOfTypeAll 调用失败: " + ex.GetType().Name + ": " + ex.Message);
                continue;
            }

            var batch = UnityReflection.EnumerateObjects(found);
            var added = 0;
            foreach (var item in batch)
            {
                var pointer = reflection.ReadPointer(item);
                if (pointer is not null && !seenPointers.Add(pointer.Value)) continue;
                rawComponents.Add(new KeyValuePair<object, Type>(item, scanType));
                added++;
            }
            if (_scanTypeLogged++ < 6)
                Say("[扫描类型] " + scanType.FullName + " → 查到 " + batch.Count + " 个（新增 " + added + "）");
        }

        var observed = new HashSet<long>();
        var translatable = 0;

        // ★★ 关键一步：把每个对象**按它自己的来源类型**重新包装。
        //
        //   FindObjectsOfTypeAll 的返回数组在互操作层是 UnityEngine.Object[]，
        //   元素因此都被包成基类 Object，反射找不到 text 属性。
        //   用原生指针按互操作类型重新构造一次，属性就能正常读写。
        //   ★ 必须按**各自的来源类型**包装：TMP 组件包成 TMP 类型、
        //     旧版 UI.Text 包成 UnityEngine.UI.Text —— 混用会取不到正确的成员。
        var typedComponents = new List<object>(rawComponents.Count);
        var rewrapFailed = 0;
        foreach (var pair in rawComponents)
        {
            var typed = reflection.RewrapAs(pair.Key, pair.Value);
            if (typed is null) { rewrapFailed++; continue; }
            typedComponents.Add(typed);
        }

        if (_rewrapLogged < 4)
        {
            _rewrapLogged++;
            Say("[重新包装] 组件=" + rawComponents.Count + " 成功=" + typedComponents.Count
                + " 失败=" + rewrapFailed + "（按各自来源类型包装）");
        }
        if (typedComponents.Count == 0 && rawComponents.Count > 0)
        {
            if (_scannedLogged++ < 3) Say("重新包装全部失败，无法读取文字");
            return;
        }

        // 按类型分开点清：TMP 与旧版 UI.Text 是两条完全不同的字体路径，
        // 合成一个数字就看不出"到底哪一类有文字、哪一类在被送翻"。
        var uiTextWithText = 0; var tmpWithText = 0;
        var uiTextSent = 0; var tmpSent = 0;

        foreach (var component in typedComponents)
        {
            if (!reflection.IsActiveAndEnabled(component)) continue;
            var text = reflection.ReadText(component);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var isTmpComponent = (component.GetType().FullName ?? "")
                .IndexOf("TMPro.", StringComparison.Ordinal) == 0;
            if (isTmpComponent) tmpWithText++; else uiTextWithText++;

            var id = GetStableId(component);
            observed.Add(id);

            if (TranslatorState.States.TryGetValue(id, out var existing))
            {
                existing.Component = component;
                if (text == existing.Translated) continue;
                if (text == existing.Source)
                {
                    // 游戏把我们的译文改回了原文（或组件被重建），重新送一次
                    if (TextSelectionPolicy.RetryDue(existing.Attempts, existing.LastSentAt, Time.unscaledTime))
                    {
                        // 同原文已有请求在飞 → 搭车，不重复联网（见 TranslatorState.InFlight）
                        if (AttachToInFlight(id, existing.Source)) continue;
                        existing.RequestId = Guid.NewGuid().ToString("N");
                        existing.LastSentAt = Time.unscaledTime;
                        existing.Attempts++;
                        SendRequest(id, existing.Source, existing.RequestId, existing.Generation);
                    }
                    continue;
                }
                // The text CHANGED for a component we already track -- the game is still typing
                // this line, or it reused the component for a new line.
                //
                // Abandon the old state HERE rather than falling through and keeping two states
                // for one component. Without this the abandoned state keeps its own `Source`
                // (an intermediate prefix such as "黒岩市が") and the settle loop would still send
                // it once it aged past the debounce window -- the prefix flood would survive the
                // debounce. It also keeps one live state per component, which is what RetryDue /
                // ReplyMatches / FlushDedup already assume.
                existing.Layout?.Restore(component);
                TranslatorState.States.Remove(id);
            }

            var state = new TrackedTextState
            {
                Source = text,
                Component = component,
                Layout = new ReflectionTextLayout(component),
                Generation = ++TranslatorState.NextGeneration,
            };
            TranslatorState.States[id] = state;

            if (TextSelectionPolicy.IsTranslatable(text))
            {
                translatable++;
                if (isTmpComponent) tmpSent++; else uiTextSent++;

                // ★★ Request debounce. Do NOT send yet: remember the text and let it settle.
                //    If the game keeps typing, the intermediate prefixes are simply never sent,
                //    which is the whole point (see TrackedTextState for the measured numbers).
                //    The send happens in the settle loop further down.
                state.DiscoveredMs = TranslatorState.NowMs();
                state.Pending = text;
                state.PendingSince = Time.unscaledTime;
                state.PendingFirstSeen = Time.unscaledTime;
                state.PendingSent = false;
                if (TranslatorState.DebounceTrace && TranslatorState.DebounceHeld < 40)
                {
                    TranslatorState.DebounceHeld++;
                    Say("[debounce] holding " + id + " (text may still be typing): \""
                        + OneLine(text) + "\"");
                }
                continue;
            }
        }

        // ---- send whatever has SETTLED (or waited too long) ------------------------------
        // Two bounds, both needed:
        //   * settle: text unchanged for DebounceSeconds -> it is not being typed any more;
        //   * cap:    text has existed for DebounceMaxWaitSeconds -> send anyway, so a
        //             continuously animating component can never be starved of translation.
        // The predicate itself lives in Core so that it is UNIT TESTED
        // (TranslationDebounceTests); keep the two in step.
        // ★★ 发送循环。**不能**用 `if (DebounceSeconds > 0f)` 把整段包起来 ——
        //    那样 `UT_DEBOUNCE_SEC=0`（关掉去抖、回到修复前行为，做 A/B 对照用的）
        //    会变成"一个字都不翻"，而不是"立刻发"。实测这个坑我自己差点踩进去。
        //    判据交给 Core 的 `ShouldSendNow`：settleSeconds<=0 时它直接返回 true。
        {
            var now = Time.unscaledTime;
            foreach (var state in TranslatorState.States.Values)
            {
                // It may have become inactive while its text was settling. Cleanup runs below.
                if (!reflection.IsActiveAndEnabled(state.Component)) continue;
                if (state.PendingSent) continue;
                if (string.IsNullOrEmpty(state.Pending)) continue;
                if (state.Pending != state.Source) continue;
                if (!TextSelectionPolicy.IsTranslatable(state.Pending)) continue;
                if (!TranslationDebounce.ShouldSendNow(
                        TranslatorState.DebounceSeconds, TranslatorState.DebounceMaxWaitSeconds,
                        state.PendingSince, state.PendingFirstSeen, now, state.PendingSent))
                    continue;

                // ★ 预热的落点：**限制每秒发出的条数**。
                //
                //   实机实测：打开道具栏时 ~26 条说明在**同一瞬间**一起发出去，
                //   服务端排队 → 最慢那条 5.7 秒（中位数仍是 ~1.7 秒）。那不是单条慢，
                //   是**同时开火**造成的。所以这里按秒给配额，把爆发摊开：
                //   第一批立刻出中文，其余在随后几秒内陆续到位，而不是全部一起挤。
                //
                //   `UT_PREWARM=0` 关掉限流（回到"有多少发多少"）。
                if (!TranslatorState.PrewarmDisabled && TranslatorState.PrewarmPerSecond > 0)
                {
                    var nowSec = Time.unscaledTime;
                    if (nowSec - TranslatorState.PrewarmWindowStart >= 1f)
                    {
                        TranslatorState.PrewarmWindowStart = nowSec;
                        TranslatorState.PrewarmInWindow = 0;
                    }
                    if (TranslatorState.PrewarmPerSecond > 0 && TranslatorState.PrewarmInWindow >= TranslatorState.PrewarmPerSecond) continue;
                    TranslatorState.PrewarmInWindow++;
                }
                state.PendingSent = true;
                var sid = GetStableId(state.Component);
                if (AttachToInFlight(sid, state.Pending)) continue;
                state.RequestId = Guid.NewGuid().ToString("N");
                state.LastSentAt = now;
                state.Attempts++;
                SendRequest(sid, state.Pending, state.RequestId, state.Generation);
                if (TranslatorState.DebounceTrace && TranslatorState.DebounceSent < 40)
                {
                    TranslatorState.DebounceSent++;
                    Say("[debounce] sending " + sid + ": \"" + OneLine(state.Pending) + "\"");
                }
            }
        }

        // 组件消失了就清掉记录
        foreach (var id in TranslatorState.States.Keys.ToArray())
            if (!observed.Contains(id))
            {
                var vanished = TranslatorState.States[id];
                vanished.Layout?.Restore(vanished.Component);
                TranslatorState.States.Remove(id);
            }

        if (_scannedLogged < 3)
        {
            _scannedLogged++;
            Say("扫描: 组件=" + typedComponents.Count + " 跟踪中=" + TranslatorState.States.Count
                + " 本次可翻译=" + translatable + " 已连接=" + (TranslatorState.Pipe?.IsConnected ?? false));
            Say("扫描分类: 有文字 UI.Text=" + uiTextWithText + " TMP=" + tmpWithText
                + "；本轮送翻 UI.Text=" + uiTextSent + " TMP=" + tmpSent);
        }
    }

    /// <summary>
    /// 用对象指针当稳定 ID。IL2CPP 下指针是 64 位（实测约 2.4e12），
    /// 用 int 会直接溢出，所以全程用 long。
    ///
    /// 指针读取交给 UnityReflection.ReadPointer：它同时处理 System.IntPtr 与
    /// Il2CppSystem.IntPtr。早期版本这里写 <c>is IntPtr</c>，对 Il2CppSystem.IntPtr
    /// 匹配失败，掉到 GetHashCode() —— 托管包装实例的哈希会变，同一个组件每轮扫描
    /// 都被当成新组件，于是无限重发翻译请求。
    /// </summary>
    private static long GetStableId(object component) =>
        UnityReflectionBridge.Reflection.ReadPointer(component) ?? component.GetHashCode();

    /// <summary>
    /// 写回前确保组件字体能显示这段译文（**IL2CPP 的中文字体路径**）。
    ///
    /// 只处理旧版 UnityEngine.UI.Text：它有
    /// <c>Font.CreateDynamicFontFromOSFont(name, size)</c> 这条简单路径 ——
    /// 直接拿一个用系统字体渲染的动态字体，赋给 <c>Text.font</c> 即可，
    /// 不需要像 TMP 那样自己装配 SDF 图集。
    /// （该 API 在 Mono 侧的实机日志里已证明本机可用：SimHei / 微软雅黑 / 宋体 全部"成功"。）
    ///
    /// TMP 组件这里**跳过** —— 它的中文装配要遍历 FontEngine / TextCore 的一大批类型，
    /// 在 IL2CPP 反射下是另一项工作，不能在这里假装做了。
    /// </summary>
    private static void EnsureFontCanRender(object component, string translated)
    {
        try
        {
            var type = component.GetType();
            if (type.FullName != "UnityEngine.UI.Text")
            {
                // ★ TMP 的中文字体路径。
                //   实测（DECOY 截图）：游戏自带 TMP 字体是日文字体，简体独有的字渲染成空心方框。
                //   做法：建一个中文 Dynamic 字体资产，挂到 TMP 的回退字体表上，让 TMP 自己按需补字形。
                ReportTmpFont(component, translated);
                EnsureTmpFontCanRender(component, translated);
                return;
            }

            var fontProperty = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
            if (fontProperty is null || !fontProperty.CanRead || !fontProperty.CanWrite)
            {
                LogFontBranch(component, "★属性不可读写(直接返回,一个字都不会写)");
                return;
            }

            var currentFont = fontProperty.GetValue(component);
            if (FontHasGlyphs(currentFont, translated))
            {
                // 可观测性：把"当前字体已够用、不需要换"也记下来。
                // 否则日志里没有任何 [字体] 行时，分不清是"不需要换"还是"根本没走到这一步"
                // —— 实测就被这一点卡住过一轮。
                LogFontBranch(component, "已够用(字体=" + (currentFont?.GetType().FullName ?? "null") + ")");
                return;
            }

            var osFont = GetChineseOsFont();
            if (osFont is null)
            {
                // ★ 这一条是"译文写进去了但画不出来"最可疑的分支：
                //   字体缺字形 + 又拿不到中文字体。与 [字体分支] 同 id 的写回行一对照即可定案。
                LogFontBranch(component, "★★缺字形且拿不到中文字体(译文写进去也画不出来)");
                return;
            }

            fontProperty.SetValue(component, osFont);
            LogFontBranch(component, "已换成系统中文动态字体");
        }
        catch (Exception ex)
        {
            LogFontBranch(component, "★替换字体抛异常：" + Describe(ex));
        }
    }

    /// <summary>
    /// 记录"这个组件走了哪条字体分支"——**按组件 id 去重，每个组件只留一条**。
    ///
    /// 为什么必须按组件（而不是像原来那样全局 8 条）：
    ///   实测全局上限把日志吃光之后，出问题的那组组件一条都没留下，
    ///   于是"译文写进去了但画不出来"与"根本没写进去"这两种完全无法区分。
    ///   字体分支 + 写回结果两边都带组件 id，一对照就能定案。
    /// </summary>
    private static void LogFontBranch(object component, string branch)
    {
        long id;
        try { id = GetStableId(component); } catch (Exception) { return; }
        if (!TranslatorState.FontBranchLogged.Add(id)) return;
        if (TranslatorState.WriteLogTotal >= 800) return;
        TranslatorState.WriteLogTotal++;
        Say("[字体分支] id=" + id + " " + branch);
    }

    /// <summary>
    /// 记录"一条答复被丢掉了，以及为什么"——**按组件 id 去重，每个组件只留一条**。
    ///
    /// 为什么必须有它（第 100 轮实机症状）：
    ///   用户在 Symbiont 里看到底部对话框**一直是日文**，而 App 侧日志明明写着
    ///   `API=1033ms 结果=成功`、第二次还命中了缓存 —— 也就是**译文确实回到了插件**。
    ///   但屏幕上没有任何变化，而 `WriteBack` 的五条早退**全是静默 return**，
    ///   日志里一个字都没有，**无法判断它是在哪一条被丢掉的**。
    ///   这正是本项目方法论第 5 条（会静默失败的检查比没有检查更危险）的又一次实例。
    /// </summary>
    private static void LogDrop(long componentId, string reason)
    {
        if (!TranslatorState.DropLogged.Add(componentId)) return;
        if (TranslatorState.WriteLogTotal >= 800) return;
        TranslatorState.WriteLogTotal++;
        Say("[丢弃答复] id=" + componentId + " 原因：" + reason);
    }

    /// <summary>
    /// 端到端延迟埋点：把"原文出现 → 译文写到组件上"拆成三段。
    ///
    /// 为什么需要（第 100 轮）：用户说"正常文本显示的时候翻译过程还是有点太慢了"，
    /// 而我们当时**所有数字都是 App 侧的**（API P50 1670ms）或扫描侧的（<1ms）——
    /// **没有一个"原文出现到译文可见"的端到端数字**，所以"慢"到底是
    /// "我们太晚才发现"还是"服务本身慢"完全分不开。
    ///
    /// 三段（同一个进程内的单一 Stopwatch，差值有意义；不做跨进程时钟对齐）：
    ///   * 发现→发送：扫描节奏 + 去抖造成的等待（**这一段是我们自己的，可以调**）；
    ///   * 发送→回包：IPC + App 查库 + 联网 API（**这一段基本是服务的**）；
    ///   * 回包→写回：主线程什么时候把答复写上去。
    /// 每个组件只报一次（第一次成功写回时），并累计样本，每 100 条打一行汇总。
    /// </summary>
    private static void LogEndToEnd(long componentId, TrackedTextState state)
    {
        if (state.TimingLogged) return;
        state.TimingLogged = true;
        if (state.SentMs <= 0 || state.RecvMs <= 0 || state.WrittenMs <= 0) return;

        var ipc = state.RecvMs - state.SentMs;
        var write = state.WrittenMs - state.RecvMs;
        var total = state.WrittenMs - state.DiscoveredMs;
        var discoverToSend = state.SentMs - state.DiscoveredMs;
        if (total < 0) total = 0;
        if (ipc < 0) ipc = 0;
        if (write < 0) write = 0;
        if (discoverToSend < 0) discoverToSend = 0;

        TranslatorState.IpcSamples.Add(ipc);
        TranslatorState.WaitSamples.Add(write);
        TranslatorState.DiscoverToSendSamples.Add(discoverToSend);
        TranslatorState.TotalSamples.Add(total);

        if (TranslatorState.TimingLogShown++ < 80)
        {
            Say("[端到端] id=" + componentId + " 原文出现→译文可见=" + total
                + "ms（其中 发现→发送=" + discoverToSend
                + "ms 发送→回包=" + ipc
                + "ms 回包→写回=" + write + "ms）");
        }
        if (TranslatorState.TotalSamples.Count % 100 == 0)
        {
            Say("[端到端汇总] n=" + TranslatorState.TotalSamples.Count
                + " 合计 平均=" + Avg(TranslatorState.TotalSamples) + "ms"
                + " P95=" + Pct(TranslatorState.TotalSamples, 0.95) + "ms"
                + " | 发现→发送 平均=" + Avg(TranslatorState.DiscoverToSendSamples) + "ms"
                + " | 发送→回包 平均=" + Avg(TranslatorState.IpcSamples) + "ms"
                + " P95=" + Pct(TranslatorState.IpcSamples, 0.95) + "ms"
                + " | 回包→写回 平均=" + Avg(TranslatorState.WaitSamples) + "ms");
        }
    }

    private static long Avg(List<long> values)
    {
        if (values.Count == 0) return 0;
        long sum = 0;
        for (var i = 0; i < values.Count; i++) sum += values[i];
        return sum / values.Count;
    }

    private static long Pct(List<long> values, double p)
    {
        if (values.Count == 0) return 0;
        var copy = new List<long>(values);
        copy.Sort();
        var index = (int)(copy.Count * p);
        if (index >= copy.Count) index = copy.Count - 1;
        return copy[index];
    }

    /// <summary>
    /// 记住"这个组件的这句原文 → 这句译文"。
    ///
    /// 为什么要按**组件**记、而不是全局一张表：
    ///   底部对话框是**同一个组件**轮流显示不同台词（实机日志证明）：
    ///     "隔壁房间里也许有幸存者……" / "首先去帮助旁边的幸存者吧。" / "在周围找找，确认有没有其他路吧。"
    ///   每句都要能立刻写回去，而全局表无法回答"这个组件现在该显示哪句"。
    /// 每个组件最多记 64 句，防止长局里无限增长。
    /// </summary>
    private static void RememberTranslation(long componentId, string source, string translated)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated)) return;
        Dictionary<string, string> map;
        if (!TranslatorState.SeenByComponent.TryGetValue(componentId, out map!))
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            TranslatorState.SeenByComponent[componentId] = map;
        }
        if (map.Count >= 64 && !map.ContainsKey(source)) return;
        map[source] = translated;
    }

    /// <summary>
    /// ★★ 从"组件级小词典"里把译文直接写回去 —— **不联网、不等答复、不看待发**。
    ///
    /// 用户的判断是对的：**我们的保护策略太保守了。** 原来的守门只在
    /// `当前文字 == state.Source` 时才补写，而对话框换一句之后前提就不成立了，
    /// 于是只能重新走一遍"发请求 → 等服务"（1.2~5.7 秒）。
    ///
    /// 现在的判据非常简单粗暴，但**只做安全的事**：
    ///   组件现在的文字，只要在我们为它记过的词典里 → 立刻把对应译文写上去。
    /// 为什么安全：写进去的是**这个组件自己那句原文的译文**，不是别的组件的、也不是猜的；
    ///   组件一旦又变回原文（不管因为什么），我们下一轮（0.1 秒内）就补回来。
    /// </summary>
    private void ApplyFromMemory()
    {
        var reflection = UnityReflectionBridge.Reflection;
        foreach (var pair in TranslatorState.States)
        {
            Dictionary<string, string>? map;
            if (!TranslatorState.SeenByComponent.TryGetValue(pair.Key, out map)) continue;
            if (map.Count == 0) continue;
            string? current;
            try { current = reflection.ReadText(pair.Value.Component); }
            catch (Exception) { continue; }
            if (string.IsNullOrEmpty(current)) continue;
            string? known;
            if (!map.TryGetValue(current!, out known)) continue;      // 显示的是我们没见过的文字
            if (string.IsNullOrEmpty(known)) continue;
            var state = pair.Value;
            if (!string.Equals(state.Source, current, StringComparison.Ordinal))
            {
                // Memory runs before Scan. Release the previous line's fitting
                // before recording a reused component, otherwise the old shrink
                // becomes the next line's supposed native size.
                state.Layout?.Restore(state.Component);
                state.Layout = new ReflectionTextLayout(state.Component);
                state.Source = current!;
                state.Translated = null;
                state.Generation = ++TranslatorState.NextGeneration;
                state.RequestId = Guid.NewGuid().ToString("N");
                state.Pending = string.Empty;
                state.PendingSent = true;
            }
            bool ok;
            try { ok = TryApplyTranslation(pair.Key, known!); }
            catch (Exception) { continue; }
            if (ok) state.Translated = known;
            if (ok && TranslatorState.TmLogShown < 60)
            {
                TranslatorState.TmLogShown++;
                Say("[词典直写] id=" + pair.Key + " 组件换回我们译过的句子，立刻写回（零联网）：「"
                    + OneLine(current!) + "」");
            }
        }
    }

    /// <summary>All reply, guard and memory writes share measured fitting and ownership.</summary>
    private static bool TryApplyTranslation(long componentId, string translated)
    {
        if (!TranslatorState.States.TryGetValue(componentId, out var state)) return false;
        var layout = state.Layout ??= new ReflectionTextLayout(state.Component);
        var applied = layout.TryApply(state.Component, translated);
        if (applied && (state.Component.GetType().FullName ?? "").IndexOf("TMPro.", StringComparison.Ordinal) == 0)
            ForceTmpRefresh(state.Component);
        if (state.LayoutLogCount++ < 3)
            Say("[字号适配] id=" + componentId + " 原=" + layout.OriginalFontSize.ToString("0.##")
                + " 现=" + (UnityMemberReader.Read<float>(state.Component, "fontSize") ?? 0f).ToString("0.##")
                + " 成功=" + applied + (applied ? "" : " 原因=" + layout.FailureReason));
        return applied;
    }

    /// <summary>
    /// 记录"我们往这个组件写了几次"。
    ///
    /// 为什么必须有它（第 100 轮实机）：`[重新写回]` 已经证明我们**确实**把译文写进去了，
    /// 但用户屏幕上仍是原文 —— 那只可能是"我们写一次、游戏改回去一次"的**拉锯**。
    /// 判定拉锯唯一的直接证据就是**同一个组件的写回次数**：
    ///   写 1 次就再没动过 → 我们这边没问题，是渲染/别的组件；
    ///   同一个 id 写 2、3、4…次还在被还原 → 游戏每开一次菜单就重置一次，我们每次都得补。
    /// 每个组件最多报前 6 次，避免刷屏。
    /// </summary>
    private static void NoteWriteAttempt(long componentId, bool ok, string translated)
    {
        var counts = TranslatorState.WriteAttempts;
        int n;
        if (!counts.TryGetValue(componentId, out n)) n = 0;
        n++;
        counts[componentId] = n;
        if (n > 6) return;
        if (TranslatorState.WriteLogTotal >= 900) return;
        TranslatorState.WriteLogTotal++;
        Say("[写回次数] id=" + componentId + " 第 " + n + " 次" + (ok ? "成功" : "失败")
            + " -> \"" + OneLine(translated) + "\"");
    }

    /// <summary>
    /// TMP 组件的中文字体保障：把带简体字形的回退字体资产挂上去。
    ///
    /// 为什么用"回退字体表"而不是直接换掉 <c>TMP_Text.font</c>：
    /// 直接换字体等于丢掉游戏原来的字体风格与材质（描边/阴影/图集都在字体资产里），
    /// 画面会明显割裂 —— 这正是本项目之前"贴黑方块"被否掉的那类做法。
    /// 挂回退只补"原字体没有的字"，其余字形仍由游戏原字体渲染，观感保持一致。
    /// </summary>
    private static void EnsureTmpFontCanRender(object component, string translated)
    {
        try
        {
            // 回滚开关：环境变量 UT_TMP_CJK_FALLBACK=0 时完全不动字体（最小、可回滚的对照实验）。
            var toggle = Environment.GetEnvironmentVariable("UT_TMP_CJK_FALLBACK");
            if (toggle == "0")
            {
                if (TranslatorState.TmpFallbackLogShown++ < 3)
                    Say("[字体/TMP] UT_TMP_CJK_FALLBACK=0：本次实验不挂回退字体");
                return;
            }

            var fontProperty = component.GetType().GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
            var font = fontProperty?.GetValue(component);
            if (font is null) return;

            var availability = CheckGlyphs(font, translated, out var missing);

            // 探针失灵 → 什么也不做。宁可留着原字体，也不能因为"问不出来"就去改游戏字体。
            if (availability == 0)
            {
                if (TranslatorState.TmpGlyphLogShown++ < 10)
                    Say("[字体/TMP] 逐字探针不可用（这个 TMP 版本没有可用的 HasCharacter 重载），保持游戏字体不变");
                return;
            }

            // 原字体自己就够用（拉丁字母、日文汉字等）→ 一个字都不用补，保持原样。
            if (availability == 1)
            {
                if (TranslatorState.TmpGlyphLogShown++ < 10)
                    Say("[字体/TMP] 逐字检查通过（" + translated.Length + " 字全部有字形），保持游戏字体不变");
                return;
            }

            if (TranslatorState.TmpGlyphLogShown++ < 10)
                Say("[字体/TMP] 逐字检查缺 " + missing.Length + "/" + translated.Length
                    + " 个字形（缺：" + missing + "），挂中文回退字体；译文=\"" + OneLine(translated) + "\"");

            var cjk = GetOrCreateCjkTmpFontAsset();
            if (cjk is null) return;

            AttachFallbackFont(font, cjk);
            AttachGlobalFallbackFont(cjk);
            PrepopulateGlyphs(cjk, translated);
        }
        catch (Exception ex)
        {
            if (TranslatorState.TmpFallbackLogShown++ < 8)
                Say("[字体/TMP] 挂回退字体失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// 建一个中文 TMP 字体资产（只建一次）。用系统字体族名建，
    /// 内部是 Dynamic 图集 + 多图集，字形按需加入。
    ///
    /// ★ 候选字体只能"失败才试下一个"：
    ///   <c>CreateFontAsset</c> 内部会调 <c>FontEngine.LoadFontFace</c>，而 face 是**进程级全局状态**，
    ///   在同一进程里连续加载多个不同字体文件会原生崩溃（本项目实测 0xc0000005）。
    ///   找不到字族时它返回 null 且没有加载任何 face，所以那种情况下继续试是安全的。
    /// </summary>
    private static object? GetOrCreateCjkTmpFontAsset()
    {
        if (TranslatorState.CjkTmpFontAsset is not null) return TranslatorState.CjkTmpFontAsset;
        if (TranslatorState.CjkTmpFontAttempted) return null;
        TranslatorState.CjkTmpFontAttempted = true;

        var reflection = UnityReflectionBridge.Reflection;
        var fontAssetType = reflection.ResolveType("Unity.TextMeshPro", "TMPro.TMP_FontAsset")
                            ?? reflection.ResolveType("Unity.TextMeshPro", "TMPro.TMP_FontAsset");
        if (fontAssetType is null) { Say("[字体/TMP] 找不到 TMPro.TMP_FontAsset"); return null; }

        var allMethods = fontAssetType.GetMethods(BindingFlags.Public | BindingFlags.Static);

        // ★ 路线 A：CreateFontAsset(familyName, styleName, pointSize)。
        //   只有较新的 TMP 才有这个重载。
        var create = allMethods.FirstOrDefault(m => m.Name == "CreateFontAsset"
                                 && m.GetParameters().Length == 3
                                 && m.GetParameters()[0].ParameterType == typeof(string)
                                 && m.GetParameters()[1].ParameterType == typeof(string));

        // ★ 路线 B：CreateFontAsset(fontFilePath, faceIndex, pointSize, padding, renderMode,
        //                          atlasWidth, atlasHeight, populationMode, multiAtlasSupport)。
        //   这是更早就存在的重载，直接喂**系统字体文件路径**。
        //
        //   为什么必须补这条路：实测 圣骑士恩（Unity 2021.2.10）的 TMP **没有**路线 A，
        //   日志只留下一句"这个 TMP 版本没有 CreateFontAsset(familyName, styleName, pointSize)"
        //   就再无下文 —— 结果是回退字体资产建不出来，而该游戏字体
        //   （KingnamMaiyuan SDF）对简体字逐字检查是"缺 4/4"，写回去的中文只能显示成方框。
        var createFromFile = allMethods.FirstOrDefault(m => m.Name == "CreateFontAsset"
                                     && m.GetParameters().Length == 9
                                     && m.GetParameters()[0].ParameterType == typeof(string)
                                     && m.GetParameters()[1].ParameterType == typeof(int));

        // ★ 路线 C：CreateFontAsset(Font, pointSize, padding, renderMode,
        //                          atlasWidth, atlasHeight, populationMode, multiAtlas)。
        //   实测 圣骑士恩（Unity 2021.2.10，较老的 TMP）**只有**这一条和 CreateFontAsset(Font)：
        //   既没有 (familyName, styleName, pointSize)，也没有 9 参数的 (fontFilePath, …)。
        //   喂给它的 Font 用 Font.CreateDynamicFontFromOSFont 拿 —— TMP 内部会按
        //   font.name（字体族名）去 LoadFontFace，这条路在本机是验证过可用的。
        var createFromFont = allMethods.FirstOrDefault(m => m.Name == "CreateFontAsset"
                                     && m.GetParameters().Length == 8
                                     && m.GetParameters()[0].ParameterType.Name == "Font");

        if (create is null && createFromFile is null)
        {
            if (createFromFont is null)
            {
                Say("[字体/TMP] 这个 TMP 版本没有任何可用的 CreateFontAsset 重载 —— 无法建中文字体资产");
                return null;
            }

            Say("[字体/TMP] 这个 TMP 版本没有 (familyName,…)/(fontFilePath,…) 重载，改用 CreateFontAsset(Font,…)");

            var osFont = GetChineseOsFont();
            if (osFont is null)
            {
                Say("[字体/TMP] 拿不到可用的系统中文 Font 对象，无法建中文字体资产");
                // ★ 探针必须挂在**所有**失败出口上。
                //   上一轮只挂在路线 B 的出口，而圣骑士恩走的是路线 C 的这个出口，
                //   于是探针一次都没被调用、日志里没有 [探针] 行，白跑一轮。
                ProbeFontEngine();
                return null;
            }

            try
            {
                var asset = createFromFont.Invoke(null, new object[]
                {
                    osFont,
                    90,            // samplingPointSize
                    9,             // atlasPadding
                    ResolveGlyphRenderMode(fontAssetType, "SDFAA"),
                    1024,          // atlasWidth
                    1024,          // atlasHeight
                    ResolveAtlasPopulationMode(fontAssetType, "Dynamic"),
                    true,          // enableMultiAtlasSupport
                });

                if (asset is null)
                {
                    Say("[字体/TMP] CreateFontAsset(Font,…) 返回空");
                    return null;
                }

                TranslatorState.CjkTmpFontAsset = asset;
                Say("[字体/TMP] 已用系统 Font 创建中文回退字体资产（Dynamic 图集）");
                return asset;
            }
            catch (Exception ex)
            {
                Say("[字体/TMP] CreateFontAsset(Font,…) 抛异常：" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        // 路线 A 的候选：系统字体族名
        var candidates = new[]
        {
            new[] { "Microsoft YaHei", "Regular" },
            new[] { "Microsoft YaHei UI", "Regular" },
            new[] { "SimHei", "Regular" },
            new[] { "SimSun", "Regular" },
        };

        if (create is not null)
        {
            foreach (var candidate in candidates)
            {
                object? asset = null;
                try
                {
                    asset = create.Invoke(null, new object[] { candidate[0], candidate[1], 90 });
                }
                catch (Exception ex)
                {
                    Say("[字体/TMP] CreateFontAsset(" + candidate[0] + ") 抛异常："
                        + ex.GetType().Name + " " + ex.Message);
                }

                if (asset is null) continue;   // 字族没找到 → 没加载 face → 可以安全试下一个

                TranslatorState.CjkTmpFontAsset = asset;
                Say("[字体/TMP] 已创建中文回退字体资产：" + candidate[0] + " " + candidate[1]
                    + "（Dynamic 图集，字形按需加入）");
                return asset;
            }
        }
        else
        {
            Say("[字体/TMP] 这个 TMP 版本没有 CreateFontAsset(familyName, styleName, pointSize)，改用字体文件路径");
        }

        // 路线 B：直接用 Windows 系统字体文件。只试**一个**文件就收手 ——
        // CreateFontAsset 内部会调 FontEngine.LoadFontFace，而 face 是进程级全局状态，
        // 连续加载多个不同字体文件会原生崩溃（本项目实测 0xc0000005）。
        foreach (var file in new[] { "msyh.ttc", "simhei.ttf", "simsun.ttc" })
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", file);
            if (!System.IO.File.Exists(path)) continue;

            object? asset = null;
            try
            {
                asset = createFromFile!.Invoke(null, new object[]
                {
                    path,          // fontFilePath
                    0,             // faceIndex
                    90,            // samplingPointSize
                    9,             // atlasPadding
                    // GlyphRenderMode.SDFAA —— 用**名字**取枚举值，不要按序号（序号会变，
                    // 早期版本按序号取到过 bitmap 模式，喂给 SDF shader 就是一堆空心方块）。
                    ResolveGlyphRenderMode(fontAssetType, "SDFAA"),
                    1024,          // atlasWidth
                    1024,          // atlasHeight
                    ResolveAtlasPopulationMode(fontAssetType, "Dynamic"),
                    true });       // enableMultiAtlasSupport
            }
            catch (Exception ex)
            {
                Say("[字体/TMP] CreateFontAsset(" + file + ") 抛异常：" + ex.GetType().Name + " " + ex.Message);
                break;             // 已经动过 face 了，不再试下一个，避免原生崩溃
            }

            if (asset is null) continue;

            TranslatorState.CjkTmpFontAsset = asset;
            Say("[字体/TMP] 已用字体文件创建中文回退字体资产：" + file + "（Dynamic 图集）");
            return asset;
        }

        Say("[字体/TMP] 两条路线都没能建出中文字体资产");
        ProbeFontEngine();
        return null;
    }

    /// <summary>
    /// 诊断：这个游戏里 TextCore 的 <c>FontEngine</c> 还有哪些入口是活的。
    ///
    /// 为什么需要：圣骑士恩上取系统字的 `Font.CreateDynamicFontFromOSFont` 报
    /// <c>NotSupportedException: Method unstripping failed</c>（IL2CPP 托管代码剥离把原生实现删了）。
    /// 那么"退一步，自己用 FontEngine 装配 SDF 图集"（即 Mono 侧 RuntimeCjkFontAsset 那条路）
    /// 到底还可不可行？**必须实地问一次**：
    /// 如果 FontEngine.LoadFontFace 也被剥离，那条路就直接判死，不必再投入；
    /// 如果它是活的，说明还有一条可以走的路，值得下一步去做。
    ///
    /// 注意：这里只做**一次**探测，且只加载**一个**字体文件 ——
    /// face 是进程级全局状态，连续 LoadFontFace 多个不同文件会原生崩溃（本项目实测 0xc0000005）。
    /// </summary>
    private static void ProbeFontEngine()
    {
        if (TranslatorState.FontEngineProbed) return;
        TranslatorState.FontEngineProbed = true;

        try
        {
            var reflection = UnityReflectionBridge.Reflection;
            var engine = reflection.ResolveType("UnityEngine.TextCoreFontEngineModule",
                              "UnityEngine.TextCore.LowLevel.FontEngine")
                         ?? reflection.ResolveType("UnityEngine.TextCoreModule",
                              "UnityEngine.TextCore.LowLevel.FontEngine");
            if (engine is null)
            {
                Say("[探针] 找不到 FontEngine 类型 —— 手工装配 SDF 图集这条路不可行");
                return;
            }

            var statics = engine.GetMethods(BindingFlags.Public | BindingFlags.Static);
            var loaders = statics.Where(m => m.Name == "LoadFontFace").ToArray();
            var described = string.Join(" | ", loaders.Select(m =>
                "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")").ToArray());
            Say("[探针] FontEngine 公开静态方法=" + statics.Length + "；LoadFontFace 重载：" + described);

            var byBytes = loaders.FirstOrDefault(m => m.GetParameters().Length >= 2
                                                      && IsByteArrayLike(m.GetParameters()[0].ParameterType));
            if (byBytes is null)
            {
                Say("[探针] 没有任何接受字节数组的 LoadFontFace 重载 —— 按文件字节装配这条路不可行");
                return;
            }

            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "msyh.ttc");
            if (!System.IO.File.Exists(path)) { Say("[探针] 找不到 " + path); return; }

            var bytes = System.IO.File.ReadAllBytes(path);

            // ★ 必须用 Il2CppStructArray<byte> 传参。
            //   Il2CppInterop 把托管 byte[] 映射成 Il2CppStructArray<byte>，
            //   所以 `ParameterType == typeof(byte[])` **匹配不上**，
            //   早先的探针因此把"重载存在"误报成"没有该重载"（与 HasCharacter 那次同类错误）。
            var arrayType = byBytes.GetParameters()[0].ParameterType;
            object payload;
            try
            {
                payload = Activator.CreateInstance(arrayType, new object[] { bytes.Length })!;
                var indexer = arrayType.GetProperty("Item");
                for (var i = 0; i < bytes.Length; i++) indexer?.SetValue(payload, bytes[i], new object[] { i });
            }
            catch (Exception ex)
            {
                var innerEx = ex.InnerException ?? ex;
                Say("[探针] 构造 " + arrayType.Name + " 失败：" + innerEx.GetType().Name + " " + innerEx.Message);
                return;
            }

            var probeArgs = byBytes.GetParameters().Length == 2
                ? new object[] { payload, 90 }
                : new object[] { payload, 90, 0 };

            var result = byBytes.Invoke(null, probeArgs);
            Say("[探针] FontEngine.LoadFontFace(" + bytes.Length + " 字节, …) 返回 = " + result
                + "（Success 表示按字节装配这条路是活的）");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            Say("[探针] FontEngine 探测失败：" + inner.GetType().Name + " " + inner.Message);
        }
    }

    /// <summary>
    /// 这个参数类型是不是"字节数组"。
    /// 除了 <c>byte[]</c>，还必须认 <c>Il2CppStructArray&lt;byte&gt;</c> ——
    /// Il2CppInterop 就是用后者表示托管 byte[] 的，漏掉它会把"有重载"判成"没重载"。
    /// </summary>
    private static bool IsByteArrayLike(Type type)
    {
        if (type == typeof(byte[])) return true;
        var name = type.Name ?? string.Empty;
        if (name.IndexOf("Il2CppStructArray", StringComparison.Ordinal) < 0) return false;
        var args = type.GetGenericArguments();
        return args.Length == 1 && args[0] == typeof(byte);
    }

    /// <summary>按**名字**取枚举值（序号会随版本变化，不能按序号）。取不到返回 0。</summary>
    private static object ResolveGlyphRenderMode(Type fontAssetType, string name)
    {
        var enumType = fontAssetType.Assembly.GetType("UnityEngine.TextCore.LowLevel.GlyphRenderMode")
                       ?? UnityReflectionBridge.Reflection.ResolveType(
                              "UnityEngine.TextCoreFontEngineModule", "UnityEngine.TextCore.LowLevel.GlyphRenderMode")
                       ?? UnityReflectionBridge.Reflection.ResolveType(
                              "UnityEngine.TextCoreModule", "UnityEngine.TextCore.LowLevel.GlyphRenderMode");
        if (enumType is null) return 0;
        try { return Enum.Parse(enumType, name); } catch (Exception) { return 0; }
    }

    /// <summary>按名字取 AtlasPopulationMode（Dynamic / Static）。</summary>
    private static object ResolveAtlasPopulationMode(Type fontAssetType, string name)
    {
        var enumType = fontAssetType.Assembly.GetType("TMPro.AtlasPopulationMode");
        if (enumType is null) return 0;
        try { return Enum.Parse(enumType, name); } catch (Exception) { return 0; }
    }

    /// <summary>把中文字体资产加进"组件自己字体"的回退表 —— 这样只补缺字，不动原字体风格。</summary>
    private static void AttachFallbackFont(object gameFont, object cjkAsset)
    {
        var tableProperty = gameFont.GetType().GetProperty("fallbackFontAssetTable",
            BindingFlags.Public | BindingFlags.Instance);
        if (tableProperty is null || !tableProperty.CanRead) return;

        var table = tableProperty.GetValue(gameFont);
        if (table is null)
        {
            // 表为空时 TMP 允许直接赋值一个新列表
            if (!tableProperty.CanWrite) return;
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(cjkAsset.GetType());
            var fresh = Activator.CreateInstance(listType);
            tableProperty.SetValue(gameFont, fresh);
            table = fresh;
            if (table is null) return;
        }

        if (ListContains(table, cjkAsset)) return;
        var add = table.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
        add?.Invoke(table, new[] { cjkAsset });

        if (TranslatorState.TmpFallbackLogShown++ < 8)
            Say("[字体/TMP] 已把中文回退字体挂到组件字体 " + DescribeFont(gameFont) + " 的回退表上");
    }

    /// <summary>全局回退：TMP_Settings.fallbackFontAssets —— 之后新建的 TMP 组件也能受益。</summary>
    private static void AttachGlobalFallbackFont(object cjkAsset)
    {
        if (TranslatorState.TmpGlobalFallbackInstalled) return;

        var settingsType = UnityReflectionBridge.Reflection
            .ResolveType("Unity.TextMeshPro", "TMPro.TMP_Settings");
        if (settingsType is null) return;

        var property = settingsType.GetProperty("fallbackFontAssets",
            BindingFlags.Public | BindingFlags.Static);
        if (property is null || !property.CanRead) return;

        var table = property.GetValue(null);
        if (table is null) return;

        TranslatorState.TmpGlobalFallbackInstalled = true;
        if (ListContains(table, cjkAsset)) return;

        var add = table.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
        add?.Invoke(table, new[] { cjkAsset });
        Say("[字体/TMP] 已把中文回退字体挂到 TMP_Settings.fallbackFontAssets（全局）");
    }

    /// <summary>
    /// 预先把这个译文用到的字形加进中文字体资产的图集。
    /// 用 <c>TryAddCharacters</c> 而不是等 TMP 渲染时再补，是为了拿到一个**可验证的布尔结果**
    /// —— 日志里能直接看到"这批字到底加进去了没有"，而不是靠画面猜。
    /// </summary>
    private static void PrepopulateGlyphs(object cjkAsset, string translated)
    {
        try
        {
            var tryAdd = cjkAsset.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "TryAddCharacters"
                                     && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (tryAdd is null) return;

            var ok = tryAdd.Invoke(cjkAsset, new object[] { translated, false }) is bool added && added;
            if (TranslatorState.TmpGlyphLogShown++ < 10)
                Say("[字体/TMP] TryAddCharacters(\"" + OneLine(translated) + "\") = " + ok);
        }
        catch (Exception ex)
        {
            if (TranslatorState.TmpGlyphLogShown++ < 10)
                Say("[字体/TMP] 预加字形失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>改完字体表后重建 TMP 网格，否则屏幕上仍是旧的方框。</summary>
    private static void ForceTmpRefresh(object component)
    {
        try
        {
            var method = component.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ForceMeshUpdate" && m.GetParameters().Length == 2);
            method?.Invoke(component, new object[] { false, false });
        }
        catch (Exception) { /* 刷新失败不影响已经写进去的文本 */ }
    }

    /// <summary>列表里是否已经有这个元素（比较托管包装对象本身）。</summary>
    private static bool ListContains(object list, object item)
    {
        try
        {
            var contains = list.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Contains" && m.GetParameters().Length == 1);
            return contains?.Invoke(list, new[] { item }) is bool has && has;
        }
        catch (Exception) { return false; }
    }

    /// <summary>字体资产的可读描述，用于日志。</summary>
    private static string DescribeFont(object font)
    {
        try
        {
            var name = font.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(font) as string;
            return (string.IsNullOrEmpty(name) ? "?" : name) + "(" + font.GetType().FullName + ")";
        }
        catch (Exception) { return font.GetType().FullName ?? "?"; }
    }

    /// <summary>
    /// 报告 TMP 组件当前的字体状况（**只读，不改字体**）。
    ///
    /// 为什么先只报告不改：TMP 的中文字形要落进 TMP_FontAsset 的图集才会显示，
    /// 而"这个字体资产有没有这些字形"是可以直接问出来的（HasCharacter）。
    /// 先拿到这个答案，才知道 TMP 路径到底要不要做、以及做在哪一层
    /// —— 本项目已经因为"看着像没有处理"而误判过一轮，不再靠猜。
    /// </summary>
    private static void ReportTmpFont(object component, string translated)
    {
        if (TranslatorState.TmpFontLogShown >= 12) return;
        TranslatorState.TmpFontLogShown++;

        try
        {
            var type = component.GetType();
            var fontProperty = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
            var font = fontProperty?.GetValue(component);
            if (font is null)
            {
                Say("[字体/TMP] " + type.FullName + " 的 font=null；译文=\"" + OneLine(translated) + "\"");
                return;
            }

            var availability = CheckGlyphs(font, translated, out var missing);
            var verdict = availability switch
            {
                1 => "全部有字形",
                2 => "缺 " + missing.Length + "/" + translated.Length + " 个（缺：" + missing + "）",
                _ => "探针不可用",
            };

            Say("[字体/TMP] 组件=" + type.FullName
                + " 字体=" + DescribeFont(font)
                + " 逐字检查=" + verdict
                + " 译文=\"" + OneLine(translated) + "\"");
        }
        catch (Exception ex)
        {
            Say("[字体/TMP] 读取字体失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// 逐字符检查字体有没有这段译文的字形。
    ///
    /// 返回值：0 = 探针不可用（**不知道**，调用方必须保持游戏字体不变）；
    ///         1 = 全部字符都有；2 = 有字符缺失（missing 里列出缺哪些）。
    ///
    /// ★ 为什么要三态：之前这里只找"单 char 参数"的重载，而实测的 TMP 版本**只有**
    ///   <c>HasCharacter(char, Boolean, Boolean)</c>，一个都没匹配上就无脑返回 false ——
    ///   于是日志打出"含译文全部字形=False"这种**看起来像缺字、其实是探针失灵**的结论，
    ///   差一点据此去动游戏字体。探针失灵必须和"确实缺字"分开，前者不许改任何东西。
    ///
    /// ★ 为什么第 2 个参数传 true、第 3 个传 false：
    ///   签名是 <c>HasCharacter(char character, bool searchFallbacks, bool tryAddCharacter)</c>。
    ///   searchFallbacks=true 会把回退字体一起算进来（判断"画面上到底有没有字"要的就是这个）；
    ///   tryAddCharacter 保持 false —— 让它去补字形会加载字体 face，
    ///   对游戏自带的、源字体数据被剥离的字体资源会原生崩溃（本项目实测 0xc0000005）。
    /// </summary>
    private static int CheckGlyphs(object? font, string text, out string missing)
    {
        missing = "";
        if (font is null) return 2;

        try
        {
            var methods = font.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);

            var charMethod = methods.Where(m => m.Name == "HasCharacter"
                                                && m.GetParameters().Length >= 1
                                                && m.GetParameters()[0].ParameterType == typeof(char))
                                    .OrderByDescending(m => m.GetParameters().Length)
                                    .FirstOrDefault();
            if (charMethod is not null)
            {
                var count = charMethod.GetParameters().Length;
                var absent = new System.Text.StringBuilder();
                foreach (var ch in text)
                {
                    var args = count switch
                    {
                        1 => new object[] { ch },
                        2 => new object[] { ch, true },
                        _ => new object[] { ch, true, false },
                    };
                    if (charMethod.Invoke(font, args) is bool ok && ok) continue;
                    absent.Append(ch);
                }
                if (absent.Length == 0) return 1;
                missing = absent.ToString();
                return 2;
            }

            var wholeString = methods.FirstOrDefault(m => m.Name == "HasCharacters"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(string));
            if (wholeString is not null)
                return wholeString.Invoke(font, new object[] { text }) is bool all && all ? 1 : 2;

            return 0;   // 问不出来 —— 绝不据此改字体
        }
        catch (Exception) { return 0; }
    }

    /// <summary>保持旧调用点可读：探针明确说"全部都有"才返回 true（不知道 → false，交由三态判断）。</summary>
    private static bool FontAssetHasGlyphs(object font, string text) =>
        CheckGlyphs(font, text, out _) == 1;

    /// <summary>字体是否含这段文字的全部字形；取不到信息按"没有"处理（宁可多换一次）。</summary>
    private static bool FontHasGlyphs(object? font, string text)
    {
        if (font is null) return false;
        try
        {
            var hasCharacter = font.GetType().GetMethod("HasCharacter",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(char) }, null);
            if (hasCharacter is null) return false;
            foreach (var ch in text)
                if (!(hasCharacter.Invoke(font, new object[] { ch }) is bool ok && ok)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>取一个能显示中文的系统动态字体（只取一次并缓存）。候选顺序与 Mono 侧一致。</summary>
    private static object? GetChineseOsFont()
    {
        if (_chineseOsFont is not null) return _chineseOsFont;

        // 出错时用来说明"卡在哪一步"。必须在 try **外面**声明，
        // 否则 catch 块里看不到它（C# 的作用域规则），也就无法把失败步骤带进日志。
        var step = "起始";

        try
        {
            var fontType = UnityReflectionBridge.Reflection.ResolveType("UnityEngine.TextRenderingModule", "UnityEngine.Font")
                           ?? UnityReflectionBridge.Reflection.ResolveType("UnityEngine.CoreModule", "UnityEngine.Font");
            if (fontType is null) { Say("[字体] 找不到 UnityEngine.Font"); return null; }

            var names = new[] { "Microsoft YaHei", "SimHei", "SimSun" };

            // ── 路线 1：Font.CreateDynamicFontFromOSFont(name, size) ──
            step = "查找 CreateDynamicFontFromOSFont(string,int) 重载";
            var create = fontType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreateDynamicFontFromOSFont"
                                     && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (create is not null)
            {
                foreach (var name in names)
                {
                    try
                    {
                        step = "CreateDynamicFontFromOSFont(" + name + ")";
                        var candidate = create.Invoke(null, new object[] { name, 20 });
                        if (candidate is null || !FontHasGlyphs(candidate, "中文测试")) continue;
                        _chineseOsFont = candidate;
                        Say("[字体] 选定系统中文动态字体（CreateDynamicFontFromOSFont）：" + name);
                        return candidate;
                    }
                    catch (Exception ex)
                    {
                        // ★ 必须把 InnerException 也打出来。
                        //   反射调用抛的是 TargetInvocationException，真正的原因在 InnerException 里；
                        //   上一轮只打了 ex.Message（"Exception has been thrown by the target of an invocation."），
                        //   等于什么都没说，白跑一轮实机。
                        Say("[字体] CreateDynamicFontFromOSFont(" + name + ") 失败：" + Describe(ex));
                    }
                }
            }
            else
            {
                Say("[字体] 这个 Unity 版本没有 CreateDynamicFontFromOSFont(string,int)");
            }

            // ── 路线 2：直接构造一个 Font 对象并把族名写进去 ──
            //
            //   为什么需要：实测 圣骑士恩（Unity 2021.2.10 IL2CPP）上**路线 1 整个抛异常**，
            //   而那一版 TMP 又只有 CreateFontAsset(Font, …) 一条建资产路
            //   （既没有 (familyName,…) 也没有 (fontFilePath,…)），于是彻底卡死。
            //   查它的 interop 程序集发现 UnityEngine.Font 有**公开的 Font(string name) 构造函数**，
            //   而且 fontNames 是可写的 —— 那就自己造一个 Font 交给 TMP，
            //   让 TMP 内部自己的 LoadFontFace(Font) 去解析族名（这是 TMP 支持的正路，
            //   与上面那条会抛异常的 API 不是同一条）。
            step = "查找 Font(string) 构造函数";
            var ctor = fontType.GetConstructor(new[] { typeof(string) });
            if (ctor is null) { Say("[字体] UnityEngine.Font 没有 Font(string) 构造函数"); return null; }

            foreach (var name in names)
            {
                try
                {
                    step = "new Font(" + name + ")";
                    var font = ctor.Invoke(new object[] { name });
                    if (font is null) continue;

                    // 双保险：把 name 再显式写一遍（TMP 的 LoadFontFace(Font) 读的就是 font.name）。
                    fontType.GetProperty("name", BindingFlags.Public | BindingFlags.Instance)
                        ?.SetValue(font, name);

                    if (!FontHasGlyphs(font, "中文测试")) continue;
                    _chineseOsFont = font;
                    Say("[字体] 选定系统中文动态字体（自建 Font 对象）：" + name);
                    return font;
                }
                catch (Exception ex)
                {
                    Say("[字体] new Font(" + name + ") 失败：" + Describe(ex));
                }
            }

            Say("[字体] 两条路线都没拿到可用的中文 Font 对象");
        }
        catch (Exception ex)
        {
            // ★ 这条以前只打 `inner.GetType().Name`，而 inner 本身可能仍是
            //   TargetInvocationException（一种**包装**异常，真正的原因还在它里面一层），
            //   于是日志里只剩下"Exception has been thrown by the target of an invocation."
            //   —— 等于什么都没说，还看不出是哪一步抛的。
            //   现在：拆到最内层 + 打出最外层类型（能看出是哪一层包装）+ 出错步骤。
            Say("[字体] 取系统中文动态字体失败：最外层=" + ex.GetType().Name
                + "；步骤=" + step
                + "；根因=" + Describe(ex));
        }
        return null;
    }

    /// <summary>
    /// 把异常链拆到最内层，拼成"类型 消息"。
    ///
    /// 为什么需要：反射调用（`MethodInfo.Invoke` / `ConstructorInfo.Invoke`）**总是**把真实异常
    /// 包在 <see cref="System.Reflection.TargetInvocationException"/> 里，
    /// 只打最外层等于只看到"调用目标引发了异常" —— 完全无法定位。
    /// 这里递归剥壳，并把"消息完全相同的重复层"去掉，避免打出一长串同样的话。
    /// </summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        var current = (Exception?)ex;
        var guard = 0;
        while (current is not null && guard++ < 8)
        {
            var text = current.GetType().Name + " " + current.Message;
            if (parts.Count == 0 || parts[parts.Count - 1] != text) parts.Add(text);
            current = current.InnerException;
        }
        return string.Join(" <- ", parts);
    }

    /// <summary>缓存选定的系统中文动态字体。</summary>
    private static object? _chineseOsFont;

    /// <summary>字体相关日志计数。</summary>

    /// <summary>
    /// 发翻译请求。
    ///
    /// 参数只允许**基元类型与 string**：Il2CppInterop 注册 MonoBehaviour 时会遍历方法，
    /// 遇到自定义托管类型参数会报 "has unsupported parameter ... of type" 并可能导致
    /// 整个类型注册失败（实测：插件加载到一半就没了、行为组件根本没注册上）。
    /// 所以这里把 <c>TrackedTextState</c> 拆成了它的几个字段。
    /// </summary>
    /// <summary>
    /// 发一条翻译请求，并**登记"这句话正在飞"**（后来的同原文组件会搭车，见
    /// <see cref="TranslatorState.InFlight"/>）。
    ///
    /// 返回 true 表示确实发出去了。管道还没建立时返回 false —— 那种情况下**不能**登记，
    /// 否则后来的同原文组件会搭上一辆永远不来的车（这是"静默丢弃"的老毛病换了个样子）。
    /// </summary>
    private bool SendRequest(long id, string source, string requestId, int generation)
    {
        if (!TranslatorState.States.TryGetValue(id, out var sentState)
            || !UnityReflectionBridge.Reflection.IsActiveAndEnabled(sentState.Component)) return false;
        // end-to-end timing: this is the moment the request actually leaves the plugin.
        sentState.SentMs = TranslatorState.NowMs();
        var pipe = TranslatorState.Pipe;
        if (pipe is null) return false;
        try
        {
            pipe.Send(new RequestMessage
            {
                RequestId = requestId,
                Source = source,
                SourceLanguage = TextSelectionPolicy.DetectLanguage(source),
                ComponentId = id,
                Generation = generation,
            });
        }
        catch (Exception ex)
        {
            if (_sendErrors++ < 3) Say("请求发送失败: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
        // 登记放在发送**之后**：发送抛异常时不该留下一条永远等不到答复的登记。
        TranslatorState.PrewarmSent++;// prewarm/pacing stat
        TranslatorState.InFlight[source] = new InFlightRequest
        {
            Source = source,
            Primary = id,
            SinceUnscaledTime = Time.unscaledTime,
        };
        return true;
    }

    private int _sendErrors;

    /// <summary>
    /// 同一句原文已经有一条请求在路上 → 本组件只登记为"搭车"，**不重复发请求**。
    /// 返回 true 表示已搭车、调用方不要再发送。
    ///
    /// 搭车期间本组件的 RequestId/Generation 会被**对齐到主请求** ——
    /// 因为答复是按主请求的编号回来的，`ReplacementPolicy.ReplyMatches` 要用它判定。
    /// 对齐是安全的：本组件刚创建状态，还没发出过任何请求。
    /// </summary>
    private bool AttachToInFlight(long id, string source)
    {
        if (TranslatorState.InFlight.Count == 0) return false;
        if (!TranslatorState.InFlight.TryGetValue(source, out var flight)) return false;
        if (!TranslatorState.States.TryGetValue(flight.Primary, out var primary))
        {
            TranslatorState.InFlight.Remove(source);
            return false;
        }
        if (!TranslatorState.States.TryGetValue(id, out var mine)) return false;
        mine.RequestId = primary.RequestId;
        mine.Generation = primary.Generation;
        mine.LastSentAt = primary.LastSentAt;
        flight.Dependents.Add(id);
        TranslatorState.DedupAttached++;
        TranslatorState.DedupSkipped++;
        if (TranslatorState.DedupTrace && TranslatorState.DedupSkipped <= 20)
            Say("[去重] 组件 " + id + " 搭车（同原文已在飞，主组件 " + flight.Primary + "）：「"
                + OneLine(source) + "」");
        return true;
    }

    /// <summary>
    /// 丢掉失去意义的在飞登记：主组件没了、或等太久还没答复。
    /// **只丢登记，绝不碰 `States` 里的组件状态**（那里面有已经拿到的译文）。
    /// </summary>
    private static void PruneInFlight()
    {
        if (TranslatorState.InFlight.Count == 0) return;
        List<string>? dead = null;
        foreach (var pair in TranslatorState.InFlight)
        {
            var stale = Time.unscaledTime - pair.Value.SinceUnscaledTime
                        > TranslatorState.InFlightTimeoutSeconds;
            var primaryGone = !TranslatorState.States.ContainsKey(pair.Value.Primary);
            if (!stale && !primaryGone) continue;
            (dead ??= new List<string>()).Add(pair.Key);
        }
        if (dead is null) return;
        foreach (var key in dead) TranslatorState.InFlight.Remove(key);
    }

    private void OnDestroy()
    {
        TranslatorState.Pipe?.Dispose();
        foreach (var state in TranslatorState.States.Values) state.Layout?.Restore(state.Component);
        TranslatorState.States.Clear();
        // 状态全清 → 在飞登记也必须清，否则后来的同原文组件会搭上一辆永远不来的车。
        TranslatorState.InFlight.Clear();
    }
}
