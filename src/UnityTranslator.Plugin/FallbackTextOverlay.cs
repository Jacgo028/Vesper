using System;
using TMPro;
using UnityEngine;
using UnityTranslator.Core;

namespace UnityTranslator.Plugin;

public sealed partial class Plugin
{
    /// <summary>一次最多画多少条覆盖，避免极端情况下把画面铺满。</summary>
    private const int MaxOverlayItems = 24;

    private Font? _overlayFont;
    private GUIStyle? _overlayStyle;
    private int _overlayUsesLogged;
    private bool _overlayFontFailureLogged;
    private int _overlayDrawnLogged;

    /// <summary>
    /// TextMeshPro cannot create a usable font asset in some shipped games.
    /// The Unity IMGUI path can use an installed OS font directly, so display
    /// the translation over the original text without changing game assets.
    /// </summary>
    private bool TryUseFallbackOverlay(TextState state, string translated)
    {
        if (!TryGetOverlayRect(state, out _)) return false;
        if (!EnsureOverlayFont(translated)) return false;
        if (_overlayUsesLogged++ < 5)
            Logger.LogInfo("游戏字体无法显示整句中文，改用游戏内覆盖显示（组件 "
                + (state.Tmp != null ? state.Tmp.GetInstanceID() : state.Ui?.GetInstanceID()) + "）");
        return true;
    }

    private bool EnsureOverlayFont(string text)
    {
        try
        {
            if (_overlayFont != null && OverlayFontHasText(_overlayFont, text)) return true;
            foreach (var names in new[]
            {
                new[] { "Microsoft YaHei", "SimHei", "SimSun" },
                new[] { "SimHei", "Microsoft YaHei", "SimSun" },
                new[] { "DengXian", "Microsoft YaHei", "SimSun" }
            })
            {
                var font = Font.CreateDynamicFontFromOSFont(names, 16);
                if (font == null || !OverlayFontHasText(font, text)) continue;
                _overlayFont = font;
                _overlayStyle = null;
                Logger.LogInfo("游戏内覆盖显示已找到可用的系统字体：" + string.Join(" / ", names));
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!_overlayFontFailureLogged)
                Logger.LogWarning("游戏内覆盖字体检查失败：" + ex.GetType().Name + " " + ex.Message);
        }
        if (!_overlayFontFailureLogged)
        {
            _overlayFontFailureLogged = true;
            Logger.LogWarning("游戏内覆盖显示也缺少这段译文所需的字形，保留游戏原文。");
        }
        return false;
    }

    private static bool OverlayFontHasText(Font font, string text)
    {
        foreach (var character in FontReplacementPolicy.RequiredGlyphs(text))
            if (!font.HasCharacter(character)) return false;
        return true;
    }

    /// <summary>
    /// 中文字体是否已经就绪。就绪后覆盖层**彻底停用**，全部走直接替换。
    /// 由 <c>Plugin.TickCjkFont</c> 在装配完成时置位。
    /// </summary>
    private bool _cjkFontReady;

    /// <summary>
    /// 是否允许使用 IMGUI 覆盖层兜底。**默认 false**。
    ///
    /// 它的观感是"带不透明黑底的方块"，用户明确拒绝：
    /// 替换不上时**宁可保留游戏原文**，也不要在画面上糊一块黑方块。
    /// 代码保留（少数游戏仍可能需要），但默认不参与。
    /// </summary>
    private const bool AllowFallbackOverlay = false;

    /// <summary>
    /// 把译文画到游戏文字原来的位置上。
    ///
    /// V0.4 相对 V0.3 改了三件事，都是实机截图暴露出来的：
    ///
    /// 1. **底色改成不透明**。V0.3 用 0.92 透明度的黑底，游戏原文会从下面透出来，
    ///    和译文叠在一起变成一团糊字。译文要替代原文，底就必须盖死。
    /// 2. **字号按"放得下"来选**。V0.3 固定用 <c>rect.height * 0.72</c>，
    ///    中文比日文/英文占宽，稍长一点就换行溢出矩形，看上去像被切掉一截。
    ///    现在从大往小试，取第一个宽高都放得下的字号；实在放不下才允许加高矩形。
    /// 3. **给字加描边**。游戏背景颜色不可控，白字直接压上去可能看不清，
    ///    一圈深色描边能让译文在任何背景上都读得出来。
    /// </summary>
    private void DrawFallbackTranslations()
    {
        if (_overlayFont == null) return;

        // ★★ 中文字体一旦就绪，覆盖层必须**彻底停用**。
        //
        //   实测踩到的"同一处文字被画两遍"就是这个原因：
        //   覆盖层在字体装配完成前给译文画了一个**不透明黑底 + 白字**的方块，
        //   字体就绪后直接替换又把同一句话写进游戏组件 ——
        //   两套渲染同时存在，画面上就是"黑方块 + 重复字"叠在一起
        //   （日志证据：`译文覆盖已绘制：位置 134,279 尺寸 352x67` 与
        //   `已直接替换游戏原文（组件 174156）` 同时出现）。
        //
        //   覆盖层本来就是"字体还没装好时的临时兜底"，字体好了就不该再画：
        //   它画的是**不透明黑底**，正是用户明确不要的那种"糊黑方块"效果。
        if (_cjkFontReady) return;

        // 产品要求：替换不上时保留游戏原文，不用带不透明黑底的覆盖块。
        if (!AllowFallbackOverlay) return;

        _overlayStyle ??= new GUIStyle(GUI.skin.label)
        {
            font = _overlayFont,
            richText = true,
            wordWrap = true,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(5, 5, 2, 2)
        };
        _overlayStyle.normal.textColor = Color.white;

        var previousDepth = GUI.depth;
        GUI.depth = -1000;
        try
        {
            var drawn = 0;
            foreach (var state in _states.Values)
            {
                if (!state.UsesOverlay || state.Translated is null ||
                    !IsLive(state) || CurrentText(state) != state.Source ||
                    !TryGetOverlayRect(state, out var rect)) continue;
                if (++drawn > MaxOverlayItems) break;

                // 原文字的宽度高度：矩形至少要盖住它们，否则原文会露在外面。
                var originalWidth = rect.width;
                var originalHeight = rect.height;

                // 排版全部交给 Core 里的纯计算（那部分有单元测试钉死"一定放得下"）。
                var layout = OverlayTextLayout.Compute(state.Translated, originalWidth, originalHeight,
                    Screen.width, Screen.height, rect.x, rect.y);
                if (layout.Width < 8f || layout.Height < 8f) continue;
                _overlayStyle.fontSize = layout.FontSize;

                // 原文贴着屏幕右边缘时排版会给出一个负偏移，让覆盖层向左让位。
                var box = new Rect(rect.x + layout.OffsetX, rect.y, layout.Width, layout.Height);
                var previousColor = GUI.color;

                // 1) 不透明底：盖住原文。游戏原文可能带颜色，只靠描边盖不干净。
                GUI.color = Color.black;
                GUI.DrawTexture(box, Texture2D.whiteTexture);

                // 2) 深色描边：向八个方向各偏 2 像素铺一遍黑字，
                //    译文压在浅色游戏背景上也能看清（"回"字形描边）。
                GUI.color = Color.black;
                for (var dx = -2; dx <= 2; dx += 2)
                    for (var dy = -2; dy <= 2; dy += 2)
                    {
                        if (dx == 0 && dy == 0) continue;
                        GUI.Label(new Rect(box.x + dx, box.y + dy, box.width, box.height), state.Translated, _overlayStyle);
                    }

                // 3) 正文
                GUI.color = Color.white;
                GUI.Label(box, state.Translated, _overlayStyle);
                GUI.color = previousColor;

                if (_overlayDrawnLogged++ < 5)
                    Logger.LogInfo("译文覆盖已绘制：位置 " + box.x.ToString("0") + "," + box.y.ToString("0")
                        + " 尺寸 " + box.width.ToString("0") + "x" + box.height.ToString("0")
                        + " 字号 " + layout.FontSize + " 折行 " + layout.Lines
                        + " 文字「" + Shorten(state.Translated) + "」");
            }
        }
        catch (Exception ex)
        {
            if (!_overlayFontFailureLogged)
            {
                _overlayFontFailureLogged = true;
                Logger.LogWarning("游戏内覆盖显示失败，游戏原文保持不变：" + ex.GetType().Name + " " + ex.Message);
            }
        }
        finally
        {
            GUI.depth = previousDepth;
        }
    }


    private static bool TryGetOverlayRect(TextState state, out Rect rect)
    {
        rect = default;
        try
        {
            RectTransform? transform = state.Tmp != null ? state.Tmp.rectTransform : state.Ui?.rectTransform;
            if (transform == null) return false;
            var canvas = transform.GetComponentInParent<Canvas>();
            if (canvas == null) return false;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera ?? Camera.main;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera == null) return false;

            var corners = new Vector3[4];
            transform.GetWorldCorners(corners);
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var minY = float.MaxValue;
            var maxY = float.MinValue;
            foreach (var corner in corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
            }
            var left = Mathf.Clamp(minX - 2, 0, Screen.width);
            var right = Mathf.Clamp(maxX + 2, 0, Screen.width);
            var top = Mathf.Clamp(Screen.height - maxY - 2, 0, Screen.height);
            var bottom = Mathf.Clamp(Screen.height - minY + 2, 0, Screen.height);
            if (right - left < 8 || bottom - top < 8) return false;
            rect = new Rect(left, top, right - left, bottom - top);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
