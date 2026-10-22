using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.Core;

/// <summary>
/// Shared pure-BCL receiver for the desktop's OCR envelope. Unity JsonUtility can
/// return an empty nested Boxes array for a valid reply, so this source is linked
/// into the plugin rather than relying on Unity serialization or another library.
/// </summary>
public static class OcrOverlayWire
{
    public const int MaxMessageBytes = 65536;
    public const int MaxBoxes = 64;
    public const int MaxTextLength = 4096;

    public static bool TryRead(string? json, out OcrWireOverlay? overlay)
    {
        overlay = null;
        if (string.IsNullOrWhiteSpace(json) || json!.Length > MaxMessageBytes ||
            Encoding.UTF8.GetByteCount(json) > MaxMessageBytes || !HasSafeTokens(json)) return false;
        if (!MiniJson.TryParse(json, out var root) || !root.IsObject ||
            !root.TryGetInt32("Version", out var version) || version != 1 ||
            !root.TryGetString("Kind", out var kind) || kind != "OcrOverlay" ||
            !root.TryGetInt32("FrameId", out var frameId) || frameId < 1 ||
            !root.TryGetInt32("Width", out var width) || width < 1 || width > 16384 ||
            !root.TryGetInt32("Height", out var height) || height < 1 || height > 16384 ||
            !root.TryGet("Boxes", out var boxes) || !boxes.IsArray || boxes.Items.Count > MaxBoxes) return false;

        var parsed = new OcrWireOverlayBox[boxes.Items.Count];
        for (var index = 0; index < parsed.Length; index++)
        {
            var item = boxes.Items[index];
            if (!item.IsObject || !item.TryGetInt32("X", out var x) || x < 0 || x > width ||
                !item.TryGetInt32("Y", out var y) || y < 0 || y > height ||
                !item.TryGetInt32("Width", out var boxWidth) || boxWidth < 4 || boxWidth > width - x ||
                !item.TryGetInt32("Height", out var boxHeight) || boxHeight < 4 || boxHeight > height - y ||
                !item.TryGetString("Translated", out var text) || string.IsNullOrWhiteSpace(text) ||
                text.Length > MaxTextLength) return false;
            parsed[index] = new OcrWireOverlayBox(x, y, boxWidth, boxHeight, text);
        }
        overlay = new OcrWireOverlay(version, kind, frameId, width, height, parsed);
        return true;
    }

    // MiniJson also serves numeric cheat payloads and intentionally accepts real
    // numbers. OCR fields are integers: reject coercion, duplicate keys, raw string
    // controls and excessive nesting before using that existing reader.
    private static bool HasSafeTokens(string json)
    {
        var levels = new List<HashSet<string>?>();
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (character == '"')
            {
                var start = index++;
                var closed = false;
                for (; index < json.Length; index++)
                {
                    if (json[index] < ' ') return false;
                    if (json[index] == '\\') { index++; if (index >= json.Length) return false; continue; }
                    if (json[index] == '"') { closed = true; break; }
                }
                if (!closed) return false;
                var next = index + 1;
                while (next < json.Length && IsWhitespace(json[next])) next++;
                if (next < json.Length && json[next] == ':')
                {
                    if (levels.Count == 0 || levels[levels.Count - 1] == null) return false;
                    var keyJson = "{\"k\":" + json.Substring(start, index - start + 1) + "}";
                    if (!MiniJson.TryParse(keyJson, out var key) || !key.TryGetString("k", out var name) ||
                        !levels[levels.Count - 1]!.Add(name)) return false;
                }
            }
            else if (character == '{' || character == '[')
            {
                if (levels.Count >= 16) return false;
                levels.Add(character == '{' ? new HashSet<string>(StringComparer.Ordinal) : null);
            }
            else if (character == '}' || character == ']')
            {
                if (levels.Count == 0) return false;
                levels.RemoveAt(levels.Count - 1);
            }
            else if (character == '-' || character >= '0' && character <= '9')
            {
                var start = index;
                if (character == '-') index++;
                if (index >= json.Length || json[index] < '0' || json[index] > '9') return false;
                var first = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index - first > 1 && json[first] == '0') return false;
                if (index < json.Length && !IsWhitespace(json[index]) && json[index] != ',' && json[index] != '}' && json[index] != ']') return false;
                if (!int.TryParse(json.Substring(start, index - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return false;
                index--;
            }
            else if (character == '+' || character < ' ' && !IsWhitespace(character)) return false;
        }
        return levels.Count == 0;
    }

    private static bool IsWhitespace(char value) => value == ' ' || value == '\r' || value == '\n' || value == '\t';
}

public sealed class OcrWireOverlay
{
    public int Version { get; }
    public string Kind { get; }
    public int FrameId { get; }
    public int Width { get; }
    public int Height { get; }
    public OcrWireOverlayBox[] Boxes { get; }
    internal OcrWireOverlay(int version, string kind, int frameId, int width, int height, OcrWireOverlayBox[] boxes)
    { Version = version; Kind = kind; FrameId = frameId; Width = width; Height = height; Boxes = boxes; }
}

public sealed class OcrWireOverlayBox
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public string Translated { get; }
    internal OcrWireOverlayBox(int x, int y, int width, int height, string translated)
    { X = x; Y = y; Width = width; Height = height; Translated = translated; }
}
