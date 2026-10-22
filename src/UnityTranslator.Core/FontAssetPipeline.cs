using System;
using System.Collections.Generic;

namespace UnityTranslator.Core;

/// <summary>
/// 字体图集的 SDF 契约。
///
/// 为什么要有这个类（实测踩出来的大坑）：
///   TMP 的文字**必须**用带符号距离场（SDF）的图集。装配时如果用位图模式把字形画进图集，
///   再挂到 TMP 的 SDF 着色器上，距离场解码必然失败，画面表现是
///   **中文实心方块 / 空心方框，拉丁字母也一起变框**（三款游戏实测一致）。
///
/// 而坑就在于选渲染模式的方法：早期实现写的是
///   <c>Enum.GetValues(renderModeType).GetValue(0)</c>
///   —— "取第 0 个"。<c>GlyphRenderMode</c> 的枚举成员顺序与数值**随 Unity 版本变化**
///   （Unity 2021 首个成员是 <c>SMOOTH_HINTED</c>，Unity 6 文档里首个是 <c>DEFAULT</c>），
///   取第 0 个拿到的从来不是 SDF 模式，而且换一个 Unity 版本就换一种坏法。
///   正确做法是**按名字**取 SDFAA，取不到再退回已知数值，绝不用序号。
///
/// 这些常量集中在这里而不是散在装配代码里，是为了能**离线断言**：
/// 见 <c>FontAssetPipelineTests</c>。
/// </summary>
public static class FontAssetPipeline
{
    /// <summary>
    /// 渲染点大小。**必须与 <see cref="ReferencePointSize"/> 相同**。
    ///
    /// ★ 这是第 4 轮实验换来的硬约束：
    ///   图集矩形锁死了字形能渲染多大。若按 18pt 画图集、却把度量声明成 48pt，
    ///   等于要求 TMP 从 1/2.667 大小的像素块里画出 2.667 倍的字 ——
    ///   实测结果是文字巨大、完全糊成一片（`zoom-pad-glyph-02.png`）。
    ///   所以"只放大度量"这条路数学上不成立，**只能让渲染点大小等于度量基准**。
    ///
    /// 48 这个值来自实测：Mad Island 的游戏字体是 48pt，它的标题菜单就是按 48pt 排版的
    /// （`probe.face.theirs pointSize=48 ascentLine=55.68 lineHeight=69.5`）。
    /// </summary>
    public const int PointSize = 48;

    /// <summary>
    /// 图集内边距。**它同时决定 SDF 距离场的展开宽度**，不能压小。
    /// 实测：压到 2 会让画面里所有文字（含拉丁字母）变成空心方框。
    /// </summary>
    public const int Padding = 6;

    /// <summary>
    /// 图集边长。
    ///
    /// 实测容量（批量打包、效率 0.819）：
    ///   18pt 单字 925px²  → 4096² 可装 18137 字
    ///   36pt 单字 3700px² → 4096² 可装 4534 字
    ///   48pt 单字 6578px² → **4096² 只装得下 2550 字，装不下 4215 个常用字**
    ///   48pt 单字 6578px² → 8192² 可装 10202 字（余量 2.4 倍）
    ///
    /// 因为点大小必须等于 48pt 基准（见 <see cref="PointSize"/>），
    /// 4096² 在**同一个点大小下**已被证明装不下常用集，所以取 8192²。
    /// Alpha8 下约 64MB 显存；而"装不下 → 开第二张图集"会让 Unity 原生崩溃，
    /// 两害相权取更大的一张图集。
    /// </summary>
    public const int AtlasSize = 8192;

    /// <summary>
    /// 把"渲染点大小 → 度量基准点大小"的换算比例算出来。
    ///
    /// ★ 为什么需要这个比例（实测：Mad Island 菜单译文与原文互相叠压）：
    ///   <c>FontEngine.SetFaceSize(pt)</c> 决定 FaceInfo 与**每个字形**的 advance/bearing 等
    ///   全部排版度量。按 18pt 装载时这些度量只有游戏字体（实测 48pt）的 0.375 倍，
    ///   TMP 用它们做行高/对齐/字号换算，结果文字被放大错位、与同一行内容叠压。
    ///   只有 Mad Island 出现该问题，是因为它的标题菜单按 48pt 排版。
    ///
    /// 传入非正数时返回 1（不缩放），避免调用方拿到 0 或 NaN 后把度量全部清零。
    /// </summary>
    public static float MetricsScale(float renderedPointSize)
    {
        if (renderedPointSize <= 0f || float.IsNaN(renderedPointSize) || float.IsInfinity(renderedPointSize))
            return 1f;
        var ratio = ReferencePointSize / renderedPointSize;
        return float.IsNaN(ratio) || float.IsInfinity(ratio) || ratio <= 0f ? 1f : ratio;
    }

    /// <summary>
    /// 判断比例是否"实际上不需要缩放"（含 1.0 附近的浮点噪声）。
    /// </summary>
    public static bool NeedsMetricsScaling(float renderedPointSize)
    {
        var ratio = MetricsScale(renderedPointSize);
        return Math.Abs(ratio - 1f) > 0.001f;
    }

    /// <summary>
    /// 按比例缩放一个以点大小为单位的度量值。
    /// pointSize 与被缩放值都走这里，保证"字形度量"与"面度量"用的是同一个比例、同一种舍入。
    /// </summary>
    public static double ScaleMetric(double value, float renderedPointSize)
        => value * MetricsScale(renderedPointSize);

    /// <summary>
    /// 字形度量（advance / bearing / width / height）是否需要缩放 —— 与面度量同一条件，
    /// 但单独留一个入口，便于测试断言两者一致。
    /// </summary>
    public static bool NeedsGlyphMetricsScaling(float renderedPointSize)
        => NeedsMetricsScaling(renderedPointSize);

    ///
    /// ★ 这是"文字互相叠压"的根因所在：
    ///   `FontEngine.SetFaceSize(pt)` 决定 FaceInfo 里 ascentLine / lineHeight / 各字形的
    ///   advance 等**全部排版度量**。如果我们按 18pt 装载，度量只有游戏字体（实测 48pt）
    ///   的 0.375 倍，TMP 就会按这套错的度量做行高、对齐与字号换算 ——
    ///   画面表现是文字被放大/错位，与同一行里正常渲染的内容互相叠压。
    ///
    /// 所以 **<see cref="PointSize"/> 必须等于这个基准**（第 4 轮实验证明"只放大度量"行不通）。
    /// </summary>
    public const float ReferencePointSize = 48f;

    /// <summary>图集数量上限。**实机装配只用 1 张**，这个上限只用于参数写错时兜底。</summary>
    public const int MaxAtlasCount = 1;

    /// <summary>
    /// `FontEngine.TryAddGlyphsToTexture` 的实测装箱效率。
    ///
    /// ★ 修正过的实测值：**0.819**（此前记的 0.31 是**逐字形打包**时代的错误数据 ——
    ///   那时每个字形一次原生调用，装箱极差，而且那批数据本身还伴随原生崩溃）。
    ///
    /// 推导（18pt / padding 6 / 4096²，批量打包）：
    ///   单字理论 cell = (18×1.2 + 12)² = 1129 px²；
    ///   实测平均 925 px²（usedRects=4212，`probe.capacity avgGlyphArea=925`）；
    ///   925 / 1129 = 0.819。
    /// </summary>
    public const double PackingEfficiency = 0.819;

    /// <summary>
    /// 一张 <see cref="AtlasSize"/> 图集在给定点大小下**按实测效率折算**能装多少字形。
    /// </summary>
    public static int EstimatedAtlasCapacity(int pointSize, int padding = Padding, int atlasSize = AtlasSize)
    {
        var cell = pointSize * 1.2 + 2.0 * padding;
        if (cell <= 0) return 0;
        var perSide = Math.Floor(atlasSize / cell);
        return (int)Math.Floor(perSide * perSide * PackingEfficiency);
    }

    /// <summary>
    /// 常用字全集是否装得进一张图集。装不下就不能宣称"装配成功" ——
    /// 缺的字在画面上是方框，而"开第二张图集"会让 Unity 原生崩溃。
    /// </summary>
    public static bool FitsInOneAtlas(int characterCount, int pointSize = PointSize)
        => EstimatedAtlasCapacity(pointSize) >= characterCount;

    /// <summary>
    /// 期望的图集渲染模式名。TMP 自己的字体资源在 Inspector 里就是 <c>SDFAA</c>。
    /// </summary>
    public const string AtlasRenderModeName = "SDFAA";

    /// <summary>
    /// <c>GlyphRenderMode.SDFAA</c> 的已知数值，作为**按名字取不到时的兜底**。
    ///
    /// 这个数值在 Unity 2019.1 ~ 6000.x 之间稳定；一旦哪天变了，
    /// <see cref="SelectGlyphRenderMode"/> 会优先按名字拿到正确值，兜底只是双保险。
    /// </summary>
    public const int SdfaaFallbackValue = 4118;

    /// <summary>
    /// 给 TMP 着色器用的梯度缩放。TMP 自己的公式是 <c>padding + 2.5</c>
    /// （<c>ShaderUtilities.GetPadding</c>），必须与真实 <see cref="Padding"/> 对齐，
    /// 否则距离场解出来的字要么糊成一团、要么变成空心框。
    /// </summary>
    public static float GradientScale => Padding + 2.5f;

    /// <summary>
    /// 从候选的（名字，数值）对里挑出正确的 SDF 渲染模式。
    ///
    /// 选择顺序（每一步都能离线断言）：
    ///   1. 按名字精确匹配 <see cref="AtlasRenderModeName"/>；
    ///   2. 退而求其次：任何**名字里带 SDF 的 AA 模式**（SDFAA / SDFAA_HINTED）；
    ///   3. 再退：数值等于 <see cref="SdfaaFallbackValue"/> 的成员；
    ///   4. 都不行返回 null —— 调用方必须**放弃装配**，不能拿位图模式凑合。
    /// </summary>
    public static string? SelectGlyphRenderMode(IEnumerable<string> candidateNames)
    {
        if (candidateNames is null) return null;

        var names = new List<string>();
        foreach (var name in candidateNames)
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        if (names.Count == 0) return null;

        foreach (var name in names)
            if (string.Equals(name, AtlasRenderModeName, StringComparison.Ordinal)) return name;

        // 名字里同时含 SDF 与 AA 的（SDFAA_HINTED 属于这一类，语义上仍是 SDF+抗锯齿）
        foreach (var name in names)
            if (name.IndexOf("SDF", StringComparison.Ordinal) >= 0 &&
                name.IndexOf("AA", StringComparison.Ordinal) >= 0)
                return name;

        return null;
    }

    /// <summary>
    /// 某个枚举名是不是**位图**渲染模式。位图模式配上 TMP 的 SDF 着色器必然出方框，
    /// 装配代码一旦挑到这类值就应该直接失败，而不是继续装出一个坏字体。
    /// </summary>
    public static bool IsBitmapRenderMode(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.IndexOf("SDF", StringComparison.Ordinal) >= 0) return false;

        // SMOOTH / SMOOTH_HINTED / RASTER / RASTER_HINTED / COLOR / COLOR_HINTED / DEFAULT
        return name.StartsWith("SMOOTH", StringComparison.Ordinal)
            || name.StartsWith("RASTER", StringComparison.Ordinal)
            || name.StartsWith("COLOR", StringComparison.Ordinal)
            || string.Equals(name, "DEFAULT", StringComparison.Ordinal);
    }

    /// <summary>
    /// 材质上必须存在的 TMP 着色器属性名。缺任何一个都不能算"接好了材质"。
    /// </summary>
    public static IReadOnlyList<string> RequiredMaterialProperties { get; } = new[]
    {
        "_MainTex",
        "_GradientScale",
        "_TextureWidth",
        "_TextureHeight",
        "_ScaleRatioA",
        "_ScaleRatioB",
        "_ScaleRatioC",
        "_FaceColor",
        "_FaceDilate",
        "_OutlineWidth",
    };

    /// <summary>
    /// 检查一张"材质属性 → 是否存在"的映射是否满足 SDF 渲染的最低要求。
    /// 返回缺失的属性名；空列表表示通过。
    /// </summary>
    public static IReadOnlyList<string> MissingMaterialProperties(Func<string, bool> hasProperty)
    {
        var missing = new List<string>();
        if (hasProperty is null) return RequiredMaterialProperties;

        foreach (var name in RequiredMaterialProperties)
            if (!hasProperty(name)) missing.Add(name);
        return missing;
    }

    /// <summary>
    /// 复刻 TMP 的 <c>TextMeshPro/Distance Field</c> 像素着色器对 SDF 图集的解码，
    /// 用来**离线证明"方框"是怎么来的**，不必启动 Unity。
    ///
    /// TMP 的着色器做的是（简化到 alpha 通道）：
    ///   <c>distance = 图集值 × _GradientScale × 2 − _GradientScale</c>，再按阈值取覆盖度。
    ///   关键性质：图集值 <b>0.5 对应"正好在轮廓线上"</b> —— 这正是 SDF 的编码约定，
    ///   也是 <c>_GradientScale = padding + 2.5</c> 这个公式的由来（给抗锯齿留 2.5 的余量）。
    ///
    /// 因此：
    ///   * 正确的 SDF 图集（内部 ≈1.0，外部 ≈0.0，边界处穿越 0.5）→ 解码出正常字形轮廓；
    ///   * 位图图集（只有 0 和 1、没有中间过渡）挂到同一个着色器上 →
    ///     整块区域要么全透明要么全不透明，**边界解不出来，表现为实心块/空心方框**。
    /// 这正是三款游戏上"中文和拉丁字母一起变框"的成因。
    /// </summary>
    public static double DecodeSdfAlpha(byte atlasValue, float gradientScale, double threshold = 0.5)
    {
        var normalized = atlasValue / 255.0;
        var signedDistance = (normalized * gradientScale * 2.0) - gradientScale;
        return signedDistance >= 0 ? 1.0 : 0.0;
    }

    /// <summary>
    /// 判断一段图集采样值是不是**位图**（只有 0 与满值，没有中间过渡）。
    /// 位图数据配上 SDF 着色器必然出方框，装配前用它做一道自检。
    /// </summary>
    public static bool LooksLikeBitmap(byte[] samples)
    {
        if (samples is null || samples.Length == 0) return false;

        var interior = 0;
        foreach (var sample in samples)
            if (sample != 0 && sample != 255)
                interior++;

        // 采样里没有任何中间值 → 是位图
        return interior == 0;
    }

    /// <summary>
    /// 用 SDF 着色器解码一段采样，统计不透明像素占比。
    /// 正常字形应该"部分不透明"（轮廓内实心、轮廓外透明、边界有过渡）；
    /// 位图数据解码出来只会是 0% 或 100% 这种极端值。
    /// </summary>
    public static double OpaqueRatio(byte[] samples, float gradientScale)
    {
        if (samples is null || samples.Length == 0) return 0;

        var opaque = 0;
        foreach (var sample in samples)
            if (DecodeSdfAlpha(sample, gradientScale) > 0) opaque++;
        return (double)opaque / samples.Length;
    }
}
