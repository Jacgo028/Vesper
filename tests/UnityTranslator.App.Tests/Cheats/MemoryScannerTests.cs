using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class MemoryScannerTests
{
    [Fact]
    public async Task FindsValueAcrossReadChunks()
    {
        var fake = new FakeMemory(64);
        fake.SetInt32(15, 5301237);
        var hits = await new MemoryScanner(16).FirstExactAsync(fake, NumericKind.Int32,
            BitConverter.GetBytes(5301237), 100, null, CancellationToken.None);
        Assert.Contains(hits, hit => hit.Address == 15);
    }

    [Fact]
    public async Task SkipsUnreadableRegionAndFindsLaterValue()
    {
        var fake = new FakeMemory(64) { UnreadableStart = 16, UnreadableEnd = 32 };
        fake.SetInt32(40, 5301237);
        var hits = await new MemoryScanner(16).FirstExactAsync(fake, NumericKind.Int32,
            BitConverter.GetBytes(5301237), 100, null, CancellationToken.None);
        Assert.Contains(hits, hit => hit.Address == 40);
        Assert.DoesNotContain(hits, hit => hit.Address >= 16 && hit.Address < 32);
    }

    [Fact]
    public async Task ReportsTooBroadInsteadOfReturningTruncatedHits()
    {
        var fake = new FakeMemory(64);
        await Assert.ThrowsAsync<ScanTooBroadException>(() => new MemoryScanner(16).FirstExactAsync(
            fake, NumericKind.Int32, BitConverter.GetBytes(0), 10, null, CancellationToken.None));
    }

    [Fact]
    public async Task RefinesIncreasedDecreasedChangedUnchangedAndExact()
    {
        var fake = new FakeMemory(64);
        fake.SetInt32(4, 5301237);
        fake.SetInt32(16, 5301237);
        fake.SetInt32(28, 5301237);
        var scanner = new MemoryScanner(16);
        var first = await scanner.FirstExactAsync(fake, NumericKind.Int32, BitConverter.GetBytes(5301237),
            100, null, CancellationToken.None);
        fake.SetInt32(4, 5301238);
        fake.SetInt32(16, 5301236);
        Assert.Single(await scanner.RefineAsync(fake, first, ScanFilter.Increased, null, CancellationToken.None));
        Assert.Single(await scanner.RefineAsync(fake, first, ScanFilter.Decreased, null, CancellationToken.None));
        Assert.Equal(2, (await scanner.RefineAsync(fake, first, ScanFilter.Changed, null, CancellationToken.None)).Count);
        Assert.Single(await scanner.RefineAsync(fake, first, ScanFilter.Unchanged, null, CancellationToken.None));
        var exact = await scanner.RefineAsync(fake, first, ScanFilter.Exact, BitConverter.GetBytes(5301238), CancellationToken.None);
        Assert.Equal(4UL, Assert.Single(exact).Address);
    }

    [Fact]
    public async Task CancelsBeforeReadingProcessMemory()
    {
        var fake = new FakeMemory(64);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MemoryScanner(16).FirstExactAsync(
            fake, NumericKind.Int32, BitConverter.GetBytes(53), 100, null, stop.Token));
        Assert.Equal(0, fake.ReadCount);
    }

    private sealed class FakeMemory : IProcessMemory
    {
        private readonly byte[] _data;
        public FakeMemory(int length) => _data = new byte[length];
        public int UnreadableStart { get; set; } = -1;
        public int UnreadableEnd { get; set; } = -1;
        public int ReadCount { get; private set; }
        public bool IsAlive => true;
        public int ProcessId => 123;
        public long StartedUtcTicks => 456;

        public IReadOnlyList<MemoryRegion> Regions() =>
            UnreadableStart < 0
                ? [new MemoryRegion(0, (ulong)_data.Length, true, true)]
                : [new MemoryRegion(0, (ulong)UnreadableStart, true, true),
                   new MemoryRegion((ulong)UnreadableStart, (ulong)(UnreadableEnd - UnreadableStart), false, false),
                   new MemoryRegion((ulong)UnreadableEnd, (ulong)(_data.Length - UnreadableEnd), true, true)];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            ReadCount++;
            if (address > (ulong)_data.Length || destination.Length > _data.Length - (int)address ||
                (UnreadableStart >= 0 && (long)address < UnreadableEnd && (long)address + destination.Length > UnreadableStart))
                return false;
            _data.AsSpan((int)address, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address > (ulong)_data.Length || source.Length > _data.Length - (int)address) return false;
            source.CopyTo(_data.AsSpan((int)address, source.Length));
            return true;
        }

        public void SetInt32(int address, int value) => BitConverter.GetBytes(value).CopyTo(_data, address);
    }
}
