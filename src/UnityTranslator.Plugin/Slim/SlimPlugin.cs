#if SLIM_NO_TMP
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;
using UnityTranslator.Core;

namespace UnityTranslator.Plugin;

/// <summary>
/// **精简变体**的插件本体，替换掉 `Plugin.cs`（后者在精简变体里整份排除）。
///
/// 为什么另写一个而不是给 `Plugin.cs` 加 `#if`（进展记录 B82）：
/// `Plugin.cs` 是 1522 行、TMP 类型**贯穿核心文本管线** ——
/// `Observe(...)` 的参数、`TextState` 的字段、字体决策、字号归一化都带 `TMP_FontAsset`/`TMP_Text`，
/// 实测 54 个错误分布在 26 个位置、且不是"包几个方法"能解决的。
/// 对照目标允许的"功能受限的变体"，另写一个自包含的小插件更划算也更诚实。
///
/// **本变体做什么**：只处理 `UnityEngine.UI.Text`（uGUI），
/// 判定是否该翻译 → 发请求 → 收到译文后直接替换组件文字；
/// 若系统字体可用，顺带把 `Text.font` 换成动态中文字体。
///
/// **本变体不做什么**（都因为目标运行时 Unity 5.6.7 / CLR 2.0 没有相应设施）：
///   * 不使用 TextMeshPro（游戏里根本没有 TMP）；
///   * 不装配字体图集（那整簇代码在精简变体里已排除）；
///   * 不做 OCR、不做作弊功能（依赖 .NET 4.0 并发类型与 ReadOnlySpan）。
/// </summary>
[BepInPlugin("org.codex.unitytranslator", "Unity Translator (slim)", "0.5.7")]
public sealed class Plugin : BaseUnityPlugin
{
    private sealed class TextState
    {
        public string Source = string.Empty;
        public string RequestId = string.Empty;
        public int Generation;
        public int Attempts;
        public string Translated;
    }

    private readonly ConcurrentQueue<ReplyMessage> _replies = new ConcurrentQueue<ReplyMessage>();
    private readonly ConcurrentQueue<OcrOverlayMessage> _ocrReplies = new ConcurrentQueue<OcrOverlayMessage>();
    private readonly ConcurrentQueue<CheatCommandMessage> _cheatCommands = new ConcurrentQueue<CheatCommandMessage>();
    private readonly Dictionary<long, TextState> _states = new Dictionary<long, TextState>();

    private PipeClient _pipe;
    private Font _cjkFont;
    private bool _fontTried;
    private float _nextScan;

    private void Awake()
    {
        PipeClient.Info = delegate (string message)
        {
            // 日志调用本身在某些运行时上可能抛，绝不能让它影响主链路
            try { Logger.LogInfo(message); } catch (Exception) { }
        };

        _pipe = new PipeClient(_replies, _ocrReplies, _cheatCommands);
        _pipe.Start();

        Logger.LogInfo("[精简] Unity Translator 精简变体已加载：只处理 uGUI Text，不使用 TextMeshPro");
    }

    private void Update()
    {
        // 传输由主线程驱动（这个运行时上非主线程的 socket 操作会停住，见进展记录 B77/B79）
        try { _pipe.TickTcp(); } catch (Exception) { }

        ApplyReplies();

        if (Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + 0.5f;
        Scan();
    }

    /// <summary>扫描所有 uGUI Text 组件，把该翻译的文字送出去。</summary>
    private void Scan()
    {
        Text[] components;
        try { components = Resources.FindObjectsOfTypeAll<Text>(); }
        catch (Exception) { return; }

        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (component == null) continue;
            try
            {
                if (!component.gameObject.activeInHierarchy) continue;
                var text = component.text;
                if (string.IsNullOrEmpty(text)) continue;
                if (!TextSelectionPolicy.IsTranslatable(text)) continue;

                var id = (long)component.GetInstanceID();
                TextState state;
                if (_states.TryGetValue(id, out state))
                {
                    // 已经是这个内容了（可能已翻译、也可能在等回包）→ 不重复发
                    if (state.Source == text) continue;
                }
                else
                {
                    state = new TextState();
                    _states[id] = state;
                }

                state.Source = text;
                state.Translated = null;
                state.RequestId = Guid.NewGuid().ToString("N");
                state.Generation++;
                state.Attempts++;

                if (_pipe != null && _pipe.IsConnected)
                {
                    _pipe.Send(new RequestMessage
                    {
                        RequestId = state.RequestId,
                        Source = state.Source,
                        SourceLanguage = TextSelectionPolicy.DetectLanguage(state.Source),
                        ComponentId = id,
                        Generation = state.Generation
                    });
                    if (state.Attempts == 1)
                        Logger.LogInfo("[精简·发送] 组件 " + id + "：「" + Shorten(state.Source) + "」");
                }
            }
            catch (Exception) { }
        }
    }

    private void ApplyReplies()
    {
        ReplyMessage reply;
        while (_replies.TryDequeue(out reply))
        {
            if (reply == null) continue;
            try { Apply(reply); } catch (Exception) { }
        }
    }

    private void Apply(ReplyMessage reply)
    {
        if (_pipe == null || !_pipe.IsConnected) return;

        TextState state;
        if (!_states.TryGetValue(reply.ComponentId, out state)) return;
        if (reply.Translated == null || reply.Translated.Trim().Length == 0) return;
        if (state.Translated != null) return;

        var component = FindComponent(reply.ComponentId);
        if (component == null) return;

        // 用与主变体同一套守卫：确认「还是那一条、中间没被别人改过」
        var current = component.text;
        if (!ReplacementPolicy.ReplyMatches(state.Source, current, state.Generation,
                state.RequestId, reply.Generation, reply.RequestId)) return;

        state.Translated = reply.Translated;

        EnsureCjkFont(component);
        component.text = reply.Translated;
        Logger.LogInfo("[精简·替换] 组件 " + reply.ComponentId + "：「" + Shorten(state.Source)
            + "」→「" + Shorten(reply.Translated) + "」");
    }

    private static Text FindComponent(long id)
    {
        Text[] components;
        try { components = Resources.FindObjectsOfTypeAll<Text>(); }
        catch (Exception) { return null; }
        for (var i = 0; i < components.Length; i++)
        {
            var c = components[i];
            if (c != null && (long)c.GetInstanceID() == id) return c;
        }
        return null;
    }

    /// <summary>
    /// 精简变体唯一的字体手段：动态系统字体。
    /// 没有 TMP 图集可装配，所以只能靠 `Font.CreateDynamicFontFromOSFont`；
    /// 拿不到就保持游戏原字体（此时中文可能显示为方框，但至少不会崩）。
    /// </summary>
    private void EnsureCjkFont(Text component)
    {
        if (!_fontTried)
        {
            _fontTried = true;
            var names = new[] { "Microsoft YaHei", "SimHei", "SimSun", "DengXian" };
            for (var i = 0; i < names.Length && _cjkFont == null; i++)
            {
                try
                {
                    _cjkFont = Font.CreateDynamicFontFromOSFont(names[i], component.fontSize);
                    if (_cjkFont != null)
                        Logger.LogInfo("[精简·字体] 已使用系统字体：" + names[i]);
                }
                catch (Exception) { _cjkFont = null; }
            }
            if (_cjkFont == null)
                Logger.LogWarning("[精简·字体] 系统动态字体都拿不到，将沿用游戏原字体（中文可能显示为方框）");
        }

        if (_cjkFont != null) component.font = _cjkFont;
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= 24 ? text : text.Substring(0, 24) + "…";
    }
}
#endif
