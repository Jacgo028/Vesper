using System.Net;
using System.Text;
using System.Text.Json;
using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class TranslationProviderFactoryTests
{
    [Fact]
    public async Task FreeBingIgnoresInvalidApiSettingsAndSendsOnlyUnauthenticatedEdgeRequest()
    {
        Uri? endpoint = null;
        string[]? headers = null;
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            sent++;
            endpoint = request.RequestUri;
            headers = request.Headers.Select(header => header.Key).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"detectedLanguage\":{\"language\":\"en\",\"score\":1.0},\"translations\":[{\"text\":\"开始\",\"to\":\"zh-Hans\"}]}]",
                    Encoding.UTF8, "application/json")
            });
        }));

        var selected = TranslationProviderFactory.Create(http, TranslationMode.Free, FreeTranslationProvider.Bing,
            "invalid-private-api-url", "", "private-api-secret");

        Assert.IsType<BingFreeTranslationClient>(selected.Translator);
        Assert.Equal(0, sent);
        var translated = await selected.Translator.TranslateAsync("Start", "en", CancellationToken.None);

        Assert.Equal("开始", translated);
        Assert.Equal(1, sent);
        Assert.Equal("https://edge.microsoft.com/translate/translatetext?from=en&to=zh-Hans&isEnterpriseClient=false", endpoint?.AbsoluteUri);
        Assert.DoesNotContain(headers!, header => header.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || header.Contains("key", StringComparison.OrdinalIgnoreCase)
            || header.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("free:bing", selected.CacheScope);
        Assert.Equal("免费翻译 · 必应 Bing", selected.DisplayName);
        Assert.DoesNotContain("private-api", selected.ToString());
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void FreeBingDoesNotRequireAnyApiField()
    {
        using var http = new HttpClient();

        var selected = TranslationProviderFactory.Create(http, TranslationMode.Free, FreeTranslationProvider.Bing,
            null!, null!, null!);

        Assert.IsType<BingFreeTranslationClient>(selected.Translator);
        Assert.Equal("free:bing", selected.CacheScope);
    }

    [Theory]
    [InlineData(TranslationMode.Free, FreeTranslationProvider.Baidu)]
    [InlineData(TranslationMode.Free, (FreeTranslationProvider)999)]
    [InlineData((TranslationMode)999, FreeTranslationProvider.Bing)]
    public void UnsupportedSelectionThrowsWithoutSendingOrFallingBack(TranslationMode mode, FreeTranslationProvider provider)
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var error = Assert.Throws<InvalidOperationException>(() => TranslationProviderFactory.Create(http, mode, provider,
            "https://private-user:private-password@api.example.test/chat?token=private-query", "private-model", "private-api-secret"));

        Assert.Equal(0, sent);
        Assert.DoesNotContain("private-", error.ToString());
        if (mode == TranslationMode.Free && provider == FreeTranslationProvider.Baidu)
            Assert.Contains("百度", error.Message);
    }

    [Fact]
    public async Task ApiSelectionUsesConfiguredEndpointModelAndPerRequestBearerWithoutWarmup()
    {
        Uri? endpoint = null;
        string? model = null;
        string? source = null;
        string? scheme = null;
        string? key = null;
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler(async (request, token) =>
        {
            sent++;
            endpoint = request.RequestUri;
            scheme = request.Headers.Authorization?.Scheme;
            key = request.Headers.Authorization?.Parameter;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            model = body.RootElement.GetProperty("model").GetString();
            source = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            return ApiResponse();
        }));

        var selected = TranslationProviderFactory.Create(http, TranslationMode.Api, (FreeTranslationProvider)999,
            "https://api.example.test/chat/completions", " model-a ", "private-api-secret");

        Assert.IsType<ChatTranslationClient>(selected.Translator);
        Assert.Equal(0, sent);
        var translated = await selected.Translator.TranslateAsync("始める", "ja", CancellationToken.None);

        Assert.Equal("开始", translated);
        Assert.Equal(1, sent);
        Assert.Equal("https://api.example.test/chat/completions", endpoint?.AbsoluteUri);
        Assert.Equal("model-a", model);
        Assert.Equal("始める", source);
        Assert.Equal("Bearer", scheme);
        Assert.Equal("private-api-secret", key);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal("API 翻译", selected.DisplayName);
    }

    [Fact]
    public async Task SwitchingFromApiToFreeDoesNotCarryApiAuthorizationToEdge()
    {
        var requests = new List<(string? Host, string? Key)>();
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            requests.Add((request.RequestUri?.Host, request.Headers.Authorization?.Parameter));
            return Task.FromResult(request.RequestUri?.Host == "edge.microsoft.com"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"translations\":[{\"text\":\"开始\",\"to\":\"zh-Hans\"}]}]", Encoding.UTF8, "application/json")
                }
                : ApiResponse());
        }));
        var api = TranslationProviderFactory.Create(http, TranslationMode.Api, FreeTranslationProvider.Bing,
            "https://api.example.test/chat/completions", "model-a", "private-api-secret");
        await api.Translator.TranslateAsync("Start", "en", CancellationToken.None);
        var free = TranslationProviderFactory.Create(http, TranslationMode.Free, FreeTranslationProvider.Bing,
            "https://api.example.test/chat/completions", "model-a", "private-api-secret");

        await free.Translator.TranslateAsync("Start", "en", CancellationToken.None);

        Assert.Equal(new (string? Host, string? Key)[]
            { ("api.example.test", "private-api-secret"), ("edge.microsoft.com", null) }, requests);
    }

    [Theory]
    [InlineData("http://api.example.test/private-endpoint", "model-a", "private-api-secret")]
    [InlineData("ftp://api.example.test/private-endpoint", "model-a", "private-api-secret")]
    [InlineData("invalid-private-endpoint", "model-a", "private-api-secret")]
    [InlineData("/private-endpoint", "model-a", "private-api-secret")]
    [InlineData("", "model-a", "private-api-secret")]
    [InlineData("https://api.example.test/private-endpoint", "", "private-api-secret")]
    [InlineData("https://api.example.test/private-endpoint", " \t", "private-api-secret")]
    [InlineData("https://api.example.test/private-endpoint", "model-a", "")]
    [InlineData("https://api.example.test/private-endpoint", "model-a", " \t")]
    public void InvalidApiConfigurationThrowsWithoutSendingOrExposingSettings(string endpoint, string model, string apiKey)
    {
        var sent = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(ApiResponse());
        }));

        var error = Assert.Throws<InvalidOperationException>(() => TranslationProviderFactory.Create(http,
            TranslationMode.Api, FreeTranslationProvider.Bing, endpoint, model, apiKey));

        Assert.Equal(0, sent);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Fact]
    public void ApiCacheScopeUsesCanonicalEndpointAndTrimmedModelHash()
    {
        using var http = new HttpClient();

        var selected = TranslationProviderFactory.Create(http, TranslationMode.Api, FreeTranslationProvider.Bing,
            "HTTPS://API.EXAMPLE.TEST:443/chat/completions", " model-a ", "private-api-secret");

        Assert.Equal("api:74879400AC8D9EA94A1CDB833477EAE77D9561673D0FA17ABB8430194DC446AB", selected.CacheScope);
    }

    [Theory]
    [InlineData("https://other.example.test/chat/completions", "model-a")]
    [InlineData("https://api.example.test/chat/completions", "model-b")]
    public void EndpointOrModelChangeGetsItsOwnCacheScope(string changedEndpoint, string changedModel)
    {
        using var http = new HttpClient();
        var original = TranslationProviderFactory.Create(http, TranslationMode.Api, FreeTranslationProvider.Bing,
            "https://api.example.test/chat/completions", "model-a", "private-api-secret");

        var changed = TranslationProviderFactory.Create(http, TranslationMode.Api, FreeTranslationProvider.Bing,
            changedEndpoint, changedModel, "private-api-secret");

        Assert.NotEqual(original.CacheScope, changed.CacheScope);
        Assert.NotEqual("free:bing", changed.CacheScope);
    }

    [Fact]
    public void ApiKeyRotationKeepsTheSameCacheScope()
    {
        using var http = new HttpClient();
        var original = TranslationProviderFactory.Create(http, TranslationMode.Api, FreeTranslationProvider.Bing,
            "https://api.example.test/chat/completions", "model-a", "old-private-api-secret");

        var rotated = TranslationProviderFactory.Create(http, TranslationMode.Api, FreeTranslationProvider.Baidu,
            "https://api.example.test/chat/completions", "model-a", "new-private-api-secret");

        Assert.Equal(original.CacheScope, rotated.CacheScope);
    }

    [Fact]
    public void ApiCacheAndDisplayDoNotExposeEndpointCredentialsQueriesModelOrKey()
    {
        using var http = new HttpClient();

        var selected = TranslationProviderFactory.Create(http, TranslationMode.Api, FreeTranslationProvider.Bing,
            "https://private-user:private-password@api.example.test/chat/completions?token=private-query",
            "private-model", "private-api-secret");

        Assert.Matches("^api:[0-9A-F]{64}$", selected.CacheScope);
        Assert.Equal("API 翻译", selected.DisplayName);
        Assert.DoesNotContain("private-", selected.ToString());
    }

    private static HttpResponseMessage ApiResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"开始\"},\"finish_reason\":\"stop\"}]}",
            Encoding.UTF8, "application/json")
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
