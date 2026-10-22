using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class TranslationCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-coordinator-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CorrectionWinsAndDoesNotCallProvider()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "translations.db");
        using (var store = new TranslationStore(db)) store.SaveCorrection("ja", "開始", "开始游戏");
        using var reopened = new TranslationStore(db);
        var provider = new FakeOnline("启动");
        var coordinator = new TranslationCoordinator(reopened, provider);
        var reply = await coordinator.TranslateAsync(new("r1", "開始", "ja", 42, 1), CancellationToken.None);
        Assert.Equal("开始游戏", reply?.Translated);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task UnsafeProviderOutputIsNotSaved()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new FakeOnline("生命最大"));
        var request = new TranslateRequest("r2", "HP {0} <b>MAX</b>", "en", 4, 1);
        Assert.Null(await coordinator.TranslateAsync(request, CancellationToken.None));
        Assert.Null(store.Find("en", request.Source));
    }

    [Fact]
    public async Task MissingProviderKeepsOriginal()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, null);
        Assert.Null(await coordinator.TranslateAsync(new("r3", "Start", "en", 4, 1), CancellationToken.None));
    }

    [Fact]
    public async Task ProviderFailureKeepsOriginal()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new FakeOnline(null));
        Assert.Null(await coordinator.TranslateAsync(new("r4", "Start", "en", 4, 1), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedProviderResponseKeepsPipeServiceAlive()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new MalformedOnline());
        Assert.Null(await coordinator.TranslateAsync(new("r5", "Start", "en", 4, 1), CancellationToken.None));
    }

    private sealed class MalformedOnline : IOnlineTranslator
    {
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken) =>
            throw new System.Text.Json.JsonException("bad response");
    }

    private sealed class ThrowingOnline(Exception error) : IOnlineTranslator
    {
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken) =>
            throw error;
    }

    /// <summary>
    /// 翻译失败必须留下可读的诊断，否则"游戏里没有翻译"完全无法排查。
    /// V0.3 及以前这些分支全部静默返回 null，app.log 里一个字都没有。
    /// </summary>
    [Fact]
    public async Task MissingProviderIsReportedInDiagnostics()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, null);
        var notes = new List<string>();
        coordinator.Diagnostics = notes.Add;

        Assert.Null(await coordinator.TranslateAsync(new("r6", "設定", "en", 4, 1), CancellationToken.None));

        var note = Assert.Single(notes);
        Assert.Contains("没有可用的联网翻译服务", note);
        Assert.Contains("設定", note);
    }

    [Fact]
    public async Task ProviderFailureIsReportedWithExceptionType()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new ThrowingOnline(new HttpRequestException("连接被拒绝")));
        var notes = new List<string>();
        coordinator.Diagnostics = notes.Add;

        Assert.Null(await coordinator.TranslateAsync(new("r7", "装備", "en", 4, 1), CancellationToken.None));

        var note = Assert.Single(notes);
        Assert.Contains("翻译服务调用失败", note);
        // 必须带上异常类型与消息，否则只知道"失败了"却不知道失败在哪一步。
        Assert.Contains("HttpRequestException", note);
        Assert.Contains("连接被拒绝", note);
    }

    /// <summary>
    /// 中文原文被原样返回时：不替换，但日志要说明原因，并明确这是"没翻"而不是"出错"。
    /// 这是纯汉字文本一律送去翻译之后必须保证的行为。
    /// </summary>
    [Fact]
    public async Task EchoedChineseIsRejectedAndExplained()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new FakeOnline("继续游戏"));
        var notes = new List<string>();
        coordinator.Diagnostics = notes.Add;

        Assert.Null(await coordinator.TranslateAsync(new("r8", "继续游戏", "en", 4, 1), CancellationToken.None));

        Assert.Single(notes);
        Assert.Contains("未通过校验", notes[0]);
        Assert.Null(store.Find("en", "继续游戏"));
    }

    [Fact]
    public async Task SuccessfulTranslationIsReported()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new FakeOnline("设置"));
        var notes = new List<string>();
        coordinator.Diagnostics = notes.Add;

        var reply = await coordinator.TranslateAsync(new("r9", "設定", "en", 4, 1), CancellationToken.None);

        Assert.Equal("设置", reply?.Translated);
        var note = Assert.Single(notes);
        Assert.Contains("翻译成功", note);
        Assert.Contains("設定", note);
        Assert.Contains("设置", note);
    }

    /// <summary>
    /// 没有接诊断回调时也不能抛异常 —— 诊断是可选能力，不能变成新的故障点。
    /// </summary>
    [Fact]
    public async Task DiagnosticsIsOptional()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new FakeOnline("设置"));

        var reply = await coordinator.TranslateAsync(new("r10", "設定", "en", 4, 1), CancellationToken.None);

        Assert.Equal("设置", reply?.Translated);
    }

    /// <summary>
    /// 实测事故：模型回了"请提供需要翻译的英语游戏文字内容。"，格式校验放它过去了，
    /// 于是它被写进游戏、还存进了译文库。现在整条丢掉，绝不落库。
    /// </summary>
    [Fact]
    public async Task ChatReplyFromProviderIsRejectedAndNotSaved()
    {
        Directory.CreateDirectory(_root);
        using var store = new TranslationStore(Path.Combine(_root, "translations.db"));
        var coordinator = new TranslationCoordinator(store, new FakeOnline("请提供需要翻译的英语游戏文字内容。"));
        var notes = new List<string>();
        coordinator.Diagnostics = notes.Add;

        Assert.Null(await coordinator.TranslateAsync(new("r11", "設定", "en", 4, 1), CancellationToken.None));

        Assert.Contains("对话式回答", Assert.Single(notes));
        Assert.Null(store.Find("en", "設定"));
    }

    /// <summary>
    /// 更要紧的一条：旧版本已经把这句话存进译文库了，而缓存命中这条路以前完全不校验，
    /// 所以它每次启动都会被原样贴回游戏界面。命中时必须重新校验，发现是垃圾就删掉重翻。
    /// </summary>
    [Fact]
    public async Task PoisonedCacheEntryIsDeletedAndRetranslated()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "translations.db");
        // 旧版本的 SaveGenerated 拦不住它（IsSafe 只查格式），照原样写进库里。
        using (var seed = new TranslationStore(db))
            seed.SaveGenerated("en", "設定", "请提供需要翻译的英语游戏文字内容。");

        using var store = new TranslationStore(db);
        var provider = new FakeOnline("设置");
        var coordinator = new TranslationCoordinator(store, provider);
        var notes = new List<string>();
        coordinator.Diagnostics = notes.Add;

        var reply = await coordinator.TranslateAsync(new("r12", "設定", "en", 4, 1), CancellationToken.None);

        Assert.Equal("设置", reply?.Translated);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("设置", store.Find("en", "設定"));                  // 脏数据已被正确译文替换
        Assert.Contains(notes, note => note.Contains("对话式回复"));
    }

    /// <summary>
    /// 用户手工改过的译文即使字面上像对话，也必须无条件采信 —— 那是他自己要的。
    /// </summary>
    [Fact]
    public async Task UserCorrectionIsTrustedEvenIfItLooksLikeAChatReply()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "translations.db");
        using (var seed = new TranslationStore(db))
            seed.SaveCorrection("en", "設定", "请提供需要翻译的英语游戏文字内容。");

        using var store = new TranslationStore(db);
        var provider = new FakeOnline("设置");
        var coordinator = new TranslationCoordinator(store, provider);

        var reply = await coordinator.TranslateAsync(new("r13", "設定", "en", 4, 1), CancellationToken.None);

        Assert.Equal("请提供需要翻译的英语游戏文字内容。", reply?.Translated);
        Assert.Equal(0, provider.Calls);
    }

    private sealed class FakeOnline(string? result) : IOnlineTranslator
    {
        public int Calls { get; private set; }
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
