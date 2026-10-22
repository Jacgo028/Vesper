using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 覆盖层排版的核心保证：**算出来的字号与矩形，一定放得下这段译文**。
///
/// 这一条是拿实机截图换来的：V0.3 用 GUIStyle.CalcSize 量高度，
/// 而实测它返回的是单行高度，于是"判定放得下"、实际被裁成两行只剩一行。
/// 现在把计算搬进 Core，用测试钉死。
/// </summary>
public sealed class OverlayTextLayoutTests
{
    // 1280x720 游戏窗口里一个常见按钮：214x84，位于 (899,397)。
    private const float ScreenWidth = 1280f;
    private const float ScreenHeight = 720f;

    [Fact]
    public void ShortChineseFitsInsideTheOriginalRectangle()
    {
        var layout = OverlayTextLayout.Compute("开始游戏", 214f, 84f, ScreenWidth, ScreenHeight, 899f, 397f);

        Assert.True(layout.Fits, $"译文没放进去：{layout.TextWidth}x{layout.TextHeight} vs {layout.Width}x{layout.Height}");
        Assert.True(layout.Width >= 214f);   // 至少要盖住原文
        Assert.True(layout.Height >= 84f);
        Assert.True(layout.FontSize >= OverlayTextLayout.MinFontSize);
    }

    /// <summary>
    /// 这是实机里翻车的那一种：译文较长（约 30 个汉字），
    /// 必须要么缩字号、要么加矩形，总之最后一定放得下 —— 不允许溢出。
    /// </summary>
    [Theory]
    [InlineData("这是很长的一段中文译文用来测试覆盖层会不会把文字裁掉真的很容易出问题")]
    [InlineData("設定")]
    [InlineData("继续游戏")]
    [InlineData("感谢您的游玩，本作品纯属虚构，出场人物与团体均与现实无关。")]
    [InlineData("New Game")]
    [InlineData("A very long English sentence that also needs to wrap inside the overlay rectangle.")]
    public void EveryTranslationActuallyFits(string text)
    {
        var layout = OverlayTextLayout.Compute(text, 214f, 84f, ScreenWidth, ScreenHeight, 899f, 397f);

        Assert.True(layout.Fits, $"「{text}」没放进去：文字 {layout.TextWidth}x{layout.TextHeight}，"
            + $"矩形 {layout.Width}x{layout.Height}，字号 {layout.FontSize}");
    }

    /// <summary>
    /// 译文更长时一定要付出某种代价（更大/更高的框、或更小的字号），
    /// 绝不允许"什么都没变、文字却溢出了"。
    /// </summary>
    [Fact]
    public void LongerTextMustGiveSomethingUp()
    {
        var shortText = OverlayTextLayout.Compute("设置", 214f, 84f, ScreenWidth, ScreenHeight, 899f, 397f);
        var longText = OverlayTextLayout.Compute(
            "这是很长的一段中文译文用来测试覆盖层会不会把文字裁掉真的很容易出问题",
            214f, 84f, ScreenWidth, ScreenHeight, 899f, 397f);

        Assert.True(longText.Fits, "长译文没放进去");
        Assert.True(longText.FontSize < shortText.FontSize
                    || longText.Height > shortText.Height
                    || longText.Width > shortText.Width,
            $"长文本没有任何让步：字号 {longText.FontSize} vs {shortText.FontSize}，"
            + $"高度 {longText.Height} vs {shortText.Height}，宽度 {longText.Width} vs {shortText.Width}");
    }

    [Fact]
    public void RectangleNeverLeavesTheScreen()
    {
        // 贴着右下角的文字：矩形（含左移后的位置）不许超出画面。
        var layout = OverlayTextLayout.Compute(
            "这是一段相当长的中文译文需要折行才能显示完整",
            200f, 60f, ScreenWidth, ScreenHeight, 1240f, 690f);

        var left = 1240f + layout.OffsetX;
        Assert.True(left >= 0f, $"左边界 {left} 出屏了");
        Assert.True(left + layout.Width <= ScreenWidth + 0.01f, $"右边界 {left + layout.Width} 出屏了");
        Assert.True(layout.Height <= ScreenHeight - 690f + 0.01f);
    }

    /// <summary>
    /// 原文很短（"Quit" 这种按钮可能只有 88 像素宽）时，译文不许被挤成一列竖排。
    /// 这是实机截图核对出来的：Mad Island 的窄按钮上中文被折成 3 行、字号掉到 14。
    /// </summary>
    [Fact]
    public void NarrowOriginalDoesNotSqueezeTheTranslation()
    {
        // 屏幕中部：右边有充足空间。
        var layout = OverlayTextLayout.Compute("UTMARK02号译文", 88f, 83f,
            ScreenWidth, ScreenHeight, 400f, 300f);

        Assert.True(layout.Width >= ScreenWidth * OverlayTextLayout.MinWidthFraction,
            $"宽度 {layout.Width} 仍然被原文的窄框限制住");
        Assert.True(layout.FontSize > OverlayTextLayout.MinFontSize,
            $"字号掉到了下限 {layout.FontSize}，说明还是被挤住了");
        Assert.True(layout.Lines <= 2, $"译文被折成了 {layout.Lines} 行");
        Assert.True(layout.Fits, "译文没放进去");
    }

    /// <summary>
    /// 原文贴着屏幕右边缘（Mad Island 的按钮就在 x=1189/1280）：右边放不下，
    /// 必须允许覆盖层向左扩展，否则译文只能挤成一列竖排。
    /// </summary>
    [Fact]
    public void RightEdgeTextGrowsLeftwardsInsteadOfBeingSqueezed()
    {
        var layout = OverlayTextLayout.Compute("UTMARK02号译文", 88f, 83f,
            ScreenWidth, ScreenHeight, 1189f, 591f);

        Assert.True(layout.OffsetX < 0f, "没有向左让位，译文会被右边界挤住");
        Assert.True(layout.FontSize > OverlayTextLayout.MinFontSize,
            $"字号仍被压到下限 {layout.FontSize}");
        Assert.True(layout.Lines <= 2, $"译文被折成了 {layout.Lines} 行");
        Assert.True(layout.Fits, "译文没放进去");
        // 左移之后整体仍在屏幕内
        Assert.True(1189f + layout.OffsetX >= 0f);
        Assert.True(1189f + layout.OffsetX + layout.Width <= ScreenWidth + 0.01f);
    }

    /// <summary>
    /// 越长的译文，矩形就越宽（"能宽则宽"），避免不必要的折行。
    /// </summary>
    [Fact]
    public void LongerTranslationGetsAWiderBox()
    {
        var shortBox = OverlayTextLayout.Compute("设置", 100f, 60f, ScreenWidth, ScreenHeight, 100f, 200f);
        var longBox = OverlayTextLayout.Compute(
            "这是一段明显更长的中文译文内容", 100f, 60f, ScreenWidth, ScreenHeight, 100f, 200f);

        Assert.True(longBox.Width > shortBox.Width,
            $"长译文没有拿到更宽的框：{longBox.Width} vs {shortBox.Width}");
    }

    /// <summary>原文本来就宽时，覆盖层至少要盖住它，不能比原文窄。</summary>
    [Fact]
    public void OverlayIsNeverNarrowerThanTheOriginal()
    {
        var layout = OverlayTextLayout.Compute("设置", 700f, 40f, ScreenWidth, ScreenHeight, 100f, 200f);

        Assert.True(layout.Width >= 700f, $"覆盖层宽度 {layout.Width} 小于原文宽度 700");
        Assert.True(layout.Height >= 40f);
    }

    /// <summary>左移只发生在必要的时候：屏幕中部不该无缘无故偏移。</summary>
    [Fact]
    public void OffsetStaysZeroWhenThereIsEnoughRoom()
    {
        var layout = OverlayTextLayout.Compute("设置", 100f, 60f, ScreenWidth, ScreenHeight, 300f, 200f);

        Assert.Equal(0f, layout.OffsetX);
    }

    /// <summary>
    /// 一行放得下的译文就必须排成一行。
    ///
    /// 这条专门盯住一个真实的 bug：矩形宽度算对了（285）、文字一行也只要 285.3，
    /// 但调用处把内边距又扣了一次，可用宽度变成 277，于是被折成两行。
    /// 实机截图里表现为"明明有空间却换行了"。
    /// </summary>
    [Fact]
    public void TextThatFitsInOneLineStaysOnOneLine()
    {
        // 与实机完全一致的场景：Leticia 的菜单按钮 204x84，位于 (899,397)。
        var layout = OverlayTextLayout.Compute("UTMARK01号译文", 204f, 84f,
            ScreenWidth, ScreenHeight, 899f, 397f);

        Assert.Equal(1, layout.Lines);
        Assert.True(layout.Fits, $"文字 {layout.TextWidth}x{layout.TextHeight} "
            + $"没放进 {layout.Width}x{layout.Height}");
    }

    /// <summary>
    /// 反过来：一行确实放不下时要老实折行，不能靠"少扣内边距"假装放得下。
    /// </summary>
    [Fact]
    public void TextTooWideForTheScreenStillWraps()
    {
        var layout = OverlayTextLayout.Compute(new string('字', 100), 204f, 84f,
            ScreenWidth, ScreenHeight, 899f, 397f);

        Assert.True(layout.Lines > 1, "100 个汉字不可能在一行里排完");
    }

    [Fact]
    public void ExtremelyLongTextStillProducesAUsableBox()
    {
        // 200 个汉字：矩形会被撑宽撑高，字号可能降，但结果必须有效且不越界。
        var text = new string('翻', 200);
        var layout = OverlayTextLayout.Compute(text, 214f, 84f, ScreenWidth, ScreenHeight, 899f, 397f);
        var diagnostic = $"字号={layout.FontSize} 尺寸={layout.Width}x{layout.Height} "
            + $"文字={layout.TextWidth}x{layout.TextHeight} 行={layout.Lines} 偏移={layout.OffsetX}";

        Assert.True(layout.Width > 0f, diagnostic);
        Assert.True(layout.Height > 0f, diagnostic);
        Assert.True(899f + layout.OffsetX >= 0f, diagnostic);
        Assert.True(899f + layout.OffsetX + layout.Width <= ScreenWidth + 0.01f, diagnostic);
        Assert.True(layout.Height <= ScreenHeight - 397f + 0.01f, diagnostic);
        // 200 个汉字不可能塞进一行。
        Assert.True(layout.Lines > 1, diagnostic);
    }

    /// <summary>
    /// 布局给出的矩形必须真的放得下文字 —— 这条是覆盖层不裁字的根本保证，
    /// 用多个长度、多个位置一起扫一遍。
    /// </summary>
    [Theory]
    [InlineData(1, 100f, 40f, 0f, 0f)]
    [InlineData(10, 120f, 50f, 500f, 300f)]
    [InlineData(50, 214f, 84f, 899f, 397f)]
    [InlineData(200, 214f, 84f, 899f, 397f)]
    [InlineData(200, 88f, 83f, 1189f, 591f)]
    [InlineData(400, 300f, 100f, 900f, 600f)]
    public void ComputedBoxAlwaysFitsTheText(int length, float originalWidth, float originalHeight,
        float originX, float originY)
    {
        var text = new string('翻', length);
        var layout = OverlayTextLayout.Compute(text, originalWidth, originalHeight,
            ScreenWidth, ScreenHeight, originX, originY);

        var diagnostic = $"长度={length} 原点=({originX},{originY}) 原框={originalWidth}x{originalHeight} "
            + $"→ 字号={layout.FontSize} 尺寸={layout.Width}x{layout.Height} "
            + $"文字={layout.TextWidth}x{layout.TextHeight} 行={layout.Lines} 偏移={layout.OffsetX}";

        Assert.True(layout.Width >= originalWidth - 0.01f, "覆盖层比原文窄：" + diagnostic);
        Assert.True(layout.Height >= originalHeight - 0.01f, "覆盖层比原文矮：" + diagnostic);
        Assert.True(originX + layout.OffsetX >= -0.01f, "左边出屏：" + diagnostic);
        Assert.True(originX + layout.OffsetX + layout.Width <= ScreenWidth + 0.01f, "右边出屏：" + diagnostic);
        Assert.True(layout.Height <= ScreenHeight - originY + 0.01f, "下面出屏：" + diagnostic);
    }

    [Fact]
    public void NewlinesAreCountedAsLines()
    {
        var single = OverlayTextLayout.MeasureAt("两行文字", 24, 400f);
        var doubled = OverlayTextLayout.MeasureAt("两行文字\n两行文字", 24, 400f);

        Assert.Equal(1, single.Lines);
        Assert.Equal(2, doubled.Lines);
        Assert.True(doubled.Height > single.Height);
    }

    /// <summary>
    /// 空文本不该让排版炸掉，也不该产出零尺寸。
    /// （真实链路里空译文会被 <c>string.IsNullOrWhiteSpace(reply.Translated)</c> 提前挡掉，
    /// 这里只是保证这个纯函数本身是健壮的，不给"绝不抛异常"留缺口。）
    /// </summary>
    [Fact]
    public void EmptyTextDoesNotThrowAndProducesUsableSize()
    {
        var layout = OverlayTextLayout.Compute(string.Empty, 100f, 40f, ScreenWidth, ScreenHeight, 0f, 0f);

        Assert.True(layout.Width >= 8f, $"宽度 {layout.Width} 太小");
        Assert.True(layout.Height >= 8f, $"高度 {layout.Height} 太小");
        Assert.True(layout.Width <= ScreenWidth);
        Assert.True(layout.Height <= ScreenHeight);
        Assert.InRange(layout.FontSize, OverlayTextLayout.MinFontSize, OverlayTextLayout.MaxFontSize);
    }

    /// <summary>
    /// 逐字折行的宽度必须真的被限制在可用宽度内 —— 这正是 CalcSize 做不到的事。
    /// </summary>
    [Fact]
    public void WrappedLineWidthNeverExceedsTheLimit()
    {
        var text = new string('字', 50);
        var measured = OverlayTextLayout.MeasureAt(text, 20, 200f);

        Assert.True(measured.Width <= 200f + 0.01f, $"折行后宽度 {measured.Width} 超过 200");
        Assert.True(measured.Lines >= 5, $"50 个汉字在 200 宽、20 号字下至少 5 行，实际 {measured.Lines}");
    }

    [Fact]
    public void NumberOfLinesMatchesTheGeometry()
    {
        // 可用文本宽度 = 200 - 10 = 190；每字 20 × 1.06 安全余量 = 21.2 宽
        // → 每行 8 个字；50 字 → 7 行。（实机核对后加的安全余量，见 WidthSafetyFactor）
        var measured = OverlayTextLayout.MeasureAt(new string('字', 50), 20, 200f);

        Assert.Equal(7, measured.Lines);
    }

    /// <summary>
    /// 字宽留了安全余量：算出来的宽度必须**大于**理论字宽之和，
    /// 免得实机渲染时右边最后一个字被切掉（这是实机截图核对出来的）。
    /// </summary>
    [Fact]
    public void WidthEstimateLeavesSafetyMargin()
    {
        const int size = 20;
        var theoretical = 10 * size;                      // 10 个汉字，理论 10 个字身
        var measured = OverlayTextLayout.MeasureAt(new string('字', 10), size, 900f);

        Assert.True(measured.Width > theoretical,
            $"测量宽度 {measured.Width} 没有留出余量（理论 {theoretical}）");
    }

    [Fact]
    public void AsciiIsNarrowerThanHan()
    {
        Assert.True(OverlayTextLayout.Advance('A', 20) < OverlayTextLayout.Advance('字', 20));
        // 半角片假名也比全角窄
        Assert.True(OverlayTextLayout.Advance('\uff76', 20) < OverlayTextLayout.Advance('カ', 20));
    }

    [Fact]
    public void FontSizeStaysWithinConfiguredBounds()
    {
        for (var length = 1; length <= 60; length++)
        {
            var layout = OverlayTextLayout.Compute(new string('字', length), 214f, 84f,
                ScreenWidth, ScreenHeight, 899f, 397f);
            Assert.InRange(layout.FontSize, OverlayTextLayout.MinFontSize, OverlayTextLayout.MaxFontSize);
        }
    }
}
