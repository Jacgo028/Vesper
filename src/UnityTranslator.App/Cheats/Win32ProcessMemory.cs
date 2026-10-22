using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnityTranslator.App.Cheats;

public sealed class Win32ProcessMemory : IProcessMemory, IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint PageGuard = 0x100;
    private readonly Process _process;
    private readonly SafeProcessHandle _readHandle;
    private SafeProcessHandle? _writeHandle;

    public int ProcessId { get; }
    public long StartedUtcTicks { get; }
    public int PointerSize { get; }

    public Win32ProcessMemory(Process process)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("数值扫描需要 Windows x64 进程。");
        _process = process ?? throw new ArgumentNullException(nameof(process));
        ProcessId = process.Id;
        StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        _readHandle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, ProcessId);
        if (_readHandle.IsInvalid)
        {
            _readHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取游戏进程内存");
        }
        if(!IsWow64Process(_readHandle,out var wow64))
        {
            _readHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(),"无法确认游戏指针宽度");
        }
        PointerSize=wow64?4:8;
    }

    public bool IsAlive
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }
    }

    public IReadOnlyList<MemoryRegion> Regions()
    {
        var regions = new List<MemoryRegion>();
        if (!IsAlive) return regions;
        nuint cursor = 0;
        var infoSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQueryEx(_readHandle, cursor, out var info, infoSize) == infoSize)
        {
            var address = (ulong)info.BaseAddress;
            var size = (ulong)info.RegionSize;
            if (size == 0 || address > ulong.MaxValue - size) break;
            var baseProtection = info.Protect & 0xFF;
            var readable = info.State == MemCommit && (info.Protect & PageGuard) == 0 &&
                baseProtection is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
            var writable = readable && baseProtection is 0x04 or 0x08 or 0x40 or 0x80;
            if (readable) regions.Add(new MemoryRegion(address, size, true, writable));
            var next = address + size;
            if (next <= (ulong)cursor) break;
            cursor = (nuint)next;
        }
        return regions;
    }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (!IsAlive || _readHandle.IsClosed) return false;
        if (destination.Length == 0) return true;
        var buffer = new byte[destination.Length];
        if (!ReadProcessMemory(_readHandle, (nuint)address, buffer, (nuint)buffer.Length, out var bytesRead) ||
            bytesRead != (nuint)buffer.Length) return false;
        buffer.CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
    {
        if (!IsAlive || _readHandle.IsClosed) return false;
        if (source.Length == 0) return true;
        _writeHandle ??= OpenProcess(ProcessVmWrite | ProcessVmOperation, false, ProcessId);
        if (_writeHandle.IsInvalid) return false;
        var buffer = source.ToArray();
        return WriteProcessMemory(_writeHandle, (nuint)address, buffer, (nuint)buffer.Length, out var bytesWritten) &&
               bytesWritten == (nuint)buffer.Length;
    }

    public void Dispose()
    {
        _writeHandle?.Dispose();
        _readHandle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nuint BaseAddress;
        public nuint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(SafeProcessHandle process,[MarshalAs(UnmanagedType.Bool)]out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nuint address,
        out MemoryBasicInformation information, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nuint address,
        [Out] byte[] buffer, nuint length, out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(SafeProcessHandle process, nuint address,
        byte[] buffer, nuint length, out nuint bytesWritten);
}
