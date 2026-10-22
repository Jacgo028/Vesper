using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace UnityTranslator.App;

/// <summary>Edge 公共翻译入口；逐条发送，无密钥，默认请求超时 15 秒、请求间隔 1 秒。</summary>
public sealed class BingFreeTranslationClient : IOnlineTranslator
{
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _minimumRequestInterval;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _lastRequestStarted;
    private bool _hasSentRequest;
    private DateTimeOffset _retryAfterUntil;

    public BingFreeTranslationClient(HttpClient client, TimeSpan? requestTimeout = null,
        TimeSpan? minimumRequestInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _minimumRequestInterval = minimumRequestInterval ?? TimeSpan.FromSeconds(1);
        if (_requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "请求超时必须为正数。");
        if (_minimumRequestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval), "请求间隔不能为负数。");
    }

    public async Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _retryAfterUntil)
                throw new HttpRequestException("Bing 免费翻译限流冷却中，请稍后再试。", null, HttpStatusCode.TooManyRequests);

            if (_hasSentRequest)
            {
                var remaining = _minimumRequestInterval - Stopwatch.GetElapsedTime(_lastRequestStarted);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _lastRequestStarted = Stopwatch.GetTimestamp();
            _hasSentRequest = true;
            return await SendTranslationAsync(source, sourceLanguage, cancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<string?> SendTranslationAsync(string source, string sourceLanguage, CancellationToken cancellationToken)
    {
        // HttpClient 可能与 API 模式共用；拒绝会被自动附到免费请求上的默认凭据。
        if (_client.DefaultRequestHeaders.Contains("Authorization")
            || _client.DefaultRequestHeaders.Contains("Proxy-Authorization")
            || _client.DefaultRequestHeaders.Contains("Ocp-Apim-Subscription-Key")
            || _client.DefaultRequestHeaders.Contains("X-Api-Key")
            || _client.DefaultRequestHeaders.Contains("Api-Key"))
            throw new InvalidOperationException("Bing 免费翻译不能使用带凭据的 HTTP 客户端。");

        var language = NormalizeSourceLanguage(sourceLanguage);
        // 旧插件的 en 也用于纯汉字菜单的缓存分桶，不能把它当成可靠的英语识别结果。
        // 出现非拉丁文字时交给服务自动识别；不改变请求中的缓存语言或显式 ja 提示。
        if (language == "en" && source.Any(c => c > 127 && char.IsLetter(c))) language = null;
        var endpoint = new Uri("https://edge.microsoft.com/translate/translatetext?"
            + (language is null ? "" : "from=" + Uri.EscapeDataString(language) + "&")
            + "to=zh-Hans&isEnterpriseClient=false");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new[] { source })
        };
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var now = DateTimeOffset.UtcNow;
                var retryAfter = response.Headers.RetryAfter;
                var until = retryAfter?.Date ?? now + (retryAfter?.Delta ?? TimeSpan.FromSeconds(30));
                _retryAfterUntil = until > now ? until : now.AddSeconds(30);
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Bing 免费翻译请求失败。", null, response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0
                || root[0].ValueKind != JsonValueKind.Object
                || !root[0].TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0
                || translations[0].ValueKind != JsonValueKind.Object
                || !translations[0].TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString()))
                throw new InvalidOperationException("Bing 免费翻译未返回有效译文。");
            return text.GetString()!.Trim();
        }
        catch (OperationCanceledException)
        {
            var callerCanceled = cancellationToken.IsCancellationRequested;
            throw new TaskCanceledException(callerCanceled ? "Bing 免费翻译已取消。" : "Bing 免费翻译请求超时。", null,
                callerCanceled ? cancellationToken : timeout.Token);
        }
        catch (HttpRequestException error)
        {
            var message = error.StatusCode is { } status
                ? $"Bing 免费翻译请求失败（HTTP {(int)status}）。"
                : "Bing 免费翻译网络请求失败。";
            throw new HttpRequestException(message, null, error.StatusCode);
        }
        catch (IOException)
        {
            throw new HttpRequestException("Bing 免费翻译响应读取失败。");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Bing 免费翻译响应格式无效。");
        }
    }

    private static string? NormalizeSourceLanguage(string language) => language.Trim().Replace('_', '-').ToLowerInvariant() switch
    {
        "" or "auto" or "auto-detect" => null,
        "ja-jp" => "ja",
        "en-us" or "en-gb" => "en",
        "zh" or "zh-cn" or "zh-hans" => "zh-Hans",
        var normalized => normalized
    };
}
