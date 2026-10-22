using System.IO;
using System.Net.Http;
using System.Text.Json;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public interface IOnlineTranslator
{
    Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken);
}

/// <summary>
/// 管道读取循环只依赖这个接口，因此作弊命令可以在翻译请求很慢时照常处理，
/// 测试也可以在没有 SQLite 与联网服务的情况下验证这一点。
/// </summary>
public interface ITranslationDispatcher
{
    Task<TranslateReply?> TranslateAsync(TranslateRequest request, CancellationToken cancellationToken);
}

public sealed class TranslationCoordinator(TranslationStore store, IOnlineTranslator? provider, string? providerScope = null) : ITranslationDispatcher
{
    private readonly object _sourceGatesLock = new();
    private readonly Dictionary<(string Language, string Source), SourceGate> _sourceGates = new();

    private sealed class SourceGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    /// <summary>翻译过程的关键节点写到这里，方便在 app.log 里回答"为什么不翻译"。</summary>
    public Action<string>? Diagnostics { get; set; }

    /// <summary>
    /// 延迟埋点的**独立通道**（用户新增需求：降低可见延迟）。
    ///
    /// 为什么不复用 <see cref="Diagnostics"/>：那里的契约是"**一条请求产生一条诊断**"，
    /// 既有测试就是按这个断言的（`Assert.Single`）。把每次请求的两三行计时混进去，
    /// 会同时污染日志语义和既有测试 —— 所以另开一条通道，由 App 决定接到哪里。
    /// 用单调时钟（<c>Stopwatch</c>）而不是 <c>DateTime</c>：后者会被系统对时跳变污染。
    /// </summary>
    public Action<string>? Latency { get; set; }

    public async Task<TranslateReply?> TranslateAsync(TranslateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Source)) return null;

        var total = System.Diagnostics.Stopwatch.StartNew();
        var key = (request.SourceLanguage, request.Source);
        SourceGate gate;
        lock (_sourceGatesLock)
        {
            if (!_sourceGates.TryGetValue(key, out gate!))
                _sourceGates.Add(key, gate = new SourceGate());
            gate.Users++;
        }

        var entered = false;
        try
        {
            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken);
                entered = true;
            }
            catch (OperationCanceledException) { return null; }

            // 同一句在等待期间可能已经入库；查库必须在取得锁之后。
            // 不共享调用者的任务或取消令牌，回复仍使用各自的组件与请求编号。
            return await TranslateCoreAsync(request, cancellationToken, total);
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            lock (_sourceGatesLock)
            {
                if (--gate.Users == 0)
                {
                    _sourceGates.Remove(key);
                    gate.Semaphore.Dispose();
                }
            }
        }
    }

    private async Task<TranslateReply?> TranslateCoreAsync(TranslateRequest request,
        CancellationToken cancellationToken, System.Diagnostics.Stopwatch total)
    {
        // ── 延迟埋点（用户新增需求）─────────────────────────────────────────────
        // 用 Stopwatch（单调时钟）而不是 DateTime：后者会被系统对时跳变污染。
        // 分两段：`查库`（本地译文库）与 `API`（联网调用）。
        // 插件侧另有两段（发现→发送、发送→回包），两边按 RequestId 对齐 ——
        // **不做跨进程时钟对齐**：各自只算本进程内的差值，避免时钟不同源带来的假数据。
        var lookup = System.Diagnostics.Stopwatch.StartNew();
        var cached = store.FindEntry(request.SourceLanguage, request.Source, providerScope);
        lookup.Stop();
        var lookupMs = lookup.ElapsedMilliseconds;
        var apiMs = 0L;
        // 用户手工改过的译文无条件采信；自动翻出来的要重新过一遍校验。
        // 以前缓存命中这条路完全不校验，于是旧版本漏进来的那句
        // "请提供需要翻译的英语游戏文字内容。" 会一直贴在游戏界面上，怎么重启都在。
        if (cached is not null &&
            (cached.UserCorrected || !TextSafety.LooksLikeChatReply(request.Source, cached.Translated)))
        {
            Diagnostics?.Invoke($"翻译命中本地译文库（{request.SourceLanguage}{(providerScope is null ? "" : " / " + providerScope)}）：「{Shorten(request.Source)}」");
            Latency?.Invoke($"[延迟] req={request.RequestId} 缓存=命中 查库={lookupMs}ms API=0ms "
                + $"合计={total.ElapsedMilliseconds}ms 「{Shorten(request.Source)}」");
            return new TranslateReply(request.RequestId, cached.Translated, request.ComponentId, request.Generation);
        }

        if (cached is not null)
        {
            store.Delete(request.SourceLanguage, request.Source, providerScope);
            Diagnostics?.Invoke($"译文库里存着一条对话式回复而不是译文，已删除并重新翻译："
                + $"「{Shorten(request.Source)}」→「{Shorten(cached.Translated)}」");
        }

        if (provider is null)
        {
            // 最常见的"没有翻译"原因：没有填服务地址或密钥，插件照样会发请求过来。
            Diagnostics?.Invoke($"没有可用的联网翻译服务，原文无法翻译：「{Shorten(request.Source)}」"
                + "（请到“翻译服务设置（高级）”检查服务地址与密钥）");
            Latency?.Invoke($"[延迟] req={request.RequestId} 缓存=未命中 查库={lookupMs}ms API=0ms "
                + $"合计={total.ElapsedMilliseconds}ms 结果=无服务 「{Shorten(request.Source)}」");
            return null;
        }

        string? translated = null;
        var api = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            translated = await provider.TranslateAsync(request.Source, request.SourceLanguage, cancellationToken);
            api.Stop();
            apiMs = api.ElapsedMilliseconds;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or
                                   TaskCanceledException or JsonException or KeyNotFoundException)
        {
            api.Stop();
            if (!cancellationToken.IsCancellationRequested &&
                FindCorrectionReply(request, lookupMs, api.ElapsedMilliseconds, total) is { } corrected)
                return corrected;
            // V0.3 及以前这里把异常直接吞掉，日志里什么都看不到，
            // 于是"翻译没反应"完全无法排查。现在把类型与消息写出来。
            Diagnostics?.Invoke($"翻译服务调用失败（{request.SourceLanguage}）：「{Shorten(request.Source)}」→ "
                + ex.GetType().Name + "：" + ex.Message);
            Latency?.Invoke($"[延迟] req={request.RequestId} 缓存=未命中 查库={lookupMs}ms "
                + $"API={api.ElapsedMilliseconds}ms 合计={total.ElapsedMilliseconds}ms 结果=失败 "
                + $"「{Shorten(request.Source)}」");
            return null;
        }
        catch (Exception ex)
        {
            api.Stop();
            if (!cancellationToken.IsCancellationRequested &&
                FindCorrectionReply(request, lookupMs, api.ElapsedMilliseconds, total) is { } corrected)
                return corrected;
            Diagnostics?.Invoke($"翻译服务调用出现未预期错误（{request.SourceLanguage}）：「{Shorten(request.Source)}」→ "
                + ex.GetType().Name + "：" + ex.Message);
            Latency?.Invoke($"[延迟] req={request.RequestId} 缓存=未命中 查库={lookupMs}ms "
                + $"API={api.ElapsedMilliseconds}ms 合计={total.ElapsedMilliseconds}ms 结果=异常 "
                + $"「{Shorten(request.Source)}」");
            return null;
        }

        if (cancellationToken.IsCancellationRequested) return null;
        if (FindCorrectionReply(request, lookupMs, apiMs, total) is { } correctionReply)
            return correctionReply;

        if (!TextSafety.IsSafe(request.Source, translated))
        {
            // 两种可能：服务返回空，或者译文与原文相同（中文原文被原样返回）。
            // 后者是正常的——纯汉字的中文界面会被原样送进来，这里就当没翻。
            Diagnostics?.Invoke(string.IsNullOrWhiteSpace(translated)
                ? $"翻译服务没有返回内容：「{Shorten(request.Source)}」"
                : $"译文未通过校验（与原文相同或占位符/换行不一致），本次不替换：「{Shorten(request.Source)}」→「{Shorten(translated!)}」");
            return null;
        }

        // 格式没问题、但内容是"请提供需要翻译的…"这种对话式回答：同样不能写进游戏。
        if (TextSafety.LooksLikeChatReply(request.Source, translated))
        {
            Diagnostics?.Invoke($"翻译服务返回的是对话式回答而不是译文，本次不替换："
                + $"「{Shorten(request.Source)}」→「{Shorten(translated!)}」");
            return null;
        }

        store.SaveGenerated(request.SourceLanguage, request.Source, translated!, providerScope);
        Diagnostics?.Invoke($"翻译成功（{request.SourceLanguage}{(providerScope is null ? "" : " / " + providerScope)}）：「{Shorten(request.Source)}」→「{Shorten(translated!)}」");
        Latency?.Invoke($"[延迟] req={request.RequestId} 缓存=未命中 查库={lookupMs}ms "
            + $"API={apiMs}ms 合计={total.ElapsedMilliseconds}ms 结果=成功 「{Shorten(request.Source)}」");
        return new TranslateReply(request.RequestId, translated!, request.ComponentId, request.Generation);
    }

    private TranslateReply? FindCorrectionReply(TranslateRequest request, long lookupMs, long apiMs,
        System.Diagnostics.Stopwatch total)
    {
        // 在线请求进行时用户也能校正；即使服务失败，已发表的校正仍可用于未取消的请求。
        var correction = store.FindEntry(request.SourceLanguage, request.Source, providerScope);
        if (correction?.UserCorrected != true) return null;
        Diagnostics?.Invoke($"翻译期间收到用户校正，已采用校正译文：「{Shorten(request.Source)}」");
        Latency?.Invoke($"[延迟] req={request.RequestId} 缓存=用户校正 查库={lookupMs}ms "
            + $"API={apiMs}ms 合计={total.ElapsedMilliseconds}ms 「{Shorten(request.Source)}」");
        return new TranslateReply(request.RequestId, correction.Translated, request.ComponentId, request.Generation);
    }

    private static string Shorten(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 60 ? flat : flat.Substring(0, 60) + "…";
    }
}
