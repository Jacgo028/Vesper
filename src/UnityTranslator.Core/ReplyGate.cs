using System.Text;
using System.Text.Json;

namespace UnityTranslator.Core;

public sealed record OcrFrame(int FrameId, string ImagePath, int Width, int Height, IReadOnlyList<OcrRect> KnownUiBoxes);
public sealed record OcrOverlayItem(int X, int Y, int Width, int Height, string Translated);

public static class ReplyGate
{
    public static bool Accept(long componentId, int generation, TranslateReply reply) =>
        reply.ComponentId == componentId && reply.Generation == generation &&
        !string.IsNullOrWhiteSpace(reply.Translated);
}

public static class PipeProtocol
{
    public const int MaxCharacters = 65536;

    public static bool TryReadRequest(string line, out TranslateRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(line) || Encoding.UTF8.GetByteCount(line) > MaxCharacters) return false;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("Version").GetInt32() != 1) return false;
            var id = root.GetProperty("RequestId").GetString();
            var source = root.GetProperty("Source").GetString();
            var language = root.GetProperty("SourceLanguage").GetString();
            var component = root.GetProperty("ComponentId").GetInt64();
            var generation = root.GetProperty("Generation").GetInt32();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(source) ||
                language is not ("ja" or "en") || generation < 1) return false;
            request = new TranslateRequest(id, source, language, component, generation);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static string WriteReply(TranslateReply reply) => JsonSerializer.Serialize(new
    {
        Version = 1, reply.RequestId, reply.Translated, reply.ComponentId, reply.Generation
    });

    public static bool TryReadOcrFrame(string line, out OcrFrame? frame)
    {
        frame = null;
        if (string.IsNullOrWhiteSpace(line) || Encoding.UTF8.GetByteCount(line) > MaxCharacters) return false;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("Version").GetInt32() != 1 || root.GetProperty("Kind").GetString() != "OcrFrame") return false;
            var id = root.GetProperty("FrameId").GetInt32();
            var path = root.GetProperty("ImagePath").GetString();
            var width = root.GetProperty("Width").GetInt32();
            var height = root.GetProperty("Height").GetInt32();
            if (id < 1 || string.IsNullOrWhiteSpace(path) || width is < 1 or > 16384 || height is < 1 or > 16384) return false;
            var rects = new List<OcrRect>();
            if (root.TryGetProperty("KnownUiBoxes", out var boxes) && boxes.ValueKind == JsonValueKind.Array)
            {
                if (boxes.GetArrayLength() > 500) return false;
                foreach (var box in boxes.EnumerateArray())
                {
                    var rect = new OcrRect(box.GetProperty("X").GetInt32(), box.GetProperty("Y").GetInt32(),
                        box.GetProperty("Width").GetInt32(), box.GetProperty("Height").GetInt32());
                    if (rect.Width < 0 || rect.Height < 0) return false;
                    rects.Add(rect);
                }
            }
            frame = new OcrFrame(id, path, width, height, rects);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static string WriteOcrOverlay(OcrFrame frame, IReadOnlyList<OcrOverlayItem> boxes) =>
        JsonSerializer.Serialize(new { Version = 1, Kind = "OcrOverlay", FrameId = frame.FrameId,
            frame.Width, frame.Height, Boxes = boxes });
}
