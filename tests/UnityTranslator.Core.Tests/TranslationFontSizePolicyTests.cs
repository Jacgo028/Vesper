using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class TranslationFontSizePolicyTests
{
    [Fact]
    public void ExistingSizeIsKeptWhenTranslationFits()
    {
        Assert.Equal(30f, TranslationFontSizePolicy.Fit(30f, size => size <= 30f));
    }

    [Fact]
    public void LongTranslationShrinksOnlyEnoughToFit()
    {
        var size = TranslationFontSizePolicy.Fit(30f, candidate => candidate <= 20f);

        Assert.InRange(size, 19.8f, 20f);
    }

    [Fact]
    public void TranslationTooLongToRemainReadableIsRejected()
    {
        Assert.Equal(0f, TranslationFontSizePolicy.Fit(30f, _ => false));
    }

    [Fact]
    public void InvalidOriginalSizeIsRejectedWithoutMeasuring()
    {
        Assert.Equal(0f, TranslationFontSizePolicy.Fit(0f, _ => throw new Exception("measured")));
    }
}
