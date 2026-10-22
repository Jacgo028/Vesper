using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class BingFreeTranslationClientTests
{
    // The plugin's legacy language field is a cache bucket, not reliable detection for Han-only Japanese.
    [Theory]
    [InlineData("設定")]
    [InlineData("装備")]
    [InlineData("確認")]
    public async Task AmbiguousNativeMenuTextUsesServiceDetectionInsteadOfSyntheticEnglishHint(string source)
    {
        Uri? endpoint = null;
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            endpoint = request.RequestUri;
            return Task.FromResult(TranslationResponse());
        }));
        var client = new BingFreeTranslationClient(http);

        await client.TranslateAsync(source, TextSelectionPolicy.DetectLanguage(source), default);

        Assert.Equal("?to=zh-Hans&isEnterpriseClient=false", endpoint?.Query);
    }

    [Theory]
    [InlineData("ja", "from=ja&")]
    [InlineData("JA-jp", "from=ja&")]
    [InlineData("en-US", "from=en&")]
    [InlineData("zh-CN", "from=zh-Hans&")]
    [InlineData("zh_CN", "from=zh-Hans&")]
    [InlineData("zh-Hans", "from=zh-Hans&")]
    [InlineData("auto", "")]
    [InlineData("auto-detect", "")]
    [InlineData("", "")]
    public async Task NormalizesSourceLanguageAndOmitsAutomaticDetection(string language, string expectedFrom)
    {
        Uri? endpoint = null;
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            endpoint = request.RequestUri;
            return Task.FromResult(TranslationResponse());
        }));
        var client = new BingFreeTranslationClient(http);

        await client.TranslateAsync("Hello, world!", language, CancellationToken.None);

        Assert.Equal("?" + expectedFrom + "to=zh-Hans&isEnterpriseClient=false", endpoint?.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task DoesNotSendBlankText(string source)
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(TranslationResponse());
        }));
        var client = new BingFreeTranslationClient(http);

        var translated = await client.TranslateAsync(source, "en", CancellationToken.None);

        Assert.Null(translated);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task SendsUnauthenticatedJsonToFreeEdgeEndpointAndReadsTranslation()
    {
        Uri? endpoint = null;
        string? payload = null;
        string? mediaType = null;
        string? userAgent = null;
        string[]? headerNames = null;
        HttpMethod? method = null;
        using var http = new HttpClient(new ResponseHandler(async (request, token) =>
        {
            endpoint = request.RequestUri;
            method = request.Method;
            payload = await request.Content!.ReadAsStringAsync(token);
            mediaType = request.Content.Headers.ContentType?.MediaType;
            userAgent = request.Headers.UserAgent.ToString();
            headerNames = request.Headers.Select(header => header.Key).ToArray();
            return TranslationResponse();
        }));
        var client = new BingFreeTranslationClient(http);

        var translated = await client.TranslateAsync("Hello, world!", "en", CancellationToken.None);

        Assert.Equal("你好世界", translated);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://edge.microsoft.com/translate/translatetext?from=en&to=zh-Hans&isEnterpriseClient=false", endpoint?.AbsoluteUri);
        Assert.Equal("application/json", mediaType);
        Assert.StartsWith("Mozilla/5.0", userAgent);
        Assert.DoesNotContain(headerNames!, header => header.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || header.Contains("key", StringComparison.OrdinalIgnoreCase)
            || header.Contains("token", StringComparison.OrdinalIgnoreCase));
        using var body = JsonDocument.Parse(payload!);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("Hello, world!", body.RootElement[0].GetString());
    }

    [Theory]
    [InlineData("private-response-secret")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[{\"translations\":[]}]")]
    [InlineData("[{\"translations\":[{\"text\":null,\"to\":\"zh-Hans\"}]}]")]
    [InlineData("[{\"translations\":[{\"text\":\"  \",\"to\":\"zh-Hans\"}]}]")]
    [InlineData("[{\"translations\":\"private-response-secret\"}]")]
    [InlineData("[{\"translations\":[{\"text\":17,\"to\":\"zh-Hans\"}]}]")]
    public async Task RejectsMalformedOrEmptyTranslationWithoutExposingResponse(string responseBody)
    {
        using var http = new HttpClient(new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        })));
        var client = new BingFreeTranslationClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranslateAsync("Hello, world!", "en", CancellationToken.None));

        Assert.DoesNotContain("private-response-secret", error.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RejectsHttpFailureWithoutRetryOrResponseDisclosure(HttpStatusCode status)
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                ReasonPhrase = "private-response-secret",
                Content = new StringContent("private-response-secret", Encoding.UTF8, "text/plain")
            });
        }));
        var client = new BingFreeTranslationClient(http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TranslateAsync("Hello, world!", "en", CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, sent);
        Assert.DoesNotContain("private-response-secret", error.ToString());
    }

    [Fact]
    public async Task DoesNotExposeTransportErrorDetails()
    {
        using var http = new HttpClient(new ResponseHandler((_, _) =>
            throw new HttpRequestException("private-transport-secret")));
        var client = new BingFreeTranslationClient(http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TranslateAsync("Hello, world!", "en", CancellationToken.None));

        Assert.DoesNotContain("private-transport-secret", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundsRequestAndResponseReadingWithoutChangingSharedHttpTimeout(bool blockResponseBody)
    {
        using var http = new HttpClient(new ResponseHandler(async (_, token) =>
        {
            if (!blockResponseBody)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
            };
        })) { Timeout = TimeSpan.FromMinutes(1) };
        var client = new BingFreeTranslationClient(http, requestTimeout: TimeSpan.FromMilliseconds(30));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() =>
                client.TranslateAsync("Hello, world!", "en", stop.Token).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(TimeSpan.FromMinutes(1), http.Timeout);
        }
        finally
        {
            stop.Cancel();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsUnboundedOrNonpositiveTimeout(int milliseconds)
    {
        using var http = new HttpClient();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BingFreeTranslationClient(http, requestTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void RejectsNegativeMinimumRequestInterval()
    {
        using var http = new HttpClient();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BingFreeTranslationClient(http, minimumRequestInterval: TimeSpan.FromMilliseconds(-1)));
    }

    [Theory]
    [InlineData(null, 900)]
    [InlineData(80, 60)]
    public async Task SpacesRequestStartsByDefaultOrConfiguredInterval(int? intervalMilliseconds, int expectedMinimumMilliseconds)
    {
        var starts = new List<long>();
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            starts.Add(Stopwatch.GetTimestamp());
            return Task.FromResult(TranslationResponse());
        }));
        var client = new BingFreeTranslationClient(http,
            minimumRequestInterval: intervalMilliseconds is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null);

        await client.TranslateAsync("Hello", "en", CancellationToken.None);
        await client.TranslateAsync("World", "en", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, starts.Count);
        Assert.True(Stopwatch.GetElapsedTime(starts[0], starts[1]).TotalMilliseconds >= expectedMinimumMilliseconds,
            "The next translation started before the service's polite request interval elapsed.");
    }

    [Fact]
    public async Task CancelsQueuedTranslationWithoutSendingWhileFirstRequestIsRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref sent) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return TranslationResponse();
        }));
        var client = new BingFreeTranslationClient(http, minimumRequestInterval: TimeSpan.Zero);
        var first = client.TranslateAsync("Hello", "en", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var queuedCancellation = new CancellationTokenSource();

        try
        {
            var queued = client.TranslateAsync("World", "en", queuedCancellation.Token);
            queuedCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(1, sent);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
    }

    [Fact]
    public async Task CancelsIntervalWaitWithoutSendingAnotherRequest()
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(TranslationResponse());
        }));
        var client = new BingFreeTranslationClient(http);
        await client.TranslateAsync("Hello", "en", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.TranslateAsync("World", "en", cancellation.Token));
        Assert.Equal(1, sent);
    }

    [Theory]
    [InlineData("delta")]
    [InlineData("date")]
    [InlineData("missing")]
    public async Task FailsFastDuringRateLimitCooldownWithoutSendingOrRetrying(string retryAfterKind)
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            sent++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("private-response-secret")
            };
            response.Headers.RetryAfter = retryAfterKind switch
            {
                "delta" => new RetryConditionHeaderValue(TimeSpan.FromMinutes(5)),
                "date" => new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(5)),
                _ => null
            };
            return Task.FromResult(response);
        }));
        var client = new BingFreeTranslationClient(http, minimumRequestInterval: TimeSpan.Zero);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TranslateAsync("Hello", "en", CancellationToken.None));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TranslateAsync("World", "en", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, sent);
        Assert.DoesNotContain("private-response-secret", error.ToString());
    }

    [Fact]
    public async Task AcceptsNextTranslationAfterRetryAfterExpires()
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            if (++sent > 1) return Task.FromResult(TranslationResponse());
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(limited);
        }));
        var client = new BingFreeTranslationClient(http, minimumRequestInterval: TimeSpan.Zero);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TranslateAsync("Hello", "en", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TranslateAsync("Still cooling down", "en", CancellationToken.None));
        Assert.Equal(1, sent);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var translated = await client.TranslateAsync("World", "en", CancellationToken.None);

        Assert.Equal("你好世界", translated);
        Assert.Equal(2, sent);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    [InlineData("X-Api-Key")]
    [InlineData("Api-Key")]
    public async Task RefusesCredentialBearingSharedClientInsteadOfSendingItsSecrets(string headerName)
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(TranslationResponse());
        }));
        http.DefaultRequestHeaders.TryAddWithoutValidation(headerName, "private-api-secret");
        var client = new BingFreeTranslationClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranslateAsync("Hello", "en", CancellationToken.None));

        Assert.Equal(0, sent);
        Assert.DoesNotContain("private-api-secret", error.ToString());
    }

    [Fact]
    public async Task PreservesCallerCancellationWithoutExposingTransportDetails()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new ResponseHandler((_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException("private-transport-secret", token);
        }));
        var client = new BingFreeTranslationClient(http);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.TranslateAsync("Hello", "en", cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.DoesNotContain("private-transport-secret", error.ToString());
    }

    private static HttpResponseMessage TranslationResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("[{\"detectedLanguage\":{\"language\":\"en\",\"score\":1.0},\"translations\":[{\"text\":\"你好世界\",\"to\":\"zh-Hans\"}]}]", Encoding.UTF8, "application/json")
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
