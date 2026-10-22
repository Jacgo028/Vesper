using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class FontReplacementPolicyTests
{
    [Fact]
    public void KeepsTheCurrentFontWhenItAlreadyHasTheText()
    {
        // 当前字体够用就不换 —— 不换就不会坏
        var decision = FontReplacementPolicy.Decide("继续游戏", currentFontHasText: true,
            candidateExists: true, candidateCharacterCount: 100, candidateHasText: true);

        Assert.Equal(FontReplacementPolicy.Decision.NotNeeded, decision);
        Assert.False(FontReplacementPolicy.CanReplace(decision));
    }

    [Fact]
    public void ReplacesWhenCurrentFontLacksGlyphsAndCandidateIsComplete()
    {
        var decision = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 2, candidateHasText: true);

        Assert.Equal(FontReplacementPolicy.Decision.Replace, decision);
        Assert.True(FontReplacementPolicy.CanReplace(decision));
    }

    [Fact]
    public void RefusesAnAssetWithNoCharacters()
    {
        // 这是实测踩过的坑：字符表为空的资源换上去，原本正常的文字会整个消失
        var decision = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 0, candidateHasText: false);

        Assert.Equal(FontReplacementPolicy.Decision.AssetHasNoCharacters, decision);
        Assert.False(FontReplacementPolicy.CanReplace(decision));
    }

    [Fact]
    public void RefusesWhenTheFontAssetCouldNotBeCreated()
    {
        // Unity 2022 那款游戏实测 CreateFontAsset 一律返回 null
        var decision = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: false, candidateCharacterCount: 0, candidateHasText: false);

        Assert.Equal(FontReplacementPolicy.Decision.AssetUnavailable, decision);
        Assert.False(FontReplacementPolicy.CanReplace(decision));
    }

    [Fact]
    public void RefusesWhenTheCandidateStillMissesGlyphs()
    {
        var decision = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 50, candidateHasText: false);

        Assert.Equal(FontReplacementPolicy.Decision.MissingGlyphs, decision);
        Assert.False(FontReplacementPolicy.CanReplace(decision));
    }

    /// <summary>
    /// 实测里最重要的一条：字体"看起来创建成功"（HasCharacters 返回 true）但实际缺字形时，
    /// 仍然必须判定为 MissingGlyphs —— 否则译文会被写进游戏，缺字就显示成方框（□）。
    /// </summary>
    [Fact]
    public void MissingGlyphCountOverridesTheOptimisticHasCharactersFlag()
    {
        var decision = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 50, candidateHasText: true,
            candidateMissingGlyphCount: 1);

        Assert.Equal(FontReplacementPolicy.Decision.MissingGlyphs, decision);
        Assert.False(FontReplacementPolicy.CanShowTranslation(decision));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void AnyMissingGlyphBlocksBothReplacementAndWriting(int missing)
    {
        var decision = FontReplacementPolicy.Decide("鸣谢这个字体缺字", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 900, candidateHasText: true,
            candidateMissingGlyphCount: missing);

        Assert.Equal(FontReplacementPolicy.Decision.MissingGlyphs, decision);
        Assert.False(FontReplacementPolicy.CanReplace(decision));
        Assert.False(FontReplacementPolicy.CanShowTranslation(decision));
    }

    [Fact]
    public void GlyphCompleteCandidateWithZeroMissingStillReplaces()
    {
        var decision = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 2, candidateHasText: true,
            candidateMissingGlyphCount: 0);

        Assert.Equal(FontReplacementPolicy.Decision.Replace, decision);
        Assert.True(FontReplacementPolicy.CanShowTranslation(decision));
    }

    /// <summary>图集没准备好（换上去什么都画不出来）同样要拦下。</summary>
    [Fact]
    public void RefusesWhenTheCandidateAtlasIsNotReady()
    {
        var decision = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 50, candidateHasText: true,
            candidateMissingGlyphCount: 0, candidateAtlasReady: false);

        Assert.Equal(FontReplacementPolicy.Decision.AssetAtlasUnavailable, decision);
        Assert.False(FontReplacementPolicy.CanReplace(decision));
        Assert.False(FontReplacementPolicy.CanShowTranslation(decision));
    }

    /// <summary>缺字形的说明要能写出"缺了几个、缺哪些字"，方便以后照着排查。</summary>
    [Fact]
    public void MissingGlyphDescriptionListsTheCharacters()
    {
        var described = FontReplacementPolicy.DescribeMissingGlyphs(new[] { '鸣', '谢' });

        Assert.Contains("2", described, StringComparison.Ordinal);
        Assert.Contains("鸣", described, StringComparison.Ordinal);
        Assert.Contains("谢", described, StringComparison.Ordinal);
        Assert.Equal("0 个", FontReplacementPolicy.DescribeMissingGlyphs(Array.Empty<char>()));
        Assert.Equal("0 个", FontReplacementPolicy.DescribeMissingGlyphs(null));
    }

    /// <summary>缺失字符很多时要截断，不能把整段译文都写进日志。</summary>
    [Fact]
    public void MissingGlyphDescriptionTruncatesLongLists()
    {
        var many = "鸣谢缺字测试一二三四五六七八九十".ToCharArray();
        var described = FontReplacementPolicy.DescribeMissingGlyphs(many, maxShown: 3);

        Assert.Contains("只列出前 3 个", described, StringComparison.Ordinal);
        Assert.True(described.Length < 60, "截断后的说明不该很长：" + described);
    }

    /// <summary>空格、换行、零宽字符不该算成缺字形，否则一个换行就会让整条译文被放弃。</summary>
    [Fact]
    public void WhitespaceAndZeroWidthCharactersDoNotRequireGlyphs()
    {
        Assert.False(FontReplacementPolicy.RequiresGlyph(' '));
        Assert.False(FontReplacementPolicy.RequiresGlyph('\n'));
        Assert.False(FontReplacementPolicy.RequiresGlyph('\t'));
        Assert.False(FontReplacementPolicy.RequiresGlyph('\u200B'));
        Assert.False(FontReplacementPolicy.RequiresGlyph('\uFEFF'));
        Assert.True(FontReplacementPolicy.RequiresGlyph('鸣'));
        Assert.True(FontReplacementPolicy.RequiresGlyph('A'));
        Assert.True(FontReplacementPolicy.RequiresGlyph('。'));
    }

    [Fact]
    public void RequiredGlyphsSkipsIgnorableCharactersAndDeduplicates()
    {
        var required = FontReplacementPolicy.RequiredGlyphs("鸣 谢\n鸣\u200B谢");

        Assert.Equal(new[] { '鸣', '谢' }, required);
        Assert.Empty(FontReplacementPolicy.RequiredGlyphs("  \n\t"));
        Assert.Empty(FontReplacementPolicy.RequiredGlyphs(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DoesNotReplaceForEmptyText(string? text)
    {
        var decision = FontReplacementPolicy.Decide(text, currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 10, candidateHasText: true);

        Assert.Equal(FontReplacementPolicy.Decision.NotNeeded, decision);
    }

    [Fact]
    public void OnlyTheReplaceDecisionIsAllowed()
    {
        foreach (var decision in Enum.GetValues<FontReplacementPolicy.Decision>())
            Assert.Equal(decision == FontReplacementPolicy.Decision.Replace,
                FontReplacementPolicy.CanReplace(decision));
    }

    [Theory]
    [InlineData(FontReplacementPolicy.Decision.NotNeeded, true)]
    [InlineData(FontReplacementPolicy.Decision.Replace, true)]
    [InlineData(FontReplacementPolicy.Decision.AssetUnavailable, false)]
    [InlineData(FontReplacementPolicy.Decision.AssetHasNoCharacters, false)]
    [InlineData(FontReplacementPolicy.Decision.AssetAtlasUnavailable, false)]
    [InlineData(FontReplacementPolicy.Decision.MissingGlyphs, false)]
    public void TranslationIsAllowedOnlyWhenAnUsableFontIsAvailable(
        FontReplacementPolicy.Decision decision, bool expected)
    {
        Assert.Equal(expected, FontReplacementPolicy.CanShowTranslation(decision));
    }

    /// <summary>
    /// **本版最重要的判定**：当前字体只覆盖译文的一部分时，不能写入译文。
    ///
    /// 真机实测（Sister Leticia，2026-09-25）：游戏自带字体 GenSenRounded-B SDF 的字符表有
    /// 7389 项，对 231 个常用汉字的并集能显示其中 120 个 —— 是**部分覆盖**。
    /// 原实现只看 TMP 的 HasCharacters（"字符表里有这个条目"就返回 true），
    /// 于是判定"当前字体够用"，把译文写进组件，缺的那 111 个字就显示成方框（□）。
    ///
    /// 所以这里明确三档：全覆盖 → 不用换；一个都没有 → 换字体；**只覆盖一部分 → 也要换字体**。
    /// 换不到就由 CanShowTranslation 拦下（保留原文，宁可不翻译也不给方框）。
    /// </summary>
    [Fact]
    public void PartialCoverageMustNotBeTreatedAsEnough()
    {
        // 当前字体一个字形都没有 → 需要换字体，换不到就不写
        var none = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: false,
            candidateExists: false, candidateCharacterCount: 0, candidateHasText: false);
        Assert.Equal(FontReplacementPolicy.Decision.AssetUnavailable, none);
        Assert.False(FontReplacementPolicy.CanShowTranslation(none));

        // 当前字体全覆盖 → 保持原样，可以写
        var all = FontReplacementPolicy.Decide("鸣谢", currentFontHasText: true,
            candidateExists: false, candidateCharacterCount: 0, candidateHasText: false);
        Assert.Equal(FontReplacementPolicy.Decision.NotNeeded, all);
        Assert.True(FontReplacementPolicy.CanShowTranslation(all));
    }

    /// <summary>
    /// 部分覆盖 + 候选字体也补不齐 → 必须保留原文，绝不能把缺字的译文写进去。
    /// </summary>
    [Fact]
    public void PartialCoverageWithAnIncompleteCandidateKeepsTheOriginal()
    {
        var decision = FontReplacementPolicy.Decide("继续游戏", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 900, candidateHasText: false,
            candidateMissingGlyphCount: 2);

        Assert.Equal(FontReplacementPolicy.Decision.MissingGlyphs, decision);
        Assert.False(FontReplacementPolicy.CanShowTranslation(decision));
        Assert.False(FontReplacementPolicy.CanReplace(decision));
    }

    /// <summary>
    /// 部分覆盖 + 候选字体能完整渲染 → 允许换字体并写入。
    /// 这是"有救"的那种情况，不能被上面那条连坐掉。
    /// </summary>
    [Fact]
    public void PartialCoverageWithACompleteCandidateMayBeReplaced()
    {
        var decision = FontReplacementPolicy.Decide("继续游戏", currentFontHasText: false,
            candidateExists: true, candidateCharacterCount: 900, candidateHasText: true,
            candidateMissingGlyphCount: 0);

        Assert.Equal(FontReplacementPolicy.Decision.Replace, decision);
        Assert.True(FontReplacementPolicy.CanReplace(decision));
        Assert.True(FontReplacementPolicy.CanShowTranslation(decision));
    }
}
