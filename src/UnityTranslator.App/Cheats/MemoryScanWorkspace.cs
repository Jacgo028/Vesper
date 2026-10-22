using System.IO;
using System.Diagnostics;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Cheats;

public enum InitialScanMode { Exact, Unknown, Between, Greater, Less }
public sealed record MemoryScanResult(long TotalCount,IReadOnlyList<ScanHit> Hits);
public sealed class ScanStorageLimitException : IOException
{
    public ScanStorageLimitException():base("扫描快照超过 256 MB 上限，已停止且没有截断候选。请填写更窄的起止地址，或先用范围/精确数值查找。") { }
}

/// <summary>Candidate snapshots stay on disk. A display limit never discards candidates needed by the next scan.</summary>
public sealed class MemoryScanWorkspace(long maxSnapshotBytes=256L*1024*1024) : IDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly CancellationTokenSource _lifetime=new();
    private string? _path;
    private NumericKind _kind;
    private int _processId;
    private long _started;
    private bool _disposed;
    public bool HasScan=>_path is not null;
    public long TotalCount { get; private set; }
    public NumericKind Kind=>_kind;

    public Task<MemoryScanResult> FirstAsync(IProcessMemory memory,NumericKind kind,InitialScanMode mode,
        string input,string upper,int displayLimit,IProgress<ScanProgress>? progress,CancellationToken token,
        ulong start=0,ulong stop=0x7FFFFFFFFFFF,bool aligned=true) => RunAsync(memory,token,() =>
    {
        if(start>stop)throw new ArgumentException("起始地址不能大于结束地址。");
        byte[] exact=[],second=[];
        if(mode!=InitialScanMode.Unknown && !NumericValue.TryParse(input,kind,out exact))throw new ArgumentException("请输入与数值类型一致的数字。");
        if(mode==InitialScanMode.Between && (!NumericValue.TryParse(upper,kind,out second) || NumericValue.Compare(exact,second,kind)>0))
            throw new ArgumentException("范围上限必须是有效数字，且不能小于下限。");
        var width=NumericValue.Width(kind);
        var pending=NewPath();
        try
        {
            using(var file=new BinaryWriter(new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536)))
            {
                var regions=memory.Regions().Where(x=>x.Readable && x.Base<=stop && x.Size>0 && x.Base+x.Size>start).ToArray();
                var total=regions.Aggregate(0UL,(s,r)=>s+Math.Min(r.Base+r.Size-1,stop)-Math.Max(r.Base,start)+1);
                ulong read=0; long hits=0;
                var update=Stopwatch.StartNew();
                foreach(var region in regions)
                {
                    var begin=Math.Max(region.Base,start);var end=Math.Min(region.Base+region.Size-1,stop);
                    for(var address=begin;address<=end;)
                    {
                        Check(memory,token);
                        var length=(int)Math.Min(65536UL,end-address+1);
                        var readLength=(int)Math.Min((ulong)length+(ulong)width-1,end-address+1);
                        var buffer=new byte[readLength];
                        if(memory.TryRead(address,buffer))
                        {
                            var first=aligned?(int)(((ulong)width-address%(ulong)width)%(ulong)width):0;
                            var step=aligned?width:1;
                            for(var i=first;i<length && i+width<=buffer.Length;i+=step)
                            {
                                if((i&4095)==0)Check(memory,token);
                                var bytes=buffer.AsSpan(i,width);bool keep;
                                try { keep=mode switch {InitialScanMode.Unknown=>true,InitialScanMode.Exact=>bytes.SequenceEqual(exact),
                                    InitialScanMode.Between=>NumericValue.Compare(bytes,exact,kind)>=0 && NumericValue.Compare(bytes,second,kind)<=0,
                                    InitialScanMode.Greater=>NumericValue.Compare(bytes,exact,kind)>0,InitialScanMode.Less=>NumericValue.Compare(bytes,exact,kind)<0,_=>false}; }
                                catch(ArgumentException){keep=false;}
                                if(!keep)continue;
                                if((hits+1)*(8+width)>maxSnapshotBytes)throw new ScanStorageLimitException();
                                file.Write(address+(ulong)i);file.Write(bytes);hits++;
                            }
                        }
                        read+=(ulong)length;
                        if(update.ElapsedMilliseconds>=100 || read==total)
                        {progress?.Report(new(read,total,(int)Math.Min(int.MaxValue,hits)));update.Restart();}
                        if((ulong)length>end-address)break;address+=(ulong)length;
                    }
                }
            }
            Check(memory,token);
            var result=ReadPage(pending,kind,displayLimit);
            Swap(pending,memory,kind,result.TotalCount);return result;
        }
        catch{Delete(pending);throw;}
    });

    public Task<MemoryScanResult> RefineAsync(IProcessMemory memory,ScanFilter filter,string input,int displayLimit,
        IProgress<ScanProgress>? progress,CancellationToken token) => RunAsync(memory,token,() =>
    {
        if(_path is null || memory.ProcessId!=_processId || memory.StartedUtcTicks!=_started)throw new InvalidOperationException("扫描会话已失效，请重新首次查找。");
        byte[] exact=[];
        if(filter==ScanFilter.Exact && !NumericValue.TryParse(input,_kind,out exact))throw new ArgumentException("请输入与首次查找类型一致的数字。");
        var pending=NewPath();var width=NumericValue.Width(_kind);
        try
        {
            using(var reader=new BinaryReader(new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.Read,65536)))
            using(var writer=new BinaryWriter(new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536)))
            {
                ulong cachePage=ulong.MaxValue;var cache=new byte[4096];bool cacheValid=false;
                var current=new byte[width];long read=0,hits=0;
                while(reader.BaseStream.Position<reader.BaseStream.Length)
                {
                    Check(memory,token);var address=reader.ReadUInt64();var previous=reader.ReadBytes(width);
                    var page=address&~4095UL;
                    if(cachePage!=page){cachePage=page;cacheValid=memory.TryRead(page,cache);}
                    if(cacheValid && address-page+(ulong)width<=4096)cache.AsSpan((int)(address-page),width).CopyTo(current);
                    else if(!memory.TryRead(address,current)){read++;continue;}
                    bool keep;
                    try { var compare=filter==ScanFilter.Exact?0:NumericValue.Compare(current,previous,_kind);
                        keep=filter switch {ScanFilter.Exact=>current.AsSpan().SequenceEqual(exact),ScanFilter.Changed=>compare!=0,
                            ScanFilter.Unchanged=>compare==0,ScanFilter.Increased=>compare>0,ScanFilter.Decreased=>compare<0,_=>false}; }
                    catch(ArgumentException){keep=false;}
                    if(keep){writer.Write(address);writer.Write(current);hits++;}
                    read++;
                    if((read&16383)==0)progress?.Report(new((ulong)read,(ulong)TotalCount,(int)Math.Min(int.MaxValue,hits)));
                }
            }
            Check(memory,token);var result=ReadPage(pending,_kind,displayLimit);Swap(pending,memory,_kind,result.TotalCount);return result;
        }
        catch{Delete(pending);throw;}
    });

    private async Task<MemoryScanResult> RunAsync(IProcessMemory memory,CancellationToken token,Func<MemoryScanResult> action)
    {
        using var combined=CancellationTokenSource.CreateLinkedTokenSource(token,_lifetime.Token);
        await _gate.WaitAsync(combined.Token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed,this); return await Task.Run(()=>{Check(memory,token);return action();},combined.Token).ConfigureAwait(false); }
        finally{_gate.Release();}
    }
    private void Check(IProcessMemory memory,CancellationToken token)
    {token.ThrowIfCancellationRequested();_lifetime.Token.ThrowIfCancellationRequested();if(!memory.IsAlive)throw new IOException("游戏进程已经退出。");}
    private static string NewPath()=>Path.Combine(Path.GetTempPath(),"UnityTranslator-scan-"+Guid.NewGuid().ToString("N")+".bin");
    private void Swap(string pending,IProcessMemory memory,NumericKind kind,long count)
    {var old=_path;_path=pending;_kind=kind;_processId=memory.ProcessId;_started=memory.StartedUtcTicks;TotalCount=count;Delete(old);}
    private static MemoryScanResult ReadPage(string path,NumericKind kind,int limit)
    {
        var width=NumericValue.Width(kind);using var reader=new BinaryReader(File.OpenRead(path));
        var total=reader.BaseStream.Length/(8+width);var result=new List<ScanHit>();
        for(long i=0;i<Math.Min(total,Math.Clamp(limit,1,5000));i++)result.Add(new(reader.ReadUInt64(),kind,reader.ReadBytes(width)));
        return new(total,result);
    }
    private static void Delete(string? path){if(path is not null)try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
    public void Clear(){_gate.Wait();try{Delete(_path);_path=null;TotalCount=0;}finally{_gate.Release();}}
    public void Dispose(){if(_disposed)return;_disposed=true;_lifetime.Cancel();_gate.Wait();try{Delete(_path);_path=null;}finally{_gate.Release();}}
}
