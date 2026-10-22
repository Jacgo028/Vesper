using System.Diagnostics;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class CheatTableCompatibilityTests
{
    [Fact]
    public void ImportsHierarchyAndPointersWithoutExecutingScripts()
    {
        const string xml = """
            <CheatTable CheatEngineTableVersion="45"><CheatEntries>
              <CheatEntry><ID>1</ID><Description>"Player"</Description><GroupHeader>1</GroupHeader><CheatEntries>
                <CheatEntry><ID>2</ID><Description>"Health"</Description><VariableType>4 Bytes</VariableType>
                  <Address>"Game.exe"+1234</Address><Offsets><Offset>10</Offset><Offset>20</Offset></Offsets></CheatEntry>
                <CheatEntry><ID>3</ID><Description>"Script"</Description><VariableType>Auto Assembler Script</VariableType><AssemblerScript>danger</AssemblerScript></CheatEntry>
              </CheatEntries></CheatEntry>
            </CheatEntries><LuaScript>error('must never execute')</LuaScript></CheatTable>
            """;
        var table = CheatTableDocument.Parse(xml);
        Assert.Equal(2, table.Entries.Count);
        Assert.True(table.Entries[0].IsGroup);
        Assert.Equal(table.Entries[0].Id, table.Entries[1].ParentId);
        Assert.Equal("\"Game.exe\"+1234", table.Entries[1].Address);
        Assert.Equal(new long[] { 0x10, 0x20 }, table.Entries[1].Offsets);
        Assert.Equal(1, table.SkippedScripts);
        Assert.True(table.HasLuaScript);
        var exported = table.ToXml();
        Assert.DoesNotContain("LuaScript", exported);
        Assert.DoesNotContain("AssemblerScript", exported);
        var reopened = CheatTableDocument.Parse(exported);
        Assert.Equal(2, reopened.Entries.Count);
        Assert.Equal(new long[] { 0x10, 0x20 }, reopened.Entries[1].Offsets);
    }

    [Theory]
    [InlineData("$lua(return 123)")]
    [InlineData("123;os.execute('x')")]
    [InlineData("Game.exe+10\nprint('x')")]
    public void RejectsExecutableAddressExpressions(string address) =>
        Assert.Throws<ArgumentException>(() => CheatTableDocument.ValidateAddress(address));

    [Theory]
    [InlineData("[100]+4")]
    [InlineData("health")]
    [InlineData("Game.exe+20")]
    [InlineData("10000000000000000")]
    [InlineData("\"Game.exe\"+10000000000000000")]
    public void Rejects_unsupported_or_out_of_range_addresses_before_import(string address) =>
        Assert.Throws<ArgumentException>(() => CheatTableDocument.ValidateAddress(address));

    [Fact]
    public void Rejects_whole_document_when_a_later_numeric_row_has_unsupported_address_syntax()
    {
        const string xml = """
            <CheatTable><CheatEntries>
              <CheatEntry><VariableType>4 Bytes</VariableType><Address>100</Address></CheatEntry>
              <CheatEntry><VariableType>4 Bytes</VariableType><Address>[100]+4</Address></CheatEntry>
            </CheatEntries></CheatTable>
            """;

        Assert.Throws<ArgumentException>(() => CheatTableDocument.Parse(xml));
    }

    [Fact]
    public void Rejects_explicit_negative_offset_beyond_signed_64bit_range() =>
        Assert.Throws<ArgumentException>(() => CheatTableDocument.ParseOffset("-FFFFFFFFFFFFFFFF"));

    [Fact]
    public void Accepts_smallest_signed_64bit_offset_without_overflow() =>
        Assert.Equal(long.MinValue, CheatTableDocument.ParseOffset("-8000000000000000"));

    [Fact]
    public void RejectsExternalXmlEntities() => Assert.ThrowsAny<Exception>(() => CheatTableDocument.Parse(
        "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><CheatTable><CheatEntries>&e;</CheatEntries></CheatTable>"));

    [Fact]
    public async Task BuiltInToolsScanRefineWriteAndFreezeOnlyControlledHelper()
    {
        const string script = "$p=[Runtime.InteropServices.Marshal]::AllocHGlobal(4096); " +
            "[Runtime.InteropServices.Marshal]::WriteInt32($p,5301237); " +
            "[Console]::Out.WriteLine('READY '+$PID+' '+$p.ToInt64()); " +
            "while($true) { $line=[Console]::ReadLine(); if($line -eq 'exit' -or $null -eq $line) { break }; " +
            "if($line.StartsWith('set ')){[Runtime.InteropServices.Marshal]::WriteInt32($p,[int]$line.Substring(4))}; " +
            "[Console]::Out.WriteLine([Runtime.InteropServices.Marshal]::ReadInt32($p)) }";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(arg);
        using var target = Process.Start(start)!;
        try
        {
            var ready = (await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!.Split(' ');
            var address = ulong.Parse(ready[2]);
            using var memory = new Win32ProcessMemory(target);
            using var scanner = new MemoryScanWorkspace();
            var first = await scanner.FirstAsync(memory,NumericKind.Int32,InitialScanMode.Exact,"5301237","",100,null,
                CancellationToken.None,address,address+63);
            Assert.Contains(first.Hits, hit => hit.Address == address);
            await target.StandardInput.WriteLineAsync("set 5301238");
            Assert.Equal("5301238", await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var refined = await scanner.RefineAsync(memory,ScanFilter.Exact,"5301238",100,null,CancellationToken.None);
            Assert.Equal(address, Assert.Single(refined.Hits).Address);
            Assert.True(memory.TryWrite(address, BitConverter.GetBytes(5301239)));
            var readback = new byte[4];
            Assert.True(memory.TryRead(address, readback));
            Assert.Equal(5301239, BitConverter.ToInt32(readback));
            var entry = new CheatTableEntry { Id = 1, Description = "Controlled health", Address = address.ToString("X"), Kind = NumericKind.Int32 };
            using var engine = new MemoryAddressTable(memory,IntPtr.Size);
            engine.AddEntry(entry);
            engine.SetEntryValue(entry.Id, "99");
            engine.SetEntryFrozen(entry.Id, true);
            await target.StandardInput.WriteLineAsync("set 23");
            await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            await target.StandardInput.WriteLineAsync("read");
            Assert.Equal("99", await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            engine.SetEntryFrozen(entry.Id, false);
            await target.StandardInput.WriteLineAsync("set 24");
            Assert.Equal("24", await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.Delay(200);
            await target.StandardInput.WriteLineAsync("read");
            Assert.Equal("24", await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(Assert.Single(engine.RefreshEntries()).Frozen);
            Assert.Equal("24", Assert.Single(engine.RefreshEntries()).Value);
        }
        finally
        {
            if (!target.HasExited) { await target.StandardInput.WriteLineAsync("exit"); await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
