using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class TranslationCoordinatorConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mango-coordinator-concurrency-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentDuplicateAndLatestRetryReuseOneResultWithTheirOwnReplyIdentity()
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var provider = new BoundaryProvider((_, _, _, token) => release.Task.WaitAsync(token));
        var coordinator = new TranslationCoordinator(store, provider, "free:bing");

        var first = coordinator.TranslateAsync(new("old-request", "Start", "en", 42, 7), stop.Token);
        var retry = coordinator.TranslateAsync(new("latest-request", "Start", "en", 42, 7), stop.Token);
        var otherComponent = coordinator.TranslateAsync(new("other-component", "Start", "en", 99, 11), stop.Token);
        release.SetResult("开始游戏");
        var replies = await Task.WhenAll(first, retry, otherComponent).WaitAsync(Limit);

        Assert.Equal(new TranslateReply("old-request", "开始游戏", 42, 7), replies[0]);
        Assert.Equal(new TranslateReply("latest-request", "开始游戏", 42, 7), replies[1]);
        Assert.Equal(new TranslateReply("other-component", "开始游戏", 99, 11), replies[2]);
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData("Start", "ja")]
    [InlineData("Continue", "en")]
    [InlineData(" Start", "en")]
    public async Task DifferentExactSourceOrLanguageCanReachProviderWhileAnotherKeyIsBlocked(string source, string language)
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var provider = new BoundaryProvider((_, _, _, token) => release.Task.WaitAsync(token));
        var coordinator = new TranslationCoordinator(store, provider, "free:bing");

        var first = coordinator.TranslateAsync(new("first", "Start", "en", 42, 7), stop.Token);
        var second = coordinator.TranslateAsync(new("second", source, language, 99, 11), stop.Token);
        var enteredBeforeRelease = provider.Calls;
        release.SetResult("开始游戏");
        var replies = await Task.WhenAll(first, second).WaitAsync(Limit);

        Assert.Equal(2, enteredBeforeRelease);
        Assert.All(replies, reply => Assert.Equal("开始游戏", reply?.Translated));
    }

    [Fact]
    public async Task DifferentProviderCoordinatorsKeepConcurrentResultsAndCacheIsolated()
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var firstProvider = new BoundaryProvider(async (_, _, _, token) => { await release.Task.WaitAsync(token); return "开始游戏"; });
        var secondProvider = new BoundaryProvider(async (_, _, _, token) => { await release.Task.WaitAsync(token); return "从头开始"; });
        var firstCoordinator = new TranslationCoordinator(store, firstProvider, "free:bing");
        var secondCoordinator = new TranslationCoordinator(store, secondProvider, "api:custom");

        var first = firstCoordinator.TranslateAsync(new("first", "Start", "en", 42, 7), stop.Token);
        var second = secondCoordinator.TranslateAsync(new("second", "Start", "en", 99, 11), stop.Token);
        var bothEnteredBeforeRelease = firstProvider.Calls == 1 && secondProvider.Calls == 1;
        release.SetResult(null);
        var replies = await Task.WhenAll(first, second).WaitAsync(Limit);

        Assert.True(bothEnteredBeforeRelease);
        Assert.Equal("开始游戏", replies[0]?.Translated);
        Assert.Equal("从头开始", replies[1]?.Translated);
        Assert.Equal("开始游戏", store.FindEntry("en", "Start", "free:bing")?.Translated);
        Assert.Equal("从头开始", store.FindEntry("en", "Start", "api:custom")?.Translated);
    }

    [Fact]
    public async Task CancelingDuplicateWaiterDoesNotCancelOwnerOrSendAnotherProviderRequest()
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        using var waiterStop = new CancellationTokenSource();
        var release = NewResult();
        var provider = new BoundaryProvider((_, _, _, token) => release.Task.WaitAsync(token));
        var coordinator = new TranslationCoordinator(store, provider, "free:bing");

        var owner = coordinator.TranslateAsync(new("owner", "Start", "en", 42, 7), stop.Token);
        var waiter = coordinator.TranslateAsync(new("canceled-waiter", "Start", "en", 99, 11), waiterStop.Token);
        waiterStop.Cancel();
        var canceledReply = await waiter.WaitAsync(Limit);
        var ownerWasStillWaiting = !owner.IsCompleted;
        release.SetResult("开始游戏");
        var reply = await owner.WaitAsync(Limit);

        Assert.Null(canceledReply);
        Assert.True(ownerWasStillWaiting);
        Assert.Equal(new TranslateReply("owner", "开始游戏", 42, 7), reply);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CancelingOwnerReleasesSameSourceForTheNextWaitingRequest()
    {
        using var store = OpenStore();
        using var ownerStop = new CancellationTokenSource(Limit);
        using var stop = new CancellationTokenSource(Limit);
        var provider = new BoundaryProvider(async (call, _, _, token) =>
        {
            if (call == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "开始游戏";
        });
        var coordinator = new TranslationCoordinator(store, provider, "free:bing");

        var owner = coordinator.TranslateAsync(new("owner", "Start", "en", 42, 7), ownerStop.Token);
        var waiter = coordinator.TranslateAsync(new("next-request", "Start", "en", 99, 11), stop.Token);
        ownerStop.Cancel();
        var replies = await Task.WhenAll(owner, waiter).WaitAsync(Limit);

        Assert.Null(replies[0]);
        Assert.Equal(new TranslateReply("next-request", "开始游戏", 99, 11), replies[1]);
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("echo")]
    [InlineData("chat")]
    [InlineData("failure")]
    [InlineData("unexpected")]
    public async Task FailedOrUnsafeResultDoesNotPreventTheWaitingRequestFromTryingAgain(string result)
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var provider = new BoundaryProvider(async (call, source, _, token) =>
        {
            if (call > 1) return "开始游戏";
            await release.Task.WaitAsync(token);
            return result switch
            {
                "empty" => null,
                "echo" => source,
                "chat" => "请提供需要翻译的英语游戏文字内容。",
                "unexpected" => throw new NotSupportedException("controlled unexpected provider failure"),
                _ => throw new HttpRequestException("controlled provider failure")
            };
        });
        var coordinator = new TranslationCoordinator(store, provider, "free:bing");

        var first = coordinator.TranslateAsync(new("failed", "Start", "en", 42, 7), stop.Token);
        var retry = coordinator.TranslateAsync(new("retry", "Start", "en", 99, 11), stop.Token);
        release.SetResult(null);
        var replies = await Task.WhenAll(first, retry).WaitAsync(Limit);

        Assert.Null(replies[0]);
        Assert.Equal(new TranslateReply("retry", "开始游戏", 99, 11), replies[1]);
        Assert.Equal(2, provider.Calls);
        Assert.Equal("开始游戏", store.FindEntry("en", "Start", "free:bing")?.Translated);
    }

    [Theory]
    [InlineData(null, "自动生成译文")]
    [InlineData("free:bing", "自动生成译文")]
    [InlineData("free:bing", null)]
    public async Task CorrectionSavedWhileProviderIsRunningWinsForThePendingReply(string? scope, string? generated)
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var provider = new BoundaryProvider((_, _, _, token) => release.Task.WaitAsync(token));
        var coordinator = new TranslationCoordinator(store, provider, scope);

        var pending = coordinator.TranslateAsync(new("pending", "Start", "en", 42, 7), stop.Token);
        store.SaveCorrection("en", "Start", "我选择的开始按钮");
        release.SetResult(generated);
        var reply = await pending.WaitAsync(Limit);

        Assert.Equal(new TranslateReply("pending", "我选择的开始按钮", 42, 7), reply);
        Assert.Equal(new StoredTranslation("我选择的开始按钮", true), store.FindEntry("en", "Start", scope));
    }

    [Theory]
    [InlineData(null, "http")]
    [InlineData("free:bing", "http")]
    [InlineData(null, "unexpected")]
    [InlineData("free:bing", "unexpected")]
    public async Task CorrectionSavedBeforeProviderFailureReachesPendingAndWaitingRequests(string? scope, string failure)
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var provider = new BoundaryProvider(async (_, _, _, token) =>
        {
            await release.Task.WaitAsync(token);
            throw failure == "http"
                ? new HttpRequestException("controlled provider failure")
                : new NotSupportedException("controlled unexpected provider failure");
        });
        var coordinator = new TranslationCoordinator(store, provider, scope);

        var pending = coordinator.TranslateAsync(new("pending", "Start", "en", 42, 7), stop.Token);
        var waiter = coordinator.TranslateAsync(new("waiter", "Start", "en", 99, 11), stop.Token);
        store.SaveCorrection("en", "Start", "我选择的开始按钮");
        release.SetResult(null);
        var replies = await Task.WhenAll(pending, waiter).WaitAsync(Limit);

        Assert.Equal(new TranslateReply("pending", "我选择的开始按钮", 42, 7), replies[0]);
        Assert.Equal(new TranslateReply("waiter", "我选择的开始按钮", 99, 11), replies[1]);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(new StoredTranslation("我选择的开始按钮", true), store.FindEntry("en", "Start", scope));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("unexpected")]
    [InlineData("canceled")]
    [InlineData("success")]
    public async Task CallerCancellationWinsOverCorrectionWhileOtherWaiterCanUseIt(string outcome)
    {
        using var store = OpenStore();
        using var ownerStop = new CancellationTokenSource(Limit);
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var provider = new BoundaryProvider(async (_, _, _, token) =>
        {
            // A completed provider can race cancellation or ignore its token.
            await release.Task.WaitAsync(stop.Token);
            return outcome switch
            {
                "http" => throw new HttpRequestException("controlled provider failure"),
                "unexpected" => throw new NotSupportedException("controlled unexpected provider failure"),
                "canceled" => throw new TaskCanceledException("controlled cancellation", null, token),
                _ => "自动生成译文"
            };
        });
        var coordinator = new TranslationCoordinator(store, provider, "free:bing");

        var pending = coordinator.TranslateAsync(new("canceled-owner", "Start", "en", 42, 7), ownerStop.Token);
        var waiter = coordinator.TranslateAsync(new("waiter", "Start", "en", 99, 11), stop.Token);
        store.SaveCorrection("en", "Start", "我选择的开始按钮");
        ownerStop.Cancel();
        release.SetResult(null);
        var replies = await Task.WhenAll(pending, waiter).WaitAsync(Limit);

        Assert.Null(replies[0]);
        Assert.Equal(new TranslateReply("waiter", "我选择的开始按钮", 99, 11), replies[1]);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CompletedSourceCanBeEvictedAndRequestedRepeatedlyWithoutLosingWaiters()
    {
        using var store = OpenStore();
        using var stop = new CancellationTokenSource(Limit);
        var release = NewResult();
        var provider = new BoundaryProvider((_, _, _, token) => release.Task.WaitAsync(token));
        var coordinator = new TranslationCoordinator(store, provider, "free:bing");

        for (var round = 0; round < 12; round++)
        {
            release = NewResult();
            var first = coordinator.TranslateAsync(new("owner-" + round, "Start", "en", 42, round + 1), stop.Token);
            var second = coordinator.TranslateAsync(new("waiter-" + round, "Start", "en", 99, round + 1), stop.Token);
            release.SetResult("开始游戏");
            var replies = await Task.WhenAll(first, second).WaitAsync(Limit);
            Assert.All(replies, reply => Assert.Equal("开始游戏", reply?.Translated));
            store.Delete("en", "Start", "free:bing");
        }

        Assert.Equal(12, provider.Calls);
    }

    private TranslationStore OpenStore() => new(Path.Combine(_directory, "translations.db"));
    private static TaskCompletionSource<string?> NewResult() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class BoundaryProvider(Func<int, string, string, CancellationToken, Task<string?>> translate) : IOnlineTranslator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken) =>
            translate(Interlocked.Increment(ref _calls), source, sourceLanguage, cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
