using System;
using System.Globalization;
using System.Reflection;

namespace UnityTranslator.Core;

/// <summary>Measures a reflected Unity text component without a compile-time Unity dependency.</summary>
public sealed class ReflectionTextLayout
{
    private float _originalSize;
    private float _appliedSize;
    private bool? _originalAutoSize;
    private bool _applied;
    private string? _lastAppliedText;

    public ReflectionTextLayout(object component)
    {
        _originalSize = UnityMemberReader.Read<float>(component, "fontSize") ?? 0f;
        _originalAutoSize = ReadAutoSize(component);
    }

    public float OriginalFontSize => _originalSize;
    public float AppliedFontSize => _appliedSize;
    public string FailureReason { get; private set; } = string.Empty;

    /// <summary>
    /// Applies text and a measured size as one transaction on Unity's main thread.
    /// Font, material, fallback tables, wrapping and the rectangle remain native.
    /// Unknown layouts and text below the readable floor retain the current text.
    /// </summary>
    public bool TryApply(object component, string translated)
    {
        var text = Property(component, "text");
        var size = Property(component, "fontSize");
        var auto = AutoSizeProperty(component);
        var textBefore = UnityMemberReader.ReadString(component, "text");
        var sizeBefore = UnityMemberReader.Read<float>(component, "fontSize") ?? 0f;
        var autoBefore = ReadAutoSize(component);
        FailureReason = string.Empty;
        if (text?.CanWrite != true || size?.CanWrite != true || textBefore is null
            || !Positive(sizeBefore) || string.IsNullOrEmpty(translated))
        {
            FailureReason = "text or size unavailable";
            return false;
        }

        // A game can change sizes on the same component (for example opening a
        // different panel). Only our last exact size belongs to this state.
        if (!_applied || !SameSize(sizeBefore, _appliedSize))
        {
            _originalSize = sizeBefore;
            // A new size does not itself relinquish ownership of our false
            // auto-size flag. Preserve its native baseline independently.
            if (!_applied || autoBefore != false) _originalAutoSize = autoBefore;
        }

        var committed = false;
        var sourceNeedsRestore = _applied && !string.Equals(textBefore, _lastAppliedText, StringComparison.Ordinal);
        try
        {
            var rect = UnityMemberReader.ReadRaw(UnityMemberReader.ReadRaw(component, "rectTransform"), "rect");
            var width = UnityMemberReader.Read<float>(rect, "width") ?? 0f;
            var height = UnityMemberReader.Read<float>(rect, "height") ?? 0f;
            var margin = UnityMemberReader.ReadRaw(component, "margin");
            width -= (UnityMemberReader.Read<float>(margin, "x") ?? 0f)
                     + (UnityMemberReader.Read<float>(margin, "z") ?? 0f);
            height -= (UnityMemberReader.Read<float>(margin, "y") ?? 0f)
                      + (UnityMemberReader.Read<float>(margin, "w") ?? 0f);
            if (!Positive(width) || !Positive(height))
            {
                FailureReason = "layout rectangle unavailable";
                return false;
            }

            var preferred = component.GetType().GetMethod("GetPreferredValues",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(float), typeof(float) }, null);
            var preferredWidth = Property(component, "preferredWidth");
            var preferredHeight = Property(component, "preferredHeight");
            var legacyWrap = UnityMemberReader.ReadRaw(component, "horizontalOverflow");
            var legacy = preferred is null && preferredWidth?.CanRead == true
                         && preferredHeight?.CanRead == true && legacyWrap is not null;
            if (preferred is null && !legacy)
            {
                FailureReason = "native measurement unavailable";
                return false;
            }
            if (autoBefore == true && auto?.CanWrite != true)
            {
                FailureReason = "native auto sizing cannot be controlled";
                return false;
            }
            if (auto?.CanWrite == true) auto.SetValue(component, false, null);
            if (legacy) text.SetValue(component, translated, null);

            var fitted = TranslationFontSizePolicy.Fit(_originalSize, candidate =>
            {
                SetSize(size, component, candidate);
                float measuredWidth;
                float measuredHeight;
                if (preferred is not null)
                {
                    var measured = preferred.Invoke(component, new object[] { translated, width, float.PositiveInfinity });
                    measuredWidth = UnityMemberReader.Read<float>(measured, "x") ?? float.NaN;
                    measuredHeight = UnityMemberReader.Read<float>(measured, "y") ?? float.NaN;
                }
                else
                {
                    measuredWidth = Convert.ToSingle(preferredWidth!.GetValue(component, null), CultureInfo.InvariantCulture);
                    measuredHeight = Convert.ToSingle(preferredHeight!.GetValue(component, null), CultureInfo.InvariantCulture);
                    // UI.Text.preferredWidth is deliberately unwrapped, whereas
                    // preferredHeight uses the current rect width. Native Wrap
                    // therefore establishes the horizontal bound itself.
                    if (string.Equals(legacyWrap!.ToString(), "Wrap", StringComparison.Ordinal))
                        measuredWidth = Math.Min(measuredWidth, width);
                }
                if (!Positive(measuredWidth) || !Positive(measuredHeight))
                    throw new InvalidOperationException("Native layout returned no usable dimensions.");
                return measuredWidth <= width + 0.05f && measuredHeight <= height + 0.05f;
            });
            if (fitted <= 0f)
            {
                FailureReason = "translation exceeds readable layout";
                return false;
            }
            SetSize(size, component, fitted);
            text.SetValue(component, translated, null);
            _appliedSize = UnityMemberReader.Read<float>(component, "fontSize") ?? fitted;
            _lastAppliedText = translated;
            _applied = true;
            committed = true;
            return true;
        }
        catch (Exception ex)
        {
            FailureReason = (ex.InnerException ?? ex).GetType().Name;
            return false;
        }
        finally
        {
            if (!committed)
            {
                // Rollback is best effort for destroyed Unity objects. One
                // unavailable component must not interrupt the polling loop.
                TrySet(text, component, textBefore);
                try { SetSize(size, component, sizeBefore); } catch (Exception) { }
                if (autoBefore.HasValue) TrySet(auto, component, autoBefore.Value);
                if (sourceNeedsRestore) Restore(component);
            }
        }
    }

    /// <summary>Releases only the size/auto-sizing changes still owned by us.</summary>
    public void Restore(object component)
    {
        if (!_applied) return;
        var current = UnityMemberReader.Read<float>(component, "fontSize") ?? 0f;
        if (SameSize(current, _appliedSize))
        {
            try { SetSize(Property(component, "fontSize"), component, _originalSize); } catch (Exception) { }
        }
        if (_originalAutoSize.HasValue && ReadAutoSize(component) == false)
            TrySet(AutoSizeProperty(component), component, _originalAutoSize.Value);
        _applied = false;
    }

    private static PropertyInfo? Property(object component, string name) =>
        component.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    private static PropertyInfo? AutoSizeProperty(object component) =>
        Property(component, "enableAutoSizing") ?? Property(component, "resizeTextForBestFit");

    private static bool? ReadAutoSize(object component) =>
        UnityMemberReader.Read<bool>(component, "enableAutoSizing")
        ?? UnityMemberReader.Read<bool>(component, "resizeTextForBestFit");

    private static void SetSize(PropertyInfo? property, object component, float size)
    {
        if (property?.CanWrite != true) throw new InvalidOperationException("Font size is not writable.");
        var actual = property.PropertyType == typeof(int) ? Math.Floor(size) : size;
        property.SetValue(component, Convert.ChangeType(actual, property.PropertyType, CultureInfo.InvariantCulture), null);
    }

    private static void TrySet(PropertyInfo? property, object component, object value)
    {
        try { if (property?.CanWrite == true) property.SetValue(component, value, null); }
        catch (Exception) { }
    }

    private static bool Positive(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool SameSize(float first, float second) => Math.Abs(first - second) < 0.02f;
}
