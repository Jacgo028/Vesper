using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public sealed record ConfiguredTranslationProvider(IOnlineTranslator Translator, string CacheScope, string DisplayName);

public static class TranslationProviderFactory
{
    public static ConfiguredTranslationProvider Create(HttpClient client, TranslationMode mode,
        FreeTranslationProvider freeProvider, string endpoint, string model, string apiKey)
    {
        if (mode == TranslationMode.Free)
            return freeProvider switch
            {
                FreeTranslationProvider.Bing => new ConfiguredTranslationProvider(new BingFreeTranslationClient(client),
                    "free:bing", "免费翻译 · 必应 Bing"),
                FreeTranslationProvider.Baidu => throw new InvalidOperationException("百度免费翻译目前不可用，请选择必应 Bing。"),
                _ => throw new InvalidOperationException("未知的免费翻译服务，请重新选择。")
            };

        if (mode != TranslationMode.Api)
            throw new InvalidOperationException("未知的翻译模式，请重新选择。");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("API 服务地址必须是有效的 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("请填写 API 模型名称。");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请填写 API 密钥。");

        var normalizedModel = model.Trim();
        var identity = Encoding.UTF8.GetBytes(uri.AbsoluteUri + "\n" + normalizedModel);
        var scope = "api:" + Convert.ToHexString(SHA256.HashData(identity));
        return new ConfiguredTranslationProvider(new ChatTranslationClient(client, uri, normalizedModel, apiKey),
            scope, "API 翻译");
    }
}
