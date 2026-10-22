using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 字体字符集与分批规划。这一块决定"哪些字能被显示"和"启动卡不卡"，
/// 写错的表现是"译文偶尔变方框"或"一直在补字"，都难以在游戏里复现 —— 所以用测试钉死。
/// </summary>
public sealed class CjkCharacterSetTests
{
    /// <summary>
    /// 内嵌的高频汉字种子必须真的能覆盖日常游戏文本。
    /// 这是"新装的软件、译文库还空着"时唯一的兜底字符来源。
    /// </summary>
    [Theory]
    [InlineData("继续游戏")]
    [InlineData("鸣谢")]
    [InlineData("设定")]
    [InlineData("装备")]
    [InlineData("确认")]
    [InlineData("取消")]
    [InlineData("攻击力")]
    [InlineData("经验值")]
    [InlineData("道具")]
    [InlineData("魔法")]
    [InlineData("返回")]
    [InlineData("存档")]
    [InlineData("读取")]
    [InlineData("开始新游戏")]
    [InlineData("感谢您的游玩")]
    [InlineData("生命值不足")]
    public void SeedCoversEverydayGameText(string text)
    {
        var seed = new HashSet<char>(CjkCharacterSet.SeedHanziSet());

        foreach (var value in text)
            Assert.True(seed.Contains(value), $"种子表里没有「{value}」（来自「{text}」）");
    }

    [Fact]
    public void SeedIsSubstantialButNotBloated()
    {
        var seed = CjkCharacterSet.SeedHanziSet();

        // GB2312 一级常用字共 3755 个，这是权威的常用字集合
        Assert.Equal(3755, seed.Count);
        Assert.All(seed, value => Assert.InRange(value, '\u4e00', '\u9fff'));
        Assert.Equal(seed.Count, new HashSet<char>(seed).Count);   // 无重复
    }

    [Fact]
    public void MinimumSetContainsAsciiKanaPunctuationAndSeed()
    {
        var set = new HashSet<char>(CjkCharacterSet.MinimumSet());

        // ASCII
        Assert.Contains('A', set);
        Assert.Contains('z', set);
        Assert.Contains('0', set);
        // 标点与全角符号
        Assert.Contains('　', set);
        Assert.Contains('。', set);
        Assert.Contains('、', set);
        Assert.Contains('，', set);
        Assert.Contains('？', set);
        Assert.Contains('！', set);
        Assert.Contains('…', set);
        Assert.Contains('—', set);
        Assert.Contains('「', set);
        Assert.Contains('」', set);
        // 假名（游戏里会有译文与未翻译日文混排的情况，缺假名会变方框）
        Assert.Contains('あ', set);
        Assert.Contains('ア', set);
        Assert.Contains('ー', set);
        Assert.Contains('ゃ', set);
        // 种子汉字
        Assert.Contains('继', set);
        Assert.Contains('设', set);
    }

    [Fact]
    public void MinimumSetHasNoDuplicates()
    {
        var list = CjkCharacterSet.MinimumSet();
        Assert.Equal(list.Count, new HashSet<char>(list).Count);
    }

    /// <summary>分批必须覆盖全部字符，一个都不能丢。</summary>
    [Fact]
    public void BatchesCoverEveryCharacterExactlyOnce()
    {
        var all = CjkCharacterSet.MinimumSet();
        var batches = CjkCharacterSet.PlanBatches(all, firstBatch: 240, refillBatch: 64);

        var flattened = new List<char>();
        foreach (var batch in batches) flattened.AddRange(batch);

        Assert.Equal(all.Count, flattened.Count);
        Assert.Equal(new HashSet<char>(all), new HashSet<char>(flattened));
    }

    [Fact]
    public void FirstBatchIsSmallEnoughToFinishFast()
    {
        var batches = CjkCharacterSet.PlanBatches(CjkCharacterSet.MinimumSet(),
            firstBatch: 240, refillBatch: 64);

        Assert.True(batches.Count > 1, "最小集不可能只装一批");
        Assert.Equal(240, batches[0].Count);
        Assert.Equal(64, batches[1].Count);
    }

    [Fact]
    public void FirstBatchCoversAsciiKanaPunctuationAndHanzi()
    {
        var batches = CjkCharacterSet.PlanBatches(CjkCharacterSet.MinimumSet());
        var first = new HashSet<char>(batches[0]);

        for (var code = 0x20; code <= 0x7e; code++)
            Assert.True(first.Contains((char)code), "缺少 ASCII 字符 U+" + code.ToString("X4"));

        Assert.Contains('あ', first);
        Assert.Contains('ア', first);
        Assert.Contains('ー', first);

        foreach (var value in "，。、！？：；「」『』（）")
            Assert.True(first.Contains(value), "缺少常用标点「" + value + "」");

        // 常用汉字必须全在 —— 少一个就会出现"这句话没法直接替换"
        const string everyday = "继续游戏鸣谢设定装备确认取消攻击力经验值道具魔法返回存档读取创意工坊制作人员";
        foreach (var value in everyday)
            Assert.True(first.Contains(value), $"缺少「{value}」（日常文本「{everyday}」）");

        var hanzi = first.Count(c => c >= '\u4e00' && c <= '\u9fff');
        Assert.True(hanzi >= 3700, $"只有 {hanzi} 个汉字，常用字集应该接近 3755");
    }

    [Fact]
    public void DefaultPlanInstallsEverythingInOneBatch()
    {
        var all = CjkCharacterSet.MinimumSet();
        var batches = CjkCharacterSet.PlanBatches(all);

        // 默认就应该一次装完：分批只用于"运行中发现的新字"。
        // 早期版本"先装 240 / 1500、其余按需补"会导致某个常用字刚好在批次之外，
        // 于是每遇到一个新字就重建一次字体 —— 实测踩过（「继」就落在 1500 之外）。
        Assert.Single(batches);
        Assert.Equal(all.Count, batches[0].Count);
    }

    [Fact]
    public void ExplicitBatchingStillWorksForNewlyDiscoveredCharacters()
    {
        // 分批机制本身必须保留：新字要按 refill 批量攒着，避免每来一个字就重建字体。
        var batches = CjkCharacterSet.PlanBatches(CjkCharacterSet.MinimumSet(),
            firstBatch: 1500, refillBatch: 400);

        Assert.Equal(1500, batches[0].Count);
        Assert.Equal(400, batches[1].Count);
    }

    [Fact]
    public void BatchesHandleEmptyAndSmallInputs()
    {
        Assert.Empty(CjkCharacterSet.PlanBatches(Array.Empty<char>()));
        Assert.Empty(CjkCharacterSet.PlanBatches(string.Empty));

        var few = CjkCharacterSet.PlanBatches("abc", firstBatch: 240, refillBatch: 64);
        Assert.Single(few);
        Assert.Equal(3, few[0].Count);
    }

    [Fact]
    public void BatchSizesAreNeverZeroOrNegative()
    {
        // 传 0 或负数不能让分批死循环
        var batches = CjkCharacterSet.PlanBatches("abcdef", firstBatch: 0, refillBatch: -3);
        var flattened = new List<char>();
        foreach (var batch in batches) flattened.AddRange(batch);
        Assert.Equal("abcdef".Length, flattened.Count);
    }

    /// <summary>按需补字的判据：装了的字不算缺，没装的算缺，重复只报一次。</summary>
    [Fact]
    public void MissingForReportsOnlyUninstalledCharacters()
    {
        // 用固定的一小批"已装字符"，避免依赖内嵌种子表的具体内容
        var installed = new HashSet<char> { '继', '续', '游', '戏', 'A', '。', ' ' };

        var missing = CjkCharacterSet.MissingFor(installed, "继续游戏 新內容 A。");

        Assert.Contains('新', missing);
        Assert.Contains('內', missing);
        Assert.Contains('容', missing);
        Assert.DoesNotContain('继', missing);
        Assert.DoesNotContain('A', missing);
        Assert.DoesNotContain('。', missing);
        Assert.DoesNotContain(' ', missing);
        Assert.Equal(missing.Distinct().Count(), missing.Count);
    }

    [Fact]
    public void MissingForIgnoresLineBreaksAndHandlesEmptyText()
    {
        var installed = new HashSet<char> { '甲' };

        Assert.Empty(CjkCharacterSet.MissingFor(installed, string.Empty));
        Assert.Empty(CjkCharacterSet.MissingFor(installed, null));
        Assert.Empty(CjkCharacterSet.MissingFor(installed, "甲\n\r\t"));
    }

    [Fact]
    public void MissingForWorksWithAnyEnumerable()
    {
        var missing = CjkCharacterSet.MissingFor(new[] { '甲', '乙' }, "甲乙丙");
        Assert.Single(missing);
        Assert.Equal('丙', missing[0]);
    }

    /// <summary>合并多个来源（种子 + 译文库里出现过的字）。</summary>
    [Fact]
    public void CombineMergesAndDeduplicates()
    {
        var combined = CjkCharacterSet.Combine(
            CjkCharacterSet.SeedHanziSet(),
            CjkCharacterSet.CharactersOf("继续游戏 鎏"),
            new[] { 'A', 'B' });

        var set = new HashSet<char>(combined);
        Assert.Contains('继', set);
        Assert.Contains('鎏', set);      // 译文库里出现过的生僻字也必须在内
        Assert.Contains('A', set);
        Assert.Equal(combined.Count, set.Count);
    }

    [Fact]
    public void CharactersOfExtractsDistinctCharacters()
    {
        // 结果按码位排序：字 U+5B57 在 汉 U+6C49 之前
        var characters = CjkCharacterSet.CharactersOf("aabb汉汉字");
        Assert.Equal(new[] { 'a', 'b', '字', '汉' }, characters);
        Assert.Empty(CjkCharacterSet.CharactersOf(null));
        Assert.Empty(CjkCharacterSet.CharactersOf(string.Empty));
    }

    /// <summary>
    /// 排序函数必须是一个合法的比较器：可自反、方向一致，且**同档内按码位兜底**。
    /// 同档返回 0 会让不稳定排序把顺序打乱（实测第一批开头变成 "edcba"），
    /// 比较器不一致则会在 Sort 时抛异常 —— 那是在游戏里才炸的。
    /// </summary>
    [Fact]
    public void CompareByUsefulnessIsConsistent()
    {
        // 自反
        Assert.Equal(0, CjkCharacterSet.CompareByUsefulness('A', 'A'));
        Assert.Equal(0, CjkCharacterSet.CompareByUsefulness('汉', '汉'));
        // 同档内按码位（'A' 0x41 < 'z' 0x7A）
        Assert.True(CjkCharacterSet.CompareByUsefulness('A', 'z') < 0);
        Assert.True(CjkCharacterSet.CompareByUsefulness('z', 'A') > 0);
        // 跨档
        Assert.True(CjkCharacterSet.CompareByUsefulness('A', '。') < 0);
        Assert.True(CjkCharacterSet.CompareByUsefulness('。', 'あ') < 0);
        Assert.True(CjkCharacterSet.CompareByUsefulness('あ', '漢') < 0);
        Assert.True(CjkCharacterSet.CompareByUsefulness('漢', 'A') > 0);
        // 反对称
        Assert.Equal(-Math.Sign(CjkCharacterSet.CompareByUsefulness('汉', 'A')),
            Math.Sign(CjkCharacterSet.CompareByUsefulness('A', '汉')));

        var list = "汉Aあ。z鎏".ToList();
        list.Sort(CjkCharacterSet.CompareByUsefulness);
        Assert.Equal(list.Count, list.Distinct().Count());
        Assert.Equal(new[] { 'A', 'z', '。', 'あ', '汉', '鎏' }, list);
    }

    /// <summary>假名与标点的生成器不能漏项。</summary>
    [Fact]
    public void KanaAndPunctuationGeneratorsAreComplete()
    {
        var kana = new HashSet<char>(CjkCharacterSet.Kana());
        Assert.Contains('ぁ', kana);     // 平假名首字
        Assert.Contains('ん', kana);     // 平假名末字
        Assert.Contains('ァ', kana);     // 片假名首字
        Assert.Contains('ヶ', kana);
        Assert.Contains('ー', kana);
        Assert.Contains('\u3099', kana); // 浊点
        Assert.Contains('\u309a', kana); // 半浊点

        var punctuation = new HashSet<char>(CjkCharacterSet.Punctuation());
        Assert.Contains('　', punctuation);   // 全角空格
        Assert.Contains('、', punctuation);
        Assert.Contains('。', punctuation);
        Assert.Contains('〜', punctuation);
        Assert.Contains('※', punctuation);
    }

    /// <summary>
    /// 假名不能被当成标点排序：U+3000..U+303F 区间的末尾紧邻平假名，
    /// 归类写反会导致"第一批里没有假名"，译文与未翻译日文混排处先显示不出来。
    /// </summary>
    [Fact]
    public void KanaRanksBeforeHanziButPunctuationComesFirst()
    {
        Assert.Equal(-1, CjkCharacterSet.CompareByUsefulness('。', 'あ'));   // 标点先于假名
        Assert.Equal(-1, CjkCharacterSet.CompareByUsefulness('あ', '漢'));   // 假名先于汉字
        Assert.Equal(-1, CjkCharacterSet.CompareByUsefulness('ぁ', '漢'));   // 假名区首字也算假名
    }
}
