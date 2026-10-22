using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

public sealed class MemoryScanWorkspaceTests
{
    [Fact]
    public async Task RefinesCandidatesBeyondDisplayedPage()
    {
        var memory=new ArrayMemory(128);
        for(var i=0;i<8;i++) memory.Set(i*4,53123);
        using var scanner=new MemoryScanWorkspace();
        var first=await scanner.FirstAsync(memory,NumericKind.Int32,InitialScanMode.Exact,"53123","",2,null,CancellationToken.None);
        Assert.Equal(8,first.TotalCount); Assert.Equal(2,first.Hits.Count);
        memory.Set(28,53124);
        var second=await scanner.RefineAsync(memory,ScanFilter.Exact,"53124",2,null,CancellationToken.None);
        Assert.Equal(28UL,Assert.Single(second.Hits).Address);
    }
    [Fact]
    public async Task UnknownThenChangedUsesPreviousSnapshot()
    {
        var memory=new ArrayMemory(64);
        using var scanner=new MemoryScanWorkspace();
        Assert.Equal(16,(await scanner.FirstAsync(memory,NumericKind.Int32,InitialScanMode.Unknown,"","",2,null,CancellationToken.None)).TotalCount);
        memory.Set(12,97);
        var result=await scanner.RefineAsync(memory,ScanFilter.Changed,"",100,null,CancellationToken.None);
        Assert.Equal(12UL,Assert.Single(result.Hits).Address);
        Assert.Equal("97",NumericValue.Format(result.Hits[0].LastBytes,NumericKind.Int32));
    }
    [Theory]
    [InlineData(InitialScanMode.Between,"10","20",2)]
    [InlineData(InitialScanMode.Greater,"10","",2)]
    [InlineData(InitialScanMode.Less,"20","",2)]
    public async Task FirstScanSupportsNumericComparators(InitialScanMode mode,string value,string upper,int count)
    {
        var memory=new ArrayMemory(12); memory.Set(0,10); memory.Set(4,15); memory.Set(8,25);
        using var scanner=new MemoryScanWorkspace();
        var result=await scanner.FirstAsync(memory,NumericKind.Int32,mode,value,upper,100,null,CancellationToken.None);
        Assert.Equal(count,result.TotalCount);
    }
    [Fact]
    public async Task CancellationKeepsCompletedScan()
    {
        var memory=new ArrayMemory(64); memory.Set(4,83);
        using var scanner=new MemoryScanWorkspace();
        await scanner.FirstAsync(memory,NumericKind.Int32,InitialScanMode.Exact,"83","",100,null,CancellationToken.None);
        using var stop=new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>scanner.RefineAsync(memory,ScanFilter.Exact,"83",100,null,stop.Token));
        Assert.True(scanner.HasScan);
        Assert.Single((await scanner.RefineAsync(memory,ScanFilter.Exact,"83",100,null,CancellationToken.None)).Hits);
    }
    [Fact]
    public async Task StorageLimitFailsExplicitlyInsteadOfLosingCandidates()
    {
        var memory=new ArrayMemory(128);
        using var scanner=new MemoryScanWorkspace(48);
        await Assert.ThrowsAsync<ScanStorageLimitException>(()=>scanner.FirstAsync(memory,NumericKind.Int32,
            InitialScanMode.Unknown,"","",100,null,CancellationToken.None));
        Assert.False(scanner.HasScan);
    }
    private sealed class ArrayMemory(int count):IProcessMemory
    {
        private readonly byte[] bytes=new byte[count];
        public void Set(int address,int value)=>BitConverter.GetBytes(value).CopyTo(bytes,address);
        public IReadOnlyList<MemoryRegion> Regions()=>[new(0,(ulong)bytes.Length,true,true)];
        public bool TryRead(ulong address,Span<byte> output) { if(address>(ulong)bytes.Length || output.Length>bytes.Length-(int)address)return false; bytes.AsSpan((int)address,output.Length).CopyTo(output);return true; }
        public bool TryWrite(ulong address,ReadOnlySpan<byte> value) { if(address>(ulong)bytes.Length || value.Length>bytes.Length-(int)address)return false;value.CopyTo(bytes.AsSpan((int)address));return true; }
        public bool IsAlive=>true; public int ProcessId=>12; public long StartedUtcTicks=>34;
    }
}
