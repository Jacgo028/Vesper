using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Cheats;

public enum WriteStatus
{
    Ok,
    ValueChanged,
    WrongProcess,
    Unreadable,
    WriteFailed
}

public sealed record WriteResult(WriteStatus Status, string Message);

public sealed class CheatSession : IDisposable
{
    private readonly IProcessMemory _memory;
    private readonly int _processId;
    private readonly long _startedUtcTicks;
    private readonly object _gate = new();
    private UndoRecord? _undo;
    private CancellationTokenSource? _freezeStop;
    private Task? _freezeTask;
    private bool _stopped;

    public CheatSession(IProcessMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _processId = memory.ProcessId;
        _startedUtcTicks = memory.StartedUtcTicks;
    }

    public bool IsFreezing => _freezeTask is { IsCompleted: false };

    private bool IsSameProcess => !_stopped && _memory.IsAlive &&
        _memory.ProcessId == _processId && _memory.StartedUtcTicks == _startedUtcTicks;

    public WriteResult TryWrite(ScanHit hit, byte[] expectedBytes, byte[] replacementBytes)
    {
        lock (_gate)
        {
            if (!IsSameProcess) return new WriteResult(WriteStatus.WrongProcess, "游戏进程已退出或发生变化");
            var width = NumericValue.Width(hit.Kind);
            if (expectedBytes.Length != width || replacementBytes.Length != width)
                return new WriteResult(WriteStatus.WriteFailed, "数值长度与类型不符");
            var current = new byte[width];
            if (!_memory.TryRead(hit.Address, current))
                return new WriteResult(WriteStatus.Unreadable, "目标地址已不可读取");
            if (!current.AsSpan().SequenceEqual(expectedBytes))
                return new WriteResult(WriteStatus.ValueChanged, "目标数值已变化，请重新确认");
            if (!_memory.TryWrite(hit.Address, replacementBytes))
                return new WriteResult(WriteStatus.WriteFailed, "目标地址写入失败");
            var readback = new byte[width];
            if (!_memory.TryRead(hit.Address, readback) || !readback.AsSpan().SequenceEqual(replacementBytes))
                return new WriteResult(WriteStatus.WriteFailed, "写入后数值未能通过核对");
            _undo = new UndoRecord(hit.Address, hit.Kind, current, replacementBytes.ToArray());
            return new WriteResult(WriteStatus.Ok, "数值已修改");
        }
    }

    public WriteResult UndoLast() => UndoLast(out _);

    public WriteResult UndoLast(out ScanHit? restored)
    {
        restored = null;
        lock (_gate)
        {
            if (!IsSameProcess) return new WriteResult(WriteStatus.WrongProcess, "游戏进程已退出或发生变化");
            if (_undo is null) return new WriteResult(WriteStatus.WriteFailed, "本次会话没有可撤销的修改");
            var current = new byte[_undo.Replacement.Length];
            if (!_memory.TryRead(_undo.Address, current))
                return new WriteResult(WriteStatus.Unreadable, "目标地址已不可读取");
            if (!current.AsSpan().SequenceEqual(_undo.Replacement))
                return new WriteResult(WriteStatus.ValueChanged, "游戏已再次修改此数值，未执行撤销");
            if (!_memory.TryWrite(_undo.Address, _undo.Original))
                return new WriteResult(WriteStatus.WriteFailed, "撤销写入失败");
            var readback = new byte[_undo.Original.Length];
            if (!_memory.TryRead(_undo.Address, readback) || !readback.AsSpan().SequenceEqual(_undo.Original))
                return new WriteResult(WriteStatus.WriteFailed, "撤销后数值未能通过核对");
            restored = new ScanHit(_undo.Address, _undo.Kind, _undo.Original.ToArray());
            _undo = null;
            return new WriteResult(WriteStatus.Ok, "已撤销本次修改");
        }
    }

    public void StartFreeze(ScanHit hit, byte[] value, TimeSpan interval)
    {
        StopFreeze();
        if (interval < TimeSpan.FromMilliseconds(10)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (value.Length != NumericValue.Width(hit.Kind)) throw new ArgumentException("数值长度与类型不符", nameof(value));
        lock (_gate)
        {
            if (!IsSameProcess) throw new InvalidOperationException("游戏进程已退出或发生变化");
            var current = new byte[value.Length];
            if (!_memory.TryRead(hit.Address, current) || !current.AsSpan().SequenceEqual(hit.LastBytes))
                throw new InvalidOperationException("目标数值已变化，请重新确认");
            var stop = new CancellationTokenSource();
            _freezeStop = stop;
            var frozenValue = value.ToArray();
            _freezeTask = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        lock (_gate)
                        {
                            if (!IsSameProcess) return;
                            var observed = new byte[frozenValue.Length];
                            if (!_memory.TryRead(hit.Address, observed) ||
                                !_memory.TryWrite(hit.Address, frozenValue)) return;
                        }
                        await Task.Delay(interval, stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            }, stop.Token);
        }
    }

    public void StopFreeze()
    {
        var stop = _freezeStop;
        var task = _freezeTask;
        _freezeStop = null;
        _freezeTask = null;
        if (stop is null) return;
        stop.Cancel();
        try { task?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        stop.Dispose();
    }

    public void Stop()
    {
        _stopped = true;
        StopFreeze();
        _undo = null;
    }

    public void Dispose()
    {
        Stop();
        if (_memory is IDisposable disposable) disposable.Dispose();
    }

    private sealed record UndoRecord(ulong Address, NumericKind Kind, byte[] Original, byte[] Replacement);
}
