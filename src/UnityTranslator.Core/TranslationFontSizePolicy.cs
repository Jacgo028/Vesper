using System;

namespace UnityTranslator.Core;

/// <summary>
/// Keep a game's own text size when the translation fits its rectangle, and
/// shrink only when the actual font/layout measurement requires it.
/// </summary>
public static class TranslationFontSizePolicy
{
    public static float Fit(float originalSize, Func<float, bool> fits)
    {
        if (originalSize <= 0f || float.IsNaN(originalSize) || float.IsInfinity(originalSize))
            return 0f;
        if (fits is null) throw new ArgumentNullException(nameof(fits));
        if (fits(originalSize)) return originalSize;

        // Below this floor the translated text stops being usable. Let the
        // caller retain the original text instead of drawing over the UI.
        var minimum = Math.Min(originalSize, Math.Max(8f, originalSize * 0.4f));
        if (!fits(minimum)) return 0f;

        var lower = minimum;
        var upper = originalSize;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var middle = (lower + upper) * 0.5f;
            if (fits(middle)) lower = middle;
            else upper = middle;
        }
        return (float)Math.Floor(lower * 100f) / 100f;
    }
}
