using System;

namespace UnityTranslator.Core;

/// <summary>
/// 游戏内译文覆盖层的排版计算。**纯计算，不依赖 Unity**，因此可以自动化测试。
///
/// 为什么单独拆出来：
/// 覆盖层在 V0.3 里有两个只有实机截图才能发现的问题 ——
///   1. 字号固定按"原矩形高度 × 0.72"取，中文比日文宽，稍长就折行溢出，看上去被切掉一截；
///   2. 用 IMGUI 的 <c>GUIStyle.CalcSize</c> 量高度，实测（Unity 2022.3.62f2）它返回的是
///      **单行**高度，不反映折行后的真实高度，于是"判定放得下"而实际被裁。
/// 这类错误一旦写错，只有跑到游戏里、还得盯着一张截图才看得出来。
/// 把计算搬到这里之后，用单元测试就能钉死"算出来的字号一定放得下"。
/// </summary>
public static class OverlayTextLayout
{
    /// <summary>字号上限：太大挡画面。</summary>
    public const int MaxFontSize = 34;

    /// <summary>字号下限：再小就看不清了，宁可把矩形放大。</summary>
    public const int MinFontSize = 12;

    /// <summary>文本左右内边距合计（对应 GUIStyle.padding 的 5+5）。</summary>
    public const float HorizontalPadding = 10f;

    /// <summary>行高相对字号的倍数。IMGUI 默认行距大致如此。</summary>
    public const float LineHeightFactor = 1.32f;

    /// <summary>
    /// 字宽估算的安全余量。实机核对（Sister Leticia，1280x720）发现按理论字宽算出来的
    /// 宽度会和真实渲染差几个像素，右边最后一个字会贴边甚至被切掉一点。
    /// 宁可把矩形算宽一点，也不要切字。
    /// </summary>
    public const float WidthSafetyFactor = 1.06f;

    /// <summary>矩形允许纵向放大的倍数上限。</summary>
    public const float MaxHeightGrowth = 2.2f;

    /// <summary>
    /// 覆盖层至少要有屏幕宽度的这个比例，免得被原文的窄框（例如只有 88 像素宽的
    /// "Quit"）限制住，把中文译文挤成一列竖排。
    /// </summary>
    public const float MinWidthFraction = 0.22f;

    /// <summary>排版结果：字号、矩形尺寸、以及可能需要左移。</summary>
    public readonly struct Layout
    {
        public Layout(int fontSize, float width, float height, int lines, float textWidth, float textHeight,
            float offsetX = 0f)
        {
            FontSize = fontSize;
            Width = width;
            Height = height;
            Lines = lines;
            TextWidth = textWidth;
            TextHeight = textHeight;
            OffsetX = offsetX;
        }

        /// <summary>最终字号。</summary>
        public int FontSize { get; }

        /// <summary>覆盖层矩形宽度。</summary>
        public float Width { get; }

        /// <summary>覆盖层矩形高度。</summary>
        public float Height { get; }

        /// <summary>折行后的行数。</summary>
        public int Lines { get; }

        /// <summary>文字实际占用宽度（已含内边距）。</summary>
        public float TextWidth { get; }

        /// <summary>文字实际占用高度。</summary>
        public float TextHeight { get; }

        /// <summary>
        /// 矩形相对原文左上角的水平偏移（≤ 0）。
        /// 原文贴着屏幕右边缘时，靠右放不下，只能往左让 —— 文字向左延伸通常不会撞到别的东西。
        /// </summary>
        public float OffsetX { get; }

        /// <summary>文字是否完整放得进 <see cref="Width"/> × <see cref="Height"/>。</summary>
        public bool Fits => TextWidth <= Width + 0.01f && TextHeight <= Height + 0.01f;
    }

    /// <summary>
    /// 为一段译文算出覆盖层该怎么画。
    /// </summary>
    /// <param name="text">译文。</param>
    /// <param name="originalWidth">游戏原文所占矩形的宽（覆盖层至少要这么宽）。</param>
    /// <param name="originalHeight">游戏原文所占矩形的高（覆盖层至少要这么高）。</param>
    /// <param name="screenWidth">屏幕宽，用来限制不得超出画面。</param>
    /// <param name="screenHeight">屏幕高。</param>
    /// <param name="originX">矩形左上角 X，用来算还剩多少可用宽度。</param>
    /// <param name="originY">矩形左上角 Y，用来算还剩多少可用高度。</param>
    public static Layout Compute(string text, float originalWidth, float originalHeight,
        float screenWidth, float screenHeight, float originX, float originY)
    {
        if (string.IsNullOrEmpty(text)) text = string.Empty;

        var availableWidth = Max(8f, screenWidth - HorizontalPadding);
        var availableHeight = Max(8f, screenHeight - originY);

        // 译文"一行排开"时有多宽 —— 用它决定矩形该多宽，才能尽量少折行。
        // 注意这里必须传一个极大的宽度：如果传当前可用宽度，测量过程本身就已经折行了，
        // 算出来的"理想宽度"永远不超过它，等于白算。
        // （上一版就是这么写的，结果是框仍然偏窄、译文多折一行 —— 实机截图核对出来的。）
        var idealWidth = MeasureAt(text, MaxFontSize, float.MaxValue).Width;

        // 宽度策略：至少盖住原文、至少占屏幕一定比例（否则"Quit"这种 88 像素宽的按钮
        // 会把译文挤成一列竖排），并且尽量一行排得下；上限是屏幕宽。
        var minimum = Max(originalWidth, screenWidth * MinWidthFraction);
        var width = Clamp(Max(minimum, idealWidth), 8f, availableWidth);

        // 原文贴着屏幕右边缘时（Mad Island 的按钮在 x=1189/1280），右边放不下就得往左让。
        // 文字向左延伸通常不会撞到别的东西，而被右边界夹住会把译文挤成一列竖排。
        var offsetX = -Clamp(width - Max(8f, screenWidth - originX), 0f, originX);

        // 高度预算：原矩形放不下时可以纵向长一点，但不超出屏幕。
        var heightBudget = Clamp(originalHeight * MaxHeightGrowth, originalHeight, availableHeight);

        // 注意：这里传的是**整个矩形宽度**，不是"宽度减去内边距"。
        // MeasureAt 内部自己会扣一次 HorizontalPadding；调用处再扣一次就会把可用宽度
        // 凭空砍掉 10 像素，正好把"一行放得下"的译文挤成两行（实测核对出来的）。
        var textWidthLimit = width;

        // 字号：从大往小取第一个"折行之后宽高都放得下"的。
        var fontSize = MinFontSize;
        var measured = MeasureAt(text, fontSize, textWidthLimit);
        for (var size = MaxFontSize; size >= MinFontSize; size--)
        {
            var candidate = MeasureAt(text, size, textWidthLimit);
            if (candidate.Width <= width + 0.01f && candidate.Height <= heightBudget + 0.01f)
            {
                fontSize = size;
                measured = candidate;
                break;
            }
            if (size == MinFontSize) measured = candidate;
        }

        var height = Clamp(Max(originalHeight, measured.Height), 8f, availableHeight);
        measured = MeasureAt(text, fontSize, textWidthLimit);
        height = Clamp(Max(height, measured.Height), 8f, availableHeight);

        return new Layout(fontSize, width, height, measured.Lines, measured.Width, measured.Height, offsetX);
    }

    /// <summary>
    /// 按"逐字折行"算出 <paramref name="text"/> 在 <paramref name="size"/> 号字、
    /// <paramref name="availableWidth"/> 宽度下的实际占用尺寸。
    /// </summary>
    public static Measured MeasureAt(string text, int size, float availableWidth)
    {
        if (string.IsNullOrEmpty(text)) return new Measured(HorizontalPadding, size * LineHeightFactor, 1);
        var advanceLimit = Max(size, availableWidth - HorizontalPadding);
        var lines = 1;
        var lineWidth = 0f;
        var maxLineWidth = 0f;
        foreach (var value in text)
        {
            if (value == '\n')
            {
                maxLineWidth = Max(maxLineWidth, lineWidth);
                lines++;
                lineWidth = 0f;
                continue;
            }
            if (value == '\r') continue;
            var advance = Advance(value, size);
            if (lineWidth + advance > advanceLimit && lineWidth > 0f)
            {
                maxLineWidth = Max(maxLineWidth, lineWidth);
                lines++;
                lineWidth = 0f;
            }
            lineWidth += advance;
        }
        maxLineWidth = Max(maxLineWidth, lineWidth);
        return new Measured(maxLineWidth + HorizontalPadding, lines * size * LineHeightFactor, lines);
    }

    /// <summary>单个字符按字号占多宽。估值略偏大是安全的：宁可算宽，不要切字。</summary>
    public static float Advance(char value, int size)
    {
        if (value <= '\u007f') return size * 0.58f * WidthSafetyFactor;   // ASCII
        if (value >= '\uff61' && value <= '\uff9f') return size * 0.6f * WidthSafetyFactor; // 半角片假名
        return size * WidthSafetyFactor;                                  // 汉字、假名、全角标点
    }

    /// <summary>一次测量的结果。</summary>
    public readonly struct Measured
    {
        public Measured(float width, float height, int lines)
        {
            Width = width;
            Height = height;
            Lines = lines;
        }

        public float Width { get; }
        public float Height { get; }
        public int Lines { get; }
    }

    private static float Max(float a, float b) => a > b ? a : b;
    private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
}
