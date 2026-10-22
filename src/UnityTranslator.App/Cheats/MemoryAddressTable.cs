using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Cheats;

/// <summary>A numeric address table bound to one selected process.</summary>
public sealed class MemoryAddressTable : IDisposable
{
    private static readonly Regex OffsetPattern = new("(?<sign>[+-])\\s*(?<hex>(?:0[xX])?[0-9A-Fa-f]+)", RegexOptions.CultureInvariant);
    private readonly IProcessMemory _memory;
    private readonly int _pointerSize;
    private readonly int _processId;
    private readonly long _startedUtcTicks;
    private readonly Func<string, ulong?> _moduleResolver;
    private readonly object _gate = new();
    private readonly List<CheatTableEntry> _entries = [];
    private readonly Dictionary<int, byte[]> _frozen = [];
    private readonly Timer _timer;
    private UndoRecord? _undo;
    private bool _disposed;

    public MemoryAddressTable(IProcessMemory memory, int pointerSize, Func<string, ulong?>? moduleResolver = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize), "指针宽度必须为 4 或 8 字节。");
        _pointerSize = pointerSize;
        _processId = memory.ProcessId;
        _startedUtcTicks = memory.StartedUtcTicks;
        _moduleResolver = moduleResolver ?? ResolveSelectedProcessModule;
        _timer = new Timer(FreezeTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsFreezing
    {
        get { lock (_gate) return IsSameProcess && _frozen.Count > 0; }
    }

    private bool IsSameProcess => !_disposed && _memory.IsAlive
        && _memory.ProcessId == _processId && _memory.StartedUtcTicks == _startedUtcTicks;

    public void AddEntry(CheatTableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!entry.IsGroup)
            {
                ValidateExpression(entry.Address);
                _ = NumericValue.Width(entry.Kind);
                if (entry.Offsets is null || entry.Offsets.Length > 32)
                    throw new ArgumentException("单条地址的指针层数不能超过 32。", nameof(entry));
            }
            if (entry.Id <= 0) entry.Id = _entries.Count == 0 ? 1 : checked(_entries.Max(x => x.Id) + 1);
            if (_entries.Any(x => x.Id == entry.Id)) throw new ArgumentException("地址项目编号重复。", nameof(entry));
            if (entry.ParentId == entry.Id) throw new ArgumentException("分组不能包含自身。", nameof(entry));
            entry.Frozen = false;
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<CheatTableEntry> RefreshEntries()
    {
        lock (_gate)
        {
            if (!IsSameProcess) StopFreezes();
            foreach (var entry in _entries)
            {
                if (entry.IsGroup) continue;
                entry.Value = "??";
                entry.ResolvedAddress = "";
                if (!IsSameProcess) continue;
                try
                {
                    var address = Resolve(entry);
                    entry.ResolvedAddress = address.ToString("X", CultureInfo.InvariantCulture);
                    var bytes = Read(address, entry.Kind);
                    entry.Value = NumericValue.Format(bytes, entry.Kind);
                }
                catch (Exception ex) when (IsAddressError(ex)) { }
            }
            return _entries.ToArray();
        }
    }

    public string SetEntryValue(int id, string value)
    {
        lock (_gate)
        {
            EnsureProcess();
            var entry = FindNumericEntry(id);
            if (!NumericValue.TryParse(value, entry.Kind, out var replacement))
                throw new ArgumentException("请输入与所选类型匹配的有效数值。", nameof(value));
            var address = Resolve(entry);
            var original = Read(address, entry.Kind);
            EnsureProcess();
            if (!_memory.TryWrite(address, replacement)) throw new InvalidOperationException("目标地址写入失败。");
            var readback = Read(address, entry.Kind);
            if (!readback.AsSpan().SequenceEqual(replacement)) throw new InvalidOperationException("写入后数值未能通过核对。");
            _undo = new UndoRecord(id, address, original, replacement.ToArray());
            if (_frozen.ContainsKey(id)) _frozen[id] = replacement.ToArray();
            entry.ResolvedAddress = address.ToString("X", CultureInfo.InvariantCulture);
            return entry.Value = NumericValue.Format(readback, entry.Kind);
        }
    }

    public void SetEntryFrozen(int id, bool frozen)
    {
        lock (_gate)
        {
            var entry = FindNumericEntry(id);
            if (!frozen)
            {
                _frozen.Remove(id);
                entry.Frozen = false;
                if (_frozen.Count == 0 && !_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            EnsureProcess();
            var address = Resolve(entry);
            var bytes = Read(address, entry.Kind);
            _frozen[id] = bytes;
            entry.Frozen = true;
            entry.Value = NumericValue.Format(bytes, entry.Kind);
            entry.ResolvedAddress = address.ToString("X", CultureInfo.InvariantCulture);
            _timer.Change(60, 60);
        }
    }

    public void ClearEntries()
    {
        lock (_gate)
        {
            StopFreezes();
            _entries.Clear();
            _undo = null;
        }
    }

    public void RemoveEntry(int id)
    {
        lock (_gate)
        {
            var removing = new HashSet<int> { id };
            bool changed;
            do
            {
                changed = false;
                foreach (var entry in _entries)
                    if (entry.ParentId is int parent && removing.Contains(parent) && removing.Add(entry.Id)) changed = true;
            } while (changed);
            foreach (var entry in _entries.Where(x => removing.Contains(x.Id)))
            {
                entry.Frozen = false;
                _frozen.Remove(entry.Id);
            }
            _entries.RemoveAll(x => removing.Contains(x.Id));
            if (_undo is not null && removing.Contains(_undo.EntryId)) _undo = null;
            if (_frozen.Count == 0 && !_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public WriteResult UndoLast()
    {
        lock (_gate)
        {
            if (!IsSameProcess)
            {
                StopFreezes();
                return new(WriteStatus.WrongProcess, "游戏进程已退出或发生变化");
            }
            if (_undo is null) return new(WriteStatus.WriteFailed, "本次会话没有可撤销的修改");
            var sourceEntry = _entries.FirstOrDefault(x => x.Id == _undo.EntryId);
            if (sourceEntry is null) return new(WriteStatus.ValueChanged, "原地址项目已移除，未执行撤销");
            try
            {
                if (Resolve(sourceEntry) != _undo.Address)
                    return new(WriteStatus.ValueChanged, "指针目标已变化，未执行撤销");
            }
            catch (Exception ex) when (IsAddressError(ex))
            {
                return new(WriteStatus.Unreadable, "原地址已无法解析，未执行撤销");
            }
            var current = new byte[_undo.Replacement.Length];
            if (!_memory.TryRead(_undo.Address, current)) return new(WriteStatus.Unreadable, "目标地址已不可读取");
            if (!current.AsSpan().SequenceEqual(_undo.Replacement)) return new(WriteStatus.ValueChanged, "游戏已再次修改此数值，未执行撤销");
            // Releasing matching freezes prevents a later timer tick from
            // immediately undoing the user's requested undo operation.
            foreach (var entry in _entries.Where(x => _frozen.ContainsKey(x.Id)).ToArray())
            {
                try { if (Resolve(entry) != _undo.Address) continue; }
                catch (Exception ex) when (IsAddressError(ex)) { continue; }
                _frozen.Remove(entry.Id);
                entry.Frozen = false;
            }
            if (!_memory.TryWrite(_undo.Address, _undo.Original)) return new(WriteStatus.WriteFailed, "撤销写入失败");
            var readback = new byte[_undo.Original.Length];
            if (!_memory.TryRead(_undo.Address, readback) || !readback.AsSpan().SequenceEqual(_undo.Original))
                return new(WriteStatus.WriteFailed, "撤销后数值未能通过核对");
            _undo = null;
            if (_frozen.Count == 0) _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return new(WriteStatus.Ok, "已撤销本次修改");
        }
    }

    private void FreezeTick(object? state)
    {
        lock (_gate)
        {
            if (!IsSameProcess)
            {
                StopFreezes();
                return;
            }
            foreach (var id in _frozen.Keys.ToArray())
            {
                var entry = _entries.FirstOrDefault(x => x.Id == id);
                if (entry is null) { _frozen.Remove(id); continue; }
                try
                {
                    var address = Resolve(entry);
                    var target = _frozen[id];
                    _ = Read(address, entry.Kind);
                    if (!IsSameProcess || !_memory.TryWrite(address, target)) throw new InvalidOperationException("锁定写入失败。");
                    var readback = Read(address, entry.Kind);
                    if (!readback.AsSpan().SequenceEqual(target)) throw new InvalidOperationException("锁定数值未能通过核对。");
                    entry.Value = NumericValue.Format(readback, entry.Kind);
                    entry.ResolvedAddress = address.ToString("X", CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    entry.Frozen = false;
                    entry.Value = "??";
                    _frozen.Remove(id);
                }
            }
            if (_frozen.Count == 0) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void StopFreezes()
    {
        foreach (var entry in _entries) entry.Frozen = false;
        _frozen.Clear();
        if (!_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void EnsureProcess()
    {
        if (IsSameProcess) return;
        StopFreezes();
        throw new InvalidOperationException("游戏进程已退出或发生变化。");
    }

    private CheatTableEntry FindNumericEntry(int id)
    {
        var entry = _entries.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("未找到所选地址项目。", nameof(id));
        if (entry.IsGroup) throw new ArgumentException("分组不能修改或锁定数值。", nameof(id));
        return entry;
    }

    private byte[] Read(ulong address, NumericKind kind)
    {
        var bytes = new byte[NumericValue.Width(kind)];
        if (!_memory.TryRead(address, bytes)) throw new InvalidOperationException("目标地址已不可读取。");
        return bytes;
    }

    private ulong Resolve(CheatTableEntry entry)
    {
        var expression = ValidateExpression(entry.Address);
        var module = expression.Groups["module"];
        var address = module.Success ? _moduleResolver(module.Value)
            ?? throw new InvalidOperationException("所选进程中找不到模块：" + module.Value)
            : Hex(expression.Groups["hex"].Value);
        foreach (Match offset in OffsetPattern.Matches(expression.Groups["offsets"].Value))
        {
            var magnitude = Hex(offset.Groups["hex"].Value);
            address = offset.Groups["sign"].Value == "+" ? checked(address + magnitude) : checked(address - magnitude);
        }
        foreach (var offset in entry.Offsets.Reverse())
        {
            var pointer = new byte[_pointerSize];
            if (!_memory.TryRead(address, pointer)) throw new InvalidOperationException("指针地址已不可读取。");
            address = _pointerSize == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(pointer) : BinaryPrimitives.ReadUInt64LittleEndian(pointer);
            if (address == 0) throw new InvalidOperationException("指针尚未指向有效地址。");
            address = offset >= 0 ? checked(address + (ulong)offset)
                : checked(address - ((ulong)(-(offset + 1)) + 1));
        }
        return address;
    }

    private static Match ValidateExpression(string address)
    {
        return CheatTableDocument.ParseAddressExpression(address);
    }

    private static ulong Hex(string input) => ulong.Parse(input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? input[2..] : input,
        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private ulong? ResolveSelectedProcessModule(string name)
    {
        if (!IsSameProcess) return null;
        try
        {
            using var process = Process.GetProcessById(_processId);
            if (process.StartTime.ToUniversalTime().Ticks != _startedUtcTicks) return null;
            foreach (ProcessModule module in process.Modules)
                if (string.Equals(module.ModuleName, name, StringComparison.OrdinalIgnoreCase))
                    return unchecked((ulong)module.BaseAddress.ToInt64());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private static bool IsAddressError(Exception ex) => ex is ArgumentException or InvalidOperationException or OverflowException or FormatException
        or System.ComponentModel.Win32Exception;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopFreezes();
            _disposed = true;
            _undo = null;
            _timer.Dispose();
        }
    }

    private sealed record UndoRecord(int EntryId, ulong Address, byte[] Original, byte[] Replacement);
}
