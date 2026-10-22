using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("unitytranslator.runtime.layoutprobe", "Translator Runtime Layout Probe", "1.0.0")]
public sealed class LayoutProbe : BaseUnityPlugin
{
    private const string Source = "Play";
    private const string Translated = "A deliberately long menu translation";
    private readonly List<Text> _legacy = new List<Text>();
    private TMP_Text? _tmp;
    private TMP_FontAsset? _tmpFont;
    private Material? _tmpMaterial;
    private Component? _translator;
    private int _stage;
    private float _next;
    private float _deadline;
    private int _failures;
    private string RunToken => Environment.GetEnvironmentVariable("UT_LAYOUT_PROBE_RUN") ?? "manual";

    private void Awake() { _next = 5f; _deadline = 20f; }

    private void Update()
    {
        if (Time.realtimeSinceStartup < _next || _stage == 99) return;
        try
        {
            if (_stage == 0)
            {
                _translator = Resources.FindObjectsOfTypeAll<Component>().FirstOrDefault(c => c != null
                    && c.GetType().FullName == "UnityTranslator.Plugin.Plugin");
                var native = Resources.FindObjectsOfTypeAll<TMP_Text>().FirstOrDefault(t => t != null && t.font != null);
                if (_translator == null || native == null) { _next += 1; return; }
                var canvas = new GameObject("RuntimeLayoutProbeCanvas", typeof(Canvas));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                DontDestroyOnLoad(canvas);
                for (var i = 0; i < 2; i++)
                {
                    var target = new GameObject("RuntimeLegacy" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)).GetComponent<Text>();
                    target.transform.SetParent(canvas.transform, false);
                    target.rectTransform.sizeDelta = new Vector2(250, 50);
                    target.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    target.fontSize = 30;
                    target.resizeTextForBestFit = i == 1;
                    target.resizeTextMinSize = 10;
                    target.resizeTextMaxSize = 30;
                    target.horizontalOverflow = HorizontalWrapMode.Wrap;
                    target.verticalOverflow = VerticalWrapMode.Truncate;
                    target.text = Source + i;
                    _legacy.Add(target);
                }
                _tmp = new GameObject("RuntimeTMP", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
                _tmp.transform.SetParent(canvas.transform, false);
                _tmp.rectTransform.sizeDelta = new Vector2(250, 50);
                _tmp.font = native.font;
                _tmp.fontSharedMaterial = new Material(native.fontSharedMaterial);
                _tmpFont = _tmp.font;
                _tmpMaterial = _tmp.fontSharedMaterial;
                _tmp.fontSize = 30;
                _tmp.enableAutoSizing = true;
                _tmp.fontSizeMin = 10;
                _tmp.fontSizeMax = 30;
                _tmp.text = Source + " TMP";
                Logger.LogInfo("PROBE START created legacy=2 tmp=1 source-compatible fonts rect=250x50 size=30");
                _stage = 1; _next += 5;
            }
            else if (_stage == 1)
            {
                if (_legacy.Any(t => t.text != Translated) || _tmp!.text != Translated)
                {
                    if (Time.realtimeSinceStartup < _deadline) { _next += 1; return; }
                    foreach (var t in _legacy) Check(t.text == Translated, "initial translation " + t.name + " text=" + t.text);
                    Check(_tmp!.text == Translated, "initial translation TMP text=" + _tmp.text);
                }
                VerifyLayout("initial");
                foreach (var t in _legacy) { t.text = Source + (_legacy.IndexOf(t)); t.fontSize = 30; }
                _tmp!.text = Source + " TMP"; _tmp.fontSize = 30;
                _stage = 2; _next = Time.realtimeSinceStartup + 2;
            }
            else if (_stage == 2)
            {
                VerifyLayout("reapply");
                foreach (var t in _legacy) t.text = string.Empty;
                _tmp!.text = string.Empty;
                _stage = 3; _next = Time.realtimeSinceStartup + 1;
            }
            else if (_stage == 3)
            {
                foreach (var t in _legacy) Check(t.fontSize == 30 && t.resizeTextForBestFit == (_legacy.IndexOf(t) == 1), "blank restores " + t.name + " size=" + t.fontSize + " auto=" + t.resizeTextForBestFit);
                Check(Math.Abs(_tmp!.fontSize - 30) < .05f && _tmp.enableAutoSizing, "blank restores TMP size=" + _tmp.fontSize + " auto=" + _tmp.enableAutoSizing);
                foreach (var t in _legacy) t.text = Source + (_legacy.IndexOf(t));
                _tmp.text = Source + " TMP";
                _stage = 4; _next = Time.realtimeSinceStartup + 3;
            }
            else if (_stage == 4)
            {
                VerifyLayout("after blank");
                SetEnabled(false);
                Invoke("RestoreAll");
                foreach (var t in _legacy) Check(t.fontSize == 30 && t.resizeTextForBestFit == (_legacy.IndexOf(t) == 1), "disable restores " + t.name + " size=" + t.fontSize);
                Check(Math.Abs(_tmp!.fontSize - 30) < .05f && _tmp.enableAutoSizing, "disable restores TMP size=" + _tmp.fontSize);
                Check(_tmp.font == _tmpFont && _tmp.fontSharedMaterial == _tmpMaterial, "disable preserves TMP font/material");
                SetEnabled(true);
                foreach (var t in _legacy) t.text = Source + (_legacy.IndexOf(t));
                _tmp.text = Source + " TMP";
                _stage = 5; _next = Time.realtimeSinceStartup + 3;
            }
            else if (_stage == 5)
            {
                foreach (var t in _legacy) { t.fontSize = 17; t.text = "Another pooled runtime sentence"; }
                _tmp!.fontSize = 17; _tmp.text = "Another pooled runtime sentence TMP";
                _stage = 6; _next = Time.realtimeSinceStartup + 2;
            }
            else if (_stage == 6)
            {
                foreach (var t in _legacy) Check(t.fontSize <= 17, "pooled native size preserved " + t.name + " size=" + t.fontSize);
                Check(_tmp!.fontSize <= 17.05f, "pooled native size preserved TMP size=" + _tmp.fontSize);
                SetEnabled(false);
                Invoke("RestoreAll");
                SetEnabled(true);
                _tmp.fontSize = 30;
                _tmp.enableAutoSizing = false;
                _tmp.text = "Native fallback probe";
                InvokeObserve(_tmp);
                var states = (IDictionary)_translator!.GetType().GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_translator)!;
                var state = states[(long)_tmp.GetInstanceID()];
                Check(!_tmp.font.HasCharacter('译'), "CJK fixture needs a fallback glyph");
                var applied = (bool)_translator.GetType().GetMethod("TryApplyDirectly", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_translator,
                    new object[] { state!, "译文", 0L })!;
                Check(applied && _tmp.font == _tmpFont && _tmp.fontSharedMaterial == _tmpMaterial, "CJK fallback preserves primary font/material");
                _tmp.text = "Native fallback probe";
                InvokeObserve(_tmp);
                Check(_tmp.text == "译文" && _tmp.font == _tmpFont && _tmp.fontSharedMaterial == _tmpMaterial, "CJK source reset preserves primary font/material");
                SetEnabled(false);
                Invoke("RestoreAll");
                Check(_tmp.font == _tmpFont && _tmp.fontSharedMaterial == _tmpMaterial, "CJK restore preserves primary font/material");
                SetEnabled(true);
                var legacy = _legacy[0];
                legacy.fontSize = 30;
                legacy.resizeTextForBestFit = false;
                legacy.text = "Owned font fallback probe";
                var nativeFont = legacy.font;
                var nativeMaterial = legacy.material;
                InvokeObserve(legacy);
                Check(!TryDirect(legacy, new string('译', 5000)) && legacy.text == "Owned font fallback probe"
                    && legacy.font == nativeFont && legacy.material == nativeMaterial && legacy.fontSize == 30 && !legacy.resizeTextForBestFit,
                    "impossible legacy translation rolls back text/font/material/size/auto");
                // Windows Arial can already resolve CJK through its OS font
                // fallback. An empty native Font makes this fixture exercise
                // the plugin's replacement-font ownership branch explicitly.
                var emptyFont = new Font();
                legacy.font = emptyFont;
                legacy.text = "Owned CJK fallback probe";
                InvokeObserve(legacy);
                Check(TryDirect(legacy, "译文") && legacy.font != emptyFont, "legacy CJK uses owned replacement font");
                var gameFont = Font.CreateDynamicFontFromOSFont("Consolas", 19);
                var gameMaterial = new Material(legacy.material);
                legacy.font = gameFont;
                legacy.material = gameMaterial;
                legacy.fontSize = 19;
                legacy.resizeTextForBestFit = true;
                legacy.text = "Game changed the pooled label";
                SetEnabled(false);
                Invoke("RestoreAll");
                Check(legacy.font == gameFont && legacy.material == gameMaterial && legacy.fontSize == 19 && legacy.resizeTextForBestFit
                    && legacy.text == "Game changed the pooled label", "restore preserves independent game font/material/size/auto/text changes");
                Logger.LogInfo("PROBE RESULT run=" + RunToken + " failures=" + _failures);
                _stage = 99;
            }
        }
        catch (Exception ex) { Logger.LogError("PROBE ERROR run=" + RunToken + " " + ex); _stage = 99; }
    }

    private void VerifyLayout(string phase)
    {
        Canvas.ForceUpdateCanvases();
        foreach (var t in _legacy)
        {
            t.cachedTextGenerator.Populate(t.text, t.GetGenerationSettings(t.rectTransform.rect.size));
            Check(t.text == Translated && t.fontSize <= 30 && t.fontSize < 29 && !t.resizeTextForBestFit
                && t.preferredHeight <= 50.1f, phase + " " + t.name + " size=" + t.fontSize + " auto=" + t.resizeTextForBestFit + " preferredHeight=" + t.preferredHeight + " nativeVisible=" + t.cachedTextGenerator.characterCountVisible);
        }
        _tmp!.ForceMeshUpdate();
        var measured = _tmp.GetPreferredValues(_tmp.text, 250, float.PositiveInfinity);
        Check(_tmp.text == Translated && _tmp.fontSize < 29 && !_tmp.enableAutoSizing && measured.x <= 250.1f && measured.y <= 50.1f,
            phase + " TMP size=" + _tmp.fontSize + " auto=" + _tmp.enableAutoSizing + " preferred=" + measured);
        Check(_tmp.font == _tmpFont && _tmp.fontSharedMaterial == _tmpMaterial, phase + " native TMP font/material retained");
    }

    private void Check(bool pass, string detail) { if (!pass) _failures++; Logger.LogInfo("PROBE " + (pass ? "PASS " : "FAIL ") + detail); }
    private void SetEnabled(bool enabled) => _translator!.GetType().GetField("_enabled", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_translator, enabled);
    private void Invoke(string name) => _translator!.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_translator, null);
    private void InvokeObserve(TMP_Text target) => _translator!.GetType().GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_translator,
        new object?[] { (long)target.GetInstanceID(), target.text, new Action<string>(value => target.text = value), target, null });
    private void InvokeObserve(Text target) => _translator!.GetType().GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_translator,
        new object?[] { (long)target.GetInstanceID(), target.text, new Action<string>(value => target.text = value), null, target });
    private bool TryDirect(Component target, string translated)
    {
        var states = (IDictionary)_translator!.GetType().GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_translator)!;
        return (bool)_translator.GetType().GetMethod("TryApplyDirectly", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_translator,
            new object[] { states[(long)target.GetInstanceID()]!, translated, 0L })!;
    }
}
