using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 字形批处理簿记的离线回归测试。
///
/// 守的是本轮实测踩到的两个"画面全变方框"的坑：
///   1. 引擎会清空传进去的待装列表 —— 拿它做第二遍遍历就只剩最后 1 个字符进字符表；
///   2. 逐个字形调用原生打包会让 Unity 崩溃 —— 必须攒批。
///
/// 这里用一个"会清空输入列表"的假引擎复现坑 1，不依赖 UnityEngine。
/// </summary>
public sealed class GlyphBatchAccumulatorTests
{
    /// <summary>
    /// 模拟真实 <c>FontEngine.TryAddGlyphsToTexture</c>：**会清空传入的列表**，
    /// 只把"装成功的"放进返回列表。
    /// </summary>
    private static Func<List<uint>, List<uint>> EngineThatConsumesInput(Func<uint, bool>? succeeds = null)
    {
        return input =>
        {
            var rendered = new List<uint>();
            foreach (var index in input)
                if (succeeds is null || succeeds(index)) rendered.Add(index);
            input.Clear();   // ★ 真实引擎就是这个行为，坑就在这里
            return rendered;
        };
    }

    /// <summary>
    /// 回归保护：整套常用字（4215 个）走完批量流程后，
    /// **每一个字形都必须换回一个字符**，且受批大小影响。
    /// </summary>
    [Fact]
    public void EveryCollectedGlyphBecomesACharacterAcrossManyBatches()
    {
        const int total = 4215;
        var accumulator = new GlyphBatchAccumulator();

        for (var i = 0; i < total; i++)
        {
            accumulator.Add((char)(0x4E00 + i), (uint)(100 + i));
            if (accumulator.ShouldFlush) accumulator.Flush(EngineThatConsumesInput());
        }
        accumulator.Flush(EngineThatConsumesInput());

        Assert.Equal(total, accumulator.CollectedTotal);
        Assert.Equal(total, accumulator.RenderedTotal);
        Assert.Equal(total, accumulator.CharacterTotal);
        Assert.Equal(0, accumulator.FailedTotal);
        Assert.Equal(total, accumulator.CharacterMapSize);

        // 132 批左右（4215/32），远少于"每个字形一次原生调用"
        Assert.InRange(accumulator.FlushCount, 130, 135);
        Assert.True(accumulator.FlushCount < total / 20,
            $"批次应远少于字形数，实际 {accumulator.FlushCount} 批 / {total} 个字形");
    }

    /// <summary>
    /// 回归保护（本轮真正的 bug）：如果实现拿"被引擎清空过的列表"去做第二遍遍历，
    /// 字符表就只剩最后一批。这条用例把正确行为钉死。
    ///
    /// 这里刻意让**字符只有 26 个但字形有 100 个**（index 各不相同）：
    /// 每个唯一字形都要换回一个字符，所以字符数应等于**收下的字形数**，而不是不同字符的种数。
    /// </summary>
    [Fact]
    public void CharactersSurviveAnEngineThatClearsItsInput()
    {
        var accumulator = new GlyphBatchAccumulator();
        for (var i = 0; i < 100; i++) accumulator.Add((char)('A' + i % 26), (uint)(i + 1));

        // 不手动分批，一次性 Flush —— 引擎清空入参，受影响的只应是引擎自己那份清单
        accumulator.Flush(EngineThatConsumesInput());

        Assert.Equal(100, accumulator.CollectedTotal);
        Assert.Equal(100, accumulator.CharacterTotal);
        Assert.Equal(100, accumulator.CharacterMapSize);
        Assert.Equal(0, accumulator.FailedTotal);
    }

    /// <summary>每批累积：批数 × 批大小 与累计数必须对得上。</summary>
    [Fact]
    public void AccountsForEachBatchCumulatively()
    {
        var accumulator = new GlyphBatchAccumulator(10);
        var flushed = 0;

        for (var i = 0; i < 35; i++)
        {
            accumulator.Add((char)(0x4E00 + i), (uint)(i + 1));
            if (accumulator.ShouldFlush)
            {
                accumulator.Flush(EngineThatConsumesInput());
                flushed++;
            }
        }
        accumulator.Flush(EngineThatConsumesInput());
        flushed++;

        Assert.Equal(4, flushed);                       // 3 整批 + 1 尾批
        Assert.Equal(4, accumulator.FlushCount);
        Assert.Equal(35, accumulator.CollectedTotal);
        Assert.Equal(35, accumulator.CharacterTotal);
    }

    /// <summary>装不上的字形要如实记进失败清单，不能被悄悄算成成功。</summary>
    [Fact]
    public void ReportsGlyphsTheEngineCouldNotRender()
    {
        var accumulator = new GlyphBatchAccumulator();
        for (var i = 0; i < 10; i++) accumulator.Add((char)(0x4E00 + i), (uint)(i + 1));

        // 只让 index 为偶数的装成功
        accumulator.Flush(EngineThatConsumesInput(index => index % 2 == 0));

        Assert.Equal(5, accumulator.CharacterTotal);
        Assert.Equal(5, accumulator.FailedTotal);
        Assert.Equal(5, accumulator.FailedCharacters.Count);
    }

    /// <summary>采样字符必须能命中：这是"画面能不能显示这几个字"的最小断言。</summary>
    [Fact]
    public void SampleCharactersAreAllPresentAfterBatching()
    {
        const string sample = "继续游戏Aa1";
        var accumulator = new GlyphBatchAccumulator(4);   // 故意用很小的批，制造多个批次

        var map = new Dictionary<char, uint>();
        uint next = 1;
        foreach (var ch in sample)
        {
            map[ch] = next;
            accumulator.Add(ch, next);
            next++;
            if (accumulator.ShouldFlush) accumulator.Flush(EngineThatConsumesInput());
        }
        accumulator.Flush(EngineThatConsumesInput());

        foreach (var ch in sample)
        {
            Assert.Equal(ch, accumulator.CharacterOf(map[ch]));
        }
        Assert.Equal(sample.Length, accumulator.CharacterTotal);
    }

    /// <summary>同一个字形 index 不能在账上出现两次（多码位共用字形的情形）。</summary>
    [Fact]
    public void DuplicateGlyphIndexesAreCountedOnce()
    {
        var accumulator = new GlyphBatchAccumulator();

        Assert.True(accumulator.Add('Ａ', 7));    // 全角
        Assert.False(accumulator.Add('A', 7));    // 半角，共用同一个字形
        Assert.Equal(1, accumulator.CollectedTotal);
        Assert.Equal(1, accumulator.PendingCount);
    }

    [Fact]
    public void IgnoresEmptyCharactersAndHandlesEmptyFlush()
    {
        var accumulator = new GlyphBatchAccumulator();

        Assert.False(accumulator.Add('\0', 1));
        Assert.Equal(0, accumulator.CollectedTotal);
        Assert.Equal(0, accumulator.Flush(EngineThatConsumesInput()));   // 空批不该算一批
        Assert.Equal(0, accumulator.FlushCount);
    }

    [Fact]
    public void RejectsNullPacker()
    {
        var accumulator = new GlyphBatchAccumulator();
        accumulator.Add('A', 1);

        Assert.Throws<ArgumentNullException>(() => accumulator.Flush(null!));
    }
}
