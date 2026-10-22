using System.Diagnostics;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class ExternalProcessMemoryTests
{
    [Fact]
    public async Task ScansAndWritesAnIndependentHelperProcess()
    {
        const string script = "$p=[Runtime.InteropServices.Marshal]::AllocHGlobal(4096); " +
            "[Runtime.InteropServices.Marshal]::WriteInt32($p,5301237); " +
            "[Console]::Out.WriteLine('READY '+$PID+' '+$p.ToInt64()); " +
            "while($true) { $line=[Console]::ReadLine(); if($line -eq 'exit' -or $null -eq $line) { break }; " +
            "[Console]::Out.WriteLine([Runtime.InteropServices.Marshal]::ReadInt32($p)) }";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch test target");
        try
        {
            var ready = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(ready);
            var parts = ready.Split(' ');
            Assert.Equal("READY", parts[0]);
            Assert.Equal(target.Id, int.Parse(parts[1]));
            var address = ulong.Parse(parts[2]);
            using var memory = new Win32ProcessMemory(target);
            var bounded = new BoundedMemory(memory, address - 16, 64);
            var hits = await new MemoryScanner(16).FirstExactAsync(bounded, NumericKind.Int32,
                BitConverter.GetBytes(5301237), 100, null, CancellationToken.None);
            Assert.Contains(hits, hit => hit.Address == address);
            using var session = new CheatSession(memory);
            var hit = hits.Single(item => item.Address == address);
            Assert.Equal(WriteStatus.Ok, session.TryWrite(hit, BitConverter.GetBytes(5301237),
                BitConverter.GetBytes(5301238)).Status);
            await target.StandardInput.WriteLineAsync("read");
            Assert.Equal("5301238", await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(memory.TryWrite(address, BitConverter.GetBytes(53)));
            session.StartFreeze(new ScanHit(address, NumericKind.Int32, BitConverter.GetBytes(53)),
                BitConverter.GetBytes(99), TimeSpan.FromMilliseconds(20));
            await Task.Delay(100);
            await target.StandardInput.WriteLineAsync("read");
            Assert.Equal("99", await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await target.StandardInput.WriteLineAsync("exit");
            await target.WaitForExitAsync();
            await Task.Delay(50);
            Assert.False(session.IsFreezing);
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }

    private sealed class BoundedMemory(IProcessMemory inner, ulong start, ulong length) : IProcessMemory
    {
        public IReadOnlyList<MemoryRegion> Regions() => [new(start, length, true, true)];
        public bool TryRead(ulong address, Span<byte> destination) => inner.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => inner.TryWrite(address, source);
        public bool IsAlive => inner.IsAlive;
        public int ProcessId => inner.ProcessId;
        public long StartedUtcTicks => inner.StartedUtcTicks;
    }
}
