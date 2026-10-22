using System.IO;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Cheats;

public sealed record ScanHit(ulong Address, NumericKind Kind, byte[] LastBytes);
public sealed record ScanProgress(ulong BytesRead, ulong BytesTotal, int Hits);

public sealed class ScanTooBroadException : Exception
{
    public ScanTooBroadException(int limit) : base($"匹配超过 {limit} 项，请输入更有区分度的数值。") { }
}

public sealed class MemoryScanner
{
    private readonly int _chunkSize;

    public MemoryScanner(int chunkSize = 64 * 1024)
    {
        if (chunkSize < 8) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _chunkSize = chunkSize;
    }

    public Task<IReadOnlyList<ScanHit>> FirstExactAsync(IProcessMemory memory, NumericKind kind,
        byte[] expectedBytes, int maxHits, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (expectedBytes.Length != NumericValue.Width(kind)) throw new ArgumentException("数值长度与类型不符", nameof(expectedBytes));
        if (maxHits < 1) throw new ArgumentOutOfRangeException(nameof(maxHits));
        return Task.Run<IReadOnlyList<ScanHit>>(() =>
        {
            var regions = memory.Regions().Where(region => region.Readable && region.Size >= (ulong)expectedBytes.Length).ToArray();
            var total = regions.Aggregate(0UL, (sum, region) => sum + region.Size);
            ulong bytesRead = 0;
            var hits = new List<ScanHit>();
            var width = expectedBytes.Length;
            foreach (var region in regions)
            {
                for (ulong offset = 0; offset < region.Size;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!memory.IsAlive) throw new IOException("游戏进程已退出");
                    var logicalLength = (int)Math.Min((ulong)_chunkSize, region.Size - offset);
                    var readLength = (int)Math.Min((ulong)logicalLength + (ulong)(width - 1), region.Size - offset);
                    var buffer = new byte[readLength];
                    if (memory.TryRead(region.Base + offset, buffer))
                    {
                        var maxStart = Math.Min(logicalLength, readLength - width + 1);
                        for (var index = 0; index < maxStart; index++)
                        {
                            if (buffer.AsSpan(index, width).SequenceEqual(expectedBytes))
                            {
                                hits.Add(new ScanHit(region.Base + offset + (ulong)index, kind,
                                    buffer.AsSpan(index, width).ToArray()));
                                if (hits.Count > maxHits) throw new ScanTooBroadException(maxHits);
                            }
                        }
                    }
                    offset += (ulong)logicalLength;
                    bytesRead += (ulong)logicalLength;
                    progress?.Report(new ScanProgress(bytesRead, total, hits.Count));
                }
            }
            return hits;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ScanHit>> RefineAsync(IProcessMemory memory, IReadOnlyList<ScanHit> previous,
        ScanFilter filter, byte[]? exactBytes, CancellationToken cancellationToken)
    {
        if (filter == ScanFilter.Exact && exactBytes is null) throw new ArgumentNullException(nameof(exactBytes));
        return Task.Run<IReadOnlyList<ScanHit>>(() =>
        {
            var hits = new List<ScanHit>();
            foreach (var hit in previous)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!memory.IsAlive) throw new IOException("游戏进程已退出");
                var width = NumericValue.Width(hit.Kind);
                if (hit.LastBytes.Length != width) continue;
                if (filter == ScanFilter.Exact && exactBytes!.Length != width)
                    throw new ArgumentException("数值长度与类型不符", nameof(exactBytes));
                var current = new byte[width];
                if (!memory.TryRead(hit.Address, current)) continue;
                bool keep;
                if (filter == ScanFilter.Exact)
                    keep = current.AsSpan().SequenceEqual(exactBytes);
                else
                {
                    int comparison;
                    try { comparison = NumericValue.Compare(current, hit.LastBytes, hit.Kind); }
                    catch (ArgumentException) { continue; }
                    keep = filter switch
                    {
                        ScanFilter.Increased => comparison > 0,
                        ScanFilter.Decreased => comparison < 0,
                        ScanFilter.Changed => comparison != 0,
                        ScanFilter.Unchanged => comparison == 0,
                        _ => throw new ArgumentOutOfRangeException(nameof(filter))
                    };
                }
                if (keep) hits.Add(new ScanHit(hit.Address, hit.Kind, current));
            }
            return hits;
        }, cancellationToken);
    }
}
