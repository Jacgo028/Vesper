using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class NamedPipeHostTests
{
    [Fact]
    public async Task RepliesAndSurvivesMalformedMessage()
    {
        var db = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var store = new TranslationStore(db);
            using var host = new NamedPipeHost("UnityTranslatorTest" + Guid.NewGuid().ToString("N"),
                new TranslationCoordinator(store, new FakeProvider()));
            host.Start();
            using var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync("bad-json");
            await writer.WriteLineAsync("{\"Version\":1,\"RequestId\":\"r\",\"Source\":\"Start\",\"SourceLanguage\":\"en\",\"ComponentId\":7,\"Generation\":2}");
            var reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var json = JsonDocument.Parse(reply!);
            Assert.Equal("开始", json.RootElement.GetProperty("Translated").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("Generation").GetInt32());
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    [Fact]
    public async Task OcrFrameGetsOverlayReply()
    {
        var db = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var store = new TranslationStore(db);
            using var host = new NamedPipeHost("UnityTranslatorTest" + Guid.NewGuid().ToString("N"),
                new TranslationCoordinator(store, null));
            host.OcrFrameReceived = (_, _) => Task.FromResult<IReadOnlyList<OcrOverlayItem>>(
                [new OcrOverlayItem(1, 2, 3, 4, "中文")]);
            host.Start();
            using var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync("{\"Version\":1,\"Kind\":\"OcrFrame\",\"FrameId\":2,\"ImagePath\":\"C:\\\\temp\\\\f.png\",\"Width\":640,\"Height\":480,\"KnownUiBoxes\":[]}");
            var reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var json = JsonDocument.Parse(reply!);
            Assert.Equal("OcrOverlay", json.RootElement.GetProperty("Kind").GetString());
            Assert.Equal("中文", json.RootElement.GetProperty("Boxes")[0].GetProperty("Translated").GetString());
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    private sealed class FakeProvider : IOnlineTranslator
    {
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("开始");
    }
}
