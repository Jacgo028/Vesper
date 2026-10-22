using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityTranslator.Core;

namespace UnityTranslator.Plugin;

/// <summary>
/// 中文字体回退加载器：按优先级逐个尝试候选字体，返回**第一个真的能渲染这段文字**的字体资源。
///
/// 为什么要做成多候选（V0.1 实测的坑）：
/// 日志里反复出现 `Cannot load Chinese font: System.InvalidOperationException:
/// Unable to load Windows Chinese font file`，也就是**唯一那个候选**从头到尾没成功过，
/// 而 V0.1 在这一条路失败之后**没有第二方案**。于是译文仍然被写进组件，
/// 游戏只能用原字体渲染中文，原字体没有的字形就显示成方框（□）。
///
/// 反射查过这个游戏的 Unity.TextMeshPro.dll（TMP 3.0.6 接口面）：
/// **只有** `CreateFontAsset(Font)` 与 `CreateFontAsset(Font, …, AtlasPopulationMode, bool)`
/// 两个重载，**没有**任何接受字体文件路径的 `CreateFontAsset(string, …)`。
/// 所以"直接按路径创建字体资源"这条路在这个 TMP 版本上不存在，
/// 只能先把字体文件变成 UnityEngine.Font（`Font.CreateDynamicFontFromOSFont`），
/// 再交给 TMP。这也解释了为什么必须做多候选：这条路上失败的可能性很多
/// （字体名对不上、字体文件读不出来、字形不全），必须逐个试、逐个验。
///
/// 加载流程：
/// 1. 按候选名单逐个 `Font.CreateDynamicFontFromOSFont(名字, 16)`，每个单独 try/catch；
/// 2. 拿到 Font 后交给 TMP 创建字体资源，并逐字符校验字形（<see cref="CanRender"/>）；
/// 3. 缺字形时给它一次 `TryAddCharacters` 补字形的机会，补完再校验；
/// 4. 任何一个候选抛异常都只记录原因并继续下一个，绝不让异常冒泡打断流程；
/// 5. 全部失败才放弃替换 —— 调用方据此**保留原文**，不写入渲染不出来的译文。
///
/// 每一步的成功与失败都会写进 BepInEx 日志，且带原始异常类型与消息，
/// 方便下次直接从日志看出试过哪些候选、各自为什么失败、缺哪些字符。
/// </summary>
internal sealed class FontFallbackLoader
{
    /// <summary>
    /// 候选字体名，按尝试顺序。
    ///
    /// 前两个对应真正的 `.ttf` 单字体文件（simhei.ttf / Deng.ttf），
    /// 后面的可能对应 `.ttc` 字体集合（msyh.ttc / simsun.ttc）——
    /// 实测里 msyh.ttc 加载失败，`.ttf` 的单字体文件成功率更高，所以排在前面。
    /// 多放几个是因为不同 Windows 上装的字体不一样，而且每个候选都可能缺字形，
    /// 必须逐个试到"能完整渲染这段译文"的那个。
    /// </summary>
    private static readonly string[] FontNames =
    {
        "SimHei",
        "DengXian",
        "Microsoft YaHei",
        "SimSun",
        "Microsoft JhengHei",
        "KaiTi",
        "FangSong",
        "NSimSun",
        "MingLiU",
        "Microsoft YaHei UI"
    };

    /// <summary>候选字体文件，用来做"这个文件到底能不能被字体引擎读出来"的诊断。</summary>
    private static readonly string[] FontFiles =
    {
        "simhei.ttf",
        "Deng.ttf",
        "Dengb.ttf",
        "simkai.ttf",
        "simfang.ttf",
        "simsunb.ttf",
        "msyh.ttc",
        "msyhl.ttc",
        "msjh.ttc",
        "simsun.ttc"
    };

    private readonly Action<string> _info;
    private readonly Action<string> _warn;

    private TMP_FontAsset? _font;
    private bool _fontFailed;
    private string _fontSummary = "尚未尝试";
    private bool _probeLogged;

    public FontFallbackLoader(Action<string> info, Action<string> warn)
    {
        _info = info;
        _warn = warn;
    }

    /// <summary>当前缓存的中文回退字体；为 null 表示还没成功过。</summary>
    public TMP_FontAsset? Font => _font;

    /// <summary>最近一次加载结果的简述，写进日志用。</summary>
    public string Summary => _fontSummary;

    /// <summary>
    /// 检查某个字体资源能不能**完整渲染** <paramref name="text"/>。
    /// 判定顺序：资源存在 → 图集可用 → 字符表非空 → 逐字符真的能取到字形。
    /// </summary>
    public static FontCheck CanRender(TMP_FontAsset? asset, string text)
    {
        if (asset is null) return new FontCheck(false, 0, Array.Empty<char>(), "字体资源为空");
        if (!AtlasReady(asset)) return new FontCheck(false, 0, Array.Empty<char>(), "图集不可用");
        var characters = asset.characterTable;
        var count = characters?.Count ?? 0;
        if (count <= 0) return new FontCheck(false, 0, Array.Empty<char>(), "字符表为空");

        var missing = new List<char>();
        foreach (var value in FontReplacementPolicy.RequiredGlyphs(text))
            if (!HasRenderableGlyph(asset, characters!, value)) missing.Add(value);

        return missing.Count == 0
            ? new FontCheck(true, count, Array.Empty<char>(), "字形齐全")
            : new FontCheck(false, count, missing, "缺少 " + missing.Count + " 个字形");
    }

    /// <summary>
    /// 逐字符确认"字符表里有、而且能取到字形"。
    ///
    /// 单独用 <c>HasCharacters</c> 不够：TMP 的 HasCharacters 只看字符表里有没有这个条目，
    /// 字符表有条目、图集里却没有可用字形时它照样返回 true —— 这正是
    /// "字体创建成功但游戏里显示方框"的来源。所以这里再查一次字形表。
    /// </summary>
    private static bool HasRenderableGlyph(TMP_FontAsset asset, List<TMP_Character> characters, char value)
    {
        var index = characters.FindIndex(character => character.unicode == value);
        if (index < 0) return false;
        var glyphIndex = characters[index].glyphIndex;
        if (glyphIndex == 0) return false;
        var glyphs = asset.glyphTable;
        if (glyphs is null) return true;
        return glyphs.FindIndex(glyph => glyph.index == glyphIndex) >= 0;
    }

    private static bool AtlasReady(TMP_FontAsset asset)
    {
        try
        {
            var atlas = asset.atlasTexture;
            return atlas != null && atlas.width > 0 && atlas.height > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 取一个能完整渲染 <paramref name="text"/> 的中文回退字体；全都失败时返回 null。
    /// </summary>
    public TMP_FontAsset? Acquire(string text)
    {
        // 诊断是"顺便做的事"，**绝不能**决定翻译能不能用：
        // 万一它在某个 Unity 版本上抛异常（实测 Mad Island / Unity 2021 就抛过），
        // 这里必须把异常吃掉继续找字体，而不是让整条翻译链路跟着一起失效。
        try { LogProbeOnce(); }
        catch (Exception ex) { _warn("字体诊断失败（不影响后续查找字体）：" + ex.GetType().Name + " " + ex.Message); }

        // 已经成功加载过：先看它能不能渲染这次的新文字，不能才继续找别的。
        if (_font is not null)
        {
            var known = CanRender(_font, text);
            if (known.CanRender) return _font;
            if (TryGrow(_font, text, out var grown))
                return _font = grown;
            _warn("已加载的中文回退字体缺少字形，继续尝试其他候选：" + known.Reason
                + "（缺失 " + FontReplacementPolicy.DescribeMissingGlyphs(known.Missing) + "）");
            _font = null;
        }

        // 已经确认整条候选链都失败过：不再每个字符都重试一遍，避免刷日志。
        if (_fontFailed) return null;

        var failures = new List<string>();
        foreach (var name in FontNames)
        {
            var font = CreateFromOsFont(name, failures);
            if (font is null) continue;
            var asset = CreateFontAsset(font, name, failures);
            if (asset is null) continue;
            if (Accept(asset, "系统字体名 " + name, text, failures)) return _font;
        }

        // ★★ 上面整条路都失败之后，再走**自己装配**那条路（第 100 轮新增）。
        //
        //   为什么必须加这一步（cambion 实机，Player.log 原文）：
        //     `Font.CreateDynamicFontFromOSFont` 在本机对 10 个系统字体名**全部返回非空**，
        //     但那个 Font 对象**背后没有真实 face** —— 游戏 Player.log 写着
        //     `Unable to load font face for [NSimSun]. Make sure "Include Font Data" is
        //      enabled in the Font Import Settings.`
        //     于是 `TMP_FontAsset.CreateFontAsset(Font)` 对每一个候选都返回空。
        //     而 `FontEngine.LoadFontFace(字体文件路径, …)` 对同一批字体文件**全是 Success**。
        //
        //   所以"造不出中文字体"不是没有字体，而是**入口选错了**。
        //   `RuntimeCjkFontAsset` 正是为这件事写的（注释里写明"实机验证过的唯一可行路径"），
        //   但过去只被"收到译文之后"的补字路径调用 —— 而这里根本没有译文回来，形成死锁：
        //     没有字体 → 保留原文 → 没有译文 → 永远不会去装配字体。
        //   这一步就是打破这个死锁。
        var assembled = AcquireByAssembling(font: text, failures);
        if (assembled is not null) return _font = assembled;

        _fontFailed = true;
        _fontSummary = "全部候选都不可用";
        _warn("中文回退字体加载失败，所有候选都试过了，译文将保留原文。试过的候选：" +
            (failures.Count == 0 ? "没有任何候选可用" : string.Join("；", failures)));
        return null;
    }

    /// <summary>字体装配用的最小打底字符集（ASCII 可见字符 + 常用中文标点）。</summary>
    private static readonly char[] SeedCharacters = BuildSeedCharacters();

    private static char[] BuildSeedCharacters()
    {
        var list = new List<char>();
        for (var c = (char)0x20; c <= (char)0x7E; c++) list.Add(c);
        list.AddRange("，。、；：？！（）《》「」『』…—·　");
        return list.ToArray();
    }

    /// <summary>
    /// 用系统字体**文件**自己装配一个中文字体资源（绕开 TMP 工厂）。
    ///
    /// 这是 `RuntimeCjkFontAsset` 的同步入口：它用 `FontEngine.LoadFontFace` 载入字体文件、
    /// 自己建图集、自己造 `TMP_FontAsset`。cambion 上这是唯一能成功的路。
    ///
    /// 代价与风险（必须如实写在代码里）：
    ///   * 装配是**主线程同步**完成的，几千个字形要几秒到十几秒 —— 只在"这条兜底路
    ///     第一次被用到"时付一次，之后 `_font` 已缓存；
    ///   * `RuntimeCjkFontAsset` 的注释列了几条会让 Unity 原生层崩溃的禁忌
    ///     （逐字形加、开第二张图集、多线程调 FontEngine）—— 这里只是**调用它**，
    ///     禁忌由那个类自己守；但正因为它可能崩，调用点必须包在 try/catch 里，
    ///     并且**只装这一段文字需要的字**，不预装全量。
    /// </summary>
    private TMP_FontAsset? AcquireByAssembling(string font, List<string> failures)
    {
        try
        {
            var started = DateTime.UtcNow;
            // shaderSource / carryOverFrom 都传 null：
            //   * 这个加载器拿不到"画面字体"的引用（调用方没传进来），
            //     Builder 会在 shaderSource 为 null 时自己用 TMP 默认的 SDF shader；
            //   * 不做延续版 —— 这是首次装配，没有上一版可续。
            var builder = new RuntimeCjkFontAsset.Builder(
                message => _info("字体装配：" + message), null, null, _info);

            var wanted = new List<char>();
            var seen = new HashSet<char>();
            foreach (var value in font)
                if (value != '\0' && seen.Add(value)) wanted.Add(value);
            // 再补一份"最小打底集"：ASCII + 常用中文标点。
            // 只补这么多是刻意的 —— 装得越多越慢，而且真正要用的字由
            // QueueCjkCharacters 在收到译文后按需补装（它本来就是干这个的）。
            foreach (var value in SeedCharacters)
                if (seen.Add(value)) wanted.Add(value);

            builder.EnqueueNew(wanted);
            var finished = builder.RunToCompletion();

            if (!finished || !builder.IsReady || builder.Asset is null)
            {
                var reason = builder.Failure ?? "未完成";
                failures.Add("自行装配：" + reason);
                _warn("自行装配中文字体失败：" + reason
                    + "（试过 " + builder.RequestedCharacters + " 个字符，"
                    + "已装 " + builder.LoadedCharacters + " 个，字体文件="
                    + (string.IsNullOrEmpty(builder.FontFileName) ? "(未选定)" : builder.FontFileName) + "）");
                return null;
            }

            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            var check = CanRender(builder.Asset, font);
            if (!check.CanRender)
            {
                failures.Add("自行装配：字形不全（缺失 "
                    + FontReplacementPolicy.DescribeMissingGlyphs(check.Missing) + "）");
                _warn("自行装配的中文字体能建起来但缺字形，继续当失败处理");
                return null;
            }

            _fontSummary = "自行装配（字体文件=" + builder.FontFileName
                + "，图集=" + builder.AtlasCount + " 张，字符表 " + check.CharacterCount
                + " 项，耗时 " + elapsed.ToString("F0") + " ms）";
            _info("已加载中文回退字体：" + _fontSummary);
            return builder.Asset;
        }
        catch (Exception ex)
        {
            // 这条路可能触发原生崩溃（见方法注释），所以异常一律吃掉并继续当失败处理。
            failures.Add("自行装配抛异常：" + ex.GetType().Name + " " + ex.Message);
            _warn("自行装配中文字体抛异常（已忽略）：" + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 收下一个候选：先直接校验，再给它一次"补加字形"的机会，补完再校验。
    /// 两次都过不了就记下失败原因交给下一个候选。
    /// </summary>
    private bool Accept(TMP_FontAsset asset, string label, string text, List<string> failures)
    {
        var check = CanRender(asset, text);
        if (check.CanRender)
        {
            _font = asset;
            _fontSummary = label + "（字符表 " + check.CharacterCount + " 项，字形齐全）";
            _info("已加载中文回退字体：" + _fontSummary);
            return true;
        }
        if (TryGrow(asset, text, out var grown))
        {
            _font = grown;
            _fontSummary = label + "（补加字形后齐全，字符表 "
                + CanRender(grown, text).CharacterCount + " 项）";
            _info("已加载中文回退字体：" + _fontSummary);
            return true;
        }
        failures.Add(label + "：字形不全（字符表 " + check.CharacterCount + " 项，缺失 "
            + FontReplacementPolicy.DescribeMissingGlyphs(check.Missing) + "）");
        return false;
    }

    // 注意：方法里必须写全 UnityEngine.Font。TMP_FontAsset 上有一个 Font 类型成员，
    // 在实例方法里裸写 Font 会被解析成那个成员，报出"TMP_FontAsset 未包含
    // CreateDynamicFontFromOSFont"这种看起来莫名其妙的错。
    private UnityEngine.Font? CreateFromOsFont(string name, List<string> failures)
    {
        try
        {
            var font = UnityEngine.Font.CreateDynamicFontFromOSFont(name, 16);
            if (font is null)
            {
                failures.Add("系统字体名 " + name + "：CreateDynamicFontFromOSFont 返回空（系统里可能没有这个字体）");
                return null;
            }
            return font;
        }
        catch (Exception ex)
        {
            failures.Add("系统字体名 " + name + "：" + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    private TMP_FontAsset? CreateFontAsset(UnityEngine.Font font, string name, List<string> failures)
    {
        try
        {
            var asset = TMP_FontAsset.CreateFontAsset(font);
            if (asset is null)
            {
                failures.Add("系统字体名 " + name + "：TMP_FontAsset.CreateFontAsset 返回空");
                return null;
            }
            return asset;
        }
        catch (Exception ex)
        {
            failures.Add("系统字体名 " + name + "：创建字体资源失败 " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 让字体把缺失的字形补进图集（动态字体才做得到），补完再复核一次。
    /// 补不了就返回 false，由调用方去试下一个候选 —— 绝不靠"看着像成功"就放行。
    /// </summary>
    private static bool TryGrow(TMP_FontAsset asset, string text, out TMP_FontAsset grown)
    {
        grown = asset;
        try
        {
            asset.TryAddCharacters(text, out _);
        }
        catch (Exception)
        {
            return false;
        }
        return CanRender(asset, text).CanRender;
    }

    /// <summary>
    /// 调用 <c>FontEngine.LoadFontFace(string, int, int)</c>，**用反射**，并且容忍这个重载不存在。
    ///
    /// 为什么必须这样：实测 Mad Island（Unity 2021.3.45f2）里这个重载**根本不存在**，
    /// 直接调用会抛 <c>MissingMethodException: Method not found:
    /// FontEngineError FontEngine.LoadFontFace(string,single,int)</c>。
    /// 而这一句原本写在 SelfCheckChineseFont 里，异常会打断整个启动自检 ——
    /// 结果是整条翻译链路被卡死：一条文本都不会送去翻译，游戏里自然一个字都看不到。
    ///
    /// 所以这里的纪律是：**诊断永远不能让翻译流程失效**。
    /// 找不到方法就如实报告"该方法在这个 Unity 版本上不存在"，然后继续往下走。
    /// </summary>
    private static bool _faceProbeResolved;
    private static MethodInfo? _faceProbeMethod;

    private static string SafeLoadFontFace(string path, int faceIndex)
    {
        try
        {
            if (!_faceProbeResolved)
            {
                _faceProbeResolved = true;
                // 挑第一个"第一个参数是 string 路径"的重载：
                // Unity 2020/2021 与 2022 的 LoadFontFace 重载集合并不一样，
                // 与其写死签名，不如按参数类型现场挑一个能用的。
                _faceProbeMethod = typeof(FontEngine)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "LoadFontFace"
                                         && m.GetParameters().Length > 0
                                         && m.GetParameters()[0].ParameterType == typeof(string));
            }
            if (_faceProbeMethod is null) return "本 Unity 版本没有按路径加载字体的接口";

            var parameters = _faceProbeMethod.GetParameters();
            var arguments = new object?[parameters.Length];
            arguments[0] = path;
            for (var index = 1; index < parameters.Length; index++)
            {
                var type = parameters[index].ParameterType;
                var name = parameters[index].Name ?? string.Empty;
                if (type == typeof(int)) arguments[index] = name.IndexOf("face", StringComparison.OrdinalIgnoreCase) >= 0 ? faceIndex : 16;
                else if (type == typeof(float)) arguments[index] = 16f;
                else if (type.IsEnum) arguments[index] = Enum.GetValues(type).GetValue(0);
                else arguments[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : null;
            }
            var result = _faceProbeMethod.Invoke(null, arguments);
            return result?.ToString() ?? "空返回";
        }
        catch (MissingMethodException)
        {
            return "该方法在本 Unity 版本不存在";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ":" + ex.Message;
        }
    }

    /// <summary>
    /// 每个游戏进程只做一次"字体文件到底能不能被字体引擎读出来"的诊断。
    ///
    /// 用 TextCore 的 <c>FontEngine.LoadFontFace(filePath, pointSize, faceIndex)</c> 直接按文件路径试。
    /// 它返回错误码而不是抛异常，正好可以逐文件报告结果；对 `.ttc` 字体集合还会依次试 faceIndex 0/1/2，
    /// 从而明确区分"文件读不出来"和"字体集合里第几个 face 才有效"。
    /// 这一步只做诊断，不改变任何字体状态。
    /// </summary>
    private void LogProbeOnce()
    {
        if (_probeLogged) return;
        _probeLogged = true;

        var fontsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        var report = new List<string>();
        foreach (var name in FontFiles)
        {
            var path = Path.Combine(fontsDirectory, name);
            bool exists;
            try { exists = File.Exists(path); }
            catch (Exception) { exists = false; }
            if (!exists)
            {
                report.Add(name + "=不存在");
                continue;
            }
            var results = new List<string>();
            for (var face = 0; face < 3; face++)
                results.Add(face + ":" + SafeLoadFontFace(path, face));
            report.Add(name + "=[" + string.Join(",", results) + "]");
        }
        _info("字体文件诊断（LoadFontFace 按文件路径，face:错误码）：" + string.Join(" ", report));

        // 名字那一路：能不能拿到 Font 对象。
        var names = new List<string>();
        foreach (var name in FontNames)
        {
            try
            {
                names.Add(name + "=" + (UnityEngine.Font.CreateDynamicFontFromOSFont(name, 16) is null ? "空" : "成功"));
            }
            catch (Exception ex)
            {
                names.Add(name + "=" + ex.GetType().Name);
            }
        }
        _info("字体名诊断（CreateDynamicFontFromOSFont）：" + string.Join(" ", names));
    }

    /// <summary>一次字体能力检查的结果。</summary>
    public readonly struct FontCheck
    {
        public FontCheck(bool canRender, int characterCount, IReadOnlyList<char> missing, string reason)
        {
            CanRender = canRender;
            CharacterCount = characterCount;
            Missing = missing;
            Reason = reason;
        }

        public bool CanRender { get; }

        public int CharacterCount { get; }

        public IReadOnlyList<char> Missing { get; }

        public string Reason { get; }
    }
}
