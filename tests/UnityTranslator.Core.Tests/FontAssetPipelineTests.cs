using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 字体图集 SDF 契约的离线回归测试。
///
/// 这组用例守的是**实测踩过的最大一个坑**：字形以位图模式画进图集、
/// 再挂到 TMP 的 SDF 着色器上，画面表现是"中文空心方框 + 拉丁字母也变框"
/// （Mad Island / Kurea / Leticia 三款游戏一致）。
///
/// 根因是选了 <c>Enum.GetValues(...).GetValue(0)</c>（枚举第 0 个），
/// 而这个枚举的顺序随 Unity 版本变化。下面第 1、2 条用例就是把这个根因钉死。
/// </summary>
public sealed class FontAssetPipelineTests
{
    // ───────────────────────── 渲染模式选择：不许用序号 ─────────────────────────

    /// <summary>
    /// 回归保护：Unity 2021 的 <c>GlyphRenderMode</c> 第 0 个成员是 <c>SMOOTH_HINTED</c>（位图）。
    /// 旧实现取第 0 个就会拿到它 —— 必须仍然选中 SDFAA。
    /// </summary>
    [Fact]
    public void PicksSdfaaEvenWhenABitmapModeIsFirstInTheEnum()
    {
        string[] unity2021Order =
        {
            "SMOOTH_HINTED", "SMOOTH", "COLOR_HINTED", "COLOR",
            "RASTER_HINTED", "RASTER", "SDF", "SDF8", "SDF16", "SDF32",
            "SDFAA_HINTED", "SDFAA",
        };

        Assert.Equal("SDFAA", FontAssetPipeline.SelectGlyphRenderMode(unity2021Order));
    }

    /// <summary>回归保护：Unity 6 的枚举第 0 个成员是 <c>DEFAULT</c>，同样不能选它。</summary>
    [Fact]
    public void PicksSdfaaEvenWhenDefaultIsFirstInTheEnum()
    {
        string[] unity6Order =
        {
            "DEFAULT", "SMOOTH_HINTED", "SMOOTH", "COLOR_HINTED", "COLOR",
            "RASTER_HINTED", "RASTER", "SDF", "SDF8", "SDF16", "SDF32",
            "SDFAA_HINTED", "SDFAA",
        };

        Assert.Equal("SDFAA", FontAssetPipeline.SelectGlyphRenderMode(unity6Order));
    }

    [Fact]
    public void FallsBackToAnySdfAntialiasedModeWhenSdfaaIsMissing()
    {
        Assert.Equal("SDFAA_HINTED",
            FontAssetPipeline.SelectGlyphRenderMode(new[] { "SMOOTH", "RASTER", "SDFAA_HINTED" }));
    }

    [Fact]
    public void RefusesToPickAnythingWhenNoSdfModeExists()
    {
        // 全是没有 SDF 的位图模式 → 必须返回 null，让装配明确失败，
        // 绝不能"随便挑一个"装出满屏方框
        Assert.Null(FontAssetPipeline.SelectGlyphRenderMode(
            new[] { "SMOOTH_HINTED", "SMOOTH", "RASTER", "COLOR", "DEFAULT" }));
        Assert.Null(FontAssetPipeline.SelectGlyphRenderMode(Array.Empty<string>()));
        Assert.Null(FontAssetPipeline.SelectGlyphRenderMode(null!));
    }

    // ───────────────────────── 位图模式识别 ─────────────────────────

    [Theory]
    [InlineData("SMOOTH_HINTED", true)]
    [InlineData("SMOOTH", true)]
    [InlineData("RASTER", true)]
    [InlineData("RASTER_HINTED", true)]
    [InlineData("COLOR", true)]
    [InlineData("DEFAULT", true)]
    [InlineData("SDF", false)]
    [InlineData("SDF8", false)]
    [InlineData("SDF16", false)]
    [InlineData("SDF32", false)]
    [InlineData("SDFAA", false)]
    [InlineData("SDFAA_HINTED", false)]
    public void ClassifiesBitmapVersusSdfModes(string name, bool isBitmap)
    {
        Assert.Equal(isBitmap, FontAssetPipeline.IsBitmapRenderMode(name));
    }

    // ───────────────────────── padding 与 _GradientScale 必须配套 ─────────────────────────

    /// <summary>
    /// <c>_GradientScale</c> 只能是 <c>padding + 2.5</c>（TMP 自己的 ShaderUtilities 公式）。
    /// </summary>
    [Fact]
    public void GradientScaleFollowsTheTmpFormula()
    {
        Assert.Equal(FontAssetPipeline.Padding + 2.5f, FontAssetPipeline.GradientScale);
        Assert.Equal(8.5f, FontAssetPipeline.GradientScale);
        Assert.Equal(6, FontAssetPipeline.Padding);
    }

    /// <summary>
    /// 回归保护：padding 曾经被压到 2 想多塞字形，结果**连拉丁字母都变成空心框**。
    /// 这里把"padding 太小 → 距离场解不出轮廓"这件事钉死。
    /// </summary>
    [Fact]
    public void PaddingTooSmallIsRejectedByTheContract()
    {
        // 当前常量必须高于实测会出方框的危险值
        Assert.True(FontAssetPipeline.Padding >= 4,
            "padding 小于 4 会让 SDF 距离场展开不足，画面上出现空心方框（实测）");
        // 也要明显小于点大小，否则单字占位过大、图集装不下常用集
        Assert.True(FontAssetPipeline.Padding <= FontAssetPipeline.PointSize / 3,
            $"padding {FontAssetPipeline.Padding} 相对点大小 {FontAssetPipeline.PointSize} 过大，"
            + "单字占位会暴涨，一张图集装不下常用集");
    }

    [Fact]
    public void AtlasBigEnoughToHoldTheWholeCommonSetInOneTexture()
    {
        // ★ 这是本轮最重要的回归：**必须一张图集装完整套常用字**。
        //   实测 36pt 时一张 4096² 只装下 1682 个字形，而常用集有 4215 个 ——
        //   缺的字在画面上就是方框；而"开第二张图集"会让 UnityPlayer 原生崩溃（0xc0000005）。
        const int commonSet = 4215;

        Assert.True(FontAssetPipeline.FitsInOneAtlas(commonSet),
            $"点大小 {FontAssetPipeline.PointSize} 下一张图集只能装 "
            + $"{FontAssetPipeline.EstimatedAtlasCapacity(FontAssetPipeline.PointSize)} 个字形，"
            + $"装不下 {commonSet} 个常用字 —— 缺的字会显示成方框");

        // 只允许一张图集
        Assert.Equal(1, FontAssetPipeline.MaxAtlasCount);
    }

    /// <summary>
    /// 回归保护：**渲染点大小必须等于度量基准**。
    ///
    /// 第 4 轮实验证明"只放大度量"行不通：按 18pt 画图集却声明 48pt 度量，
    /// 等于要求 TMP 从 1/2.667 大小的像素块里画出 2.667 倍的字 ——
    /// 画面变成巨大且糊成一片的字形（`zoom-pad-glyph-02.png`）。
    /// 图集矩形锁死了字形能渲染多大。
    /// </summary>
    [Fact]
    public void RenderPointSizeMustEqualTheMetricReference()
    {
        Assert.Equal(FontAssetPipeline.ReferencePointSize, FontAssetPipeline.PointSize);
        Assert.False(FontAssetPipeline.NeedsMetricsScaling(FontAssetPipeline.PointSize),
            "渲染点大小与度量基准一致时不该做任何度量缩放");
    }

    /// <summary>按实测装箱效率折算容量：必须装得下常用集。</summary>
    [Fact]
    public void CapacityUsesTheMeasuredPackingEfficiency()
    {
        Assert.Equal(0.819, FontAssetPipeline.PackingEfficiency, 3);

        // 实测：18pt 平均 925px² vs 理论 cell 1129px² = 0.819
        Assert.Equal(0.819, 925.0 / Math.Pow(18 * 1.2 + 12, 2), 3);
    }

    /// <summary>
    /// 回归保护：**实测容量证据**。48pt 下 4096² 装不下常用集，8192² 可以 ——
    /// 这就是选 8192² 的全部理由（此前"36pt 只装 1682 字"是逐字形打包时代的错误数据）。
    /// </summary>
    [Fact]
    public void CapacityEvidenceJustifiesTheAtlasSize()
    {
        const int commonSet = 4215;

        // 48pt 下 4096² 装不下
        Assert.True(FontAssetPipeline.EstimatedAtlasCapacity(48, 6, 4096) < commonSet,
            "48pt + 4096² 应被证明装不下常用集，否则不需要 8192²");

        // 48pt 下 8192² 装得下，且有余量
        Assert.True(FontAssetPipeline.FitsInOneAtlas(commonSet),
            $"8192² 在 {FontAssetPipeline.PointSize}pt 下装不下 {commonSet} 个常用字");

        // 实测：8192² 上 4214 个字形的平均面积 3405px²，估算容量 19707
        Assert.InRange(FontAssetPipeline.EstimatedAtlasCapacity(48), 10000, 30000);
    }

    // ───────────────────────── 材质必需属性 ─────────────────────────

    [Fact]
    public void RequiresEveryTmpSdfMaterialProperty()
    {
        var required = FontAssetPipeline.RequiredMaterialProperties;

        foreach (var name in new[]
                 {
                     "_MainTex", "_GradientScale", "_TextureWidth", "_TextureHeight",
                     "_ScaleRatioA", "_ScaleRatioB", "_ScaleRatioC",
                     "_FaceColor", "_FaceDilate", "_OutlineWidth",
                 })
        {
            Assert.Contains(name, required);
        }
    }

    [Fact]
    public void ReportsMissingMaterialProperties()
    {
        // 只接了 _MainTex 的自建材质正是"空心框"的成因之一
        var missing = FontAssetPipeline.MissingMaterialProperties(
            name => name == "_MainTex" || name == "_TextureWidth");

        Assert.Contains("_GradientScale", missing);
        Assert.Contains("_ScaleRatioA", missing);
        Assert.DoesNotContain("_MainTex", missing);

        // 全部齐备时不该报缺
        Assert.Empty(FontAssetPipeline.MissingMaterialProperties(_ => true));

        // 传 null 时按"全缺"处理，不能假装通过
        Assert.Equal(FontAssetPipeline.RequiredMaterialProperties,
            FontAssetPipeline.MissingMaterialProperties(null!));
    }

    // ───────────────────────── SDF 解码：证明"方框"是怎么来的 ─────────────────────────

    /// <summary>
    /// 正确的 SDF 图集采样：内部 1.0、外部 0.0、边界处穿越 0.5。
    /// 用 TMP 的着色器公式解码后，应该同时存在不透明与透明区域（即有轮廓）。
    /// </summary>
    [Fact]
    public void ProperSdfSamplesDecodeIntoAGlyphShape()
    {
        var samples = new byte[]
        {
            0, 0, 0, // 字形外
            0, 128, 0, // 边界（0.5 附近）
            255, 255, 255, // 字形内
        };

        Assert.False(FontAssetPipeline.LooksLikeBitmap(samples));

        var opaque = FontAssetPipeline.OpaqueRatio(samples, FontAssetPipeline.GradientScale);
        Assert.True(opaque > 0.0 && opaque < 1.0,
            $"正常 SDF 应该解出「部分覆盖」的轮廓，实测不透明占比 {opaque}");
    }

    /// <summary>
    /// 回归保护：位图数据（只有 0 与 255，没有中间过渡）挂到 TMP 的 SDF 着色器上，
    /// 解码结果只有"全透明"或"全不透明"两种极端 —— 这就是画面上的实心块 / 空心方框。
    /// </summary>
    [Fact]
    public void BitmapSamplesDecodeIntoASolidBlockUnderTheSdfShader()
    {
        var ring = new byte[]
        {
            255, 255, 255, 255, 255,
            255, 0, 0, 0, 255,
            255, 0, 0, 0, 255,
            255, 255, 255, 255, 255,
        };

        // 位图特征：没有任何中间值
        Assert.True(FontAssetPipeline.LooksLikeBitmap(ring));

        // 位图里没有 0.5 附近的采样 → 轮廓边界无从解出，只剩实心/空心两种极端
        var opaque = FontAssetPipeline.OpaqueRatio(ring, FontAssetPipeline.GradientScale);
        Assert.True(opaque > 0.5,
            $"位图数据被 SDF 着色器解码成整块实心（不透明占比 {opaque}）");

        // 反过来看：全 0 的"空心"部分在正确的 SDF 里本该是边界过渡
        Assert.Equal(0.0, FontAssetPipeline.OpaqueRatio(new byte[] { 0, 0, 0, 0 }, FontAssetPipeline.GradientScale));
        Assert.Equal(1.0, FontAssetPipeline.OpaqueRatio(new byte[] { 255, 255 }, FontAssetPipeline.GradientScale));
    }

    /// <summary>
    /// SDF 的编码约定：图集值 0.5 就是"轮廓线上"。这条性质一旦被破坏，
    /// _GradientScale 再怎么调也解不出正确的字。
    /// </summary>
    [Fact]
    public void HalfValueIsTheGlyphOutlineBoundary()
    {
        Assert.Equal(0.0, FontAssetPipeline.DecodeSdfAlpha(127, FontAssetPipeline.GradientScale));
        Assert.Equal(1.0, FontAssetPipeline.DecodeSdfAlpha(128, FontAssetPipeline.GradientScale));
    }

    [Fact]
    public void EmptySamplesAreHandledWithoutThrowing()
    {
        Assert.False(FontAssetPipeline.LooksLikeBitmap(Array.Empty<byte>()));
        Assert.False(FontAssetPipeline.LooksLikeBitmap(null!));
        Assert.Equal(0.0, FontAssetPipeline.OpaqueRatio(Array.Empty<byte>(), FontAssetPipeline.GradientScale));
        Assert.Equal(0.0, FontAssetPipeline.OpaqueRatio(null!, FontAssetPipeline.GradientScale));
    }
}
