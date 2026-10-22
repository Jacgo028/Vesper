using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using BepInEx;
#if !BEPINEX5
using BepInEx.Unity.Mono;
#endif
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityTranslator.Core;

namespace UnityTranslator.Plugin;

[BepInPlugin("org.codex.unitytranslator", "Unity Translator", "0.5.7")]
public sealed partial class Plugin : BaseUnityPlugin
{
    private readonly Dictionary<long, TextState> _states = new Dictionary<long, TextState>();
    private readonly ConcurrentQueue<ReplyMessage> _replies = new ConcurrentQueue<ReplyMessage>();
    private PipeClient? _pipe;
    private float _nextScan;

    // ── 同原文在飞去重（用户新增需求：「减少请求数」，第 99 轮）─────────────────
    //
    // **为什么做这个**：第 95 轮在 Symbiont（IL2CPP，213 组件）上实测拿到硬数据 ——
    //   103 条请求里有 **32 条是重复原文**（71 条唯一），重复率 **31%**：
    //     `Sell Out` ×12、`Stock：0` ×12、`Back`/`Settings`/`SavePoint` 各 ×3 …
    //   也就是说同一句话被联网翻了好几遍。API 单次 2.1~4.7 秒且并发时尾部会被拉长，
    //   所以这 31% 是**纯浪费**，减掉它直接等于减少等待。
    //
    // **为什么不去做"插件本地译文直写"**：第 97 轮已量化否掉 —— 热缓存命中端到端只要
    //   56ms（其中我们自己的处理 1~3ms），省不出东西；而且插件侧本地缓存会绕过 App 的
    //   `UserCorrected` / `TextSafety.LooksLikeChatReply` 校验收，等于把修好的正确性缺陷
    //   重新引进来。**去重不碰校验**：翻译始终由 App 做，只是同一句话不重复问。
    //
    // **为什么不是"插件本地缓存"**：这里只记**正在飞**的请求（拿到答复即删），
    //   不是缓存。删掉之后同一句话再出现，照旧走一次 IPC —— 那一趟有 App 侧正确性校验兜底。
    private readonly Dictionary<string, InFlight> _inFlight = new Dictionary<string, InFlight>(StringComparer.Ordinal);
    private int _dedupSkipped;
    private int _dedupAttached;
    private bool _dedupSummaryLogged;

    /// <summary>同原文正在飞的请求。值里的列表是"跟着这条请求一起等答复"的其他组件。</summary>
    private sealed class InFlight
    {
        public long Primary;
        public long SinceMonoMs;
        public readonly List<long> Dependents = new List<long>();
    }

    /// <summary>
    /// 主请求的答复到了 → 注销在飞登记，并把同一句译文补给**所有搭车组件**。
    ///
    /// ★ 每个搭车组件都**逐个走 <see cref="ReplacementPolicy.ReplyMatches"/> 校验**，
    ///   不是无脑照抄：
    ///     * 搭车期间它可能已经被销毁、或原文被游戏改掉（那就不该再写）；
    ///     * 这一步保证"共用一条答复"不会引入任何跨组件的错误写入。
    ///   ⚠️ 这里**不能用 `continue` 跳过校验失败的那一条**而不处理其他条 ——
    ///   搭车的每一条都必须独立判定，所以循环体里只 `continue` 自己那一轮。
    ///
    /// ★ 只补一次：注销登记后同一句话再出现会重新走 IPC（App 侧有 SQLite 缓存 + 正确性校验）。
    /// </summary>
    private void FlushDedup(string source, string translated)
    {
        if (_inFlight.Count == 0) return;
        if (!_inFlight.TryGetValue(source, out var flight)) return;
        _inFlight.Remove(source);
        if (flight.Dependents.Count == 0) return;

        var dependents = flight.Dependents.ToArray();
        var applied = 0;
        foreach (var dependentId in dependents)
        {
            if (!_states.TryGetValue(dependentId, out var dependent)) continue;
            if (!IsLive(dependent)) continue;
            if (dependent.Translated != null) continue;          // 它自己已经拿到译文了
            if (dependent.Source != source) continue;            // 原文变了，作废
            var current = CurrentText(dependent);
            if (current != source) continue;                     // 画面已经不是这句话了
            var reply = new ReplyMessage
            {
                Version = 1, RequestId = dependent.RequestId, Translated = translated,
                ComponentId = dependentId, Generation = dependent.Generation
            };
            Apply(reply, _replyDequeuedMonoMs);
            if (dependent.Translated == translated) applied++;
        }
        if (applied > 0 && _dedupAttached <= 20)
            Logger.LogInfo("[去重] 同原文「" + Shorten(source) + "」的译文已补给 "
                + applied + " 个搭车组件（省下 " + applied + " 次联网翻译）");
    }

    // ── 延迟埋点（用户新增需求：降低「原文出现 → 译文可见」的延迟）───────────────
    //
    // 为什么另开一条通道、而不是混进现有诊断日志：
    //   现有日志的契约是"**一条请求产生一条诊断**"，既有测试按 `Assert.Single` 断言；
    //   把每次请求的多行计时混进去会同时污染日志语义与既有测试。
    //   （App 侧的 `TranslationCoordinator.Latency` 出于同一原因也是独立通道。）
    //
    // 为什么默认关闭、还要限流：
    //   这是**诊断**能力，不是产品行为。每请求一行、100+ 组件的场景就是 100+ 行洪水，
    //   而 B85 刚踩过"诊断信息被刷屏淹没"的坑（那次 `[延迟] 行数 = 0` 的真因是日志被冲爆）。
    //   打开方式：`UT_LATENCY=1`；默认最多 40 行，`UT_LATENCY_MAX=<n>` 可调。
    private static readonly bool LatencyLogEnabled =
        Environment.GetEnvironmentVariable("UT_LATENCY") == "1";
    private static readonly int LatencyLogMax =
        int.TryParse(Environment.GetEnvironmentVariable("UT_LATENCY_MAX"), out var latencyMax) && latencyMax > 0
            ? latencyMax : 40;
    private int _latencyLogged;
    private int _latencySuppressed;

    /// <summary>这一轮全场景扫描**开始**的时刻（单调毫秒）。用于算"发现"这一段。</summary>
    private long _sweepStartMonoMs;

    /// <summary>辅助功能出过异常就永久停用，避免每帧刷屏并拖垮主链路。</summary>
    private bool _ocrDisabled;
    private bool _cheatsDisabled;
    private bool _enabled = true;
    private Font? _chineseFont;
    private int _nextGeneration;
    private bool _wasConnected;
    private bool _liveFontsProbed;
    private bool _scanDiagnosticsLogged;
    private int _uiFontFailuresLogged;
    private FontFallbackLoader? _fallbackLoader;
    private RuntimeCjkFontAsset.Builder? _cjkFont;
    private bool _cjkFontStarted;
    private int _cjkFontLogCount;

    /// <summary>跨字体度量对照的日志计数（前 3 次）。</summary>

    /// <summary>「等待字体就绪、保留游戏原文」的日志计数。</summary>

    /// <summary>「字体已就绪但仍渲染不了、保留游戏原文」的日志计数。</summary>
    private int _cannotApplyLogged;

    /// <summary>「当前字体已够用因而未替换」的日志计数。</summary>

    /// <summary>尺寸推算诊断的日志计数（前 3 次）。</summary>
    private int _directAppliedLogged;
    private float _fontBuildReportAt;
    private bool _startupFontSelfCheckPending = true;

    /// <summary>中文回退字体加载器：多候选逐个尝试，任何一个都不许抛异常打断流程。</summary>
    private FontFallbackLoader Fallback => _fallbackLoader ??=
        new FontFallbackLoader(Logger.LogInfo, Logger.LogWarning);

    /// <summary>
    /// 把 <c>UnityEngine.TextCoreFontEngineModule</c> 的解析请求重定向到
    /// <c>UnityEngine.TextCoreModule</c>。
    ///
    /// ★ 为什么需要（Unity 2020 上插件完全不工作的根因）：
    ///   <c>FontEngine</c> / <c>FaceInfo</c> / <c>Glyph</c> / <c>GlyphRect</c> 在
    ///   Unity 2020 里位于 <c>UnityEngine.TextCoreModule</c>；
    ///   Unity 2021 起字体引擎部分被拆成独立的 <c>UnityEngine.TextCoreFontEngineModule</c>。
    ///   插件用 2021+ 的模块名编译，跑到 2020 上就引用解析失败 ——
    ///   表现是 Unity 只打一句 "could not be instantiated!"，**没有任何托管日志**，极难排查。
    ///
    ///   这些类型的命名空间（<c>UnityEngine.TextCore</c> / <c>UnityEngine.TextCore.LowLevel</c>）
    ///   在两个版本里是一样的，所以直接换程序集就能满足引用。
    ///
    /// 这个方法**本身不能碰任何 TextCore 类型**（只做字符串匹配 + 反射），
    /// 否则会在 JIT 阶段就触发同一个问题。
    /// </summary>
    private static void InstallTextCoreModuleRedirect()
    {
        if (_textCoreRedirectInstalled) return;
        _textCoreRedirectInstalled = true;

        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    var requested = new AssemblyName(args.Name).Name;
                    if (!string.Equals(requested, "UnityEngine.TextCoreFontEngineModule", StringComparison.Ordinal))
                        return null;

                    // 先在已加载的程序集里找 TextCoreModule
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var name = loaded.GetName().Name;
                        if (string.Equals(name, "UnityEngine.TextCoreModule", StringComparison.Ordinal))
                            return loaded;
                    }

                    // 再按 Managed 目录找文件加载
                    var managed = Path.Combine(Application.dataPath, "Managed");
                    var candidate = Path.Combine(managed, "UnityEngine.TextCoreModule.dll");
                    if (File.Exists(candidate))
                        return System.Reflection.Assembly.LoadFrom(candidate);
                }
                catch (Exception)
                {
                    // 解析失败就让运行时按原逻辑报错，不要在这里再抛
                }
                return null;
            };
        }
        catch (Exception)
        {
        }
    }

    private static bool _textCoreRedirectInstalled;

    private void Awake()
    {
        // ★★ 第一件事：装 Unity 版本兼容的程序集重定向。
        //
        //   实测（Unity 2020.1.2：《The Blood of Yidhra》）：插件完全不工作，
        //   游戏自己的 Player.log 里只有一句
        //     "The script 'UnityTranslator.Plugin.Plugin' could not be instantiated!"
        //   而 BepInEx 的 LogOutput.log 毫无线索。
        //
        //   根因：插件引用了 UnityEngine.TextCoreFontEngineModule（FontEngine / FaceInfo /
        //   Glyph / GlyphRect）。**Unity 2020 没有这个程序集** —— 那些类型当时在
        //   UnityEngine.TextCoreModule 里；Unity 2021 起才把字体引擎部分拆成独立模块。
        //   引用解析不到 → 类型加载失败 → Unity 实例化不了插件，且不产生任何托管日志。
        //
        //   这里把请求重定向到 TextCoreModule。必须在**任何接触 TextCore 类型的代码
        //   被 JIT 之前**注册，所以放在 Awake 的最前面。
        InstallTextCoreModuleRedirect();

        // 管道启动必须包起来：失败时给出可读的异常，而不是让 Unity 只吐一句
        // "could not be instantiated" 就没了（BepInEx 日志里看不到任何东西）。
        try
        {
            PipeClient.Info = Logger.LogInfo;
            _pipe = new PipeClient(_replies, _ocrReplies, _cheatCommands);
            _pipe.Start();
        }
        catch (Exception ex)
        {
            Logger.LogError("命名管道启动失败，插件无法工作：" + ex);
            return;
        }

        Logger.LogInfo("Unity Translator plugin loaded");
        // 字体自检要等 Unity 已进入帧循环后再做：某些游戏的替代 BepInEx
        // 入口会在 UI/字体原生层准备好之前调用 Awake，过早碰 FontEngine 会使
        // 游戏在原生层崩溃。Update 会在同一启动中尽快执行这项诊断。
    }

    /// <summary>
    /// 中文字体自检：把"这段中文能不能显示"这件事在日志里说清楚。
    ///
    /// 为什么值得单独做一步：
    /// V0.1 的日志里只有 `Cannot load Chinese font`，看不出到底哪一步坏了；
    /// 玩家看到的却是"译文写进去了、但缺字显示成方框（□）"。
    /// 现在启动时就把结论写清楚，配合下面的 <see cref="ExplainFontLimitationIfAny"/>，
    /// 下次任何人看日志开头就知道这次能不能显示中文、为什么不能。
    /// </summary>
    private void SelfCheckChineseFont()
    {
        // 故意混入中文标点与常见汉字，覆盖译文里最常出现的字符。
        const string Sample = "继续游戏，鸣谢。";
        try
        {
            var asset = Fallback.Acquire(Sample);
            var check = FontFallbackLoader.CanRender(asset, Sample);
            Logger.LogInfo("中文字体自检：样例「" + Sample + "」→ "
                + (asset is null ? "没有可用候选" : Fallback.Summary)
                + "；字符表 " + check.CharacterCount + " 项；缺失字形 "
                + FontReplacementPolicy.DescribeMissingGlyphs(check.Missing));
        }
        catch (Exception ex)
        {
            // 自检失败**不能**影响翻译：真正的字形判定在 Apply 里按每条译文单独做。
            Logger.LogWarning("中文字体自检异常（不影响翻译流程）：" + ex);
        }
        try { ExplainFontLimitationIfAny(); }
        catch (Exception ex) { Logger.LogWarning("字体受限说明生成失败（不影响翻译流程）：" + ex.GetType().Name); }
    }

    /// <summary>
    /// 当这台机器上确实拿不到可用的中文字体时，把原因一次性讲清楚，并明确说明后果：
    /// **译文不会被写进游戏**，游戏继续显示原文 —— 而不是塞进去一堆方框。
    ///
    /// 这个"讲清楚"本身就是本次修复的一部分：实测里最难的不是改代码，
    /// 而是从日志看出"字体没建成、译文却写了"这种自相矛盾的状态。
    /// </summary>
    private void ExplainFontLimitationIfAny()
    {
        try
        {
            var probe = Fallback.Acquire("鸣");
            if (probe is not null) return; // 有可用中文字体，不需要解释

            var fontsDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            var loadable = new List<string>();
            foreach (var name in new[] { "simhei.ttf", "Deng.ttf", "msyh.ttc", "simsun.ttc" })
            {
                var path = System.IO.Path.Combine(fontsDirectory, name);
                try
                {
                    if (!System.IO.File.Exists(path)) continue;
                    // ★ 用**反射**调 LoadFontFace，不写死重载。
                    //   实测：Unity 2020 的 FontEngine 没有 (string,int,int) 这个重载，
                    //   直接写会导致**编译失败**（CS1501），整个 Unity 2020 变体编不出来。
                    //   反射按参数类型挑，各版本都能用。
                    var text = RuntimeCjkFontAsset.InvokeOverloadByArity(
                        typeof(UnityEngine.TextCore.LowLevel.FontEngine), "LoadFontFace",
                        new object?[] { path, 16, 0 });
                    loadable.Add(name + "=" + text);
                }
                catch (Exception ex)
                {
                    loadable.Add(name + "=" + ex.GetType().Name);
                }
            }
            Logger.LogWarning("游戏原有字体无法直接显示中文；收到译文后将尝试在游戏内覆盖显示。原因：本机系统字体文件本身读得到"
                + "（FontEngine：" + (loadable.Count == 0 ? "没有可测的字体文件" : string.Join(" ", loadable))
                + "），但游戏里所有 UnityEngine.Font 都被剥离了字体数据，"
                + "无法据此创建 TMP 字体资源；直接给游戏字体补字形会让游戏崩溃（实测），所以不做。"
                + "覆盖显示也失败时保留原文，避免出现方框。");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("字体受限说明生成失败（不影响流程）：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>
    /// 游戏跑起来、画面上真的有文字之后，报告一次"正在使用的字体能不能显示中文"。
    ///
    /// **只诊断，绝不改动游戏字体。** 实测（2026-09-25，Sister Leticia）：
    /// 对游戏自己的动态字体资源调用 TryAddCharacters 会让 Unity 原生层直接崩溃
    /// —— 整个游戏进程消失，不是可捕获的异常。原因是这类字体资源的源字体被剥离了字体数据
    /// （Unity 日志："Unable to load font face for [...]. Make sure "Include Font Data"
    /// is enabled in the Font Import Settings."）。所以任何"给游戏字体补字形"的尝试都必须禁止。
    /// </summary>
    private void ReportLiveFonts()
    {
        const string Sample = "继续游戏，鸣谢。";
        try
        {
            var seen = new HashSet<int>();
            var active = 0;
            foreach (var component in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (component is null || !component.gameObject.activeInHierarchy) continue;
                var font = component.font;
                if (font is null || !seen.Add(font.GetInstanceID())) continue;
                active++;
                Logger.LogInfo("画面字体：" + SafeName(font)
                    + "（模式=" + SafeMode(font) + "，字符表=" + SafeCharCount(font)
                    + "，源字体=" + SafeSourceName(font) + "）能显示样例「" + Sample + "」="
                    + SafeHasCharacters(font, Sample));
            }
            Logger.LogInfo("画面字体：画面上正在使用 " + active + " 个字体资源（本游戏的方框问题见上面的字体受限说明）");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("画面字体诊断异常（不影响翻译流程）：" + ex);
        }
        ReportChineseCoverage();
    }

    /// <summary>
    /// 统计"常用汉字"在游戏自带字体里到底覆盖了多少。
    ///
    /// 这一步是为了把结论定死：如果覆盖数是 0，那"部分中文正常、部分方框"就不可能来自
    /// 系统字体回退，只能说明原文里那些"看起来正常"的字本来就在游戏的字符表里，
    /// 而中文译文一个字都显示不了。这里只看**所有** TMP 字体资源的并集，只读不改。
    /// </summary>
    private void ReportChineseCoverage()
    {
        try
        {
            // 用一段覆盖常用字的样本，而不是只测三五个字。
            var builder = new System.Text.StringBuilder();
            for (var code = 0x4E00; code < 0x4E00 + 200; code++) builder.Append((char)code); // 常用汉字区
            builder.Append("继续游戏鸣谢确定取消开始设置退出返回生命金钱攻击防御等级经验值");
            var text = builder.ToString();

            var fonts = new List<TMP_FontAsset>();
            var seen = new HashSet<int>();
            foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (font is null || !seen.Add(font.GetInstanceID())) continue;
                fonts.Add(font);
            }

            var unique = new HashSet<char>();
            var coveredUnique = new HashSet<char>();
            foreach (var ch in text)
            {
                unique.Add(ch);
                foreach (var font in fonts)
                {
                    if (!SafeHasCharacter(font, ch)) continue;
                    coveredUnique.Add(ch);
                    break;
                }
            }
            // 只列前 20 个能显示的字，便于核对我们对"方框"的判断。
            var sampleCovered = new System.Text.StringBuilder();
            var listed = 0;
            foreach (var ch in coveredUnique)
            {
                if (listed++ >= 20) break;
                sampleCovered.Append(ch);
            }
            Logger.LogInfo("中文字形覆盖统计：样本 " + unique.Count + " 个不同汉字，游戏自带 "
                + fonts.Count + " 个字体资源的并集能显示其中 " + coveredUnique.Count + " 个"
                + (coveredUnique.Count == 0
                    ? "（0 说明游戏字体里一个中文字形都没有，方框不可能靠系统字体解决）"
                    : "（能显示的例子：" + sampleCovered + "）"));
        }
        catch (Exception ex)
        {
            Logger.LogWarning("中文字形覆盖统计异常（不影响流程）：" + ex);
        }
    }

    private static bool SafeHasCharacter(TMP_FontAsset font, char value)
    {
        try { return font.HasCharacter(value); }
        catch (Exception) { return false; }
    }

    private static string SafeName(TMP_FontAsset font)
    {
        try { return font.name; } catch (Exception) { return "(名字读不到)"; }
    }

    private static string SafeHasCharacters(TMP_FontAsset font, string text)
    {
        try { return font.HasCharacters(text).ToString(); }
        catch (Exception ex) { return "抛出 " + ex.GetType().Name; }
    }

    private static string SafeMode(TMP_FontAsset font)
    {
        try { return font.atlasPopulationMode.ToString(); }
        catch (Exception ex) { return "抛出 " + ex.GetType().Name; }
    }

    private static string SafeCharCount(TMP_FontAsset font)
    {
        try { return (font.characterTable?.Count ?? 0).ToString(); }
        catch (Exception ex) { return "抛出 " + ex.GetType().Name; }
    }

    private static string SafeSourceName(TMP_FontAsset font)
    {
        try { return font.sourceFontFile?.name ?? "空"; }
        catch (Exception ex) { return "抛出 " + ex.GetType().Name; }
    }

    /// <summary>主线程 TCP 回退驱动是否已被异常停用（与 OCR/秘籍同样的隔离策略）。</summary>
    private bool _tcpTickDisabled;

    private void Update()
    {
        if (_startupFontSelfCheckPending && Time.unscaledTime > 2f)
        {
            _startupFontSelfCheckPending = false;
            SelfCheckChineseFont();
        }

        // 画面上应该已经有文字之后，报告一次"正在使用的字体能不能显示中文"。
        // 只记录信息，不碰游戏字体资源（碰了会让游戏崩溃，见 ReportLiveFonts 的说明）。
        if (!_liveFontsProbed && Time.unscaledTime > 25f)
        {
            _liveFontsProbed = true;
            ReportLiveFonts();
        }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            _enabled = !_enabled;
            if (!_enabled) RestoreAll();
        }
        var connected = _pipe != null && _pipe.IsConnected;
        if (_wasConnected && !connected) RestoreAll();
        _wasConnected = connected;
        TickCjkFont();

        // ★ 辅助功能（OCR / 秘籍）必须**单独隔离**，不能让它们的异常打断整个 Update。
        //
        //   为什么：这些功能不是翻译主链路，但它们跑在文字扫描**之前**。
        //   实测 Antivirus Girl（Unity 6000.3.13）上 OcrCapture 引用了一个已经消失的内部
        //   API（Scene.handle），于是 Update 每帧都在扫描之前抛 MissingMethodException：
        //   插件看起来"加载正常、管道已连接"，实际**一条翻译请求都没发出**，
        //   游戏 Player.log 里堆了 10968 条同一异常，而 BepInEx 日志里什么都看不到。
        //   一个辅助功能不可用，不该让整个翻译功能陪葬。
        if (!_ocrDisabled)
        {
            try { TickOcr(); }
            catch (Exception ex)
            {
                _ocrDisabled = true;
                Logger.LogWarning("OCR 辅助功能已停用（异常后不再调用，不影响翻译）：" + ex.GetType().Name + " " + ex.Message);
            }
        }
        if (!_cheatsDisabled)
        {
            try { TickCheats(); }
            catch (Exception ex)
            {
                _cheatsDisabled = true;
                Logger.LogWarning("秘籍辅助功能已停用（异常后不再调用，不影响翻译）：" + ex.GetType().Name + " " + ex.Message);
            }
        }
        // TCP 回退（回退链第 2 级）**必须由主线程驱动**：
        // 实测 Unity 2020.1.2f1 上，进程启动完成后在非主线程做 socket 操作会永久停住
        // （线程池线程、甚至主线程上创建的常驻线程都一样）。详见 PipeClient.TickTcp 的注释。
        // 与 OCR/秘籍同理：异常必须隔离，辅助通道不能让翻译主链路陪葬。
        if (!_tcpTickDisabled)
        {
            try { _pipe?.TickTcp(); }
            catch (Exception ex)
            {
                _tcpTickDisabled = true;
                Logger.LogWarning("TCP 回退主线程驱动已停用（异常后不再调用，不影响翻译）：" + ex.GetType().Name + " " + ex.Message);
            }
        }
        // ★ 回包**每帧**取走，不要等下一次全场景扫描。
        //
        //   原先把排空 `_replies` 放在 0.4 秒扫描的最后，于是"回包到了"到"真正写回"
        //   之间必然多等 0~400ms（第 94 轮实测：排队 22~281ms，而写回本身只要 0~3ms）。
        //   这段等待与缓存冷热无关，纯粹是我们自己加的固定成本。
        //
        //   文字**发现**才需要扫描（要遍历场景），而"把已经拿到的译文写回去"只需要
        //   一次字典查找 + 一次赋值 —— 完全可以每帧做。改完这一条，
        //   `回包→写回` 就只剩真正的写回耗时。
        //
        //   注意顺序：必须在下面 `!_enabled || … return` **之前**，否则这一帧就跳过了。
        DrainReplies();
        // 词典直写：组件换回我们译过的句子就立刻写回（很便宜，每帧做）
        ApplyFromMemory();

        if (!_enabled || Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + 0.4f;
        // 去重登记也要有寿命：主组件被销毁、或答复等太久时丢掉登记，
        // 让搭车的组件自己重发一次（App 侧有 SQLite 缓存，这一趟很便宜）。
        PruneInFlight();
        LogDedupSummary();
        // 延迟埋点：记下本轮扫描的起点。文字"在画面上出现"与"被我们发现"之间的差值
        // 就是这一段扫描周期的固定成本（最坏 0.4 秒）。
        _sweepStartMonoMs = PipeClient.MonoNowMs();
        var observed = new HashSet<long>();
        foreach (var component in Resources.FindObjectsOfTypeAll<TMP_Text>())
            if (component != null && component.gameObject.activeInHierarchy)
            {
                var id = (long)component.GetInstanceID();
                observed.Add(id);
                Observe(id, component.text, value => component.text = value, component, null);
            }
        foreach (var component in Resources.FindObjectsOfTypeAll<Text>())
            if (component != null && component.gameObject.activeInHierarchy)
            {
                var id = (long)component.GetInstanceID();
                observed.Add(id);
                Observe(id, component.text, value => component.text = value, null, component);
            }
        if (!_scanDiagnosticsLogged && Time.unscaledTime > 15f)
        {
            _scanDiagnosticsLogged = true;
            Logger.LogInfo("文字扫描诊断：活动组件 " + observed.Count + " 个，翻译服务连接=" + connected
                + "，已记录文字 " + _states.Count + " 条。");

            // ★ 逐条打印"对象层级路径 + 父子关系 + 当前文字"。
            //
            // 为什么需要：实测画面里同一处文字被**画了两遍**（拉丁译文与中文互相叠压）。
            // 要判断这是"游戏用两个 TMP 组件做正本+阴影"还是"我们把同一段文字写了两遍"，
            // 唯一可靠的办法是把每个组件的层级路径和它父节点上的文字摊出来对照 ——
            // 凭缩放截图猜是猜不出来的。
            var shown = 0;
            foreach (var pair in _states)
            {
                if (shown++ >= 20) break;
                var tmp = pair.Value.Tmp;
                var ui = pair.Value.Ui;
                var component = (Component?)(object?)tmp ?? ui;
                if (component is null) continue;

                var path = BuildHierarchyPath(component.transform);
                var parentText = ReadParentText(component.transform);
                var own = tmp is not null ? tmp.text : ui?.text;
                Logger.LogInfo("[层级] id=" + pair.Key
                    + " 路径=" + path
                    + " 类型=" + component.GetType().Name
                    + " 字号=" + (tmp is not null ? tmp.fontSize.ToString("0.#") : "-")
                    + " 字体=" + (tmp?.font is not null ? tmp.font.name : "-")
                    + " 字型=" + (tmp is not null ? tmp.fontStyle.ToString() : "-")
                    + " 屏幕位置=" + DescribeScreenRect(component.transform)
                    + " 原文=「" + Shorten(pair.Value.Source) + "」"
                    + (string.IsNullOrEmpty(parentText) ? string.Empty : " 父节点文字=「" + Shorten(parentText!) + "」")
                    + " 文字=「" + Shorten(own) + "」");
            }

            // ★★ 全场景复核：还有没有**别的组件仍在显示原文**？
            //
            //   叠字的两种可能成因必须分开：
            //     (a) 存在第二个未被跟踪的 TMP 组件，仍显示游戏原文 → 场景里能搜到原文；
            //     (b) 只有一个组件，但 TMP 内部把同一段文字画了两遍（阴影/描边/双 pass）→ 搜不到原文。
            //   这一段就是判据：把每个仍含"我们译过的原文"的活动组件列出来。
            var sources = new List<string>();
            foreach (var state in _states.Values)
                if (!string.IsNullOrEmpty(state.Source)) sources.Add(state.Source);
            var leftovers = 0;
            foreach (var component in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (component is null || !component.gameObject.activeInHierarchy) continue;
                var text = component.text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                foreach (var source in sources)
                {
                    if (text != source) continue;
                    leftovers++;
                    if (leftovers > 12) break;
                    Logger.LogWarning("[叠字排查] 仍有组件在显示原文「" + Shorten(source) + "」："
                        + BuildHierarchyPath(component.transform)
                        + " id=" + component.GetInstanceID()
                        + " 字体=" + (component.font is not null ? component.font.name : "-")
                        + " 字号=" + component.fontSize.ToString("0.#")
                        + " 在我们跟踪列表里=" + _states.ContainsKey(component.GetInstanceID()));
                    break;
                }
                if (leftovers > 12) break;
            }
            if (leftovers == 0)
                Logger.LogInfo("[叠字排查] 场景里没有其他组件在显示原文 —— 叠字若仍可见，来自同一组件内部的重复绘制");
        }
        // ★★ 文字**暂时看不见**不等于**文字没了** —— 这是"等待太长"的一个真实来源。
        //
        //   原来这里只要"本轮扫描没看到"就 Restore + Remove，把状态整个丢掉。后果是一条完整的
        //   重复劳动链（第 94 轮埋点实测抓到，Leticia 标题菜单 5 条里中了 2 条）：
        //     ① 组件因菜单动画/转场短暂 inactive → 被移出 _states；
        //     ② 于是它的 RequestId 与**已经拿到的译文**一起丢失；
        //     ③ 组件重新出现时被当成全新组件 → **重新发一次请求**（重复劳动）；
        //     ④ 第一次那条回包回来时 RequestId 已经对不上 → 被 ReplacePolicy 丢弃。
        //   实测代价：热缓存明明 1ms 就能查完，玩家却要多等一整次 IPC 往返；
        //   实测那两条是 15715ms 之后才拿到译文（`发现→发送=15715ms 发送→回包=37ms`）。
        //
        //   所以判据从"本轮没扫到"改成"**对象是否真的被销毁**"：
        //   `Tmp == null`（Unity 重载的 == 对已销毁对象返回 true）才是真的没了。
        //   **暂不可见就保留状态**，组件回来时 Observe 会走
        //   `current == old.Source && old.Translated != null` 那条分支，**直接用已有译文写回，零往返**。
        foreach (var id in new List<long>(_states.Keys))
        {
            if (observed.Contains(id)) continue;
            var state = _states[id];
            if (DecideStateRetention(state) != StateRetention.Forget) continue;
            Restore(state);
            _states.Remove(id);
        }
        // 回包在主线程被取走的**确切时刻**：`回包→写回` 里"排队等扫描"的部分到这里为止。
        // 每一帧都会调用（见 Update 里的说明），所以队列里通常只有刚到的那些回包。
        // 为什么要单独记这一笔：整段延迟可能来自
        //   ① 回包在 `_replies` 里等下一次扫描（现在基本消除了），
        //   ② 或者 `Apply` 内部写回本身慢（实测字体装配期间单帧能到 0.5 秒）。
        // 只看"回包→写回"一个总数是分不开这两者的，于是也无法判断该改哪里。
        DrainReplies();
    }

    /// <summary>把已经收到的回包全部处理掉（每帧调用，见 <see cref="Update"/>）。</summary>
    private void DrainReplies()
    {
        while (_replies.TryDequeue(out var reply))
        {
            var dequeuedMonoMs = PipeClient.MonoNowMs();
            Apply(reply, dequeuedMonoMs);
        }
    }

    /// <summary>一轮扫描之后，某个文字组件的状态该怎么处理。</summary>
    internal enum StateRetention
    {
        /// <summary>本轮扫到了，或只是暂时不可见 —— 保留状态（含 RequestId 与已拿到的译文）。</summary>
        Remember,

        /// <summary>组件对象真的被销毁了 —— 还原并丢弃状态。</summary>
        Forget
    }

    /// <summary>
    /// 判断状态该留还是该丢。**抽成独立函数是为了能被单元测试钉住** ——
    /// "暂时不可见"与"已销毁"混为一谈正是第 94 轮实测抓到的重复请求的根因，
    /// 靠读代码很难发现，靠测试却可以永久拦住。
    ///
    /// 判据只看**组件对象是否还存在**：Unity 重载的 <c>==</c> 让**已销毁**的组件在
    /// <c>Tmp is null</c> 处返回 true，而**只是被禁用**（activeInHierarchy=false）的组件不是 null。
    /// 所以"存在但本轮没扫到"必须**保留**。
    /// </summary>
    internal static StateRetention DecideStateRetention(bool componentExists) =>
        componentExists ? StateRetention.Remember : StateRetention.Forget;

    private static StateRetention DecideStateRetention(TextState state) =>
        DecideStateRetention(Exists(state));

    /// <summary>把一个 Transform 的层级路径拼出来（最多向上 4 层），用于定位重复绘制的来源。</summary>
    private static string BuildHierarchyPath(Transform transform)
    {
        try
        {
            var parts = new List<string>(4);
            var current = transform;
            for (var depth = 0; current is not null && depth < 4; depth++)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }
        catch (Exception)
        {
            return "(取层级失败)";
        }
    }

    /// <summary>读父节点上的文字（判断是否存在"正本 + 阴影"两个组件）。</summary>
    private static string? ReadParentText(Transform transform)
    {
        try
        {
            var parent = transform.parent;
            if (parent is null) return null;
            var sibling = parent.GetComponent<TMP_Text>();
            return sibling is not null && sibling.transform != transform ? sibling.text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Observe(long id, string? current, Action<string> write, TMP_Text? tmp, Text? ui)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            if (_states.TryGetValue(id, out var blank)) RestoreStyle(blank);
            _states.Remove(id);
            return;
        }
        if (_states.TryGetValue(id, out var old))
        {
            old.Write = write;
            if (current == old.Translated) return;
            if (current == old.Source)
            {
                if (old.UsesOverlay) return;
                if (ReplacementPolicy.ShouldReapply(old.Source, current, old.Translated))
                    TryApplyDirectly(old, old.Translated!);
                else if (_pipe != null && _pipe.IsConnected &&
                         TextSelectionPolicy.RetryDue(old.Attempts, old.LastSentAt, Time.unscaledTime))
                    SendRequest(id, old);
                return;
            }
            // The game may have restyled a pooled label. Release only values
            // which are still ours before capturing its new native baseline.
            RestoreStyle(old);
        }
        var component = (object?)tmp ?? ui!;
        var state = new TextState { Source = current!, Write = write, Tmp = tmp, Ui = ui,
            OriginalTmpFont = tmp?.font, OriginalUiFont = ui?.font,
            OriginalTmpMaterial = tmp?.fontSharedMaterial, OriginalUiMaterial = ui?.material,
            Layout = new ReflectionTextLayout(component), Generation = ++_nextGeneration,
            DiscoveredThisSweep = true, DiscoveredMonoMs = _sweepStartMonoMs };
        _states[id] = state;
        if (IsTranslatable(current!))
        {
            if (AttachToInFlight(id, current!)) return;
            SendRequest(id, state);
        }
    }

    /// <summary>
    /// 同一句原文已经有一条请求在路上 → 本组件只登记为"搭车"，**不重复发请求**。
    /// 返回 true 表示已搭车、调用方不要再发送。
    ///
    /// 搭车期间本组件的 <see cref="TextState.RequestId"/>/<see cref="TextState.Generation"/>
    /// 会被**对齐到主请求**：因为答复是按主请求的编号回来的，
    /// `ReplacementPolicy.ReplyMatches` 要用它来判定这条答复是否还适用。
    /// 对齐是安全的 —— 本组件刚刚才创建状态，还没有发出过任何请求。
    /// </summary>
    private bool AttachToInFlight(long id, string source)
    {
        PruneInFlight();
        if (!_inFlight.TryGetValue(source, out var flight)) return false;
        if (!_states.TryGetValue(flight.Primary, out var primary) || !IsLive(primary))
        {
            _inFlight.Remove(source);
            return false;
        }
        if (!_states.TryGetValue(id, out var mine)) return false;
        // 对齐编号：答复会带着主请求的 RequestId/Generation 回来。
        mine.RequestId = primary.RequestId;
        mine.Generation = primary.Generation;
        mine.LastSentAt = primary.LastSentAt;
        flight.Dependents.Add(id);
        _dedupAttached++;
        _dedupSkipped++;
        if (DedupTrace && _dedupSkipped <= 20)
            Logger.LogInfo("[去重] 组件 " + id + " 搭车（同原文已在飞，主组件 " + flight.Primary
                + "）：「" + Shorten(source) + "」");
        return true;
    }

    /// <summary>
    /// 丢掉已经没有意义或等太久的在飞登记。
    ///
    /// 两种要丢的情况：
    ///   * 主组件**真的被销毁**了（<c>Exists</c> 为 false）—— 答复永远不会被应用，搭车的白等；
    ///   * 等了超过 <see cref="InFlightTimeoutSeconds"/> 还没答复 —— 宁可让搭车的那几条
    ///     自己重发一次（App 侧有 SQLite 缓存兜底，这一趟很便宜），也不要让它们一直不动。
    ///
    /// ⚠️ 只丢**登记**，绝不碰 `_states` 里的组件状态：那里面有已经拿到的译文，
    ///    第 94 轮就是因为误删状态导致"同一句话被问两遍、拿到手的译文被丢掉"。
    /// </summary>
    private void PruneInFlight()
    {
        if (_inFlight.Count == 0) return;
        var now = PipeClient.MonoNowMs();
        List<string>? dead = null;
        foreach (var pair in _inFlight)
        {
            var stale = now - pair.Value.SinceMonoMs > InFlightTimeoutSeconds * 1000;
            var primaryGone = !_states.TryGetValue(pair.Value.Primary, out var primary) || !Exists(primary);
            if (!stale && !primaryGone) continue;
            (dead ??= new List<string>()).Add(pair.Key);
        }
        if (dead is null) return;
        foreach (var key in dead) _inFlight.Remove(key);
    }

    /// <summary>在飞登记的最长有效时间（秒）。超过就让搭车的自己重发，避免永久不动。</summary>
    private const int InFlightTimeoutSeconds = 30;

    /// <summary>去重明细日志（`UT_DEDUP_TRACE=1` 打开）。默认只留一行汇总，不刷屏。</summary>
    private static readonly bool DedupTrace = Environment.GetEnvironmentVariable("UT_DEDUP_TRACE") == "1";

    /// <summary>去重汇总：只在真的省下过请求时打一行，用户能从日志里看出效果。</summary>
    private void LogDedupSummary()
    {
        if (_dedupSummaryLogged || _dedupSkipped < 5) return;
        _dedupSummaryLogged = true;
        Logger.LogInfo("[去重] 已省下 " + _dedupSkipped + " 条重复请求（同原文在飞时搭车，翻译仍由 App 做并走它的校验）");
    }

    /// <summary>
    /// 发送路径的详细追踪。默认关闭，避免正常用户的日志被刷屏；
    /// 排查"插件明明取到文字却一条请求都没发出去"（实测 Yidhra 就是这个症状）时
    /// 设 UT_SEND_TRACE=1 打开。**这个症状以前查不出来，就是因为发送失败没有任何日志。**
    /// </summary>
    private static readonly bool TraceSend = Environment.GetEnvironmentVariable("UT_SEND_TRACE") == "1";

    private void SendRequest(long id, TextState state)
    {
        state.RequestId = Guid.NewGuid().ToString("N");
        state.LastSentAt = Time.unscaledTime;
        state.Attempts++;
        var pipe = _pipe;
        if (pipe is null)
        {
            // 管道还没建立：这一条请求就此丢失。以前这里是 _pipe?.Send(...) 静默丢弃，
            // 日志里一个字都没有，事后完全无法判断"到底发没发"。
            Logger.LogWarning($"[发送] 组件 {id} 的请求没有发出：管道尚未建立（第 {state.Attempts} 次尝试）：「{Shorten(state.Source)}」");
            return;
        }
        // 限流：同屏一次性爆发时按秒摊开（见 PrewarmBudgetAllows 的说明）。
        if (!PrewarmBudgetAllows()) { state.Attempts--; return; }   // 没配额就下一轮再发
        if (TraceSend)
            Logger.LogInfo($"[发送] 组件 {id} 第 {state.Attempts} 次发送请求（连接={pipe.IsConnected}）：「{Shorten(state.Source)}」");
        // 延迟埋点：记"真正出队发送"的时刻。放在 pipe==null 检查**之后** ——
        // 管道没建立时这一条根本没发出去，打点会算出一个假的"发送→回包"。
        state.SentMonoMs = PipeClient.MonoNowMs();
        pipe.Send(new RequestMessage
        {
            RequestId = state.RequestId, Source = state.Source, SourceLanguage = SourceLanguage(state.Source),
            ComponentId = id, Generation = state.Generation
        });
        // 登记"这句话正在飞"：后来的同原文组件会搭车，不再重复问（见 `_inFlight` 的说明）。
        // 放在 `pipe.Send` **之后**：发送抛异常时不应该留下一条永远等不到答复的登记。
        _inFlight[state.Source] = new InFlight { Primary = id, SinceMonoMs = state.SentMonoMs };
    }

    // ══ 组件级译文记忆 + 请求限流（第 100 轮，与 IL2CPP 侧同构）══════════════════
    //
    // 为什么 Mono 也要有：这些修复**与具体游戏无关**，是插件通用逻辑。
    // IL2CPP 侧实机验证有效（用户确认"已经翻译过的内容再重新翻译不会出 bug 了"），
    // 而 Mono 侧此前只补了去重，所以 Leticia 这类游戏**照样会犯"翻译消失"**。
    //
    // ① 词典直写：同一组件常轮流显示多句（底部对话框就是这样）。记下"这句原文→这句译文"，
    //    屏幕上再出现我们译过的句子就**立刻写回，零联网**。
    // ② 重写回：组件被游戏还原成原文时，用手上已有的译文补写（见 Apply 开头）。
    // ③ 限流：同屏一次出现很多条时不再同一瞬间全发出去，按秒给配额，把爆发摊开。
    private readonly Dictionary<long, Dictionary<string, string>> _seenByComponent =
        new Dictionary<long, Dictionary<string, string>>();

    private static readonly int PrewarmPerSecond =
        int.TryParse(Environment.GetEnvironmentVariable("UT_PREWARM_PER_SEC"), out var pps) && pps > 0 ? pps : 0;   // 0 = 不限流（默认；见 B119）
    private static readonly bool PrewarmDisabled = Environment.GetEnvironmentVariable("UT_PREWARM") == "0";
    private float _prewarmWindowStart;
    private int _prewarmInWindow;

    /// <summary>记住"这个组件的这句原文 → 这句译文"。每个组件最多 64 句，防止长局里无限增长。</summary>
    private void RememberTranslation(long componentId, string source, string? translated)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated)) return;
        if (!_seenByComponent.TryGetValue(componentId, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            _seenByComponent[componentId] = map;
        }
        if (map.Count >= 64 && !map.ContainsKey(source)) return;
        map[source] = translated!;
    }

    /// <summary>
    /// 组件现在显示的文字，只要是我们为它译过的 → 立刻把译文写回去。**不联网、不等答复。**
    /// 每帧调用（很便宜：一次字典查找 + 一次字符串比较）。
    /// </summary>
    private void ApplyFromMemory()
    {
        if (!_enabled || _pipe == null || !_pipe.IsConnected || _seenByComponent.Count == 0) return;
        foreach (var pair in _states)
        {
            if (!_seenByComponent.TryGetValue(pair.Key, out var map) || map.Count == 0) continue;
            var state = pair.Value;
            if (!IsLive(state)) continue;
            var current = CurrentText(state);
            if (string.IsNullOrEmpty(current) || current == state.Translated
                || !map.TryGetValue(current!, out var known) || string.IsNullOrEmpty(known)) continue;
            if (current != state.Source)
            {
                RestoreStyle(state);
                state.Source = current!;
                state.Translated = null;
                state.Generation = ++_nextGeneration;
                state.RequestId = string.Empty;
                CaptureNativeStyle(state);
            }
            TryApplyDirectly(state, known!);
        }
    }

    /// <summary>按秒配额；返回 true 表示这一条现在可以发。</summary>
    private bool PrewarmBudgetAllows()
    {
        // ★★ 必须把"配额为 0"当成**不限流**，而不是"一条都不许发"。
        //   少了这一行，PrewarmPerSecond=0 时 `_prewarmInWindow >= 0` 恒为真 → 返回 false
        //   → **一条请求都发不出去，游戏里一个字都不会翻**。
        //   这与 B109 那次（把去抖设成 0 结果变成"不翻"）是**同一类错误**：
        //   "0" 这个值在两个地方的含义被搞反了。所以这里写死在最前面，
        //   不依赖调用方怎么传、也不依赖 PrewarmDisabled 有没有被正确设置。
        if (PrewarmDisabled || PrewarmPerSecond <= 0) return true;

        var now = Time.unscaledTime;
        if (now - _prewarmWindowStart >= 1f) { _prewarmWindowStart = now; _prewarmInWindow = 0; }
        if (_prewarmInWindow >= PrewarmPerSecond) return false;
        _prewarmInWindow++;
        return true;
    }

    private void Apply(ReplyMessage reply, long dequeuedMonoMs = 0)
    {
        _replyDequeuedMonoMs = dequeuedMonoMs;
        if (!_enabled || _pipe == null || !_pipe.IsConnected || !_states.TryGetValue(reply.ComponentId, out var state) ||
            !IsLive(state) ||
            !ReplacementPolicy.ReplyMatches(state.Source, CurrentText(state), state.Generation, state.RequestId,
                reply.Generation, reply.RequestId)) return;
        if (string.IsNullOrWhiteSpace(reply.Translated)) return;
        if (state.Translated != null)
        {
            if (ReplacementPolicy.ShouldReapply(state.Source, CurrentText(state), state.Translated))
                TryApplyDirectly(state, state.Translated, reply.RecvMonoMs);
            return;
        }
        FlushDedup(state.Source, reply.Translated);
        if (TryApplyDirectly(state, reply.Translated, reply.RecvMonoMs)) return;
        // Missing glyphs can become available after the asynchronous TMP atlas
        // finishes. Legacy UI.Text has its own Font path and never waits on it.
        if (state.Tmp != null) RememberPendingTranslation(state, reply.Translated);
    }

    /// <summary>
    /// 延迟埋点：写回成功后输出本进程内的三段耗时（`UT_LATENCY=1` 打开）。
    ///
    /// **不做跨进程时钟对齐**：插件与 App 各自只算**自己进程内**的差值。
    /// 两边的绝对时间戳没有共同时基，相减出来的数字是假的 ——
    /// 对齐靠 `RequestId`（App 侧的 `[延迟] req=… 合计=…` 与这里同一编号可以配成一条链）。
    ///
    /// 三段的分工：
    ///   * `发现→发送`：我们自己的**扫描节奏**成本（每 0.4 秒一轮全场景）。
    ///     非 0 说明这段文字其实早就画在画面上，只是没被我们发现。
    ///   * `发送→回包`：**IPC 往返 + App 处理**（含它那边的查库与联网 API）。
    ///   * `回包→写回`：回包已经在队列里了，但要等下一次扫描才被 `Apply` 取走 ——
    ///     **这同样是扫描节奏成本**，且它同时挂在"热缓存命中"的路径上：
    ///     缓存命中省掉了 API 的 2.5 秒，但省不掉这一段。
    ///
    /// 取 <c>state</c> 而不是 <c>reply</c>：这个函数要能被 <see cref="TryApplyDirectly"/>
    /// 调用，而那一层拿不到 <c>reply</c>。`state.RequestId` 与之等价，且一定有值
    /// （`Apply` 开头已经用 <c>ReplacementPolicy.ReplyMatches</c> 校验过两者相同）。
    /// </summary>
    /// <summary>这一轮回包被主线程取走的时刻（单调毫秒），由 <see cref="Apply"/> 设置。</summary>
    private long _replyDequeuedMonoMs;

    private void LogLatency(long id, TextState state, long recvMonoMs, long dequeuedMonoMs = 0)
    {
        if (!LatencyLogEnabled) return;
        if (_latencyLogged >= LatencyLogMax)
        {
            // 被限流掉的行必须**说一声**，否则日志看起来像"后面没有请求了"。
            if (_latencySuppressed++ == 0)
                Logger.LogInfo($"[延迟] 已达输出上限 {LatencyLogMax} 行，后续计时不再打印"
                    + "（需要更多请设 UT_LATENCY_MAX）");
            return;
        }
        _latencyLogged++;
        // 回包没打点（recvMonoMs=0）时，`发送→回包` 与 `回包→写回` 都算不出来：
        // **如实写"不可用"，不写 0ms** —— 静默的 0 会被读成"这一段不花时间"。
        var recvOk = recvMonoMs > 0 && state.SentMonoMs > 0;
        var sendMs = recvOk ? recvMonoMs - state.SentMonoMs : -1;
        var writeMs = recvMonoMs > 0 ? PipeClient.MonoNowMs() - recvMonoMs : -1;
        // 把 `回包→写回` 再拆成"在队列里等下一次扫描"与"Apply 内部真正花掉的时间"。
        var dequeuedMs = dequeuedMonoMs > 0 ? dequeuedMonoMs : _replyDequeuedMonoMs;
        var queueMs = recvMonoMs > 0 && dequeuedMs > 0 ? dequeuedMs - recvMonoMs : -1;
        var applyMs = dequeuedMs > 0 ? PipeClient.MonoNowMs() - dequeuedMs : -1;
        Logger.LogInfo("[延迟] req=" + state.RequestId
            + " 组件=" + id
            + " 发现→发送=" + (state.SentMonoMs - state.DiscoveredMonoMs) + "ms"
            + " 发送→回包=" + (sendMs >= 0 ? sendMs + "ms" : "不可用")
            + " 回包→写回=" + (writeMs >= 0 ? writeMs + "ms" : "不可用")
            + " 其中排队=" + (queueMs >= 0 ? queueMs + "ms" : "不可用")
            + " 写回=" + (applyMs >= 0 ? applyMs + "ms" : "不可用")
            + " 字体现成=" + (CjkFontReady ? "是" : "否")
            + " 尝试=" + state.Attempts
            + " 「" + Shorten(state.Translated ?? "") + "」");
    }

    /// <summary>中文字体是否已可用来直接替换原文（埋点用，避免在多处重复判空）。</summary>
    private bool CjkFontReady => _cjkFont is not null && _cjkFont.IsReady;

    /// <summary>
    /// 驱动中文字体装配：每帧只做一点点（见 <see cref="RuntimeCjkFontAsset.Builder"/> 的时间预算）。
    ///
    /// 为什么要分帧：把几千个汉字装进字体图集要逐字取字形并渲染进纹理，
    /// 一次性做实测要十几到几十秒 CPU —— 放在首次翻译时同步做，游戏会像卡死一样。
    /// 装配期间译文继续用游戏内覆盖显示，装好之后自动切换到"直接替换原文"。
    /// </summary>
    private void TickCjkFont()
    {
        var builder = _cjkFont;
        if (builder is null) return;

        // ★ 装配器已经完成，但还挂着"图集里没有这些字"的译文时，**主动补一版字体**。
        //
        //   第 98 轮实测（Kurea）暴露的漏洞：`QueueCjkCharacters` 会给新字新建一个
        //   "延续上一版"的装配器，这本来是设计好的机制；但 `TickCjkFont` 在
        //   `builder.IsFinished` 时**直接 return**，于是那条路只有"下一次正好走到
        //   TryApplyDirectly 的补字分支"才会被触发。而挂起项恰恰是走不到那条分支的那些。
        //   结果：方框一直留在画面上，字体永远不再长。
        //   现在：只要还有挂起译文缺字，就在这里把它们排进队列（会新建延续版装配器），
        //   装配完成后 `RetryPendingDirect` 会把它们换成"直接替换"。
        if (builder.IsFinished)
        {
            QueueMissingForPending();
            if (ReferenceEquals(_cjkFont, builder)) return;   // 没有新字要补
            builder = _cjkFont;
            if (builder is null) return;
        }

        builder.Step();

        // 装配过程中每隔一段时间报一次进度，方便判断"是不是卡在装配上"
        if (Time.unscaledTime >= _fontBuildReportAt && !builder.IsFinished)
        {
            _fontBuildReportAt = Time.unscaledTime + 5f;
            Logger.LogInfo("中文字体装配进度：" + builder.Stage);
        }

        if (!builder.IsFinished) return;
        if (builder.IsReady)
        {
            _cjkFontReady = true;

            // ★★ 字体就绪 → **清除所有已挂起的覆盖层状态**，让之后任何一次 OnGUI 都不再画。
            //
            //   为什么必须清标记而不只是"绘制时跳过"：实测出现过
            //   `译文覆盖已绘制：位置 134,279 尺寸 352x67`（IMGUI 黑底 + 白字）
            //   与 `已直接替换游戏原文（组件 174156）` 同时存在 ——
            //   黑底方块与替换后的文字叠在一起，正是"黑方块 + 重复字"的成因。
            //   只加绘制守卫仍然留着一个隐患：状态里 UsesOverlay 还是 true，
            //   任何走到旧分支的代码路径都可能再画一次。这里把标记也一并清掉。
            var cleared = 0;
            foreach (var state in _states.Values)
            {
                if (!state.UsesOverlay) continue;
                state.UsesOverlay = false;
                cleared++;
            }

            Logger.LogInfo("中文字体已就绪，之后收到的译文将直接替换游戏原文（不再用覆盖块）："
                + builder.Stage + "；字体=" + builder.FontFileName
                + "；图集=" + builder.AtlasCount + " 张；shader=" + builder.ShaderName
                + (cleared > 0 ? "；已清除 " + cleared + " 条覆盖层状态" : ""));

            // 之前只能覆盖显示的译文，现在改写成直接替换
            RetryPendingDirect();
        }
        else
        {
            Logger.LogWarning("中文字体装配失败，将一直使用游戏内覆盖显示：" + builder.Failure);
        }
    }

    /// <summary>装配一个中文字体资源，并先装一批常用字保证立刻能用。幂等。</summary>
    private void EnsureCjkFontStarted()
    {
        if (_cjkFontStarted) return;
        _cjkFontStarted = true;
        try
        {
            _cjkFont = new RuntimeCjkFontAsset.Builder(LogCjkFont, ActiveGameFont, null, LogCjkFontAlways);
            // 把不受条数上限的日志通道接到字形矩形诊断上（`UT_GLYPH_TRACE=1` 时才会输出）。
            RuntimeCjkFontAsset.AttachGlyphTrace(LogCjkFontAlways);

            // 排查用：限制本次装配的字符数（环境变量预设，正式使用时不设置）。
            // 用来把"原生崩溃到底是随装配字数增长还是随装配时间增长"这一维分离出来。
            var limit = ReadCharacterLimit();

            // ★★ 首批只装**现在真的要用的字**，不要再把 4215 个汉字一次性预装。
            //
            //   第 94 轮实测（Leticia，真实 API，管道路径）：译文写回本身只要 **0~3ms**，
            //   可"回包→写回"却是 53~284ms 甚至 12761ms —— 差额**全部是在等字体装配**。
            //   机制在 RuntimeCjkFontAsset.Step()：每帧预算本来只有 12ms，但累计一旦超过
            //   MaxTimeSlicedSeconds(0.5s) 就走"一次性装完"分支，**无视预算**在一个帧里
            //   把剩下 3031 个字形全部装完 —— 主线程被独占 12.8~16 秒，期间收到的译文
            //   一条都写不回去，玩家看到的就是"游戏起来了但半天不出中文"。
            //
            //   而 `MinimumSet()` 那 4215 个字是为**最坏情况**准备的（you322 实测一屏要
            //   4215 个字形）。标题菜单只有 5 句、几十个不同的字，预装 4215 个纯属浪费。
            //
            //   所以改成"**首批 = 画面上已经有的原文 + 当前这批译文**"，其余字符由
            //   `QueueCjkCharacters`（它本来就在处理"译文里有字体还没装的字"）按需补装。
            //   字体通常在第一句译文到达时就绪，首屏中文立刻可见。
            //
            //   保守处理：`MinSeedCharacters` 给一个**下限**。否则在"一句都没翻出来"的
            //   情况下首批会退化成全量分批装配，等于没改（那种情况很少，但不能退化）。
            var seed = new List<char>();
            var seen = new HashSet<char>();
            foreach (var text in EnumerateSeedTexts())
                foreach (var value in text)
                {
                    if (!seen.Add(value)) continue;
                    seed.Add(value);
                }

            // ★★ 兜底常用字**必须无条件下**，不能只在"真实文本不足 256 字"时才补。
            //
            //   第 98 轮实测（Kurea Struggle）暴露了原来的错误：首批种子取的是
            //   "**画面上已见的原文**"，而原文是**日文**（続きから / 設定 / 終了…），
            //   我们真正要渲染的却是**这些原文的中文译文**（读取游戏 / 设置 / 退出游戏）。
            //   两套字集只有部分重叠 —— 结果是「戏 / 读 / 设」三个字不在图集里，
            //   而游戏原字体（一个只有 **17 个字符表项**的精简韩文字体）也没有它们，
            //   画面上就出现「新游□」「□取游□」「□置」「退出游□」。
            //
            // ★ 兜底只补到 `MinSeedCharacters` 为止，**不能**无条件把整批 4192 个都塞进去。
            //   第 98 轮我先写成"无条件全塞"，立刻把首批从 256 顶到 4215、
            //   把第 94 轮刚消掉的 12.8 秒装配卡顿又带了回来 —— 这就是"为了修一个洞砸掉一面墙"。
            //   正确的分工：首批只保证"够用 + 常用字打底"，**剩下的靠按需补字**
            //   （`QueueCjkCharacters` + `QueueMissingForPending`）。
            var fallbackBatch = CjkCharacterSet.PlanBatches(CjkCharacterSet.MinimumSet())[0];
            var fallbackAdded = 0;
            foreach (var value in fallbackBatch)
            {
                if (seed.Count >= MinSeedCharacters) break;
                if (!seen.Add(value)) continue;
                seed.Add(value);
                fallbackAdded++;
            }

            if (limit > 0 && seed.Count > limit) seed.RemoveRange(limit, seed.Count - limit);
            Logger.LogInfo("中文字体首批装配范围：画面上已见 + 兜底常用字，共 " + seed.Count + " 个不同字符"
                + "（其中兜底新增 " + fallbackAdded + " 个；原先一次性预装 "
                + CjkCharacterSet.MinimumSet().Count + " 个）");

            foreach (var value in _cjkFont.EnqueueNew(seed)) _knownLoaded.Add(value);
            Logger.LogInfo("开始装配中文字体（分帧进行，期间译文先用游戏内覆盖显示）；首批 "
                + _cjkFont.RequestedCharacters + " 个字符"
                + (limit > 0 ? "（受限：" + limit + "）" : ""));
        }
        catch (Exception ex)
        {
            Logger.LogWarning("启动中文字体装配失败（将退回覆盖显示）：" + ex.GetType().Name + " " + ex.Message);
            _cjkFont = null;
        }
    }

    /// <summary>
    /// 字体首批装配要覆盖的文本集合：**优先真实文本**（已经拿到的译文最要紧，
    /// 其次是已经在跟踪的原文），兜底见 <see cref="MinSeedCharacters"/>。
    /// 这里刻意**不**列出完整的 <c>MinimumSet()</c>（4215 字）—— 那正是要避免的预装。
    /// </summary>
    private IEnumerable<string> EnumerateSeedTexts()
    {
        foreach (var state in _states.Values)
        {
            if (!string.IsNullOrEmpty(state.Source)) yield return state.Source;
            if (!string.IsNullOrEmpty(state.Translated)) yield return state.Translated!;
        }
    }

    /// <summary>
    /// 首批字符数的**下限**（不是上限）。真实文本不足这么多字时，才从
    /// <c>MinimumSet()</c> 按顺序补齐。取值理由：字形装配是逐字的，几百个字的
    /// 装配时间远小于原先 4215 字的 12.8~16 秒，同时又足够覆盖首屏常用字。
    /// </summary>
    private const int MinSeedCharacters = 256;

    /// <summary>读取排查用的字符数上限；未设置或不合法返回 0（表示不限制）。</summary>
    private static int ReadCharacterLimit()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("UT_CJK_CHAR_LIMIT");
            return int.TryParse(raw, out var value) && value > 0 ? value : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 把还需要补装的字符排进装配队列。
    ///
    /// 两道过滤，避免"反复新建构建器"：
    ///   * 已经在队列里、或者已经装进去的字符，不入队；
    ///   * **字体本身就没有的字形**（例如 SimHei 缺一部分假名）记进"永久缺失"，
    ///     之后不再入队 —— 否则每次收到含这些字的译文都会触发一次重新装配，
    ///     而结果永远一样，白白烧 CPU。
    /// </summary>
    private void QueueCjkCharacters(IEnumerable<char> characters)
    {
        EnsureCjkFontStarted();
        var builder = _cjkFont;
        if (builder is null || builder.Failure is not null) return;

        var retryable = new List<char>();
        foreach (var value in characters)
        {
            if (value == '\0') continue;
            if (builder.MissingCharacters.Contains(value)) continue;   // 装不进去的，不再试
            if (_knownLoaded.Contains(value)) continue;                 // 已装好的
            retryable.Add(value);
        }
        if (retryable.Count == 0) return;

        var added = builder.EnqueueNew(retryable);
        foreach (var value in added) _knownLoaded.Add(value);
        if (added.Count == 0) return;

        // 已经就绪的资源是冻结的：补字要新建一个"延续上一版"的构建器，
        // 否则就地改动会影响到已经用它渲染好的文本。
        if (builder.IsReady)
        {
            var next = new RuntimeCjkFontAsset.Builder(LogCjkFont, ActiveGameFont, builder, LogCjkFontAlways);
            _cjkFont = next;
            // 新构建器延续了上一版的字符，重新同步"已知字符"集合，
            // 否则这些字会被当成新字再次排队。
            _knownLoaded.Clear();
            foreach (var value in next.KnownCharacters) _knownLoaded.Add(value);
            if (_cjkFontLogCount++ < 20)
                LogCjkFont("出现 " + added.Count + " 个新字，开始装配下一版字体（延续 "
                    + next.LoadedCharacters + " 个已有字形）");
        }
    }

    /// <summary>已经装好或已入队的字符，避免重复排队。</summary>
    private readonly HashSet<char> _knownLoaded = new HashSet<char>();

    /// <summary>装配进度与问题走统一前缀，便于日志里检索。</summary>
    private void LogCjkFont(string message)
    {
        if (_cjkFontLogCount++ < 60) Logger.LogInfo("[字体装配] " + message);
    }

    /// <summary>
    /// **不受条数上限**的装配日志。自检结论必须无条件写出去：
    /// 实测为了"避免刷屏"的 60 条上限，把自检输出整段吞掉，白跑了一轮实机测试。
    /// </summary>
    private void LogCjkFontAlways(string message) => Logger.LogInfo("[字体装配·自检] " + message);

    /// <summary>借游戏正在使用的字体资源的 shader —— TMP 内部的 shader 引用外部换不了。</summary>
    private TMP_FontAsset? ActiveGameFont
    {
        get
        {
            try
            {
                foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                {
                    if (text is null || text.font is null) continue;
                    if (text.font.name.StartsWith("UT-CJK", StringComparison.Ordinal)) continue;
                    return text.font;
                }
            }
            catch (Exception) { }
            return null;
        }
    }

    /// <summary>
    /// **首选路径**：把游戏自己的字体换成装配好的中文字体，并把组件文本直接替换成译文。
    /// 这是"和游戏融为一体"的做法 —— 排版、描边、颜色、动画全都还是游戏自己的。
    ///
    /// 返回 true 表示已经处理完（成功替换，或者需要等字体装好）。
    /// 返回 false 表示这条路走不通，调用方应退回游戏内覆盖显示。
    /// </summary>
    private bool TryApplyDirectly(TextState state, string translated, long recvMonoMs = 0)
    {
        if (!_enabled || !IsLive(state)) return false;
        RefreshNativeFontBaseline(state);
        var tmpFontBefore = state.Tmp?.font;
        var tmpMaterialBefore = state.Tmp?.fontSharedMaterial;
        var uiFontBefore = state.Ui?.font;
        var uiMaterialBefore = state.Ui?.material;
        var fallbackCountBefore = state.OwnedFallbacks.Count;
        var committed = false;
        try
        {
            if (state.Ui != null)
            {
                if (!EnsureUiFontCanRender(state.Ui, translated)) return false;
            }
            else if (state.Tmp != null)
            {
                TrackOwnedFallbacks(state, state.Tmp.font);
                if (!CanRenderWithFallbacks(state.Tmp.font, translated))
                {
                    EnsureCjkFontStarted();
                    var asset = _cjkFont?.IsReady == true ? _cjkFont.Asset : null;
                    if (asset == null || !CanRenderWithFallbacks(asset, translated))
                    {
                        QueueCjkCharacters(translated);
                        asset = Fallback.Acquire(translated);
                    }
                    if (asset == null || !CanRenderWithFallbacks(asset, translated)) return false;
                    if (!TryAttachMissingGlyphs(state, state.Tmp, state.Tmp.font, asset, translated))
                        state.Tmp.font = asset;
                }
            }
            else return false;

            var component = (object?)state.Tmp ?? state.Ui!;
            if (!state.Layout.TryApply(component, translated))
            {
                if (_cannotApplyLogged++ < 8)
                    Logger.LogWarning("译文无法适合游戏组件，保留原文：" + state.Layout.FailureReason);
                return false;
            }
            if (state.Tmp != null && state.Tmp.font != state.OriginalTmpFont)
            {
                state.AppliedTmpFont = state.Tmp.font;
                state.AppliedTmpMaterial = state.Tmp.fontSharedMaterial;
            }
            if (state.Ui != null && state.Ui.font != state.OriginalUiFont)
            {
                state.AppliedUiFont = state.Ui.font;
                state.AppliedUiMaterial = state.Ui.material;
            }
            state.Translated = translated;
            state.UsesOverlay = false;
            var id = state.Tmp != null ? state.Tmp.GetInstanceID() : state.Ui!.GetInstanceID();
            RememberTranslation(id, state.Source, translated);
            if (_directAppliedLogged++ < 12)
                Logger.LogInfo("[字号归一化] 组件 " + id + " 原字号=" + state.Layout.OriginalFontSize.ToString("0.##")
                    + " → 新字号=" + state.Layout.AppliedFontSize.ToString("0.##"));
            LogLatency(id, state, recvMonoMs);
            committed = true;
            return true;
        }
        catch (MissingReferenceException) { return false; }
        catch (Exception ex)
        {
            Logger.LogWarning("直接替换原文失败，保留原文：" + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    if (state.Tmp != null && state.Tmp.font != tmpFontBefore)
                    {
                        state.Tmp.font = tmpFontBefore;
                        state.Tmp.fontSharedMaterial = tmpMaterialBefore;
                    }
                    if (state.Ui != null && state.Ui.font != uiFontBefore)
                    {
                        state.Ui.font = uiFontBefore;
                        state.Ui.material = uiMaterialBefore;
                    }
                }
                catch (Exception) { }
                ReleaseFallbacks(state, fallbackCountBefore);
                // A failed reapplication must not leave original source text
                // wearing an earlier translation's replacement font.
                if (CurrentText(state) != state.Translated) RestoreOwnedFonts(state);
            }
        }
    }

    private readonly List<OwnedTmpFallback> _ownedTmpFallbacks = new List<OwnedTmpFallback>();

    private static bool CanRenderWithFallbacks(TMP_FontAsset? font, string text)
    {
        foreach (var ch in FontReplacementPolicy.RequiredGlyphs(text))
        {
            // TMP takes an existing primary character before consulting any
            // fallback. A broken zero-rect entry needs a whole-font candidate.
            if (HasBrokenPrimaryGlyph(font, ch)) return false;
            if (CanRenderGlyph(font, ch, new HashSet<TMP_FontAsset>())) continue;
            var found = false;
            try
            {
                var global = TMP_Settings.fallbackFontAssets;
                if (global != null)
                    foreach (var fallback in global)
                        if (CanRenderGlyph(fallback, ch, new HashSet<TMP_FontAsset>())) { found = true; break; }
            }
            catch (Exception) { }
            if (!found) return false;
        }
        return font != null;
    }

    private static bool CanRenderGlyph(TMP_FontAsset? font, char ch, HashSet<TMP_FontAsset> visited)
    {
        if (font == null || !visited.Add(font)) return false;
        if (HasBrokenPrimaryGlyph(font, ch)) return false;
        if (RuntimeCjkFontAsset.CanRender(font, ch.ToString())) return true;
        try
        {
            var table = font.fallbackFontAssetTable;
            if (table != null)
                foreach (var fallback in table)
                    if (CanRenderGlyph(fallback, ch, visited)) return true;
        }
        catch (Exception) { }
        return false;
    }

    private static bool HasBrokenPrimaryGlyph(TMP_FontAsset? font, char ch)
    {
        try { return font != null && font.HasCharacter(ch) && !RuntimeCjkFontAsset.CanRender(font, ch.ToString()); }
        catch (Exception) { return false; }
    }

    private bool TryAttachMissingGlyphs(TextState state, TMP_Text target, TMP_FontAsset? original,
        TMP_FontAsset fallback, string translated)
    {
        if (original == null || original == fallback) return false;
        foreach (var ch in FontReplacementPolicy.RequiredGlyphs(translated))
            if (HasBrokenPrimaryGlyph(original, ch)
                || (!CanRenderWithFallbacks(original, ch.ToString()) && !CanRenderWithFallbacks(fallback, ch.ToString())))
                return false;
        try
        {
            var table = original.fallbackFontAssetTable;
            if (table == null) original.fallbackFontAssetTable = table = new List<TMP_FontAsset>();
            if (!table.Contains(fallback))
            {
                table.Add(fallback);
                var owned = new OwnedTmpFallback { Primary = original, Fallback = fallback, Table = table, References = 1 };
                _ownedTmpFallbacks.Add(owned);
                state.OwnedFallbacks.Add(owned);
            }
            else TrackOwnedFallbacks(state, original);
            target.SetAllDirty();
            return CanRenderWithFallbacks(original, translated);
        }
        catch (Exception) { return false; }
    }

    private void TrackOwnedFallbacks(TextState state, TMP_FontAsset? primary)
    {
        foreach (var owned in _ownedTmpFallbacks)
        {
            if (owned.Primary != primary || state.OwnedFallbacks.Contains(owned)) continue;
            owned.References++;
            state.OwnedFallbacks.Add(owned);
        }
    }

    private void ReleaseFallbacks(TextState state, int keep = 0)
    {
        for (var i = state.OwnedFallbacks.Count - 1; i >= keep; i--)
        {
            var owned = state.OwnedFallbacks[i];
            state.OwnedFallbacks.RemoveAt(i);
            if (--owned.References != 0) continue;
            try
            {
                if (owned.Primary != null && ReferenceEquals(owned.Primary.fallbackFontAssetTable, owned.Table))
                    owned.Table.Remove(owned.Fallback);
            }
            catch (Exception) { }
            _ownedTmpFallbacks.Remove(owned);
        }
    }

    private sealed class OwnedTmpFallback
    {
        public TMP_FontAsset Primary = null!;
        public TMP_FontAsset Fallback = null!;
        public List<TMP_FontAsset> Table = null!;
        public int References;
    }

    /// <summary>
    /// 把一个 RectTransform 的**屏幕位置与尺寸**描述出来。
    ///
    /// 为什么需要：叠字的排查必须先确认"是不是两个组件叠在同一个屏幕位置"。
    /// 只记层级路径与文字看不出这一点（实测为此多绕了一轮），
    /// 而屏幕矩形能一眼看出哪几个组件占同一块地方。
    /// </summary>
    private static string DescribeScreenRect(Transform transform)
    {
        try
        {
            var rectTransform = transform as RectTransform;
            if (rectTransform is null) return "(非 RectTransform)";

            var rect = rectTransform.rect;
            // 世界坐标 → 屏幕坐标（Overlay 画布下 world == screen，其余情况取相机）
            var canvas = transform.GetComponentInParent<Canvas>();
            var camera = canvas is not null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            var center = RectTransformUtility.WorldToScreenPoint(camera, rectTransform.position);

            return center.x.ToString("0") + "," + center.y.ToString("0")
                + " " + rect.width.ToString("0") + "x" + rect.height.ToString("0")
                + " 锚点=" + rectTransform.anchorMin.x.ToString("0.##") + "," + rectTransform.anchorMin.y.ToString("0.##")
                + "->" + rectTransform.anchorMax.x.ToString("0.##") + "," + rectTransform.anchorMax.y.ToString("0.##")
                + (canvas is not null ? " 画布=" + canvas.renderMode : " 无画布");
        }
        catch (Exception ex)
        {
            return "(读取失败 " + ex.GetType().Name + ")";
        }
    }

    private static string Shorten(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 40 ? flat : flat.Substring(0, 40) + "…";
    }

    /// <summary>
    /// 记下"暂时只能用覆盖块显示"的译文，等字体补齐后重新尝试直接替换。
    /// 只保留最近若干条，避免长时间运行时无限增长。
    /// </summary>
    private void RememberPendingTranslation(TextState state, string translated)
    {
        _pendingDirect.Add(new PendingDirect(state, translated));
        if (_pendingDirect.Count > 200) _pendingDirect.RemoveAt(0);
    }

    /// <summary>
    /// 把"还挂着的译文"里图集没有的字排进装配队列。
    ///
    /// 只做排队、不碰组件：真正换字体由装配完成后的 <see cref="RetryPendingDirect"/> 负责。
    /// 每帧调用是安全的 —— <c>QueueCjkCharacters</c> 自己会用 `_knownLoaded` 与
    /// `MissingCharacters` 两道过滤去重，不会反复入队。
    /// </summary>
    private void QueueMissingForPending()
    {
        if (_pendingDirect.Count == 0) return;
        var asset = _cjkFont?.Asset;
        if (asset is null) return;

        var needed = new List<char>();
        foreach (var pending in _pendingDirect)
        {
            foreach (var ch in RuntimeCjkFontAsset.MissingChars(asset, pending.Translated))
                if (!needed.Contains(ch)) needed.Add(ch);
        }
        if (needed.Count == 0) return;

        // 只在**真的换了装配器**（即确实新增了字）时说一声，避免每帧刷屏。
        // 判断依据就是 `QueueCjkCharacters` 在补字成功时会新建一个延续版装配器。
        var beforeBuilder = _cjkFont;
        QueueCjkCharacters(needed);
        if (!ReferenceEquals(_cjkFont, beforeBuilder))
            Logger.LogInfo("[补字] 挂起译文里还有 " + needed.Count + " 个字形不在图集里，已排入下一版字体");
    }

    /// <summary>字体换新之后，把之前只能覆盖显示的译文改成直接替换。</summary>
    private void RetryPendingDirect()
    {
        if (_pendingDirect.Count == 0) return;
        var asset = _cjkFont?.Asset;
        if (asset is null) return;

        var retried = 0;
        var applied = 0;
        var skippedMissing = 0;
        var droppedNotLive = 0;
        var droppedTextChanged = 0;
        for (var index = _pendingDirect.Count - 1; index >= 0; index--)
        {
            var pending = _pendingDirect[index];
            var componentId = pending.State.Tmp != null ? pending.State.Tmp.GetInstanceID() : 0;
            if (!_states.TryGetValue(componentId, out var active) || !ReferenceEquals(active, pending.State))
            {
                _pendingDirect.RemoveAt(index);
                continue;
            }
            // 状态已经失效（组件销毁/文字被游戏改掉）就丢掉
            if (!IsLive(pending.State))
            {
                _pendingDirect.RemoveAt(index);
                droppedNotLive++;
                continue;
            }
            // ★ 组件上**当前**要么还是原文，要么已经是这条译文 —— 两种都还要处理。
            //
            //   第 98 轮实测（Kurea）踩到的坑：原来的判据是
            //   `CurrentText(state) != state.Source` 就**丢掉**这条挂起项。
            //   可是走"挂起"这条路的前提，恰恰是 `Apply` 先判定字体不可用并**保留了原文**；
            //   而同一轮里 `RetryPendingDirect` 又会把译文**用游戏原字体写进组件** ——
            //   于是 `CurrentText` 变成了译文、不再等于 `Source`，
            //   这条挂起项就被**自己的上一次尝试作废了**，永远等不到字体补齐。
            //   表现就是方框一直留在画面上（玩家看到的「新游□」）。
            //   现在：文字还是原文、或已经是这条译文，都继续尝试换字体。
            var currentText = CurrentText(pending.State);
            if (currentText != pending.State.Source && currentText != pending.Translated)
            {
                _pendingDirect.RemoveAt(index);
                droppedTextChanged++;
                continue;
            }
            if (RuntimeCjkFontAsset.MissingChars(asset, pending.Translated).Count > 0)
            {
                skippedMissing++;
                continue;
            }
            retried++;
            if (TryApplyDirectly(pending.State, pending.Translated))
            {
                applied++;
                _pendingDirect.RemoveAt(index);
            }
        }

        // ★ 这段统计**必须无条件输出**（以前只有 applied>0 才打一行）。
        //   第 98 轮就是被这个"只在成功时说话"坑了一整轮：
        //   日志里只有"已把 1 条改成直接替换"，看不出**另外 4 条到底怎么了** ——
        //   是被判失效丢了、还是缺字形跳过、还是根本没进来。
        //   静默的跳过比报错更难查。
        Logger.LogInfo("[补齐统计] 挂起 " + (applied + skippedMissing + droppedNotLive + droppedTextChanged)
            + " 条：已应用=" + applied
            + " 缺字形跳过=" + skippedMissing
            + " 组件已失效丢弃=" + droppedNotLive
            + " 文字已变丢弃=" + droppedTextChanged);
    }

    private sealed class PendingDirect
    {
        public PendingDirect(TextState state, string translated)
        {
            State = state;
            Translated = translated;
        }

        public TextState State { get; }
        public string Translated { get; }
    }

    private readonly List<PendingDirect> _pendingDirect = new List<PendingDirect>();

    private bool EnsureUiFontCanRender(Text target, string text)
    {
        try
        {
            var current = target.font;
            if (current != null && FontHasCharacters(current, text)) return true;

            var fallback = _chineseFont;
            if (fallback != null && !FontHasCharacters(fallback, text))
            {
                // 上次加载的字体渲染不了这次的新文字：丢掉它重新走一遍候选链，
                // 而不是将就着把渲染不出来的译文写进去。
                Logger.LogWarning("已加载的中文字体缺少字形，重新查找候选（缺失 "
                    + FontReplacementPolicy.DescribeMissingGlyphs(MissingChars(fallback, text)) + "）");
                fallback = null;
                _chineseFont = null;
            }
            fallback ??= Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);
            if (fallback != null && !FontHasCharacters(fallback, text))
            {
                fallback = null;
                _chineseFont = null;
            }
            if (fallback is null)
            {
                if (_uiFontFailuresLogged++ < 5)
                    Logger.LogWarning("中文字体不可用，原文保持不变：候选字体都缺字形或系统里没有可用中文字体（缺失 "
                        + FontReplacementPolicy.DescribeMissingGlyphs(MissingChars(target.font, text)) + "）");
                return false;
            }

            _chineseFont = fallback;
            if (!ReferenceEquals(current, fallback))
                target.font = fallback;
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Cannot load Chinese font; original text kept: " + ex.Message);
            return false;
        }
    }

    /// <summary>列出某个字体渲染不出来的字符，用于日志。</summary>
    private static IReadOnlyList<char> MissingChars(Font? font, string text)
    {
        if (font is null) return FontReplacementPolicy.RequiredGlyphs(text);
        var missing = new List<char>();
        foreach (var character in FontReplacementPolicy.RequiredGlyphs(text))
        {
            bool has;
            try { has = font.HasCharacter(character); }
            catch (Exception) { has = false; }
            if (!has) missing.Add(character);
        }
        return missing;
    }

    private static bool FontHasCharacters(Font font, string text)
    {
        foreach (var character in FontReplacementPolicy.RequiredGlyphs(text))
            if (!font.HasCharacter(character))
                return false;
        return true;
    }

    private void RestoreAll()
    {
        foreach (var state in _states.Values)
            Restore(state);
        _states.Clear();
        _pendingDirect.Clear();
        // 状态全清 → 在飞登记也必须清：它的主组件已经不在 `_states` 里了，
        // 留着只会让后来的同原文组件搭上一辆永远不来的车（`PruneInFlight` 要等 30 秒才救）。
        _inFlight.Clear();
        InvalidateOcr();
    }

    private static bool Exists(TextState state) => state.Tmp != null || state.Ui != null;

    private static bool IsLive(TextState state) => Exists(state) &&
        (state.Tmp != null ? state.Tmp.gameObject.activeInHierarchy : state.Ui!.gameObject.activeInHierarchy);

    private static string? CurrentText(TextState state) => state.Tmp != null ? state.Tmp.text : state.Ui != null ? state.Ui.text : null;

    private void Restore(TextState state)
    {
        if (!Exists(state)) { ReleaseFallbacks(state); return; }
        try
        {
            if (ReplacementPolicy.ShouldRestore(CurrentText(state), state.Translated)) state.Write(state.Source);
        }
        catch (MissingReferenceException) { }
        finally { RestoreStyle(state); }
    }

    private void RestoreStyle(TextState state)
    {
        if (state.Tmp != null) state.Layout.Restore(state.Tmp);
        if (state.Ui != null) state.Layout.Restore(state.Ui);
        RestoreOwnedFonts(state);
        ReleaseFallbacks(state);
    }

    private static void RestoreOwnedFonts(TextState state)
    {
        try
        {
            if (state.Tmp != null && state.AppliedTmpFont != null && state.Tmp.font == state.AppliedTmpFont)
            {
                var materialStillOurs = state.Tmp.fontSharedMaterial == state.AppliedTmpMaterial;
                var gameMaterial = state.Tmp.fontSharedMaterial;
                state.Tmp.font = state.OriginalTmpFont;
                state.Tmp.fontSharedMaterial = materialStillOurs ? state.OriginalTmpMaterial : gameMaterial;
            }
            if (state.Ui != null && state.AppliedUiFont != null && state.Ui.font == state.AppliedUiFont)
            {
                var materialStillOurs = state.Ui.material == state.AppliedUiMaterial;
                state.Ui.font = state.OriginalUiFont;
                if (materialStillOurs) state.Ui.material = state.OriginalUiMaterial;
            }
        }
        catch (Exception) { }
        state.AppliedTmpFont = null;
        state.AppliedTmpMaterial = null;
        state.AppliedUiFont = null;
        state.AppliedUiMaterial = null;
    }

    private static void CaptureNativeStyle(TextState state)
    {
        state.OriginalTmpFont = state.Tmp?.font;
        state.OriginalTmpMaterial = state.Tmp?.fontSharedMaterial;
        state.OriginalUiFont = state.Ui?.font;
        state.OriginalUiMaterial = state.Ui?.material;
        state.Layout = new ReflectionTextLayout((object?)state.Tmp ?? state.Ui!);
    }

    private static void RefreshNativeFontBaseline(TextState state)
    {
        if (state.Tmp != null && (state.AppliedTmpFont == null || state.Tmp.font != state.AppliedTmpFont))
        {
            state.AppliedTmpFont = null;
            state.AppliedTmpMaterial = null;
            state.OriginalTmpFont = state.Tmp.font;
            state.OriginalTmpMaterial = state.Tmp.fontSharedMaterial;
        }
        if (state.Ui != null && (state.AppliedUiFont == null || state.Ui.font != state.AppliedUiFont))
        {
            state.AppliedUiFont = null;
            state.AppliedUiMaterial = null;
            state.OriginalUiFont = state.Ui.font;
            state.OriginalUiMaterial = state.Ui.material;
        }
    }

    private void OnDestroy()
    {
        RestoreAll();
        _pipe?.Dispose();
        DeleteLastFrame();
    }

    private static bool IsTranslatable(string text) => TextSelectionPolicy.IsTranslatable(text);

    private static string SourceLanguage(string text) => TextSelectionPolicy.DetectLanguage(text);

    private sealed class TextState
    {
        public string Source = string.Empty;
        public string? Translated;
        public bool UsesOverlay;
        public int Generation;
        public string RequestId = string.Empty;
        public float LastSentAt;
        public int Attempts;
        public Action<string> Write = _ => { };
        public TMP_Text? Tmp;

        // ── 延迟埋点（用户新增需求：降低「原文出现 → 译文可见」的延迟）──────────────
        // 全部用**单调**毫秒（PipeClient.MonoNowMs），不用 DateTime：
        // 后者会被系统对时跳变污染，把差值算成负数或假尖峰。

        /// <summary>本次请求**真正写出**的时刻。重发时会刷新（用于区分首次与重试）。</summary>
        public long SentMonoMs;

        /// <summary>
        /// 这一轮扫描开始时 <see cref="Source"/> 还不在 <c>_states</c> 里 ——
        /// 也就是"原文是在**这一轮**才开始被跟踪的"。用来算<b>发现延迟</b>：
        /// 文字早已在画面上，但我们每 0.4 秒才全场景翻一遍，它可能等了几百毫秒才被发现。
        /// </summary>
        public bool DiscoveredThisSweep;

        /// <summary>这一轮扫描开始的时刻（用于算"发现→发送"这一段）。</summary>
        public long DiscoveredMonoMs;

        public Text? Ui;
        public TMP_FontAsset? OriginalTmpFont;

        /// <summary>组件原来的字号。字号归一化改过它，还原时必须一并恢复。</summary>
        public ReflectionTextLayout Layout = null!;
        public Material? OriginalTmpMaterial;
        public Material? OriginalUiMaterial;
        public TMP_FontAsset? AppliedTmpFont;
        public Material? AppliedTmpMaterial;
        public Font? AppliedUiFont;
        public Material? AppliedUiMaterial;
        public readonly List<OwnedTmpFallback> OwnedFallbacks = new List<OwnedTmpFallback>();
        public Font? OriginalUiFont;
    }
}

[Serializable]
public sealed class RequestMessage
{
    public int Version = 1;
    public string RequestId = string.Empty;
    public string Source = string.Empty;
    public string SourceLanguage = string.Empty;
    public long ComponentId;
    public int Generation;
}

[Serializable]
public sealed class ReplyMessage
{
    public int Version;
    public string RequestId = string.Empty;
    public string Translated = string.Empty;
    public long ComponentId;
    public int Generation;

    /// <summary>
    /// 收包瞬间的单调毫秒数（由 <c>PipeClient.EnqueueReply</c> 在入队前打上）。
    ///
    /// 它是插件侧延迟埋点的**锚点**：`发送→回包` = 本字段 − <c>TextState.SentMonoMs</c>。
    /// 之所以要在**收包**而不是**写回**时打点：两者之间隔着
    /// ① 入队等待、② 主线程扫描周期（最长 0.4 秒）—— 把这三段分开才能知道
    /// "等待"到底是花在网络上还是花在我们自己的扫描节奏上。
    /// 默认 0 表示"这条回包没被打点"（例如旧版本 App 发来的），此时不输出该段而不是输出 0ms。
    /// </summary>
    public long RecvMonoMs;
}
