using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 「渲染点大小 → 度量基准点大小」换算规则的离线回归测试。
///
/// 守的是实测到的"菜单译文与原文互相叠压"：按 18pt 装载时，
/// FaceInfo 与所有字形度量都只有游戏字体（实测 48pt）的 0.375 倍，
/// TMP 用它们做行高/对齐/字号换算，文字就被放大错位、与同行内容叠压。
/// </summary>
public sealed class FontMetricsScaleTests
{
    /// <summary>实测数据：Mad Island 的游戏字体是 48pt，我们按 18pt 装载。</summary>
    private const float MadIslandGamePointSize = 48f;
    private const float OurRenderedPointSize = 18f;

    [Fact]
    public void ScaleIsDerivedFromTheReferencePointSize()
    {
        Assert.Equal(48f, FontAssetPipeline.ReferencePointSize);
        Assert.Equal(48f / 18f, FontAssetPipeline.MetricsScale(OurRenderedPointSize), 4);
    }

    /// <summary>
    /// 回归保护：比例必须正好等于"基准 / 渲染"。实测字形度量
    /// `advance=11.25`（18pt）换算到 48pt 基准应为 30.0。
    /// </summary>
    [Fact]
    public void ScalesMeasuredGlyphAdvanceToTheReference()
    {
        // 实测值：U+0021 '!' 在 18pt 下 advance=11.25
        Assert.Equal(30.0, FontAssetPipeline.ScaleMetric(11.25, OurRenderedPointSize), 3);

        // 实测值：ascentLine=19.05（18pt）→ 48pt 基准约 50.8
        Assert.Equal(50.8, FontAssetPipeline.ScaleMetric(19.05, OurRenderedPointSize), 2);

        // 实测值：lineHeight=23.76（18pt）→ 48pt 基准约 63.36
        Assert.Equal(63.36, FontAssetPipeline.ScaleMetric(23.76, OurRenderedPointSize), 2);
    }

    /// <summary>按基准点大小装载时不该做任何缩放（比例恰为 1）。</summary>
    [Fact]
    public void NoScalingWhenAlreadyAtTheReference()
    {
        Assert.Equal(1f, FontAssetPipeline.MetricsScale(FontAssetPipeline.ReferencePointSize));
        Assert.False(FontAssetPipeline.NeedsMetricsScaling(FontAssetPipeline.ReferencePointSize));
        Assert.False(FontAssetPipeline.NeedsGlyphMetricsScaling(FontAssetPipeline.ReferencePointSize));
    }

    [Theory]
    [InlineData(18f, true)]
    [InlineData(24f, true)]
    [InlineData(36f, true)]
    [InlineData(45f, true)]      // 48/45 = 1.067，差 6.7%，需要缩放
    [InlineData(47.99f, false)]  // 48/47.99 = 1.0002，差 0.02%，在 1‰ 容差内，不必缩放
    [InlineData(48f, false)]
    [InlineData(48.01f, false)]
    [InlineData(90f, true)]
    public void DecidesWhenScalingIsNeeded(float rendered, bool expected)
    {
        Assert.Equal(expected, FontAssetPipeline.NeedsMetricsScaling(rendered));
    }

    /// <summary>
    /// 非法输入不能把度量清零或变成 NaN —— 那会让整份字体排版彻底错乱，
    /// 比"没缩放"严重得多，所以一律退回 1（不缩放）。
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-18f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void FallsBackToOneForInvalidInput(float rendered)
    {
        Assert.Equal(1f, FontAssetPipeline.MetricsScale(rendered));
        Assert.False(FontAssetPipeline.NeedsMetricsScaling(rendered));
    }

    /// <summary>字形度量与面度量必须用同一个比例，否则两者不一致会继续错位。</summary>
    [Fact]
    public void GlyphAndFaceScalingAgree()
    {
        foreach (var rendered in new[] { 12f, 18f, 24f, 36f, 48f, 64f })
        {
            Assert.Equal(
                FontAssetPipeline.NeedsMetricsScaling(rendered),
                FontAssetPipeline.NeedsGlyphMetricsScaling(rendered));
            Assert.Equal(
                FontAssetPipeline.ScaleMetric(100.0, rendered),
                100.0 * FontAssetPipeline.MetricsScale(rendered),
                6);
        }
    }

    /// <summary>
    /// 缩放必须单调：渲染点大小越小，放大倍数越大（保证同一基准）。
    /// </summary>
    [Fact]
    public void ScaleIsMonotonicAcrossPointSizes()
    {
        var previous = 0f;
        foreach (var rendered in new[] { 48f, 36f, 24f, 18f, 12f })
        {
            var scale = FontAssetPipeline.MetricsScale(rendered);
            Assert.True(scale >= previous, $"渲染 {rendered}pt 的比例 {scale} 小于更小字号的比例 {previous}");
            previous = scale;
        }
        Assert.Equal(1f, FontAssetPipeline.MetricsScale(48f));
        Assert.Equal(4f, FontAssetPipeline.MetricsScale(12f));
    }

    /// <summary>
    /// 各字号缩放后必须落在同一基准上 —— 这是"排版与游戏 UI 对齐"的数学表述。
    /// </summary>
    [Fact]
    public void AllRenderedSizesNormalizeToTheSameReference()
    {
        foreach (var rendered in new[] { 12f, 18f, 24f, 36f })
        {
            // 取 18pt 下实测的 lineHeight=23.76 作为"名义 18pt 值"，
            // 按各字号的真实比例换算回来，结果都应是同一个基准值。
            var atEighteen = 23.76;
            var scaled = FontAssetPipeline.ScaleMetric(atEighteen * (rendered / 18f), rendered);
            Assert.Equal(23.76 * (FontAssetPipeline.ReferencePointSize / 18f), scaled, 3);
        }
    }
}
