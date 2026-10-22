using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace UnityTranslator.App;

public sealed class ChatTranslationClient : IOnlineTranslator
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public ChatTranslationClient(HttpClient client, Uri endpoint, string model, string apiKey)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("服务地址必须使用 HTTPS", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("请填写模型名称", nameof(model));
        _client = client;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
    }

    public async Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken)
    {
        // 这里以前写死了"将用户给出的{language}游戏文字翻译为简体中文"，而 language 来自
        // TextSelectionPolicy.DetectLanguage —— 纯汉字文本一律返回 "en"（为了缓存分桶稳定）。
        // 于是「設定」「编辑」被当成"英语游戏文字"发过去，模型看不到英语，
        // 就回了一句"请提供需要翻译的英语游戏文字内容。"，还被当译文写进了游戏。
        // 现在只在确实认出假名时才说是日语，其余情况如实说明输入可能是哪种语言。
        var languageHint = sourceLanguage == "ja"
            ? "输入是日语。"
            : "输入可能是日语、英语，也可能已经是中文。";
        var body = new
        {
            model = _model,
            temperature = 0.1,
            stream = false,
            messages = BuildMessages(languageHint, source),
            max_tokens = MaxTokens,
            // ★★★ 关掉"思考模式" —— 这是让翻译变快最有效的一处。
            //
            // 官方文档（api-docs.deepseek.com/guides/thinking_mode）写明：
            //   "Thinking mode is enabled by default, with the default effort being high"
            //   "before outputting the final answer, the model will first output a
            //    chain-of-thought reasoning"
            //
            // 翻译「設定」→「设置」不需要任何推理，但默认行为是**先生成一整段推理过程**
            // 再给译文 —— 用户看到的 1.2~1.7 秒里，很大一部分花在这段他永远看不到的内容上。
            //
            // 旁证：同一份文档说思考模式**不支持 temperature**（"setting these parameters
            // will not trigger an error but will also have no effect"）。我们一直设着
            // temperature=0.1 却毫无作用，正好说明请求一直跑在思考模式下。
            //
            // 非思考模式还带来两个附带好处：
            //   * temperature 真正生效（译文更稳定）；
            //   * 输出里不再有 reasoning_content，max_tokens 的上限能真正约束"译文长度"。
            //
            // 用 UT_THINKING=1 可以把它重新打开（便于对照）。
            thinking = new { type = ThinkingEnabled ? "enabled" : "disabled" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
    }

    /// <summary>
    /// 输出长度上限。**不设上限时模型可能多生成**，而我们只要一行译文 ——
    /// 多出来的每个 token 都是用户白等的生成时间。400 足够任何一句游戏文本
    /// （实测最长的道具说明约 60 字），同时挡住"解释一大堆"的情况。
    /// 可用 `UT_MAX_TOKENS` 覆盖；设为 0 表示不加这个字段（回到旧行为）。
    /// </summary>
    public static int MaxTokens { get; set; } = ReadMaxTokens();

    /// <summary>
    /// 是否让服务端"先推理再回答"。**翻译场景默认关掉**（见 TranslateAsync 里的说明）。
    /// 用 `UT_THINKING=1` 打开，便于对照两种模式的耗时与译文质量。
    /// </summary>
    public static bool ThinkingEnabled { get; set; } =
        Environment.GetEnvironmentVariable("UT_THINKING") == "1";

    private static int ReadMaxTokens()
    {
        var raw = Environment.GetEnvironmentVariable("UT_MAX_TOKENS");
        if (string.IsNullOrEmpty(raw)) return 400;
        return int.TryParse(raw, out var v) && v >= 0 ? v : 400;
    }

    private static object[] BuildMessages(string languageHint, string source) =>
    [
        new { role = "system", content = $"把用户给出的游戏界面文字翻译成简体中文。{languageHint}" +
            "只输出译文本身：不要解释、不要提问、不要加引号或任何前后缀。已经是中文就原样返回。" +
            "保留原有换行、占位符（如 {0}、%s）和富文本标签（如 <b>）。" },
        new { role = "user", content = source }
    ];

    /// <summary>
    /// 预热连接：用一次极小的请求把 TCP + TLS 握手做掉，让**第一句真正的翻译**
    /// 不用再付这笔钱。
    ///
    /// 为什么值得做（第 100 轮实测）：连接建好之后 发送→回包 P50 只有 43ms，
    /// 慢的是模型本身；但**首次**请求要额外做握手，那几百毫秒恰好落在
    /// "玩家第一次看到译文"的时刻 —— 也就是用户最容易觉得慢的地方。
    ///
    /// 设计约束：**失败绝不抛出**。它只是优化，预热不成功也必须照常翻译。
    /// </summary>
    public async Task WarmUpConnectionAsync(Uri endpoint)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = _model,
                    temperature = 0.0,
                    stream = false,
                    max_tokens = 1,
                    messages = new object[] { new { role = "user", content = "1" } }
                })
            };
            if (!string.IsNullOrWhiteSpace(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _client.SendAsync(request, cts.Token);
            // 状态码不重要（401/429 也表示连接已经建立），这里只关心握手完成。
        }
        catch (Exception)
        {
            // 预热失败不影响任何功能。
        }
    }
}
