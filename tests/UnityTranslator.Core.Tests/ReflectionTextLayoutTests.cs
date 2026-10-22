using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class ReflectionTextLayoutTests
{
    // Removing native layout measurement, or always retaining the initial size,
    // must fail: ten glyphs at size 20 need 200 units, but the box is only 100.
    [Fact]
    public void Shrinks_using_native_measurement_without_replacing_font_or_material()
    {
        var target = new TmpText { fontSize = 20f };
        var font = target.font;
        var material = target.fontSharedMaterial;
        var fallback = target.fallbackFontAssetTable;
        var layout = new ReflectionTextLayout(target);

        Assert.True(layout.TryApply(target, "abcdefghij"));

        Assert.Equal("abcdefghij", target.text);
        Assert.InRange(target.fontSize, 9.9f, 10f);
        Assert.Same(font, target.font);
        Assert.Same(material, target.fontSharedMaterial);
        Assert.Same(fallback, target.fallbackFontAssetTable);
    }

    [Fact]
    public void Keeps_native_size_when_translation_already_fits()
    {
        var target = new TmpText { fontSize = 20f };
        var layout = new ReflectionTextLayout(target);

        Assert.True(layout.TryApply(target, "abc"));

        Assert.Equal(20f, target.fontSize);
    }

    [Fact]
    public void Reapply_measures_from_native_size_instead_of_shrinking_cumulatively()
    {
        var target = new TmpText { fontSize = 20f };
        var layout = new ReflectionTextLayout(target);
        Assert.True(layout.TryApply(target, "abcdefghij"));
        target.text = "source";
        Assert.True(layout.TryApply(target, "abcdefghij"));
        Assert.InRange(target.fontSize, 9.9f, 10f);

        target.text = "source";
        Assert.True(layout.TryApply(target, "abc"));
        Assert.Equal(20f, target.fontSize);
    }

    [Fact]
    public void Restores_owned_size_and_autosizing_when_component_is_reused()
    {
        var target = new TmpText { fontSize = 20f, enableAutoSizing = true };
        var layout = new ReflectionTextLayout(target);
        Assert.True(layout.TryApply(target, "abcdefghij"));
        Assert.False(target.enableAutoSizing);
        target.text = "a different game line";

        layout.Restore(target);

        Assert.Equal("a different game line", target.text);
        Assert.Equal(20f, target.fontSize);
        Assert.True(target.enableAutoSizing);
    }

    [Fact]
    public void Does_not_restore_over_a_size_change_made_by_the_game()
    {
        var target = new TmpText { fontSize = 20f };
        var layout = new ReflectionTextLayout(target);
        Assert.True(layout.TryApply(target, "abcdefghij"));
        target.fontSize = 30f;

        layout.Restore(target);

        Assert.Equal(30f, target.fontSize);
    }

    [Fact]
    public void Releases_owned_autosizing_even_when_the_game_has_changed_only_its_size()
    {
        var target = new TmpText { fontSize = 20f, enableAutoSizing = true };
        var layout = new ReflectionTextLayout(target);
        Assert.True(layout.TryApply(target, "abcdefghij"));
        target.fontSize = 30f;

        layout.Restore(target);

        Assert.Equal(30f, target.fontSize);
        Assert.True(target.enableAutoSizing);
    }

    [Fact]
    public void Keeps_games_autosizing_change_when_releasing_a_translated_component()
    {
        var target = new TmpText { fontSize = 20f, enableAutoSizing = false };
        var layout = new ReflectionTextLayout(target);
        Assert.True(layout.TryApply(target, "abcdefghij"));
        target.enableAutoSizing = true;

        layout.Restore(target);

        Assert.Equal(20f, target.fontSize);
        Assert.True(target.enableAutoSizing);
    }

    [Fact]
    public void Failed_reapply_restores_source_native_size_after_the_game_has_restored_source_text()
    {
        var target = new TmpText { fontSize = 20f, enableAutoSizing = true };
        var layout = new ReflectionTextLayout(target);
        Assert.True(layout.TryApply(target, "abcdefghij"));
        target.text = "source";
        target.rectTransform.rect.width = 1f;

        Assert.False(layout.TryApply(target, "abcdefghij"));

        Assert.Equal("source", target.text);
        Assert.Equal(20f, target.fontSize);
        Assert.True(target.enableAutoSizing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preserves_original_text_and_style_when_text_cannot_fit_or_measurement_throws(bool throws)
    {
        var target = new TmpText { fontSize = 20f, enableAutoSizing = true, ThrowMeasurement = throws };
        var layout = new ReflectionTextLayout(target);

        Assert.False(layout.TryApply(target, new string('x', 1000)));

        Assert.Equal("source", target.text);
        Assert.Equal(20f, target.fontSize);
        Assert.True(target.enableAutoSizing);
    }

    [Fact]
    public void Rejects_component_without_a_native_layout_measurement_api()
    {
        var target = new UnknownText();
        var layout = new ReflectionTextLayout(target);

        Assert.False(layout.TryApply(target, "translated"));

        Assert.Equal("source", target.text);
        Assert.Equal(20f, target.fontSize);
    }

    [Fact]
    public void Honors_rect_margins_instead_of_fitting_against_outer_width()
    {
        var target = new TmpText { fontSize = 20f, margin = new Vector4 { x = 10f, z = 10f } };
        var layout = new ReflectionTextLayout(target);

        Assert.True(layout.TryApply(target, "abcdefghij"));

        Assert.InRange(target.fontSize, 8f, 8.05f);
    }

    [Fact]
    public void Fits_legacy_wrapped_text_using_native_preferred_height_and_integer_size()
    {
        var target = new LegacyText();
        var layout = new ReflectionTextLayout(target);

        Assert.True(layout.TryApply(target, "abcdefghij"));

        Assert.Equal("abcdefghij", target.text);
        Assert.Equal(15, target.fontSize);
    }

    // Unity is unavailable in the net8 Core test host. These objects expose the
    // public shapes read at runtime; the production helper owns the reflection,
    // candidate search, writes, restoration and exception handling under test.
    public class TmpText
    {
        public string text { get; set; } = "source";
        public float fontSize { get; set; } = 20f;
        public bool enableAutoSizing { get; set; }
        public object font { get; } = new();
        public object fontSharedMaterial { get; } = new();
        public object fallbackFontAssetTable { get; } = new();
        public RectTransform rectTransform { get; } = new();
        public Vector4 margin { get; set; } = new();
        public bool ThrowMeasurement { get; set; }
        public Vector2 GetPreferredValues(string value, float width, float height)
        {
            if (ThrowMeasurement) throw new InvalidOperationException("font rebuilding");
            return new Vector2 { x = value.Length * fontSize, y = fontSize };
        }
    }

    public sealed class UnknownText
    {
        public string text { get; set; } = "source";
        public float fontSize { get; set; } = 20f;
        public RectTransform rectTransform { get; } = new();
    }

    public sealed class LegacyText
    {
        public string text { get; set; } = "source";
        public int fontSize { get; set; } = 20;
        public bool resizeTextForBestFit { get; set; }
        public string horizontalOverflow => "Wrap";
        public RectTransform rectTransform { get; } = new();
        public float preferredWidth => text.Length * fontSize;
        public float preferredHeight => (float)Math.Ceiling(preferredWidth / 100f) * fontSize;
    }

    public sealed class RectTransform { public Rect rect { get; } = new(); }
    public sealed class Rect { public float width = 100f; public float height = 30f; }
    public sealed class Vector2 { public float x; public float y; }
    public sealed class Vector4 { public float x; public float y; public float z; public float w; }
}
