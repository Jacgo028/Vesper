using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class TextSelectionPolicyTests
{
    [Theory]
    [InlineData("はい")]                       // 纯假名
    [InlineData("本当にゲームを終了しますか?")]     // 假名 + 汉字
    [InlineData("ロード画面(仮)")]
    [InlineData("カフェ")]                     // 纯片假名
    [InlineData("Settings")]
    [InlineData("NEW GAME")]
    [InlineData("Kurea Struggle v1.0.0")]
    public void SendsJapaneseAndEnglishToTranslation(string text)
    {
        Assert.True(TextSelectionPolicy.IsTranslatable(text));
    }

    /// <summary>
    /// V0.4 的关键行为变更：**纯汉字、不带假名**的文字必须送去翻译。
    ///
    /// 这些词是日语游戏界面里最常见的形态，V0.3 把它们当成"已经是中文"全部跳过，
    /// 导致菜单、道具、技能名一个字都翻不出来 —— 这是"翻译功能几乎不可用"的主因。
    /// 中文原文被送过去只会被翻译服务原样返回，随后由桌面端 TextSafety 判为无效丢弃，
    /// 不会造成误翻（对应下一条测试）。
    /// </summary>
    [Theory]
    [InlineData("設定")]
    [InlineData("装備")]
    [InlineData("名前")]
    [InlineData("体力")]
    [InlineData("攻撃力")]
    [InlineData("経験値")]
    [InlineData("道具")]
    [InlineData("魔法")]
    [InlineData("確認")]
    [InlineData("装備品一覧")]
    public void SendsKanjiOnlyJapaneseMenuTextToTranslation(string text)
    {
        Assert.True(TextSelectionPolicy.IsTranslatable(text));
    }

    /// <summary>
    /// 扩展区汉字也要认出"这里有汉字可翻"（日语游戏偶尔用生僻字）。
    /// </summary>
    [Theory]
    [InlineData("\u9fa0")]        // 基本区边界内的字
    [InlineData("\u3400")]        // 扩展 A 区首字
    [InlineData("\uf900")]        // 兼容表意文字首字
    public void RecognisesHanFromExtendedBlocks(string text)
    {
        Assert.True(TextSelectionPolicy.IsTranslatable(text));
    }

    /// <summary>
    /// 中文界面同样会被送去翻译（这是有意的），但桌面端会把它挡在替换之前。
    /// 这条断言把"送出去"这个事实固定下来，避免有人误以为中文界面会被漏掉翻译。
    /// </summary>
    [Theory]
    [InlineData("继续游戏")]
    [InlineData("鸣谢")]
    [InlineData("设定")]
    public void ChineseTextIsAlsoSentButWillBeRejectedDownstream(string text)
    {
        Assert.True(TextSelectionPolicy.IsTranslatable(text));

        // 下游的关卡：原文与译文完全相同 → 判为无效，游戏侧什么都不会发生。
        Assert.False(TextSafety.IsSafe(text, text));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12:00:00")]
    [InlineData("1990/12/23 10:10:10")]
    [InlineData("...")]
    [InlineData("—")]
    [InlineData("　")]           // 全角空格
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SkipsNumbersPunctuationAndBlank(string? text)
    {
        Assert.False(TextSelectionPolicy.IsTranslatable(text));
    }

    [Fact]
    public void SkipsOverlyLongText()
    {
        // 超长文本会给翻译接口和排版都带来麻烦，先不送
        var longText = new string('あ', 500);

        Assert.False(TextSelectionPolicy.IsTranslatable(longText, maxLength: 400));
    }

    [Fact]
    public void SendsTextAtTheLengthBoundary()
    {
        // 边界必须包含：正好 400 字要送，401 字不送。
        Assert.True(TextSelectionPolicy.IsTranslatable(new string('あ', 400), maxLength: 400));
        Assert.False(TextSelectionPolicy.IsTranslatable(new string('あ', 401), maxLength: 400));
    }

    [Theory]
    [InlineData("はい", "ja")]
    [InlineData("本当にゲームを終了しますか?", "ja")]
    [InlineData("カフェ", "ja")]
    [InlineData("Settings", "en")]
    [InlineData("NEW GAME", "en")]
    [InlineData("1290", "en")]
    [InlineData("設定", "en")]      // 纯汉字按 en 归桶，保证缓存稳定（见实现注释）
    public void DetectsSourceLanguage(string text, string expected)
    {
        Assert.Equal(expected, TextSelectionPolicy.DetectLanguage(text));
    }

    [Fact]
    public void RetryBacksOffAndIsCapped()
    {
        // 第一次尝试后 10 秒可重试
        Assert.False(TextSelectionPolicy.RetryDue(1, 0f, 9f));
        Assert.True(TextSelectionPolicy.RetryDue(1, 0f, 10f));

        // 次数越多退避越久
        Assert.False(TextSelectionPolicy.RetryDue(3, 0f, 39f));
        Assert.True(TextSelectionPolicy.RetryDue(3, 0f, 40f));

        // 封顶 60 秒
        Assert.False(TextSelectionPolicy.RetryDue(99, 0f, 59f));
        Assert.True(TextSelectionPolicy.RetryDue(99, 0f, 60f));

        // 没发过就不重试
        Assert.False(TextSelectionPolicy.RetryDue(0, 0f, 1000f));
    }

    [Fact]
    public void MixedHanAndLatinIsSentForTranslation()
    {
        // 含汉字又含拉丁字母：现在一律送去翻译。
        // 以前这里断言 false（"已经含中文不再翻"），会把「設定 New Game」这类
        // 日语界面上的混合标题整条漏掉。中文原文仍由 TextSafety 在下游挡住。
        Assert.True(TextSelectionPolicy.IsTranslatable("开始 New Game"));
        Assert.True(TextSelectionPolicy.IsTranslatable("ステータス STATUS"));
    }
}
