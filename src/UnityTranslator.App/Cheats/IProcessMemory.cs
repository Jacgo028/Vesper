namespace UnityTranslator.App.Cheats;

public sealed record MemoryRegion(ulong Base, ulong Size, bool Readable, bool Writable);

public interface IProcessMemory
{
    IReadOnlyList<MemoryRegion> Regions();
    bool TryRead(ulong address, Span<byte> destination);
    bool TryWrite(ulong address, ReadOnlySpan<byte> source);
    bool IsAlive { get; }
    int ProcessId { get; }
    long StartedUtcTicks { get; }
}
