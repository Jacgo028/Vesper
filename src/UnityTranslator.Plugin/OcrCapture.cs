using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UnityTranslator.Plugin;

public sealed partial class Plugin
{
    private readonly ConcurrentQueue<OcrOverlayMessage> _ocrReplies = new ConcurrentQueue<OcrOverlayMessage>();
    private OcrOverlayBox[] _ocrBoxes = Array.Empty<OcrOverlayBox>();
    private float _nextOcr;
    private float _ocrSentAt;
    private float _overlayAt;
    private int _frameId;
    private bool _ocrPending;
    private bool _capturing;
    private bool _ocrEnabled = true;
    private string? _lastImagePath;
    private GUIStyle? _ocrStyle;
    private int _ocrOverlayCount;
    private string _captureScene = string.Empty;
    private string _overlayScene = string.Empty;
    private int _captureWidth;
    private int _captureHeight;
    private bool _validatingOcr;

    /// <summary>
    /// 活动场景的稳定标识。
    ///
    /// ★ 绝对不要用 <c>Scene.handle</c> —— 那是 Unity 的**内部**属性。
    ///   Unity 6000.3 上这个 getter 已经不存在，而这里是**编译期直接引用**，
    ///   执行到就抛 <c>MissingMethodException: Method not found: int UnityEngine.SceneManagement.Scene.get_handle()</c>。
    ///
    ///   后果远比"OCR 不能用"严重得多：<c>TickOcr()</c> 是在 <c>Update()</c> 的**前段**被调用的，
    ///   异常直接把整个 <c>Update</c> 打断，**后面的文字扫描一行都跑不到**。
    ///   实测 Antivirus Girl（Unity 6000.3.13）：插件加载正常、管道也连上了，
    ///   但一条翻译请求都没发出去，游戏自己的 Player.log 里躺着 **10968 条**同一异常，
    ///   而 BepInEx 的 LogOutput.log 里**一个字都没有**（又一次"错误只在 Player.log 里"）。
    ///
    ///   改用公开且长期稳定的 buildIndex + name 组合。
    /// </summary>
    private static string ActiveSceneKey()
    {
        try
        {
            var scene = SceneManager.GetActiveScene();
            return scene.buildIndex + "#" + (scene.name ?? string.Empty);
        }
        catch (Exception) { return "?"; }
    }

    private void TickOcr()
    {
        if ((_ocrPending && (ActiveSceneKey() != _captureScene ||
                             Screen.width != _captureWidth || Screen.height != _captureHeight)) ||
            (_ocrBoxes.Length > 0 && (ActiveSceneKey() != _overlayScene ||
                                      Time.unscaledTime - _overlayAt > 8f)))
        {
            _ocrBoxes = Array.Empty<OcrOverlayBox>();
            _ocrPending = false;
            ++_frameId;
            DeleteLastFrame();
        }
        if (Input.GetKeyDown(KeyCode.F9))
        {
            _ocrEnabled = !_ocrEnabled;
            if (!_ocrEnabled)
            {
                _ocrBoxes = Array.Empty<OcrOverlayBox>();
                _ocrPending = false;
                ++_frameId;
                DeleteLastFrame();
            }
        }
        while (_ocrReplies.TryDequeue(out var reply))
        {
            if (!_ocrPending || reply.FrameId != _frameId || !_ocrEnabled || !_enabled ||
                Time.unscaledTime - _ocrSentAt > 12f || reply.Width != _captureWidth || reply.Height != _captureHeight)
                continue;
            if (!_validatingOcr) StartCoroutine(ValidateOverlay(reply));
        }
        if (_ocrPending && !_validatingOcr && Time.unscaledTime - _ocrSentAt > 12f)
        {
            _ocrPending = false;
            ++_frameId;
            DeleteLastFrame();
        }
        if (_enabled && _ocrEnabled && !_ocrPending && !_capturing && Time.unscaledTime >= _nextOcr)
        {
            _nextOcr = Time.unscaledTime + 3f;
            StartCoroutine(CaptureFrame());
        }
    }

    private IEnumerator CaptureFrame()
    {
        _capturing = true;
        yield return new WaitForEndOfFrame();
        if (!_enabled || !_ocrEnabled || _pipe == null || !_pipe.IsConnected)
        {
            _capturing = false;
            yield break;
        }
        Texture2D? texture = null;
        try
        {
            texture = ScreenCapture.CaptureScreenshotAsTexture();
            var folder = Path.Combine(Path.GetTempPath(), "UnityTranslator");
            Directory.CreateDirectory(folder);
            var id = ++_frameId;
            var path = Path.Combine(folder, "ocr-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + id + ".png");
            File.WriteAllBytes(path, texture.EncodeToPNG());
            _lastImagePath = path;
            _captureScene = ActiveSceneKey();
            _captureWidth = texture.width;
            _captureHeight = texture.height;
            _pipe?.SendOcr(new OcrFrameMessage
            {
                FrameId = id, ImagePath = path, Width = texture.width, Height = texture.height,
                KnownUiBoxes = CollectUiBoxes()
            });
            _ocrPending = true;
            _ocrSentAt = Time.unscaledTime;
        }
        catch (Exception ex) { Logger.LogWarning("OCR capture failed: " + ex.Message); }
        finally
        {
            if (texture != null) Destroy(texture);
            _capturing = false;
        }
    }

    private IEnumerator ValidateOverlay(OcrOverlayMessage reply)
    {
        _validatingOcr = true;
        _capturing = true;
        yield return new WaitForEndOfFrame();
        Texture2D? original = null;
        Texture2D? current = null;
        try
        {
            if (_lastImagePath == null || !File.Exists(_lastImagePath)) yield break;
            original = new Texture2D(2, 2);
            original.LoadImage(File.ReadAllBytes(_lastImagePath));
            current = ScreenCapture.CaptureScreenshotAsTexture();
            if (!_ocrPending || reply.FrameId != _frameId || !_enabled || !_ocrEnabled ||
                ActiveSceneKey() != _captureScene ||
                Time.unscaledTime - _ocrSentAt > 12f || original.width != current.width || original.height != current.height)
                yield break;
            _ocrBoxes = (reply.Boxes ?? Array.Empty<OcrOverlayBox>())
                .Where(box => RegionMatches(original, current, box)).ToArray();
            _overlayAt = Time.unscaledTime;
            _overlayScene = _captureScene;
            if (_ocrBoxes.Length > 0 && _ocrOverlayCount++ < 10)
                Logger.LogInfo("Applied OCR overlay for frame " + reply.FrameId + " with " + _ocrBoxes.Length + " boxes");
        }
        finally
        {
            if (original != null) Destroy(original);
            if (current != null) Destroy(current);
            _capturing = false;
            _validatingOcr = false;
            _ocrPending = false;
            DeleteLastFrame();
        }
    }

    private static bool RegionMatches(Texture2D original, Texture2D current, OcrOverlayBox box)
    {
        if (box.Width < 4 || box.Height < 4 || box.X < 0 || box.Y < 0 ||
            box.X + box.Width > original.width || box.Y + box.Height > original.height) return false;
        var delta = 0f;
        for (var row = 0; row < 8; row++)
            for (var col = 0; col < 8; col++)
            {
                var x = box.X + (col * 2 + 1) * box.Width / 16;
                var y = original.height - 1 - (box.Y + (row * 2 + 1) * box.Height / 16);
                var before = original.GetPixel(x, y);
                var after = current.GetPixel(x, y);
                delta += Math.Abs(before.r - after.r) + Math.Abs(before.g - after.g) + Math.Abs(before.b - after.b);
            }
        return delta / (8 * 8 * 3) < 0.12f;
    }

    private OcrUiBox[] CollectUiBoxes()
    {
        var boxes = new List<OcrUiBox>();
        foreach (var state in _states.Values)
        {
            var visible = state.Tmp != null ? state.Tmp.gameObject.activeInHierarchy :
                state.Ui != null && state.Ui.gameObject.activeInHierarchy;
            if (!visible) continue;
            var transform = state.Tmp != null ? state.Tmp.transform as RectTransform :
                state.Ui != null ? state.Ui.transform as RectTransform : null;
            if (transform == null || boxes.Count >= 400) continue;
            var canvas = transform.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay && canvas.worldCamera == null) continue;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var corners = new Vector3[4];
            transform.GetWorldCorners(corners);
            var points = corners.Select(point => RectTransformUtility.WorldToScreenPoint(camera, point)).ToArray();
            var left = Math.Max(0, (int)points.Min(point => point.x));
            var right = Math.Min(Screen.width, (int)points.Max(point => point.x));
            var top = Math.Max(0, (int)(Screen.height - points.Max(point => point.y)));
            var bottom = Math.Min(Screen.height, (int)(Screen.height - points.Min(point => point.y)));
            if (right > left && bottom > top)
                boxes.Add(new OcrUiBox { X = left, Y = top, Width = right - left, Height = bottom - top });
        }
        return boxes.ToArray();
    }

    private void OnGUI()
    {
        // Directly observed game text uses this path when its TMP font cannot
        // render Chinese. It is independent of the optional OCR switch.
        if (_enabled) DrawFallbackTranslations();
        if (!_enabled || !_ocrEnabled || _capturing || Time.unscaledTime - _overlayAt > 8f) return;
        _chineseFont ??= Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);
        _ocrStyle ??= new GUIStyle(GUI.skin.label) { font = _chineseFont, wordWrap = true,
            alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
        foreach (var box in _ocrBoxes)
        {
            if (box.Width <= 0 || box.Height <= 0) continue;
            var rect = new Rect(box.X, box.Y, box.Width, box.Height);
            var oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.92f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            _ocrStyle.fontSize = Math.Max(13, Math.Min(34, box.Height - 2));
            GUI.Label(rect, box.Translated, _ocrStyle);
            GUI.color = oldColor;
        }
    }

    private void DeleteLastFrame()
    {
        try { if (_lastImagePath != null && File.Exists(_lastImagePath)) File.Delete(_lastImagePath); }
        catch (IOException) { }
        _lastImagePath = null;
    }

    private void InvalidateOcr()
    {
        _ocrBoxes = Array.Empty<OcrOverlayBox>();
        _ocrPending = false;
        ++_frameId;
        DeleteLastFrame();
    }
}

[Serializable]
public sealed class OcrFrameMessage
{
    public int Version = 1;
    public string Kind = "OcrFrame";
    public int FrameId;
    public string ImagePath = string.Empty;
    public int Width;
    public int Height;
    public OcrUiBox[] KnownUiBoxes = Array.Empty<OcrUiBox>();
}

[Serializable]
public sealed class OcrUiBox
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
}

[Serializable]
public sealed class OcrOverlayMessage
{
    public int Version;
    public string Kind = string.Empty;
    public int FrameId;
    public int Width;
    public int Height;
    public OcrOverlayBox[] Boxes = Array.Empty<OcrOverlayBox>();
}

[Serializable]
public sealed class OcrOverlayBox
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public string Translated = string.Empty;
}
