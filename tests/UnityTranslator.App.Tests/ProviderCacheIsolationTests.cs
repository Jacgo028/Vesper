using System.Reflection;
using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class ProviderCacheIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mango-provider-cache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SwitchingProviderDoesNotReuseAnotherProvidersGeneratedTranslation()
    {
        using var store = OpenStore();
        var bing = Create(store, new Response("开始游戏"), "free:bing");
        var baidu = Create(store, new Response("从头开始"), "free:baidu");
        Assert.Equal("开始游戏", (await bing.TranslateAsync(Request(), CancellationToken.None))?.Translated);
        Assert.Equal("从头开始", (await baidu.TranslateAsync(Request(), CancellationToken.None))?.Translated);
        Assert.Equal("开始游戏", (await Create(store, new MustNotSend(), "free:bing").TranslateAsync(Request(), CancellationToken.None))?.Translated);
        Assert.Equal("从头开始", (await Create(store, new MustNotSend(), "free:baidu").TranslateAsync(Request(), CancellationToken.None))?.Translated);
    }

    [Fact]
    public async Task OldUnattributedAutomaticCacheDoesNotMasqueradeAsANewProviderResult()
    {
        using var store = OpenStore();
        store.SaveGenerated("en", "Start", "旧服务译文");
        var provider = new Response("开始游戏");
        Assert.Equal("开始游戏", (await Create(store, provider, "free:bing").TranslateAsync(Request(), CancellationToken.None))?.Translated);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("旧服务译文", store.Find("en", "Start"));
    }

    [Fact]
    public async Task UserCorrectionWinsAcrossProvidersWithoutANetworkRequest()
    {
        using var store = OpenStore();
        await Create(store, new Response("开始游戏"), "free:bing").TranslateAsync(Request(), CancellationToken.None);
        store.SaveCorrection("en", "Start", "我的开始按钮");
        foreach (var scope in new[] { "free:bing", "free:baidu", "api:custom" })
            Assert.Equal("我的开始按钮", (await Create(store, new MustNotSend(), scope).TranslateAsync(Request(), CancellationToken.None))?.Translated);
    }

    [Fact]
    public async Task ProviderCacheSurvivesReopeningWithoutChangingTheLegacyTable()
    {
        using (var store = OpenStore())
            await Create(store, new Response("开始游戏"), "free:bing").TranslateAsync(Request(), CancellationToken.None);
        using var reopened = OpenStore();
        Assert.Equal("开始游戏", (await Create(reopened, new MustNotSend(), "free:bing").TranslateAsync(Request(), CancellationToken.None))?.Translated);
        Assert.Null(reopened.Find("en", "Start"));
    }

    [Fact]
    public async Task SameProviderKeepsLanguagesIsolated()
    {
        using var store = OpenStore();
        Assert.Equal("开始游戏", (await Create(store, new Response("开始游戏"), "free:bing").TranslateAsync(Request(), CancellationToken.None))?.Translated);
        Assert.Equal("启动", (await Create(store, new Response("启动"), "free:bing").TranslateAsync(new("jp", "Start", "ja", 2, 1), CancellationToken.None))?.Translated);
    }

    [Fact]
    public async Task FailedProviderDoesNotFallBackToAnotherProvidersCache()
    {
        using var store = OpenStore();
        await Create(store, new Response("开始游戏"), "free:bing").TranslateAsync(Request(), CancellationToken.None);
        Assert.Null(await Create(store, new Response(null), "free:baidu").TranslateAsync(Request(), CancellationToken.None));
        Assert.Equal("开始游戏", (await Create(store, new MustNotSend(), "free:bing").TranslateAsync(Request(), CancellationToken.None))?.Translated);
    }

    private TranslationStore OpenStore()
    {
        Directory.CreateDirectory(_directory);
        return new TranslationStore(Path.Combine(_directory, "translations.db"));
    }

    private static TranslationCoordinator Create(TranslationStore store, IOnlineTranslator translator, string scope)
    {
        var constructor = typeof(TranslationCoordinator).GetConstructor([typeof(TranslationStore), typeof(IOnlineTranslator), typeof(string)]);
        Assert.NotNull(constructor);
        return (TranslationCoordinator)constructor.Invoke([store, translator, scope]);
    }

    private static TranslateRequest Request() => new("real-request", "Start", "en", 2, 1);
    private sealed class Response(string? translated) : IOnlineTranslator
    {
        public int Calls { get; private set; }
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(translated); }
    }
    private sealed class MustNotSend : IOnlineTranslator
    {
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This request must be served only from its own provider cache or the user's correction.");
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
