using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityTranslator.App.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class Win32ProcessMemoryTests
{
    [Fact]
    public void ReadsAndWritesACommittedRegionInCurrentProcess()
    {
        var address = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.WriteInt32(address, 5301237);
            using var memory = new Win32ProcessMemory(Process.GetCurrentProcess());
            var numericAddress = (ulong)address.ToInt64();
            Assert.Contains(memory.Regions(), region => region.Readable && region.Writable &&
                numericAddress >= region.Base && numericAddress < region.Base + region.Size);
            var bytes = new byte[4];
            Assert.True(memory.TryRead(numericAddress, bytes));
            Assert.Equal(5301237, BitConverter.ToInt32(bytes));
            Assert.True(memory.TryWrite(numericAddress, BitConverter.GetBytes(5301238)));
            Assert.Equal(5301238, Marshal.ReadInt32(address));
        }
        finally { Marshal.FreeHGlobal(address); }
    }
}
