using UnityTranslator.Core;

namespace UnityTranslator.App;

public sealed class OcrTranslationService(IOcrRecognizer recognizer, TranslationCoordinator coordinator)
{
    private readonly OcrStabilityTracker _stability = new();

    public async Task<IReadOnlyList<OcrOverlayItem>> ProcessAsync(OcrFrame frame, CancellationToken cancellationToken)
    {
        var detections = await recognizer.RecognizeAsync(frame.ImagePath, cancellationToken);
        var candidates = OcrRegionFilter.Select(detections, frame.KnownUiBoxes, 0.8f);
        var stable = _stability.Observe(candidates);
        var overlays = new List<OcrOverlayItem>();
        var index = 0;
        foreach (var box in stable)
        {
            var language = LanguageOf(box.Text);
            if (language is null) continue;
            if (index >= 6) break;
            var request = new TranslateRequest($"ocr-{frame.FrameId}-{index}", box.Text, language,
                -1000000 - index, frame.FrameId);
            index++;
            var reply = await coordinator.TranslateAsync(request, cancellationToken);
            if (reply is null) continue;
            overlays.Add(new OcrOverlayItem(box.X, box.Y, box.Width, box.Height, reply.Translated));
        }
        return overlays;
    }

    private static string? LanguageOf(string text)
    {
        if (text.Any(c => c >= '\u3040' && c <= '\u30ff')) return "ja";
        if (text.Any(c => c >= '\u4e00' && c <= '\u9fff')) return null;
        if (text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) return "en";
        return null;
    }
}
