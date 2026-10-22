using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using UnityTranslator.Core;

namespace UnityTranslator.Plugin;

/// <summary>
/// 运行时用**系统字体文件的字节**装配一个能显示中文的 <see cref="TMP_FontAsset"/>。
///
/// 为什么必须自己装配（实机验证过的唯一可行路径）：
///   TMP 自带的两个工厂入口在本机所有候选上都是死的 ——
///   <c>Font.CreateDynamicFontFromOSFont</c> 拿到的动态字体对象**背后没有真实 face**
///   （<c>FontEngine.SetFaceSize</c> 返回 Invalid_File），于是
///   <c>TMP_FontAsset.CreateFontAsset(Font)</c> 对每个系统字体候选都返回 null。
///   而 <c>FontEngine.LoadFontFace(byte[] 字体文件字节, …)</c> 是成功的。
///   所以正确做法是绕开 TMP 的工厂，用字体引擎自己把资源装起来。
///
/// 实机验证（Unity 2021.3.45 / 2022.3.62 / 6000.0.68 都跑过）。
///
/// 绝对不能做的事（会让 Unity 原生层直接崩溃，进程消失）：
///   * 对游戏自带的、源字体被剥离字体数据的字体资源调 TryAddCharacters；
///   * 把动态 OS 字体塞进 m_SourceFontFile 并用 Dynamic 模式；
///   * **逐个字形**调用 TryAddGlyphsToTexture（实测 1200~2500 次之间必崩 0xc0000005）；
///   * 开第二张图集（同样会让原生崩溃）；
///   * 多线程调用 FontEngine（face 是进程级全局状态）。
/// </summary>
internal static class RuntimeCjkFontAsset
{
    /// <summary>
    /// 采样点大小。**必须等于 <see cref="FontAssetPipeline.ReferencePointSize"/>**：
    /// 图集矩形锁死了字形能渲染多大，渲染点大小与度量基准不一致就会出巨大/糊掉的字形
    /// （第 4 轮实测）。
    /// </summary>
    internal const int DefaultPointSize = FontAssetPipeline.PointSize;

    /// <summary>
    /// 图集内边距。**决定 SDF 的梯度展开宽度**，压小会让画面所有文字（含拉丁字母）变空心方框。
    /// </summary>
    internal const int DefaultPadding = FontAssetPipeline.Padding;

    /// <summary>图集边长。必须一张装完全部常用字（开第二张会让原生崩溃）。</summary>
    internal const int DefaultAtlasSize = FontAssetPipeline.AtlasSize;

    /// <summary>最多允许用多少张图集。**实机只用 1 张**，这个上限只用于参数写错时兜底。</summary>
    internal const int MaxAtlasCount = FontAssetPipeline.MaxAtlasCount;

    /// <summary>
    /// 找一台机器上可用的中文字体文件。
    ///
    /// **顺序按实测覆盖率排**（Mad Island / Unity 2021，400 字样本）：
    ///   msyh.ttc=400/400   simhei.ttf=398/400   Deng.ttf=398/400
    ///   simkai.ttf=398/400 simsun.ttc=398/400   simsunb.ttf=10/400
    ///
    /// ⚠️ **不能在同一个进程里把这些候选逐个 LoadFontFace 来比较覆盖率**：
    /// face 是进程级全局状态，连续载入多个不同字体文件会把它弄坏，随后原生层访问违例崩溃。
    /// </summary>
    internal static readonly string[] CandidateFontFiles =
    {
        "msyh.ttc",     // Microsoft YaHei：实测覆盖最全
        "msyh.ttf",
        "simhei.ttf",   // 黑体
        "Deng.ttf",     // 等线
        "simkai.ttf",   // 楷体
        "simsun.ttc",   // 宋体
        "simsunb.ttf",
    };

    internal static string? FindUsableFontFile(Action<string>? warn = null)
    {
        string fontsDirectory;
        try { fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
        catch (Exception ex) { warn?.Invoke("取系统字体目录失败：" + ex.GetType().Name); return null; }

        if (string.IsNullOrEmpty(fontsDirectory) || !Directory.Exists(fontsDirectory))
        {
            warn?.Invoke("系统字体目录不存在：" + fontsDirectory);
            return null;
        }

        foreach (var name in CandidateFontFiles)
        {
            var path = Path.Combine(fontsDirectory, name);
            try { if (File.Exists(path) && new FileInfo(path).Length > 0) return path; }
            catch (Exception ex) { warn?.Invoke("检查字体文件失败：" + name + " " + ex.GetType().Name); }
        }
        warn?.Invoke("候选字体一个都不存在：" + string.Join(",", CandidateFontFiles));
        return null;
    }

    /// <summary>按点大小与图集尺寸估算单张图集能装多少字形（用实测装箱效率折算）。</summary>
    internal static int EstimateGlyphsPerAtlas(int pointSize = DefaultPointSize,
        int padding = DefaultPadding, int atlasSize = DefaultAtlasSize)
        => FontAssetPipeline.EstimatedAtlasCapacity(pointSize, padding, atlasSize);

    /// <summary>
    /// 这个字体资源能否**真的画出**这段文字（逐字符，且要求图集里有实际矩形）。
    ///
    /// ★ 为什么不能只看 <c>HasCharacter</c>（第 99 轮实测踩到）：
    ///   `HasCharacter` 只回答"字符表里有没有这个条目"。而**游戏自带字体**完全可以
    ///   有条目、却没有可绘制的位图 —— 那时 `glyphRect` 是 (0,0,0,0)，
    ///   TMP 采样一个空矩形，画面上就是**空心方框**，而 `HasCharacter` 依然返回 true。
    ///
    ///   实测对象：Kurea Struggle 的 `경기천년제목_Light SDF`（字符表只有 17 项，
    ///   一个被裁剪过的韩文字体）。它自称能显示「戏 / 读 / 设」，实际画出来全是方框 ——
    ///   而插件因此判定"当前字体够用"、**不换字体**，于是方框就一直留在画面上。
    ///   把我们的插件整个移走、方框依然存在，可证那是游戏自带的中文界面本身的问题；
    ///   但**我们完全可以接管它**：我们的字体是真有这些字形的。
    /// </summary>
    internal static bool CanRender(TMP_FontAsset? asset, string text)
    {
        if (asset is null || string.IsNullOrEmpty(text)) return false;
        try
        {
            foreach (var ch in text)
            {
                if (!asset.HasCharacter(ch)) return false;
                if (!HasDrawableGlyph(asset, ch)) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 字符在字符表里**且**图集矩形是有效尺寸 —— 也就是"画得出来"而不是"有条目"。
    ///
    /// 拿不到字形对象时**保守返回 true**（沿用旧的 `HasCharacter` 行为）：
    /// 判不出来时不该让流程改成"换字体"，那会引入新的风险；
    /// 只有明确读到"矩形为 0"才判否 —— 那种情况画出来必定是方框。
    /// </summary>
    private static bool HasDrawableGlyph(TMP_FontAsset asset, char value)
    {
        try
        {
            // ★ 空白与控制字符**本来就不绘制**，它们的 glyphRect 天然是 0x0。
            //   第 99 轮我漏了这一步，于是「库蕾娅的挣扎 v1.0.0」里的那个空格
            //   把**整句**判成"画不出来"，连我们自己那个明明有字形的字体也被否掉
            //   （追踪日志：`U+0020「 」… 矩形=0,0 0x0 判定=判否`，其余字符全是"可画"）。
            //   这条规则 `FontReplacementPolicy.RequiresGlyph` 里早就写明了 ——
            //   我等于把它重犯了一遍。这里直接复用同一个判据，避免两处规则再次走偏。
            if (!FontReplacementPolicy.RequiresGlyph(value)) return true;

            if (!asset.characterLookupTable.TryGetValue(value, out var character) || character is null) return true;
            var glyph = character.glyph;
            if (glyph is null) return true;
            var ok = glyph.glyphRect.width > 0 && glyph.glyphRect.height > 0;

            // ★ 一次性诊断（`UT_GLYPH_TRACE=1` 打开）：把真实取到的矩形打出来。
            //   为什么需要：第 99 轮我加了"矩形为 0 就判否"的检查，结果**连我们自己装配的
            //   字体也被判否**（日志：`我们能否渲染此句=否`）。到底是"矩形真的为 0"，
            //   还是"这个字段在我们这条装配路径上不可靠"，光看结论分不出来 ——
            //   必须把原始数字打出来才能决定这条判据该留还是该撤。
            if (GlyphTraceEnabled && _glyphTraceLogged < 30)
            {
                _glyphTraceLogged++;
                _glyphTrace?.Invoke("[字形矩形] U+" + ((int)value).ToString("X4") + "「" + value + "」"
                    + " 字体=" + asset.name
                    + " 表内=" + (character is not null)
                    + " 矩形=" + glyph.glyphRect.x + "," + glyph.glyphRect.y
                    + " " + glyph.glyphRect.width + "x" + glyph.glyphRect.height
                    + " atlasIndex=" + glyph.atlasIndex
                    + " 判定=" + (ok ? "可画" : "判否"));
            }
            return ok;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>字形矩形诊断开关（`UT_GLYPH_TRACE=1`），只用于排查，默认关闭。</summary>
    private static readonly bool GlyphTraceEnabled =
        Environment.GetEnvironmentVariable("UT_GLYPH_TRACE") == "1";
    private static int _glyphTraceLogged;
    private static Action<string>? _glyphTrace;

    /// <summary>由装配器把日志通道接进来，这样诊断能写进 BepInEx 日志。</summary>
    internal static void AttachGlyphTrace(Action<string> log) => _glyphTrace = log;

    internal static IReadOnlyList<char> MissingChars(TMP_FontAsset? asset, string text)
    {
        var missing = new List<char>();
        if (asset is null || string.IsNullOrEmpty(text)) return missing;
        try
        {
            foreach (var ch in text)
                if ((!asset.HasCharacter(ch) || !HasDrawableGlyph(asset, ch)) && !missing.Contains(ch))
                    missing.Add(ch);
        }
        catch (Exception) { }
        return missing;
    }

    /// <summary>
    /// 装配器。分帧推进（每帧 6ms / 最多 24 字），超过 2 秒改为一次性做完 ——
    /// face 是进程级全局状态，跨帧拖太久会失效。
    /// </summary>
    internal sealed class Builder
    {
        private const double StepBudgetMilliseconds = 12.0;

        /// <summary>
        /// 每帧最多收集多少字形。
        ///
        /// ★ 实测教训（Kurea）：原来是 24，配合每帧 6ms 预算，在**菜单静止、帧率很低**的游戏里
        ///   只能做到约 **7 字/秒** —— 130 秒才装到 960 字，而常用集有 4215 字
        ///   （约需 9.5 分钟）。装配没完成，界面就一直停在"覆盖层兜底"，
        ///   画面里是**带不透明黑底的方块**，正是用户明确不要的效果。
        ///   调大到 400 后，按同样的帧率只需十几帧就能收完。
        /// </summary>
        private const int MaxGlyphsPerStep = 400;

        /// <summary>
        /// 分帧装配的时间上限。超过就一次性做完。
        ///
        /// ★ 从 2.0 降到 0.5：face 是进程级全局状态，跨帧拖太久会失效
        ///   （实测装到两千多字就再也取不到字形）。既然最终都要一次做完，
        ///   不如早点转过去，避免在低帧率游戏里长时间停在"装配中"。
        /// </summary>
        private const double MaxTimeSlicedSeconds = 0.5;

        /// <summary>
        /// 批量打包的批大小。**不是可调的性能参数，是防崩溃参数**：
        /// 逐个字形调用 <c>TryAddGlyphsToTexture</c>，装到 1200~2500 次之间
        /// UnityPlayer 会确定性崩溃（0xc0000005，两次崩溃偏移完全相同 0x00000000fedfaf）。
        /// 每批 32 个把 4215 个字形压到约 132 次调用。
        /// </summary>
        private const int MaxGlyphsPerBatch = 32;

        private readonly Action<string> _log;
        private readonly Action<string>? _directLog;
        private readonly TMP_FontAsset? _shaderSource;
        private readonly int _padding;
        private readonly int _atlasSize;

        private readonly Queue<char> _pending = new Queue<char>();
        private readonly HashSet<char> _loaded = new HashSet<char>();
        private readonly List<Texture2D> _atlases = new List<Texture2D>();
        private readonly List<Glyph> _glyphs = new List<Glyph>();
        private readonly List<TMP_Character> _characters = new List<TMP_Character>();
        private readonly List<GlyphRect> _freeRects = new List<GlyphRect>();
        private readonly List<GlyphRect> _usedRects = new List<GlyphRect>();
        private readonly HashSet<uint> _glyphIndexes = new HashSet<uint>();
        private readonly Dictionary<uint, char> _codeByGlyphIndex = new Dictionary<uint, char>();
        private readonly Dictionary<char, Glyph> _glyphByCharacter = new Dictionary<char, Glyph>();

        /// <summary>本批待渲的字形，以及**跨批持续**的"字形 index → 字符"反查表。</summary>
        private readonly List<Glyph> _batch = new List<Glyph>();
        private readonly Dictionary<uint, char> _batchChars = new Dictionary<uint, char>();

        private FaceInfo _faceInfo;
        private int _atlasIndex = -1;
        private bool _faceLoaded;
        private bool _diagnoseCharacters = true;
        private int _packFailuresLogged;
        private int _batchLogs;
        private int _requeueCycles;
        private int _requeueLogged;
        private int _atlasFullLogged;
        private int _statNoGlyph;
        private int _statShared;
        private int _statPacked;
        private int _statPackFailed;
        private char _firstFailedChar;
        private uint _firstFailedGlyphIndex;
        private double _elapsedSeconds;
        private bool _missingWritten;

        public string FontFileName { get; private set; } = string.Empty;
        public int PointSize { get; private set; } = DefaultPointSize;
        public int AtlasCount { get; private set; }
        public int LoadedCharacters { get; private set; }
        public int RequestedCharacters { get; private set; }
        public string Stage { get; private set; } = "未开始";
        public string ShaderName { get; private set; } = string.Empty;
        public string? Failure { get; private set; }
        public TMP_FontAsset? Asset { get; private set; }
        public List<char> MissingCharacters { get; } = new List<char>();
        public bool IsReady => Asset is not null;
        public bool IsFinished => IsReady || Failure is not null;
        public IReadOnlyCollection<char> KnownCharacters => _loaded;

        /// <summary>
        /// 构造。<paramref name="directLog"/> 是**不受条数上限**的日志通道 ——
        /// 自检结论必须无条件写出去（实测为了"避免刷屏"的 60 条上限，把自检输出整段吞掉，
        /// 白跑了一轮实机测试）。
        /// </summary>
        public Builder(Action<string> log, TMP_FontAsset? shaderSource = null, Builder? carryOverFrom = null,
            Action<string>? directLog = null)
        {
            _log = log ?? (_ => { });
            _directLog = directLog ?? _log;
            _shaderSource = shaderSource;
            _padding = DefaultPadding;
            _atlasSize = DefaultAtlasSize;

            if (carryOverFrom is not null && carryOverFrom.IsReady)
            {
                foreach (var texture in EnumerateAtlasTextures(carryOverFrom.Asset!)) _atlases.Add(texture);
                _atlasIndex = _atlases.Count - 1;
                if (carryOverFrom._freeRects.Count > 0) _freeRects.AddRange(carryOverFrom._freeRects);
                else _freeRects.Add(new GlyphRect(0, 0, _atlasSize, _atlasSize));
                _usedRects.AddRange(carryOverFrom._usedRects);

                foreach (var glyph in carryOverFrom._glyphs) _glyphs.Add(glyph);
                foreach (var character in carryOverFrom._characters) _characters.Add(character);
                foreach (var index in carryOverFrom._glyphIndexes) _glyphIndexes.Add(index);
                foreach (var pair in carryOverFrom._codeByGlyphIndex) _codeByGlyphIndex[pair.Key] = pair.Value;
                foreach (var pair in carryOverFrom._glyphByCharacter) _glyphByCharacter[pair.Key] = pair.Value;
                foreach (var pair in carryOverFrom._batchChars) _batchChars[pair.Key] = pair.Value;
                foreach (var value in carryOverFrom._loaded) _loaded.Add(value);
                foreach (var value in carryOverFrom.MissingCharacters) MissingCharacters.Add(value);
                _faceInfo = carryOverFrom._faceInfo;
                _faceLoaded = true;
                PointSize = carryOverFrom.PointSize;
                FontFileName = carryOverFrom.FontFileName;
                AtlasCount = _atlases.Count;
                LoadedCharacters = _glyphs.Count;
                RequestedCharacters = _loaded.Count;
                Stage = "延续上一版：" + LoadedCharacters + " 个字形";
            }
        }

        private static IEnumerable<Texture2D> EnumerateAtlasTextures(TMP_FontAsset asset)
        {
            Texture2D[]? textures = null;
            try { textures = asset.atlasTextures; } catch (Exception) { }
            if (textures is not null)
                foreach (var texture in textures)
                    if (texture is not null) yield return texture;
        }

        public void Enqueue(IEnumerable<char> characters) => EnqueueNew(characters);

        /// <summary>加入还需装配的字符，返回**本次真正新增**的那些。</summary>
        public IReadOnlyList<char> EnqueueNew(IEnumerable<char> characters)
        {
            var added = new List<char>();
            if (characters is null) return added;

            foreach (var value in characters)
            {
                if (value == '\0' || _loaded.Contains(value)) continue;
                _pending.Enqueue(value);
                _loaded.Add(value);
                added.Add(value);
            }
            RequestedCharacters = _loaded.Count;
            return added;
        }

        /// <summary>
        /// 把"请求过、但既没装进字形表、也不在永久缺失名单里"的字符重新排队。
        ///
        /// 为什么必须有：打包失败时字符会从队列消失，此时 <c>_pending</c> 已空 →
        /// <c>Finalize()</c> → 资源被冻结，缺的字永远补不上，画面就是一片方框，
        /// 而日志却写着"装配完成 N 字"。
        /// </summary>
        private int RequeueUnpacked()
        {
            if (_requeueCycles >= 8) return 0;
            _requeueCycles++;

            var requeued = 0;
            foreach (var value in _loaded)
            {
                if (_glyphByCharacter.ContainsKey(value)) continue;      // 已经装上了
                if (MissingCharacters.Contains(value)) continue;          // 装不上/字体本身没有
                _pending.Enqueue(value);
                requeued++;
            }

            if (requeued > 0 && _requeueLogged++ < 4)
                _directLog?.Invoke("[probe.requeue] 第 " + _requeueCycles + " 轮补排 " + requeued
                    + " 个未装上的字符（已装 " + _glyphs.Count + " / 请求 " + _loaded.Count
                    + "，图集 " + AtlasCount + " 张，空闲矩形 " + _freeRects.Count + "）");
            return requeued;
        }

        /// <summary>本帧推进一步。返回 true 表示还没做完。</summary>
        public bool Step()
        {
            if (IsFinished) return false;
            var watch = Stopwatch.StartNew();
            try
            {
                if (!_faceLoaded) { if (!LoadFace()) return false; }

                // 队列空了**不等于**装完了：先补排，确认真排不出东西才收尾。
                // 不能每帧都调用（会把几千个字反复入队，白白拖慢装配）。
                if (_pending.Count == 0)
                {
                    FlushBatch();
                    if (RequeueUnpacked() > 0 && _requeueCycles <= 8) { /* 继续装 */ }
                    else { Finalize(); return false; }
                }

                // 超过分帧预算就一次性做完：face 是进程级全局状态，跨帧太久会失效。
                if (_elapsedSeconds > MaxTimeSlicedSeconds && _pending.Count > 0)
                {
                    _log("分帧装配已用 " + _elapsedSeconds.ToString("0.0") + " 秒，剩余 "
                        + _pending.Count + " 个字符改为一次性装完");
                    var before = _pending.Count;
                    var processedAll = 0;
                    while (_pending.Count > 0)
                    {
                        CollectGlyph(_pending.Dequeue());
                        processedAll++;
                        if (_batch.Count >= MaxGlyphsPerBatch) FlushBatch();
                    }
                    FlushBatch();
                    _log("一次性装配完成：" + processedAll + " 个字符（队列原有 " + before + " 个）");
                    Finalize();
                    return false;
                }

                var processed = 0;
                while (_pending.Count > 0
                       && processed < MaxGlyphsPerStep
                       && watch.Elapsed.TotalMilliseconds < StepBudgetMilliseconds)
                {
                    CollectGlyph(_pending.Dequeue());
                    processed++;
                    // ★ 攒够一批才调一次原生打包：逐个调用会在 1200~2500 次时让 Unity 原生崩溃
                    if (_batch.Count >= MaxGlyphsPerBatch) FlushBatch();
                }
                _elapsedSeconds += watch.Elapsed.TotalSeconds;
                Stage = "装配中：" + LoadedCharacters + " / " + RequestedCharacters;
                if (_diagnoseCharacters && processed > 0)
                {
                    _log("[字符分类] 本帧 +" + processed
                        + "；累计 取不到字形=" + _statNoGlyph
                        + " 共用字形=" + _statShared
                        + " 已装入=" + _statPacked
                        + " 装入失败=" + _statPackFailed
                        + "；图集=" + AtlasCount + " 空闲矩形=" + _freeRects.Count);
                    _diagnoseCharacters =
                        _statNoGlyph + _statShared + _statPacked + _statPackFailed < RequestedCharacters;
                }
            }
            catch (Exception ex)
            {
                Fail("装配过程异常：" + ex.GetType().Name + " " + ex.Message);
            }
            return !IsFinished;
        }

        /// <summary>一次做完（用于预算宽裕的场合）。</summary>
        public bool RunToCompletion(int maxSteps = 100000)
        {
            var steps = 0;
            while (!IsFinished && steps++ < maxSteps) Step();
            return IsReady;
        }

        private bool LoadFace()
        {
            // 按实测覆盖率排序的候选表取第一个存在的字体，**一次只载入这一个**。
            var path = FindUsableFontFile(_log);
            if (path is null) { Fail("找不到可用的中文字体文件"); return false; }
            _log("选定字体：" + Path.GetFileName(path));
            FontFileName = Path.GetFileName(path);

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { Fail("读字体文件失败：" + ex.GetType().Name + " " + ex.Message); return false; }
            if (bytes.Length == 0) { Fail("字体文件是空的"); return false; }

            var loaded = LoadFaceBytes(bytes, PointSize);
            if (loaded != "Success") { Fail("字体引擎加载 face 失败：" + loaded); return false; }

            var sized = InvokeStatic(typeof(FontEngine), "SetFaceSize", PointSize);
            if (sized != "Success") { Fail("SetFaceSize 失败：" + sized); return false; }

            try { _faceInfo = FontEngine.GetFaceInfo(); }
            catch (Exception ex) { Fail("GetFaceInfo 异常：" + ex.GetType().Name); return false; }

            // ★ 必须**按名字反射**读 pointSize / familyName，绝不能写 `_faceInfo.pointSize`。
            //   FaceInfo.pointSize 在 Unity 2021/2022 是 int、Unity 6000 起是 float，
            //   强类型访问会把访问器绑死在编译时的那个版本上，换版本运行就抛
            //   MissingMethodException: Method not found: single FaceInfo.get_pointSize()
            //   （Mad Island + Unity 2021 上 100% 复现，整条装配失败）。
            var pointSize = UnityMemberReader.Read<float>(_faceInfo, "pointSize");
            if (pointSize is null or <= 0f)
            {
                Fail("读不到 face 的 pointSize（该 Unity 版本的 FaceInfo 成员名可能变了）");
                return false;
            }

            var family = UnityMemberReader.ReadString(_faceInfo, "familyName") ?? "(未知)";
            _faceLoaded = true;
            Stage = "face 已载入：" + FontFileName + "（" + family + " " + PointSize + "pt）";
            _log("字体装配：载入 " + FontFileName + "（family=" + family
                + "，pointSize=" + pointSize.Value.ToString("0.##")
                + "，单张图集约可容纳 " + EstimateGlyphsPerAtlas(PointSize, _padding, _atlasSize) + " 个字形）");
            return true;
        }

        /// <summary>收集一个字符对应的字形（**不打包**），攒够一批由 <see cref="FlushBatch"/> 一次渲进图集。</summary>
        private void CollectGlyph(char value)
        {
            Glyph glyph;
            try
            {
                if (!FontEngine.TryGetGlyphWithUnicodeValue(value, GlyphLoadFlags.LOAD_DEFAULT, out glyph))
                {
                    _statNoGlyph++;
                    if (!MissingCharacters.Contains(value)) MissingCharacters.Add(value);
                    if (_diagnoseCharacters && _statNoGlyph <= 6)
                        _log("[字符分类] U+" + ((int)value).ToString("X4") + "「" + value + "」→ 字体引擎取不到字形");
                    return;
                }
            }
            catch (Exception ex)
            {
                _statNoGlyph++;
                if (!MissingCharacters.Contains(value)) MissingCharacters.Add(value);
                if (_diagnoseCharacters && _statNoGlyph <= 6)
                    _log("[字符分类] U+" + ((int)value).ToString("X4") + "「" + value + "」→ 取字形抛出 "
                        + ex.GetType().Name + "：" + ex.Message);
                return;
            }

            // 多个码位可能共用一个字形（全角/半角同形字）：不重复占图集位置。
            if (_glyphIndexes.Contains(glyph.index))
            {
                _statShared++;
                if (_glyphByCharacter.ContainsKey(value)) return;
                var existing = _glyphs.FirstOrDefault(g => g.index == glyph.index);
                if (existing is not null)
                {
                    _characters.Add(new TMP_Character(value, existing));
                    _glyphByCharacter[value] = existing;
                }
                return;
            }

            foreach (var pending in _batch)
                if (pending.index == glyph.index) return;

            _batchChars[glyph.index] = value;   // 提交时靠它把字符挂回"回填过的"字形
            _batch.Add(glyph);
        }

        /// <summary>
        /// 把已攒下的字形一次渲进图集，并把成功的那些写进字形表 / 字符表。
        ///
        /// ★★ 关键：先把本批要装的字形**连同它们的字符**抄成独立清单。
        ///   <c>FontEngine.TryAddGlyphsToTexture</c> 会把传进去的"待装列表"**清空**
        ///   （把装成功的挪到输出列表里）。直接拿同一个列表做第二次遍历，
        ///   第二遍看到的是空列表 —— 字形表 4214 项、**字符表只有 1 项**，
        ///   <c>HasCharacter</c> 全假，画面全是方框（实测 `glyphs=4214 chars=1`）。
        /// </summary>
        private void FlushBatch()
        {
            if (_batch.Count == 0) return;

            var pending = new List<KeyValuePair<char, Glyph>>(_batch.Count);
            foreach (var glyph in _batch)
            {
                var value = _batchChars.TryGetValue(glyph.index, out var code) ? code : '\0';
                if (value == '\0') continue;
                pending.Add(new KeyValuePair<char, Glyph>(value, glyph));
            }
            _batch.Clear();

            var toPack = new List<Glyph>(pending.Count);
            foreach (var pair in pending) toPack.Add(pair.Value);

            var rendered = PackBatch(toPack);
            var byIndex = new Dictionary<uint, Glyph>(rendered.Count);
            foreach (var glyph in rendered) byIndex[glyph.index] = glyph;

            _batchLogs++;
            if (_batchLogs <= 8)
                _directLog?.Invoke("[probe.batch] #" + _batchLogs + " attempted=" + pending.Count
                    + " rendered=" + rendered.Count
                    + " totalGlyphs=" + (_glyphs.Count + rendered.Count)
                    + " chars=" + _characters.Count
                    + " freeRects=" + _freeRects.Count + " usedRects=" + _usedRects.Count);

            foreach (var glyph in rendered)
            {
                _glyphIndexes.Add(glyph.index);
                _glyphs.Add(glyph);
                LoadedCharacters++;
            }

            foreach (var pair in pending)
            {
                if (byIndex.TryGetValue(pair.Value.index, out var stored))
                {
                    _statPacked++;
                    if (_diagnoseCharacters && _statPacked <= 3)
                        _log("[字符分类] U+" + ((int)pair.Key).ToString("X4") + "「" + pair.Key + "」→ 已装入字形 index="
                            + stored.index + " 度量=" + stored.metrics.width.ToString("0.##")
                            + "x" + stored.metrics.height.ToString("0.##")
                            + " 图集矩形=" + stored.glyphRect.x + "," + stored.glyphRect.y
                            + " " + stored.glyphRect.width + "x" + stored.glyphRect.height);
                    _characters.Add(new TMP_Character(pair.Key, stored));
                    _glyphByCharacter[pair.Key] = stored;
                    _codeByGlyphIndex[stored.index] = pair.Key;
                }
                else
                {
                    _statPackFailed++;
                    if (_statPackFailed == 1)
                    {
                        _firstFailedChar = pair.Key;
                        _firstFailedGlyphIndex = pair.Value.index;
                    }
                    if (!MissingCharacters.Contains(pair.Key)) MissingCharacters.Add(pair.Key);
                }
            }

            // ★ 绝对不要清空 _batchChars：它是**跨批**持续的"字形 index → 字符"反查表。
        }

        /// <summary>
        /// 一次把多个字形渲进图集，返回**已回填 glyphRect / atlasIndex** 的那些字形。
        ///
        /// ★ 必须批量（原生崩溃根因）：逐个字形调用 <c>TryAddGlyphsToTexture</c>，
        ///   装配到约 1200~2500 次时 UnityPlayer 确定性崩溃
        ///   （0xc0000005，两次崩溃偏移完全相同 0x00000000fedfaf）。
        /// ★ 必须一张图集装完：开第二张图集同样会让原生崩溃。
        /// </summary>
        private List<Glyph> PackBatch(List<Glyph> glyphs)
        {
            var rendered = new List<Glyph>();
            if (glyphs.Count == 0) return rendered;

            if (_atlasIndex < 0 || _freeRects.Count == 0)
            {
                if (_atlasIndex >= 0)
                {
                    if (_atlasFullLogged++ == 0)
                        _directLog?.Invoke("[probe.atlasFull] 第一张图集已满（已装 " + _glyphs.Count
                            + " 个字形），不再开第二张 —— 开第二张会让 Unity 原生崩溃。");
                    return rendered;
                }
                if (!OpenNextAtlas()) return rendered;
            }

            var attempted = glyphs.Count;
            var packed = TryAddGlyphs(glyphs, rendered, _padding, _freeRects, _usedRects, _atlases[_atlasIndex]);
            if (rendered.Count != attempted && _packFailuresLogged < 8)
            {
                _packFailuresLogged++;
                _log("[批量打包] 请求 " + attempted + " 个，实际装下 " + rendered.Count
                    + "（引擎返回=" + packed + "，本张已用=" + _usedRects.Count
                    + "，空闲=" + _freeRects.Count + "，图集=" + AtlasCount + "）");
            }

            // ★ 引擎会把图集位置写回它**输出列表**里的字形。用没回填过的对象写进字形表，
            //   glyphRect 就是 (0,0,0,0)，TMP 采样空矩形 —— 每个字都画成方框，
            //   而 HasCharacter 仍然返回 true，光看"能显示样例"根本发现不了。
            foreach (var glyph in rendered) glyph.atlasIndex = _atlasIndex;
            return rendered;
        }

        private bool OpenNextAtlas()
        {
            if (_atlases.Count >= MaxAtlasCount)
            {
                Fail("图集张数超过上限 " + MaxAtlasCount + "，停止装配（请检查点大小与图集尺寸参数）");
                return false;
            }
            var texture = new Texture2D(_atlasSize, _atlasSize, TextureFormat.Alpha8, false)
            {
                name = "UT-CJK-Atlas-" + _atlases.Count
            };
            InvokeStatic(typeof(FontEngine), "SetSharedTexture", texture);
            _atlases.Add(texture);
            _atlasIndex = _atlases.Count - 1;
            _freeRects.Clear();
            _freeRects.Add(new GlyphRect(0, 0, _atlasSize, _atlasSize));
            _usedRects.Clear();
            AtlasCount = _atlases.Count;
            _log("字体装配：打开图集 #" + _atlasIndex + "（" + _atlasSize + "²，已装 "
                + LoadedCharacters + " 个字形）");
            return true;
        }

        private void Finalize()
        {
            if (_glyphs.Count == 0) { Fail("一个字形都没装进去"); return; }
            try
            {
                var asset = ScriptableObject.CreateInstance<TMP_FontAsset>();
                asset.name = "UT-CJK-" + Path.GetFileNameWithoutExtension(FontFileName);

                SetField(asset, "m_AtlasTextures", _atlases.ToArray());
                SetField(asset, "m_AtlasTexture", _atlases[0]);
                SetField(asset, "m_AtlasTextureIndex", _atlasIndex);
                SetField(asset, "m_AtlasWidth", _atlasSize);
                SetField(asset, "m_AtlasHeight", _atlasSize);
                SetField(asset, "m_AtlasPadding", _padding);
                SetField(asset, "m_IsMultiAtlasTexturesEnabled", _atlases.Count > 1);

                // ★ 必须是 Static：Dynamic 模式下 TMP 每次补字都会去 LoadFontFace(源字体)，
                //   而那个源字体没有真实数据 —— 正是会让原生层崩溃的那条路。
                SetProperty(asset, "atlasPopulationMode", AtlasPopulationMode.Static);

                // ★ 渲染模式必须与**打包字形时用的那个值完全一致**，而且不能写成
                //   `GlyphRenderMode.SDFAA` —— 那是编译期绑定的枚举常量，跨 Unity 版本数值会变。
                var renderMode = ResolveGlyphRenderMode(typeof(GlyphRenderMode));
                if (renderMode is null) { Fail("拿不到 SDF 字形渲染模式，拒绝装配"); return; }
                SetField(asset, "m_AtlasRenderMode", renderMode);

                SetField(asset, "m_FaceInfo", _faceInfo);
                SetField(asset, "m_Version", "1.1.0");
                SetProperty(asset, "version", "1.1.0");
                SetField(asset, "m_FreeGlyphRects", _freeRects);
                SetField(asset, "m_UsedGlyphRects", _usedRects);
                SetField(asset, "m_GlyphTable", _glyphs);
                SetField(asset, "m_CharacterTable", _characters);

                ShaderName = AttachMaterial(asset, _atlases[0], _shaderSource);
                if (ShaderName.Length == 0) { Fail("拿不到可用的 TMP shader"); return; }

                // 让 TMP 自己把查找表建起来 —— ★必须在字形表与字符表都填好之后。
                asset.ReadFontAssetDefinition();

                foreach (var texture in _atlases)
                {
                    try { texture.Apply(false, false); }
                    catch (Exception ex) { _log("图集 Apply 失败：" + ex.GetType().Name); }
                }

                VerifyAtlasContents(asset);

                Asset = asset;
                Stage = "装配完成：" + LoadedCharacters + " 字 / " + _atlases.Count + " 张图集";
                _log("字体装配完成：" + asset.name + "（" + LoadedCharacters + " 个字符，"
                    + _atlases.Count + " 张图集，shader=" + ShaderName
                    + (MissingCharacters.Count == 0 ? "" : "，字体本身缺 " + MissingCharacters.Count + " 个字形") + "）");

                WriteMissingReport();
            }
            catch (Exception ex)
            {
                Fail("装配收尾异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>把缺字清单写到游戏目录内的 ut-dump（隔离要求见 <see cref="ResolveGameRootDirectory"/>）。</summary>
        private void WriteMissingReport()
        {
            if (_missingWritten) return;
            _missingWritten = true;
            if (MissingCharacters.Count == 0) return;

            try
            {
                var root = ResolveGameRootDirectory();
                if (root is null) return;
                var directory = Path.GetFullPath(Path.Combine(root, "ut-dump"));
                // 注意：不能写 TrimEnd(Path.DirectorySeparatorChar) —— 那个 TrimEnd(char)
                // 重载是 netstandard2.1 / .NET Core 才有的，老 Mono（Unity 2020.1.2f1）
                // 只有 TrimEnd(params char[])，会在运行时抛 MissingMethodException。
                var prefix = root.TrimEnd(new[] { Path.DirectorySeparatorChar }) + Path.DirectorySeparatorChar;
                if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                if (directory.IndexOf("AppData", StringComparison.OrdinalIgnoreCase) >= 0) return;

                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "missing-characters.txt");
                var lines = new List<string> { "总共缺 " + MissingCharacters.Count + " 个字形，清单如下。" };
                foreach (var value in MissingCharacters)
                    lines.Add("U+" + ((int)value).ToString("X4") + "\t" + value);
                File.WriteAllLines(path, lines);
                _directLog?.Invoke("[probe.missing] 缺字清单已写出：" + path);
            }
            catch (Exception ex)
            {
                _directLog?.Invoke("[probe.missing] 缺字清单写出失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 装配收尾自检：把**真正决定画面会不会出方框**的东西逐条摊开，
        /// 并与游戏自己那个能正常显示拉丁字母的字体做同进程对照。
        ///
        /// 光看"装入 4214 字"完全不能判定可用 —— 实测出现过"日志说装配完成、
        /// 游戏里满屏空心方框"，而 HasCharacter 依然返回 true。
        /// 诊断文本一律用**纯 ASCII**，避免日志编码问题让诊断本身变乱码。
        /// </summary>
        private void VerifyAtlasContents(TMP_FontAsset asset)
        {
            try
            {
                var texture = _atlases[0];
                var pixels = texture.GetPixels32();
                var nonTransparent = 0;
                var maxAlpha = 0;
                for (var i = 0; i < pixels.Length; i++)
                {
                    var a = pixels[i].a;
                    if (a > maxAlpha) maxAlpha = a;
                    if (a > 8) nonTransparent++;
                }
                var ratio = pixels.Length == 0 ? 0.0 : (double)nonTransparent / pixels.Length;
                _directLog?.Invoke("[probe.atlas] format=" + texture.format + " size=" + texture.width
                    + "x" + texture.height + " nonTransparent=" + ratio.ToString("P3")
                    + " maxAlpha=" + maxAlpha);

                long usedArea = 0;
                var usable = 0;
                foreach (var rect in _usedRects)
                {
                    if (rect.width <= 0 || rect.height <= 0) continue;
                    usable++;
                    usedArea += (long)rect.width * rect.height;
                }
                var average = usable == 0 ? 0.0 : (double)usedArea / usable;
                _directLog?.Invoke("[probe.capacity] usedRects=" + usable
                    + " avgGlyphArea=" + average.ToString("0") + "px"
                    + " atlas=" + texture.width + "x" + texture.height
                    + " estimatedOneAtlas=" + (average <= 0 ? 0 : (long)((double)texture.width * texture.height / average))
                    + " requested=" + _loaded.Count + " loaded=" + _glyphs.Count);

                var emptyRects = 0;
                foreach (var glyph in _glyphs)
                    if (glyph.glyphRect.width <= 0 || glyph.glyphRect.height <= 0) emptyRects++;
                _directLog?.Invoke("[probe.glyphs] count=" + _glyphs.Count + " emptyRects=" + emptyRects
                    + " (MUST be 0, otherwise every char draws as a box)");

                var probeCount = 0;
                var cjkProbeCount = 0;
                foreach (var glyph in _glyphs)
                {
                    if (glyph.metrics.width <= 0) continue;
                    if (!_codeByGlyphIndex.TryGetValue(glyph.index, out var code)) continue;

                    // 拉丁与汉字**各抽 3 个**：只抽前几个会恰好全是拉丁字形，
                    // 而"汉字比同行拉丁大"这类问题只能在汉字度量上看见（实测为此多跑一轮）。
                    var isCjk = code >= 0x2E80;
                    if (isCjk)
                    {
                        if (cjkProbeCount++ >= 3) continue;
                    }
                    else
                    {
                        if (probeCount++ >= 3) continue;
                    }

                    _directLog?.Invoke("[probe.glyph] U+" + ((int)code).ToString("X4")
                        + (isCjk ? " (CJK)" : " (latin)")
                        + " index=" + glyph.index
                        + " atlasIndex=" + glyph.atlasIndex
                        + " rect=" + glyph.glyphRect.x + "," + glyph.glyphRect.y
                        + " " + glyph.glyphRect.width + "x" + glyph.glyphRect.height
                        + " metrics=" + glyph.metrics.width.ToString("0.##") + "x" + glyph.metrics.height.ToString("0.##")
                        + " advance=" + glyph.metrics.horizontalAdvance.ToString("0.##")
                        + " bearing=" + glyph.metrics.horizontalBearingX.ToString("0.##") + ","
                        + glyph.metrics.horizontalBearingY.ToString("0.##")
                        + " scale=" + glyph.scale.ToString("0.##"));
                }
                if (probeCount == 0)
                    _directLog?.Invoke("[probe.glyphs] no usable latin glyph at all -- assembly effectively failed");
                if (cjkProbeCount == 0)
                    _directLog?.Invoke("[probe.glyphs] no usable CJK glyph at all -- 汉字一个都没装上");

                DumpMaterial("ours", asset);
                if (_shaderSource is not null && !ReferenceEquals(_shaderSource, asset))
                    DumpMaterial("theirs", _shaderSource);

                var rereadGlyphs = UnityMemberReader.ReadRaw(asset, "m_GlyphTable") as System.Collections.ICollection;
                var rereadChars = UnityMemberReader.ReadRaw(asset, "m_CharacterTable") as System.Collections.ICollection;
                _directLog?.Invoke("[probe.tables] reread glyphs=" + (rereadGlyphs?.Count.ToString() ?? "?")
                    + " chars=" + (rereadChars?.Count.ToString() ?? "?") + " loaded=" + LoadedCharacters);

                _directLog?.Invoke("[probe.face] pointSize="
                    + UnityMemberReader.Read<float>(_faceInfo, "pointSize")?.ToString("0.##")
                    + " ascentLine=" + UnityMemberReader.Read<float>(_faceInfo, "ascentLine")?.ToString("0.##")
                    + " lineHeight=" + UnityMemberReader.Read<float>(_faceInfo, "lineHeight")?.ToString("0.##"));
                var theirFace = UnityMemberReader.ReadRaw(_shaderSource, "m_FaceInfo");
                if (theirFace is not null)
                    _directLog?.Invoke("[probe.face.theirs] pointSize="
                        + UnityMemberReader.Read<float>(theirFace, "pointSize")?.ToString("0.##")
                        + " ascentLine=" + UnityMemberReader.Read<float>(theirFace, "ascentLine")?.ToString("0.##")
                        + " lineHeight=" + UnityMemberReader.Read<float>(theirFace, "lineHeight")?.ToString("0.##"));

                var probe = "继续游戏Aa1";
                var missing = 0;
                foreach (var ch in probe)
                    if (!asset.HasCharacter(ch)) missing++;
                _directLog?.Invoke("[probe.hasCharacter] probeLength=" + probe.Length
                    + " missing=" + missing + " (MUST be 0)");

                var shortfall = _loaded.Count - _glyphs.Count;
                _directLog?.Invoke("[probe.fit] pointSize=" + PointSize
                    + " packed=" + _glyphs.Count + " requested=" + _loaded.Count
                    + " shortfall=" + shortfall + " atlases=" + _atlases.Count
                    + " capacityEstimate=" + FontAssetPipeline.EstimatedAtlasCapacity(PointSize)
                    + (shortfall <= 0 ? " RESULT=OK" : " RESULT=SHORT -- missing glyphs draw as boxes"));
            }
            catch (Exception ex)
            {
                _directLog?.Invoke("[probe] self-check itself failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private void DumpMaterial(string label, TMP_FontAsset? font)
        {
            try
            {
                if (font is null) { _directLog?.Invoke("[probe.material." + label + "] none"); return; }
                var material = UnityMemberReader.ReadRaw(font, "material") as Material;
                if (material is null) { _directLog?.Invoke("[probe.material." + label + "] no material"); return; }

                var texture = material.mainTexture;
                var scale = material.HasProperty(ShaderUtilities.ID_GradientScale)
                    ? material.GetFloat(ShaderUtilities.ID_GradientScale) : -1f;
                var dilate = material.HasProperty(ShaderUtilities.ID_FaceDilate)
                    ? material.GetFloat(ShaderUtilities.ID_FaceDilate) : -1f;
                var outline = material.HasProperty(ShaderUtilities.ID_OutlineWidth)
                    ? material.GetFloat(ShaderUtilities.ID_OutlineWidth) : -1f;
                var missing = string.Join(",", FontAssetPipeline.MissingMaterialProperties(material.HasProperty));

                _directLog?.Invoke("[probe.material." + label + "] shader=" + material.shader.name
                    + " mainTex=" + (texture?.name ?? "none")
                    + " texSize=" + (texture is null ? "-" : texture.width + "x" + texture.height)
                    + " _GradientScale=" + scale.ToString("0.##")
                    + " _FaceDilate=" + dilate.ToString("0.###")
                    + " _OutlineWidth=" + outline.ToString("0.###")
                    + " missingProps=[" + missing + "]"
                    + " keywords=[" + string.Join(",", material.shaderKeywords) + "]"
                    // ★ 渲染状态必须一起对照：若我们的材质在 depth/队列上与游戏原材质不同，
                    //   文字会**穿透**其他 UI 显示出来（画面表现就是"别处的字叠到这一处"）。
                    //   实测过"菜单里出现不属于该行的文字"，所以这两项必须摊开看。
                    + " renderQueue=" + material.renderQueue
                    + " ZWrite=" + (material.HasProperty("_ZWrite") ? material.GetFloat("_ZWrite").ToString("0") : "-")
                    + " ZTest=" + (material.HasProperty("_ZTest") ? material.GetFloat("_ZTest").ToString("0") : "-")
                    + " Cull=" + (material.HasProperty("_CullMode") ? material.GetFloat("_CullMode").ToString("0") : "-"));
            }
            catch (Exception ex)
            {
                _directLog?.Invoke("[probe.material." + label + "] read failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 游戏安装目录（<c>&lt;游戏根&gt;/&lt;游戏名&gt;_Data</c> 的上一级）。
        ///
        /// ★ 隔离要求：诊断产物**只允许**写进游戏安装目录内（实机验证时即副本目录）。
        ///   绝不能写到 <c>%USERPROFILE%\AppData\LocalLow\...</c> —— 那是玩家共享数据区
        ///   （存档、设置、游戏自己的 Player.log）。早期版本用 <c>Application.persistentDataPath</c>
        ///   落到 LocalLow，已作为安全违规记录在案。
        /// </summary>
        private static string? ResolveGameRootDirectory()
        {
            try
            {
                var dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath)) return null;
                var root = Path.GetFullPath(Path.Combine(dataPath, ".."));
                if (root.IndexOf("AppData", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                return root;
            }
            catch (Exception) { return null; }
        }

        private void Fail(string reason)
        {
            Failure = reason;
            Stage = "装配失败：" + reason;
            _log("字体装配失败：" + reason + "（将退回游戏内覆盖显示）");
        }
    }

    private static string AttachMaterial(TMP_FontAsset asset, Texture2D atlas, TMP_FontAsset? shaderSource)
    {
        try
        {
            // 优先**克隆游戏正在用的字体材质**：TMP 的 SDF 着色器依赖 _ScaleRatioA/B/C、
            // _TextureWidth/Height、_FaceColor 等一批属性，自己 new 一个只会设 _MainTex，
            // 其余全是默认值，渲染结果就是空心方框。
            TMP_FontAsset? template = shaderSource;
            if (UnityMemberReader.ReadRaw(template, "material") is null)
            {
                foreach (var candidate in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                {
                    if (candidate is null || UnityMemberReader.ReadRaw(candidate, "material") is null) continue;
                    template = candidate;
                    break;
                }
            }

            Material material;
            var templateMaterial = UnityMemberReader.ReadRaw(template, "material") as Material;
            if (templateMaterial is not null)
            {
                material = new Material(templateMaterial) { name = asset.name + " Material" };
            }
            else
            {
                var shader = Shader.Find("TextMeshPro/Distance Field")
                             ?? Shader.Find("TextMeshPro/Mobile/Distance Field");
                if (shader is null) return string.Empty;
                material = new Material(shader) { name = asset.name + " Material" };
            }

            material.SetTexture(ShaderUtilities.ID_MainTex, atlas);
            if (material.HasProperty(ShaderUtilities.ID_TextureWidth))
                material.SetFloat(ShaderUtilities.ID_TextureWidth, atlas.width);
            if (material.HasProperty(ShaderUtilities.ID_TextureHeight))
                material.SetFloat(ShaderUtilities.ID_TextureHeight, atlas.height);

            // ★ _GradientScale 必须与真实 padding 配套：TMP 的公式是 padding + 2.5。
            //   这里是**无条件覆盖**克隆来的值：游戏原有字体按它自己的 padding 设过
            //   （实测 6，对应 padding≈3.5），直接沿用会让我们的图集按错误梯度宽度解码。
            material.SetFloat(ShaderUtilities.ID_GradientScale, FontAssetPipeline.GradientScale);

            // 字形膨胀量按 TMP 默认 0 归一：克隆来的值是按游戏字体调的。
            if (material.HasProperty(ShaderUtilities.ID_FaceDilate))
                material.SetFloat(ShaderUtilities.ID_FaceDilate, 0f);

            // 关掉克隆来的下划线阴影：它按游戏原字体调过，在我们的字号下会在每个字周围
            // 糊出一圈深色轮廓，看起来就像方框。
            if (material.HasProperty(ShaderUtilities.ID_UnderlayOffsetX))
                material.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0f);
            if (material.HasProperty(ShaderUtilities.ID_UnderlayOffsetY))
                material.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, 0f);
            if (material.HasProperty(ShaderUtilities.ID_UnderlayDilate))
                material.SetFloat(ShaderUtilities.ID_UnderlayDilate, 0f);
            material.DisableKeyword(ShaderUtilities.Keyword_Underlay);

            // ★ 缺任何一个 SDF 必需属性都不能当成功：那正是"方框"的成因。
            var missing = FontAssetPipeline.MissingMaterialProperties(material.HasProperty);
            if (missing.Count > 0)
                return string.Empty;

            // ★★ 必须用**反射**设置 material，不能写 `asset.material = material`。
            //   实测（Mad Island / Unity 2021）：强类型访问会绑定到编译时那个 Unity 版本的
            //   访问器上 —— Unity 6 的 TMP 把 `material` 声明在 TMP_Asset 上，而 2021 声明在
            //   TMP_FontAsset 上，于是运行时抛
            //     MissingMethodException: Method not found: TMPro.TMP_Asset.get_material()
            //   整个字体装配失败（画面上看到的仍是游戏原文，极易被误判成"替换成功"）。
            //   反射按名字在整条继承链上找，两个版本都能命中。
            if (!SetProperty(asset, "material", material) && !SetField(asset, "material", material))
                return string.Empty;
            var source = templateMaterial is null ? "新建" : "克隆自 " + template.name;
            return material.shader.name + "（" + source + "，_GradientScale="
                + FontAssetPipeline.GradientScale.ToString("0.#") + "，padding=" + DefaultPadding + "）";
        }
        catch (Exception ex)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 按字节加载字体 face，**兼容不同 Unity 版本的 LoadFontFace 重载差异**。
    ///
    /// 实测：Unity 2022.3.62f2 有 <c>(byte[],int,int)</c>；
    /// Unity 6000.0.68f1 上**没有 int 版**，只有 <c>(byte[],Single,Int32)</c>。
    /// 按"参数个数"挑会挑到 <c>(string,int,int)</c> —— 调用抛异常、字体没载入，
    /// 但 SetFaceSize/GetFaceInfo 仍然成功，表现就是"装配日志正常、游戏里显示方框"。
    /// </summary>
    private static string LoadFaceBytes(byte[] bytes, int pointSize)
    {
        var methods = typeof(FontEngine)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "LoadFontFace")
            .Select(m => new
            {
                Method = m,
                Signature = new LoadFaceOverloadSelector.Signature(m.Name,
                    m.GetParameters().Select(p => p.ParameterType.Name).ToArray())
            })
            .ToList();

        var selected = LoadFaceOverloadSelector.Select(methods.Select(x => x.Signature));
        if (selected is null) return "本版本没有接受 byte[] 的 LoadFontFace（候选：" +
            string.Join(" | ", methods.Select(x => x.Signature)) + "）";

        var match = methods.FirstOrDefault(x => ReferenceEquals(x.Signature, selected));
        if (match is null) return "重载选择与反射结果不一致（内部错误）";

        var arguments = LoadFaceOverloadSelector.BuildArguments(selected, bytes, pointSize, 0);
        if (arguments is null) return "无法为选中的重载构造参数：" + selected;

        try
        {
            var result = match.Method.Invoke(null, arguments);
            var text = result?.ToString() ?? "成功（无返回值）";
            if (text != "Success")
                Log2("LoadFontFace 返回 " + text + "（签名 " + selected + "）");
            return text;
        }
        catch (Exception ex)
        {
            return "抛出 " + (ex.InnerException?.GetType().Name ?? ex.GetType().Name)
                + ": " + (ex.InnerException?.Message ?? ex.Message)
                + "（签名 " + selected + "）";
        }
    }

    /// <summary>诊断用日志回调（LoadFaceBytes 是静态方法，用一个静态字段转发）。</summary>
    private static Action<string>? _log;

    private static void Log2(string message) => _log?.Invoke(message);

    /// <summary>
    /// 调 <c>FontEngine.TryAddGlyphsToTexture</c>（internal，必须反射）。
    /// 签名：<c>(List&lt;Glyph&gt;, List&lt;Glyph&gt;, int padding, GlyphPackingMode,
    /// List&lt;GlyphRect&gt; free, List&lt;GlyphRect&gt; used, GlyphRenderMode, Texture2D)</c>。
    ///
    /// ★ <c>GlyphRenderMode</c> 必须按**名字**取 SDFAA：早期实现写的是
    ///   <c>Enum.GetValues(...).GetValue(0)</c>（取第 0 个），而这个枚举的成员顺序
    ///   随 Unity 版本变化，取第 0 个拿到的是**位图**模式 —— 字形以位图形式画进图集，
    ///   再挂到 TMP 的 SDF 着色器上，画面就是"中文空心方框、拉丁字母也变框"。
    /// </summary>
    private static bool TryAddGlyphs(List<Glyph> glyphsToAdd, List<Glyph> glyphsRendered, int padding,
        List<GlyphRect> freeRects, List<GlyphRect> usedRects, Texture2D texture)
    {
        var method = typeof(FontEngine)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "TryAddGlyphsToTexture" && m.GetParameters().Length == 8);
        if (method is null) return false;
        try
        {
            var parameters = method.GetParameters();

            var packingMode = ResolveEnumByName(parameters[3].ParameterType, "Fast")
                ?? Enum.GetValues(parameters[3].ParameterType).GetValue(0);

            var renderMode = ResolveGlyphRenderMode(parameters[6].ParameterType);
            if (renderMode is null)
            {
                Log2("拿不到 SDF 字形渲染模式（GlyphRenderMode 里没有 SDFAA 或任何 SDF+AA 成员），"
                    + "拒绝用位图模式装配 —— 那会让画面出现方框");
                return false;
            }

            var arguments = new object?[]
            {
                glyphsToAdd, glyphsRendered, padding,
                packingMode,
                freeRects, usedRects,
                renderMode,
                texture
            };
            return (bool)(method.Invoke(null, arguments) ?? false);
        }
        catch (Exception) { return false; }
    }

    /// <summary>解析要传给 <c>TryAddGlyphsToTexture</c> 的 <c>GlyphRenderMode</c>（先按名字，再按数值兜底）。</summary>
    private static object? ResolveGlyphRenderMode(Type enumType)
    {
        var values = Enum.GetValues(enumType);
        var names = new List<string>(values.Length);
        foreach (var value in values) names.Add(value.ToString() ?? string.Empty);

        var chosen = FontAssetPipeline.SelectGlyphRenderMode(names);
        if (chosen is null) return null;

        foreach (var value in values)
            if (string.Equals(value.ToString(), chosen, StringComparison.Ordinal)) return value;

        var fallback = FontAssetPipeline.SdfaaFallbackValue;
        foreach (var value in values)
            if (Convert.ToInt32(value) == fallback) return value;

        return null;
    }

    /// <summary>按名字取枚举成员；取不到返回 null（同样是为了避开版本漂移的序号）。</summary>
    private static object? ResolveEnumByName(Type enumType, string name)
    {
        foreach (var value in Enum.GetValues(enumType))
            if (string.Equals(value.ToString(), name, StringComparison.Ordinal)) return value;
        return null;
    }

    /// <summary>
    /// 按**参数个数**挑一个重载并调用，返回结果字符串（失败返回原因）。
    ///
    /// 为什么需要：FontEngine.LoadFontFace 的重载在各 Unity 版本里不同 ——
    /// 实测 Unity 2020 没有 (string,int,int)，直接写会**编译失败**（CS1501），
    /// 让整个 Unity 2020 变体编不出来。反射调用让一份源码适配所有版本。
    /// 仅用于诊断代码；真正加载字体走 LoadFaceBytes + LoadFaceOverloadSelector。
    /// </summary>
    internal static string InvokeOverloadByArity(Type type, string name, object?[] args)
    {
        try
        {
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length);
            if (method is null) return "无 " + args.Length + " 参数重载";
            return method.Invoke(null, args)?.ToString() ?? "成功（无返回值）";
        }
        catch (Exception ex)
        {
            return "抛出 " + (ex.InnerException?.GetType().Name ?? ex.GetType().Name)
                + ": " + (ex.InnerException?.Message ?? ex.Message);
        }
    }

    internal static string InvokeStatic(Type type, string name, params object?[] args)
    {
        try
        {
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length);
            if (method is null) return "方法不存在";
            return method.Invoke(null, args)?.ToString() ?? "成功（无返回值）";
        }
        catch (Exception ex)
        {
            return "抛出 " + (ex.InnerException?.GetType().Name ?? ex.GetType().Name)
                + ": " + (ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// 反射写字段。**不要过滤 DeclaredOnly** —— TMP 3.2.0 起把 m_Version / m_FaceInfo
    /// 搬到了父类 TMP_Asset 上，只搜本类会找不到。
    /// </summary>
    internal static bool SetField(object target, string name, object? value)
    {
        try
        {
            var field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is null) return false;
            // 类型可能随版本变化（例如 FaceInfo.pointSize 在 Unity 6000 从 int 变 float），
            // 所以能转就转，不能转就原样塞。
            if (value is not null && !field.FieldType.IsInstanceOfType(value))
            {
                try { value = Convert.ChangeType(value, field.FieldType); }
                catch (Exception) { }
            }
            field.SetValue(target, value);
            return true;
        }
        catch (Exception) { return false; }
    }

    internal static bool SetProperty(object target, string name, object? value)
    {
        try
        {
            var property = target.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = property?.GetSetMethod(true);
            if (setter is null || property is null) return false;
            if (value is not null && !property.PropertyType.IsInstanceOfType(value))
            {
                try { value = Convert.ChangeType(value, property.PropertyType); }
                catch (Exception) { }
            }
            setter.Invoke(target, new[] { value });
            return true;
        }
        catch (Exception) { return false; }
    }
}
