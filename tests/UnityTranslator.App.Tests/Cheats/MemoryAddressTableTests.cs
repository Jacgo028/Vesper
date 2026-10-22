using System.Buffers.Binary;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class MemoryAddressTableTests
{
    // Catches treating address text as decimal or returning cached UI text.
    [Fact]
    public void Reads_hex_addresses_and_returns_verified_numeric_writeback()
    {
        var memory = new FakeMemory();
        memory.SetInt32(0x20, 53);
        using var table = new MemoryAddressTable(memory, 8);
        table.AddEntry(Entry(1, "20"));

        Assert.Equal("53", Assert.Single(table.RefreshEntries()).Value);
        Assert.Equal("99", table.SetEntryValue(1, "99"));
        Assert.Equal(99, memory.GetInt32(0x20));
    }

    // Swapping pointer order or reading a 32-bit target as 64-bit must fail.
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Resolves_module_relative_pointer_chains_in_ce_offset_order(int pointerSize)
    {
        var memory = new FakeMemory();
        memory.SetPointer(0x110, 0x200, pointerSize);
        memory.SetPointer(0x220, 0x300, pointerSize);
        memory.SetInt32(0x308, 123);
        using var table = new MemoryAddressTable(memory, pointerSize,
            name => name == "Game.exe" ? 0x100UL : null);
        table.AddEntry(new CheatTableEntry { Id = 1, Address = "\"Game.exe\"+20-10", Kind = NumericKind.Int32, Offsets = [8, 0x20] });

        var entry = Assert.Single(table.RefreshEntries());

        Assert.Equal("123", entry.Value);
        Assert.Equal("308", entry.ResolvedAddress);
    }

    [Fact]
    public void Preserves_high_64bit_pointer_bits_when_target_is_above_four_gigabytes()
    {
        var memory = new FakeMemory();
        memory.SetPointer(0x100, 0x1_0000_0200, 8);
        memory.Map(0x1_0000_0208, BitConverter.GetBytes(456));
        using var table = new MemoryAddressTable(memory, 8);
        table.AddEntry(new CheatTableEntry { Id = 1, Address = "100", Kind = NumericKind.Int32, Offsets = [8] });

        var entry = Assert.Single(table.RefreshEntries());

        Assert.Equal("456", entry.Value);
        Assert.Equal("100000208", entry.ResolvedAddress);
    }

    [Fact]
    public void Deletes_a_group_with_all_descendants_without_deleting_other_roots()
    {
        using var table = new MemoryAddressTable(new FakeMemory(), 8);
        table.AddEntry(new CheatTableEntry { Id = 1, IsGroup = true });
        table.AddEntry(new CheatTableEntry { Id = 2, ParentId = 1, IsGroup = true });
        table.AddEntry(new CheatTableEntry { Id = 3, ParentId = 2, Address = "20" });
        table.AddEntry(Entry(4, "24"));

        table.RemoveEntry(1);

        Assert.Equal(4, Assert.Single(table.RefreshEntries()).Id);
    }

    [Fact]
    public void Undo_restores_verified_write_but_does_not_overwrite_a_game_change()
    {
        var memory = new FakeMemory();
        memory.SetInt32(0x20, 53);
        using var table = new MemoryAddressTable(memory, 8);
        table.AddEntry(Entry(1, "20"));
        table.SetEntryValue(1, "99");

        Assert.Equal(WriteStatus.Ok, table.UndoLast().Status);
        Assert.Equal(53, memory.GetInt32(0x20));
        table.SetEntryValue(1, "99");
        memory.SetInt32(0x20, 77);
        Assert.Equal(WriteStatus.ValueChanged, table.UndoLast().Status);
        Assert.Equal(77, memory.GetInt32(0x20));
    }

    [Fact]
    public void Undo_refuses_stale_target_when_a_pointer_relocates_after_write()
    {
        var memory = new FakeMemory();
        memory.SetPointer(0x100, 0x200, 8);
        memory.SetInt32(0x208, 53);
        memory.SetInt32(0x308, 77);
        using var table = new MemoryAddressTable(memory, 8);
        table.AddEntry(new CheatTableEntry { Id = 1, Address = "100", Kind = NumericKind.Int32, Offsets = [8] });
        table.SetEntryValue(1, "99");
        memory.SetPointer(0x100, 0x300, 8);

        Assert.Equal(WriteStatus.ValueChanged, table.UndoLast().Status);

        Assert.Equal(99, memory.GetInt32(0x208));
        Assert.Equal(77, memory.GetInt32(0x308));
    }

    [Fact]
    public void Rejects_invalid_numeric_text_and_wrong_process_identity()
    {
        var memory = new FakeMemory();
        memory.SetInt32(0x20, 53);
        using var table = new MemoryAddressTable(memory, 8);
        table.AddEntry(Entry(1, "20"));

        Assert.Throws<ArgumentException>(() => table.SetEntryValue(1, "99999999999999999999"));
        memory.StartedUtcTicks++;
        Assert.Throws<InvalidOperationException>(() => table.SetEntryValue(1, "99"));
        Assert.Equal(WriteStatus.WrongProcess, table.UndoLast().Status);
        Assert.Equal(53, memory.GetInt32(0x20));
    }

    [Fact]
    public void Rejects_script_like_address_expressions_before_read_or_write()
    {
        using var table = new MemoryAddressTable(new FakeMemory(), 8);
        Assert.Throws<ArgumentException>(() => table.AddEntry(Entry(1, "readInteger(20)")));
        Assert.Throws<ArgumentException>(() => table.AddEntry(Entry(2, "[20]+4")));
    }

    [Fact]
    public async Task Freezes_multiple_entries_and_clear_stops_all_future_writes()
    {
        var memory = new FakeMemory();
        memory.SetInt32(0x20, 53);
        memory.SetInt32(0x24, 67);
        using var table = new MemoryAddressTable(memory, 8);
        table.AddEntry(Entry(1, "20"));
        table.AddEntry(Entry(2, "24"));
        table.SetEntryFrozen(1, true);
        table.SetEntryFrozen(2, true);
        memory.SetInt32(0x20, 1);
        memory.SetInt32(0x24, 2);

        Assert.True(await WaitUntil(() => memory.GetInt32(0x20) == 53 && memory.GetInt32(0x24) == 67));
        table.ClearEntries();
        var count = memory.WriteCount;
        await Task.Delay(150);
        Assert.Equal(count, memory.WriteCount);
        Assert.Empty(table.RefreshEntries());
    }

    [Fact]
    public async Task Target_exit_stops_freezes_and_clears_frozen_flags()
    {
        var memory = new FakeMemory();
        memory.SetInt32(0x20, 53);
        using var table = new MemoryAddressTable(memory, 8);
        var entry = Entry(1, "20");
        table.AddEntry(entry);
        table.SetEntryFrozen(1, true);
        Assert.True(await WaitUntil(() => memory.WriteCount > 0));

        memory.IsAlive = false;

        Assert.True(await WaitUntil(() => !entry.Frozen));
        var count = memory.WriteCount;
        await Task.Delay(150);
        Assert.Equal(count, memory.WriteCount);
        Assert.Equal("??", Assert.Single(table.RefreshEntries()).Value);
    }

    [Fact]
    public async Task Editing_a_frozen_value_updates_the_freeze_target_and_undo_releases_it()
    {
        var memory = new FakeMemory();
        memory.SetInt32(0x20, 53);
        using var table = new MemoryAddressTable(memory, 8);
        table.AddEntry(Entry(1, "20"));
        table.SetEntryFrozen(1, true);

        Assert.Equal("99", table.SetEntryValue(1, "99"));
        memory.SetInt32(0x20, 1);
        Assert.True(await WaitUntil(() => memory.GetInt32(0x20) == 99));
        Assert.Equal(WriteStatus.Ok, table.UndoLast().Status);
        await Task.Delay(150);

        Assert.Equal(53, memory.GetInt32(0x20));
        Assert.False(Assert.Single(table.RefreshEntries()).Frozen);
    }

    private static CheatTableEntry Entry(int id, string address) => new() { Id = id, Address = address, Kind = NumericKind.Int32 };
    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private sealed class FakeMemory : IProcessMemory
    {
        private readonly object _gate = new();
        private readonly byte[] _bytes = new byte[4096];
        private readonly Dictionary<ulong, byte> _highBytes = [];
        private int _writeCount;
        public bool IsAlive { get; set; } = true;
        public int ProcessId { get; set; } = 123;
        public long StartedUtcTicks { get; set; } = 456;
        public int WriteCount => Volatile.Read(ref _writeCount);
        public IReadOnlyList<MemoryRegion> Regions() => [new(0, (ulong)_bytes.Length, true, true)];
        public bool TryRead(ulong address, Span<byte> destination)
        {
            lock (_gate)
            {
                if (!IsAlive) return false;
                if (address >= (ulong)_bytes.Length)
                {
                    for (var i = 0; i < destination.Length; i++)
                    {
                        if (!_highBytes.TryGetValue(address + (ulong)i, out var value)) return false;
                        destination[i] = value;
                    }
                    return true;
                }
                if (destination.Length > _bytes.Length - (int)address) return false;
                _bytes.AsSpan((int)address, destination.Length).CopyTo(destination);
                return true;
            }
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (!IsAlive || address > (ulong)_bytes.Length || source.Length > _bytes.Length - (int)address) return false;
                source.CopyTo(_bytes.AsSpan((int)address, source.Length));
                Interlocked.Increment(ref _writeCount);
                return true;
            }
        }
        public void SetInt32(int address, int value)
        { lock (_gate) BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(address), value); }
        public int GetInt32(int address)
        { lock (_gate) return BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(address)); }
        public void SetPointer(int address, ulong value, int pointerSize)
        {
            lock (_gate)
            {
                if (pointerSize == 4) BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(address), (uint)value);
                else BinaryPrimitives.WriteUInt64LittleEndian(_bytes.AsSpan(address), value);
            }
        }
        public void Map(ulong address, byte[] bytes)
        { lock (_gate) for (var i = 0; i < bytes.Length; i++) _highBytes[address + (ulong)i] = bytes[i]; }
    }
}
