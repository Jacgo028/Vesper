using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class CheatSessionUndoReadbackTests
{
    [Fact]
    public void SuccessfulUndoReturnsOnlyTheVerifiedOriginalTargetAndConsumesHistory()
    {
        var memory = new UndoMemory();
        using var session = new CheatSession(memory);
        var hit = new ScanHit(0x1000, NumericKind.Float32, BitConverter.GetBytes(12.5f));
        Assert.Equal(WriteStatus.Ok, session.TryWrite(hit, hit.LastBytes, BitConverter.GetBytes(99f)).Status);

        Assert.Equal(WriteStatus.Ok, session.UndoLast(out var restored).Status);

        Assert.NotNull(restored);
        Assert.Equal(0x1000UL, restored.Address);
        Assert.Equal(NumericKind.Float32, restored.Kind);
        Assert.Equal(BitConverter.GetBytes(12.5f), restored.LastBytes);
        Assert.NotSame(hit.LastBytes, restored.LastBytes);
        Assert.Equal(BitConverter.GetBytes(12.5f), memory.Bytes);
        Assert.Equal(WriteStatus.WriteFailed, session.UndoLast(out var absent).Status);
        Assert.Null(absent);
    }

    [Theory]
    [InlineData("changed", WriteStatus.ValueChanged)]
    [InlineData("process", WriteStatus.WrongProcess)]
    [InlineData("unreadable", WriteStatus.Unreadable)]
    [InlineData("write", WriteStatus.WriteFailed)]
    [InlineData("readback", WriteStatus.WriteFailed)]
    public void FailedUndoReturnsNoRestoredTargetAndKeepsHistoryForRetry(string failure, WriteStatus expected)
    {
        var memory = new UndoMemory();
        using var session = new CheatSession(memory);
        var hit = new ScanHit(0x1000, NumericKind.Float32, BitConverter.GetBytes(12.5f));
        Assert.Equal(WriteStatus.Ok, session.TryWrite(hit, hit.LastBytes, BitConverter.GetBytes(99f)).Status);
        if (failure == "changed") memory.Bytes = BitConverter.GetBytes(77f);
        else if (failure == "process") memory.StartedUtcTicks++;
        else if (failure == "unreadable") memory.Readable = false;
        else if (failure == "write") memory.Writable = false;
        else memory.CorruptAfterWrite = true;
        var before = memory.Bytes.ToArray();

        Assert.Equal(expected, session.UndoLast(out var restored).Status);

        Assert.Null(restored);
        if (failure != "readback") Assert.Equal(before, memory.Bytes);
        memory.Readable = true;
        memory.Writable = true;
        memory.CorruptAfterWrite = false;
        memory.StartedUtcTicks = 72;
        memory.Bytes = BitConverter.GetBytes(99f);
        Assert.Equal(WriteStatus.Ok, session.UndoLast(out restored).Status);
        Assert.NotNull(restored);
        Assert.Equal(BitConverter.GetBytes(12.5f), restored.LastBytes);
        Assert.Equal(BitConverter.GetBytes(12.5f), memory.Bytes);
    }

    private sealed class UndoMemory : IProcessMemory
    {
        public byte[] Bytes { get; set; } = BitConverter.GetBytes(12.5f);
        public int ProcessId => 71;
        public long StartedUtcTicks { get; set; } = 72;
        public bool IsAlive => true;
        public bool Readable { get; set; } = true;
        public bool Writable { get; set; } = true;
        public bool CorruptAfterWrite { get; set; }
        public IReadOnlyList<MemoryRegion> Regions() => [new(0x1000, 4, true, true)];
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (!Readable || address != 0x1000 || destination.Length != 4) return false;
            Bytes.CopyTo(destination);
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (!Writable || address != 0x1000 || source.Length != 4) return false;
            Bytes = source.ToArray();
            if (CorruptAfterWrite) Bytes[0]++;
            return true;
        }
    }
}
