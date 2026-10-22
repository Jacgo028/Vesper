using System.Threading.Channels;

namespace UnityTranslator.App.ExternalOcr;

internal sealed class F9EventBuffer(TimeProvider clock, bool initiallyDown)
{
    private readonly object _gate = new();
    private readonly Channel<F9InputEvent> _events = Channel.CreateBounded<F9InputEvent>(new BoundedChannelOptions(65)
    { SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });
    private F9InputLease? _target;
    private bool _down = initiallyDown, _failed;
    private long _sequence;
    internal ChannelReader<F9InputEvent> Events => _events.Reader;
    internal void SetTarget(F9InputLease? target)
    {
        lock (_gate)
        {
            if (_failed || _target == target) return;
            _target = target;
            while (_events.Reader.TryRead(out _)) { }
            // Do not rearm a held F9 when focus/session changes.
        }
    }
    internal void InitializeKeyState(bool down) { lock (_gate) _down = down; }
    internal void Key(bool down, nint foreground, int processId)
    {
        lock (_gate)
        {
            if (_failed || down == _down) return;
            _down = down;
            if (!down || _target is not { } target || target.Hwnd != foreground || target.ProcessId != processId) return;
            if (_events.Reader.Count >= 64) { Fail(); return; }
            _events.Writer.TryWrite(new(target, ++_sequence, clock.GetTimestamp()));
        }
    }
    internal void Fail()
    {
        lock (_gate)
        {
            if (_failed) return;
            _failed = true;
            while (_events.Reader.TryRead(out _)) { }
            _events.Writer.TryWrite(new(null, ++_sequence, clock.GetTimestamp(), true));
        }
    }
    internal void Complete() => _events.Writer.TryComplete();
}
