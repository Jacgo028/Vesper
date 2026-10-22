using System.Net;
using System.Text;
using System.Text.Json;
using UnityTranslator.App;

namespace UnityTranslator.App.Tests;

public sealed class ChatTranslationClientTests
{
    [Fact]
    public async Task SendsJapaneseToChinesePromptAndReadsAnswer()
    {
        var handler = new RecordingHandler();
        var client = new ChatTranslationClient(new HttpClient(handler),
            new Uri("https://example.test/chat/completions"), "model", "secret");
        var translated = await client.TranslateAsync("始める", "ja", CancellationToken.None);
        Assert.Equal("开始", translated);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("始める", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Contains("简体中文", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
    }

    [Fact]
    public void RejectsPlainHttp()
    {
        Assert.Throws<ArgumentException>(() => new ChatTranslationClient(new HttpClient(),
            new Uri("http://example.test/chat/completions"), "model", "key"));
    }

    /// <summary>
    /// 纯汉字原文会被 TextSelectionPolicy.DetectLanguage 判成 "en"，旧提示词于是告诉模型
    /// "这是英语游戏文字"。模型看不到英语，就回问"请提供需要翻译的英语游戏文字内容。"，
    /// 那句话还被当译文写进了游戏。提示词不许再谎报语种。
    /// </summary>
    [Fact]
    public async Task DoesNotClaimTheInputIsEnglishWhenOnlyKanjiWasDetected()
    {
        var handler = new RecordingHandler();
        var client = new ChatTranslationClient(new HttpClient(handler),
            new Uri("https://example.test/chat/completions"), "model", "secret");

        await client.TranslateAsync("設定", "en", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body);
        var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain("英语游戏文字", system);
        Assert.Contains("日语、英语", system);
        Assert.Contains("不要提问", system);
    }

    [Fact]
    public async Task SaysJapaneseWhenKanaWasActuallyDetected()
    {
        var handler = new RecordingHandler();
        var client = new ChatTranslationClient(new HttpClient(handler),
            new Uri("https://example.test/chat/completions"), "model", "secret");

        await client.TranslateAsync("始める", "ja", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body);
        var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("输入是日语", system);
    }

    /// <summary>
    /// 请求必须带上 max_tokens 上限。
    ///
    /// 为什么要钉住（第 100 轮）：用户反馈"翻译还是很慢"。实测后确认服务端的往返占据了
    /// 几乎全部等待时间（发送→回包 P50=43ms，而 total≈API），所以我们能做的只剩
    /// **不请求多余的输出** —— 不设上限时模型可能多生成，而那段时间是用户白等的。
    /// 这条测试保证：以后谁把这个字段删掉，会立刻有一条测试失败，而不是悄悄让翻译变慢。
    /// </summary>
    [Fact]
    public async Task SendsMaxTokensCap()
    {
        var handler = new RecordingHandler();
        var client = new ChatTranslationClient(new HttpClient(handler),
            new Uri("https://example.test/chat/completions"), "model", "secret");

        await client.TranslateAsync("始める", "ja", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body);
        Assert.True(body.RootElement.TryGetProperty("max_tokens", out var cap), "request has no max_tokens");
        Assert.True(cap.GetInt32() > 0, "max_tokens must be a positive cap");
    }

    /// <summary>
    /// 系统提示词里必须同时保留"只输出译文"与"保留富文本标签"这两条要求。
    /// 它们直接被写进游戏画面：少了前者会把"好的，翻译如下："这类话贴进游戏，
    /// 少了后者会把 &lt;Color=#FF0000&gt; 之类的标签翻坏（实机见过这类文本）。
    /// </summary>
    [Fact]
    public async Task SystemPromptKeepsOutputRules()
    {
        var handler = new RecordingHandler();
        var client = new ChatTranslationClient(new HttpClient(handler),
            new Uri("https://example.test/chat/completions"), "model", "secret");

        await client.TranslateAsync("始める", "ja", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body);
        var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("只输出译文本身", system);
        Assert.Contains("富文本标签", system);
    }

    /// <summary>
    /// 预热连接**绝不能**在失败时抛出：它只是优化，服务不可用时翻译本身仍要正常工作。
    /// 用一个一律返回 500 的 handler 验证这一点。
    /// </summary>
    [Fact]
    public async Task WarmUpNeverThrows()
    {
        var client = new ChatTranslationClient(new HttpClient(new FailingHandler()),
            new Uri("https://example.test/chat/completions"), "model", "secret");

        // 不抛出即通过
        await client.WarmUpConnectionAsync(new Uri("https://example.test/chat/completions"));
    }

    /// <summary>
    /// ★ 请求必须**关掉思考模式**。
    ///
    /// 为什么这条测试重要（第 100 轮，官方文档）：DeepSeek 的思考模式**默认开启**
    /// （"enabled by default, with the default effort being high"），
    /// 模型会先生成一整段推理过程再给译文。翻译「設定」→「设置」不需要推理，
    /// 那段时间用户完全看不到，却实实在在要等 —— 这是 1.2~1.7 秒里的大头。
    ///
    /// 这条测试保证：以后谁把这个字段删掉，会立刻失败，而不是悄悄让翻译慢回去。
    /// </summary>
    [Fact]
    public async Task DisablesThinkingModeByDefault()
    {
        ChatTranslationClient.ThinkingEnabled = false;   // 默认值，显式写出来避免测试间互相影响
        var handler = new RecordingHandler();
        var client = new ChatTranslationClient(new HttpClient(handler),
            new Uri("https://example.test/chat/completions"), "model", "secret");

        await client.TranslateAsync("始める", "ja", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body);
        Assert.True(body.RootElement.TryGetProperty("thinking", out var thinking), "request has no thinking field");
        Assert.Equal("disabled", thinking.GetProperty("type").GetString());
    }

    /// <summary>UT_THINKING=1 时应当如实打开思考模式（保留对照能力，不是把开关写死）。</summary>
    [Fact]
    public async Task ThinkingModeCanBeTurnedBackOn()
    {
        ChatTranslationClient.ThinkingEnabled = true;
        try
        {
            var handler = new RecordingHandler();
            var client = new ChatTranslationClient(new HttpClient(handler),
                new Uri("https://example.test/chat/completions"), "model", "secret");

            await client.TranslateAsync("始める", "ja", CancellationToken.None);

            using var body = JsonDocument.Parse(handler.Body);
            Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }
        finally
        {
            ChatTranslationClient.ThinkingEnabled = false;   // 还原，别影响别的测试
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("nope", Encoding.UTF8, "text/plain")
            });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body = string.Empty;
        public string? AuthorizationScheme;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"开始\"}}]}", Encoding.UTF8, "application/json")
            };
        }
    }
}
