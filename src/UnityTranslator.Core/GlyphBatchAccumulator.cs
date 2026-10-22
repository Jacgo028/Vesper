using System;
using System.Collections.Generic;

namespace UnityTranslator.Core;

/// <summary>
/// 字形批处理簿记：把"收集字形 → 批量渲进图集 → 写回字形表/字符表"这套账目算清楚，
/// **刻意不依赖 UnityEngine**，这样它能被离线单元测试覆盖。
///
/// 为什么需要它（本轮实测踩到的两个坑，都会让画面变成一片方框）：
///
///   1. **引擎会清空传入的"待装列表"**。
///      <c>FontEngine.TryAddGlyphsToTexture</c> 把装成功的字形挪到输出列表，
///      传进去的那个列表调用后就是空的。若之后还拿同一个列表做第二次遍历，
///      遍历到的是空列表 —— 实测结果：字形表 4214 项，**字符表只有 1 项**，
///      <c>HasCharacter</c> 全部为假，画面全是方框。
///      所以必须先抄一份"字符 ↔ 字形"清单，再交给引擎。
///
///   2. **逐个字形调用会让 Unity 原生崩溃**。
///      实测：一次只装 1 个字形时，装到 1200~2500 次之间进程必崩
///      （0xc0000005，UnityPlayer.dll）。所以必须攒批，每批 32 个。
///      批次账目（每批收多少、渲出多少、累计多少）就由这个类维护。
/// </summary>
public sealed class GlyphBatchAccumulator
{
    /// <summary>一批最多攒多少字形。32 是"原生调用次数足够少"与"整批失败损失可控"的折中。</summary>
    public const int DefaultBatchSize = 32;

    private readonly List<Entry> _batch = new List<Entry>();
    private readonly Dictionary<uint, char> _characterByGlyphIndex = new Dictionary<uint, char>();

    public GlyphBatchAccumulator(int batchSize = DefaultBatchSize)
    {
        BatchSize = batchSize > 0 ? batchSize : DefaultBatchSize;
    }

    /// <summary>每批上限。</summary>
    public int BatchSize { get; }

    /// <summary>当前这批攒了多少个。</summary>
    public int PendingCount => _batch.Count;

    /// <summary>是否已经攒满一批、该交给引擎了。</summary>
    public bool ShouldFlush => _batch.Count >= BatchSize;

    /// <summary>累计收下的字形数（各批之和，用于对账）。</summary>
    public int CollectedTotal { get; private set; }

    /// <summary>累计交给引擎的批次数。</summary>
    public int FlushCount { get; private set; }

    /// <summary>累计从引擎拿回的回填字形数。</summary>
    public int RenderedTotal { get; private set; }

    /// <summary>累计写进字符表的字符数。</summary>
    public int CharacterTotal { get; private set; }

    /// <summary>累计判定为"装不上"的字符数。</summary>
    public int FailedTotal { get; private set; }

    /// <summary>装不上的字符（按发现顺序）。</summary>
    public List<char> FailedCharacters { get; } = new List<char>();

    /// <summary>已经收过的字形 index，用于跨批去重。</summary>
    private readonly HashSet<uint> _known = new HashSet<uint>();

    /// <summary>
    /// 收一个字形。返回 true 表示真的收下了（重复 index 会被忽略）。
    /// </summary>
    public bool Add(char character, uint glyphIndex)
    {
        if (character == '\0') return false;
        if (_known.Contains(glyphIndex)) return false;

        _known.Add(glyphIndex);
        _characterByGlyphIndex[glyphIndex] = character;
        _batch.Add(new Entry(character, glyphIndex));
        CollectedTotal++;
        return true;
    }

    /// <summary>
    /// 把当前这批交给 <paramref name="packer"/>，并用它**回填后**的结果更新账目。
    ///
    /// <paramref name="packer"/> 收到的列表**可能被它清空**（真实的 FontEngine 就是这样），
    /// 所以这里传的是副本；账目用的是我们自己保存的清单。
    /// 返回本批渲出的字形数。
    /// </summary>
    public int Flush(Func<List<uint>, List<uint>> packer)
    {
        if (_batch.Count == 0) return 0;
        if (packer is null) throw new ArgumentNullException(nameof(packer));

        // ★ 先抄本批清单：引擎会清空传进去的列表，不能拿它做第二遍遍历
        var entries = new List<Entry>(_batch);
        _batch.Clear();

        var indexesToPack = new List<uint>(entries.Count);
        foreach (var entry in entries) indexesToPack.Add(entry.GlyphIndex);

        var rendered = packer(indexesToPack) ?? new List<uint>();
        var renderedSet = new HashSet<uint>(rendered);

        FlushCount++;
        RenderedTotal += rendered.Count;

        foreach (var entry in entries)
        {
            if (renderedSet.Contains(entry.GlyphIndex))
            {
                CharacterTotal++;
            }
            else
            {
                FailedTotal++;
                FailedCharacters.Add(entry.Character);
            }
        }

        return rendered.Count;
    }

    /// <summary>取出某个字形 index 对应的字符；没有返回 '\0'。</summary>
    public char CharacterOf(uint glyphIndex) =>
        _characterByGlyphIndex.TryGetValue(glyphIndex, out var value) ? value : '\0';

    /// <summary>反查表大小（应等于 <see cref="CollectedTotal"/>）。</summary>
    public int CharacterMapSize => _characterByGlyphIndex.Count;

    private readonly struct Entry
    {
        public Entry(char character, uint glyphIndex)
        {
            Character = character;
            GlyphIndex = glyphIndex;
        }

        public char Character { get; }
        public uint GlyphIndex { get; }
    }
}
