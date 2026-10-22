using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace UnityTranslator.App;

public sealed class LibreTranslateClient(HttpClient client, Uri baseUri, string? apiKey) : IOnlineTranslator
{
    private readonly Uri _endpoint = baseUri.Scheme == Uri.UriSchemeHttps
        ? new Uri(baseUri, "translate")
        : throw new ArgumentException("联网翻译地址必须使用 HTTPS", nameof(baseUri));

    public async Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string>
        {
            ["q"] = source,
            ["source"] = sourceLanguage,
            ["target"] = "zh"
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) body["api_key"] = apiKey;
        using var response = await client.PostAsJsonAsync(_endpoint, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("translatedText").GetString();
    }
}
