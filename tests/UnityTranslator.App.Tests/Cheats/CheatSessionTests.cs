using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class CheatSessionTests
{
    [Fact]
    public void WritesOnlyIfCurrentValueStillMatchesAndCanUndo()
    {
        var fake = new FakeMemory();
        fake.SetInt32(8, 53);
        using var session = new CheatSession(fake);
        var hit = new ScanHit(8, NumericKind.Int32, BitConverter.GetBytes(53));
        Assert.Equal(WriteStatus.ValueChanged,
            session.TryWrite(hit, BitConverter.GetBytes(52), BitConverter.GetBytes(99)).Status);
        Assert.Equal(53, fake.GetInt32(8));
        Assert.Equal(WriteStatus.Ok,
            session.TryWrite(hit, BitConverter.GetBytes(53), BitConverter.GetBytes(99)).Status);
        Assert.Equal(99, fake.GetInt32(8));
        Assert.Equal(WriteStatus.Ok, session.UndoLast().Status);
        Assert.Equal(53, fake.GetInt32(8));
    }

    [Fact]
    public void RefusesWriteAfterProcessIdentityChanges()
    {
        var fake = new FakeMemory();
        fake.SetInt32(8, 53);
        using var session = new CheatSession(fake);
        fake.StartedUtcTicks++;
        var hit = new ScanHit(8, NumericKind.Int32, BitConverter.GetBytes(53));
        Assert.Equal(WriteStatus.WrongProcess,
            session.TryWrite(hit, BitConverter.GetBytes(53), BitConverter.GetBytes(99)).Status);
        Assert.Equal(53, fake.GetInt32(8));
    }

    [Fact]
    public void UndoWillNotOverwriteAValueTheGameChangedAgain()
    {
        var fake = new FakeMemory();
        fake.SetInt32(8, 53);
        using var session = new CheatSession(fake);
        var hit = new ScanHit(8, NumericKind.Int32, BitConverter.GetBytes(53));
        Assert.Equal(WriteStatus.Ok,
            session.TryWrite(hit, BitConverter.GetBytes(53), BitConverter.GetBytes(99)).Status);
        fake.SetInt32(8, 77);
        Assert.Equal(WriteStatus.ValueChanged, session.UndoLast().Status);
        Assert.Equal(77, fake.GetInt32(8));
    }

    [Fact]
    public async Task FreezeStopsWhenProcessExits()
    {
        var fake = new FakeMemory { StopOnFirstWrite = true };
        fake.SetInt32(8, 53);
        using var session = new CheatSession(fake);
        session.StartFreeze(new ScanHit(8, NumericKind.Int32, BitConverter.GetBytes(53)),
            BitConverter.GetBytes(99), TimeSpan.FromMilliseconds(10));
        await Task.Delay(100);
        Assert.Equal(1, fake.WriteCount);
        Assert.False(session.IsFreezing);
        Assert.Equal(99, fake.GetInt32(8));
    }

    [Fact]
    public async Task ExplicitStopEndsFreezeWrites()
    {
        var fake = new FakeMemory();
        fake.SetInt32(8, 53);
        using var session = new CheatSession(fake);
        session.StartFreeze(new ScanHit(8, NumericKind.Int32, BitConverter.GetBytes(53)),
            BitConverter.GetBytes(99), TimeSpan.FromMilliseconds(10));
        await Task.Delay(40);
        session.StopFreeze();
        var writes = fake.WriteCount;
        await Task.Delay(40);
        Assert.Equal(writes, fake.WriteCount);
    }

    private sealed class FakeMemory : IProcessMemory
    {
        private readonly byte[] _bytes = new byte[64];
        public bool IsAlive { get; private set; } = true;
        public int ProcessId { get; set; } = 123;
        public long StartedUtcTicks { get; set; } = 456;
        public bool StopOnFirstWrite { get; set; }
        public int WriteCount { get; private set; }
        public IReadOnlyList<MemoryRegion> Regions() => [new(0, (ulong)_bytes.Length, true, true)];
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (!IsAlive || address > (ulong)_bytes.Length || destination.Length > _bytes.Length - (int)address) return false;
            _bytes.AsSpan((int)address, destination.Length).CopyTo(destination);
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (!IsAlive || address > (ulong)_bytes.Length || source.Length > _bytes.Length - (int)address) return false;
            source.CopyTo(_bytes.AsSpan((int)address, source.Length));
            WriteCount++;
            if (StopOnFirstWrite) IsAlive = false;
            return true;
        }
        public void SetInt32(int address, int value) => BitConverter.GetBytes(value).CopyTo(_bytes, address);
        public int GetInt32(int address) => BitConverter.ToInt32(_bytes, address);
    }
}
