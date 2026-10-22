using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class TextSafetyTests
{
    [Fact]
    public void KeepsPlaceholdersAndRichTextTags()
    {
        Assert.True(TextSafety.IsSafe("HP {0} <b>MAX</b>", "生命 {0} <b>最大</b>"));
    }

    [Fact]
    public void RejectsLostPlaceholder()
    {
        Assert.False(TextSafety.IsSafe("HP {0} <b>MAX</b>", "生命 <b>最大</b>"));
    }

    [Fact]
    public void RejectsBrokenRichText()
    {
        Assert.False(TextSafety.IsSafe("HP {0} <b>MAX</b>", "生命 {0} <b>最大"));
    }

    [Fact]
    public void RejectsEmptyOrUnchangedResult()
    {
        Assert.False(TextSafety.IsSafe("開始", ""));
        Assert.False(TextSafety.IsSafe("開始", "開始"));
    }

    /// <summary>
    /// 实测抓到的两条（app.log 原文）。纯汉字原文被当成 en 送去翻译，模型看不到英语，
    /// 就回了这么一句；它在格式上完全"合法"，所以旧版本把它写进了游戏、还存进了译文库。
    /// </summary>
    [Theory]
    [InlineData("設定", "请提供需要翻译的英语游戏文字内容。")]
    [InlineData("编辑", "请提供需要翻译的英文游戏文本。")]
    public void FormatChecksAloneWouldLetAModelChatReplyThrough(string source, string translated)
    {
        Assert.True(TextSafety.IsSafe(source, translated));                 // 格式没问题
        Assert.True(TextSafety.LooksLikeChatReply(source, translated));     // 但它是对话，不是译文
    }

    /// <summary>正常译文不能被误判，否则辛苦翻出来的界面文字会被白白丢掉。</summary>
    [Theory]
    [InlineData("設定", "设置")]
    [InlineData("ギャラリー", "画廊")]
    [InlineData("ごめんなさい", "很抱歉")]                 // 台词里本来就会出现"抱歉"
    [InlineData("以下の項目を選択", "请选择以下项目")]        // 译文里本来就会出现"以下"
    [InlineData("HP {0} <b>MAX</b>", "生命 {0} <b>最大</b>")]
    [InlineData("Press Start", "按开始键")]
    [InlineData("無", "无")]
    public void DoesNotFlagRealTranslations(string source, string translated)
    {
        Assert.False(TextSafety.LooksLikeChatReply(source, translated));
    }

    [Fact]
    public void RejectsMarkdownFencedReplies()
    {
        Assert.True(TextSafety.LooksLikeChatReply("設定", "```\n设置\n```"));
    }

    [Fact]
    public void EmptyInputIsNeverAFlaggedReply()
    {
        Assert.False(TextSafety.LooksLikeChatReply("", "设置"));
        Assert.False(TextSafety.LooksLikeChatReply("設定", ""));
    }
}
