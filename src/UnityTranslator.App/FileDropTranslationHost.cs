using System.IO;
using UnityTranslator.Core;

namespace UnityTranslator.App;

/// <summary>
/// 备用翻译通道：文件投递。
///
/// 为什么还要第三条通道：实测 Unity 2020.1.2f1 的 Mono 运行时里
/// **命名管道写不进去**（每次写都 ERROR_NO_DATA），**TCP 连接又会把调用线程整个阻塞住**
/// （连 `Task.Wait(超时)` 都轮不到）。两条通道都试过之后，只剩一条路：
/// **不碰网络栈，用文件**。插件已经证明文件 I/O 在这个进程里是好的
/// （读得到字体文件、也写得出 BepInEx 日志）。
///
/// 协议：插件把请求行写进 <c>&lt;游戏目录&gt;\.unity-translator-io\req-&lt;id&gt;.json</c>，
/// 这里轮询取走、用**同一个** <see cref="ITranslationDispatcher"/> 处理，
/// 把回复写回 <c>res-&lt;id&gt;.json</c>。空回复文件表示"这次没有结果"，插件据此停止等待。
///
/// 命名管道仍是首选；这条通道只在插件管道写失败时才会被用到。
/// </summary>
public sealed class FileDropTranslationHost : IDisposable
{
    /// <summary>约定目录名（与插件侧必须完全一致）。</summary>
    public const string DirectoryName = ".unity-translator-io";

    private readonly ITranslationDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private string? _directory;
    private Task? _worker;
    private int _passes;

    public FileDropTranslationHost(ITranslationDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>接入/断开等生命周期信息，接到 app.log 便于排查。</summary>
    public event Action<string>? Diagnostics;

    /// <summary>已经处理过的请求数，便于核对。</summary>
    public int Handled { get; private set; }

    public void Start(string gameRoot)
    {
        _directory = Path.Combine(gameRoot, DirectoryName);
        Directory.CreateDirectory(_directory);
        // 上次异常退出可能留下垃圾，启动时清一次。
        try
        {
            foreach (var stale in Directory.GetFiles(_directory, "*.json")) File.Delete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Diagnostics?.Invoke($"文件投递通道：已监听 {_directory}");

        // 诊断探针：App 自己往这个目录写一个文件并立刻回读。
        // 目的是定性"两个进程是否看到不同的目录内容" ——
        // 若外部（PowerShell）看不见这个探针，就说明是文件系统视图不一致，
        // 而不是我们代码里某个条件写错。
        try
        {
            var probe = Path.Combine(_directory, "probe-app.txt");
            File.WriteAllText(probe, DateTime.Now.ToString("O"));
            var seen = Directory.GetFiles(_directory, "*").Length;
            Diagnostics?.Invoke($"文件投递通道：探针已写入 {probe}；写完后本进程看到 {seen} 个文件");
        }
        catch (Exception ex)
        {
            Diagnostics?.Invoke($"文件投递通道：探针写入失败 {ex.GetType().Name}：{ex.Message}");
        }

        _worker ??= Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            _passes++;
            if (_passes <= 2 || _passes % 50 == 0)
            {
                var exists = Directory.Exists(_directory);
                var narrow = exists ? Directory.GetFiles(_directory!, "req-*.json").Length : -1;
                var wide = exists ? Directory.GetFiles(_directory!, "*").Length : -1;
                Diagnostics?.Invoke($"文件投递通道：第 {_passes} 次轮询（存在={exists} req={narrow} 全部={wide} "
                    + $"已处理={Handled} 目录=[{_directory}] 当前目录=[{Directory.GetCurrentDirectory()}]）");
            }
            try
            {
                foreach (var file in Directory.GetFiles(_directory!, "req-*.json"))
                {
                    Diagnostics?.Invoke($"文件投递通道：取到请求文件 {Path.GetFileName(file)}");
                    var id = Path.GetFileNameWithoutExtension(file).Substring("req-".Length);
                    TranslateReply? reply = null;
                    try
                    {
                        var line = File.ReadAllText(file);
                        if (PipeProtocol.TryReadRequest(line, out var request) && request is not null)
                            reply = await _dispatcher.TranslateAsync(request, _stop.Token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // 半截文件或读失败：下次轮询再试。
                        continue;
                    }

                    // 空文件 = "这次没有结果"，插件据此停止等待，避免无限等下去。
                    try { File.WriteAllText(Path.Combine(_directory!, "res-" + id + ".json"), reply is null ? string.Empty : PipeProtocol.WriteReply(reply)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    Handled++;
                }
            }
            catch (Exception ex)
            {
                // 这里**故意捕所有异常并记下来**：原来只捕三种，
                // 一旦抛出别的类型，整个轮询任务会**静默死掉**，
                // 表现就是"目录里堆着一堆请求、App 一个字都不处理"（实测踩到过）。
                Diagnostics?.Invoke($"文件投递通道：轮询出错（{ex.GetType().Name}）：{ex.Message}");
            }

            try { await Task.Delay(200, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
