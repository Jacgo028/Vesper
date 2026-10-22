using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UnityTranslator.App;

namespace UnityTranslator.App.Tests;

public sealed class LibreTranslateClientTests
{
    [Fact]
    public async Task SendsOnlyWhenInvokedAndRequestsSimplifiedChinese()
    {
        string? body = null;
        var handler = new FakeHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"translatedText\":\"开始\"}", Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var provider = new LibreTranslateClient(http, new Uri("https://example.test/"), null);

        var translated = await provider.TranslateAsync("Start", "en", CancellationToken.None);

        Assert.Equal("开始", translated);
        using var payload = JsonDocument.Parse(body!);
        Assert.Equal("Start", payload.RootElement.GetProperty("q").GetString());
        Assert.Equal("en", payload.RootElement.GetProperty("source").GetString());
        Assert.Equal("zh", payload.RootElement.GetProperty("target").GetString());
        Assert.False(payload.RootElement.TryGetProperty("api_key", out _));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
