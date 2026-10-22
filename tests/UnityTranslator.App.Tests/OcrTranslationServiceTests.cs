using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class OcrTranslationServiceTests
{
    [Fact]
    public async Task UnsupportedStableLinesDoNotConsumeTranslationQuota()
    {
        using var scenario = new QuotaScenario([
            new(0, 0, 100, 20, "123", 0.95f),
            new(0, 30, 100, 20, "456", 0.95f),
            new(0, 60, 100, 20, "789", 0.95f),
            new(0, 90, 100, 20, "中文", 0.95f),
            new(0, 120, 100, 20, "漢字", 0.95f),
            new(0, 150, 100, 20, "文字", 0.95f),
            new(217, 193, 109, 27, "Start", 0.95f)], "开始");

        Assert.Empty(await scenario.Service.ProcessAsync(scenario.Frame, CancellationToken.None));
        Assert.Empty(scenario.Provider.Calls);
        Assert.Empty(scenario.Latencies);

        var overlays = await scenario.Service.ProcessAsync(scenario.Frame with { FrameId = 42 }, CancellationToken.None);

        Assert.Equal(new[] { ("Start", "en") }, scenario.Provider.Calls);
        Assert.Equal(new OcrOverlayItem(217, 193, 109, 27, "开始"), Assert.Single(overlays));
        Assert.StartsWith("[延迟] req=ocr-42-0 ", Assert.Single(scenario.Latencies));
    }

    [Fact]
    public async Task StableLinesAttemptOnlyFirstSixEligibleInRecognizerOrder()
    {
        using var scenario = new QuotaScenario(InterspersedLines(), "译文");

        Assert.Empty(await scenario.Service.ProcessAsync(scenario.Frame, CancellationToken.None));
        Assert.Empty(scenario.Provider.Calls);
        Assert.Empty(scenario.Latencies);

        var overlays = await scenario.Service.ProcessAsync(scenario.Frame with { FrameId = 42 }, CancellationToken.None);

        Assert.Equal(new[] { ("Start", "en"), ("はじめる", "ja"), ("Options", "en"),
            ("つづける", "ja"), ("Load", "en"), ("おわる", "ja") }, scenario.Provider.Calls);
        Assert.Equal(new[] {
            new OcrOverlayItem(100, 30, 100, 20, "译文"),
            new OcrOverlayItem(100, 90, 100, 20, "译文"),
            new OcrOverlayItem(100, 150, 100, 20, "译文"),
            new OcrOverlayItem(100, 180, 100, 20, "译文"),
            new OcrOverlayItem(100, 210, 100, 20, "译文"),
            new OcrOverlayItem(100, 240, 100, 20, "译文") }, overlays);
        Assert.Collection(scenario.Latencies,
            line => Assert.StartsWith("[延迟] req=ocr-42-0 ", line),
            line => Assert.StartsWith("[延迟] req=ocr-42-1 ", line),
            line => Assert.StartsWith("[延迟] req=ocr-42-2 ", line),
            line => Assert.StartsWith("[延迟] req=ocr-42-3 ", line),
            line => Assert.StartsWith("[延迟] req=ocr-42-4 ", line),
            line => Assert.StartsWith("[延迟] req=ocr-42-5 ", line));
    }

    [Fact]
    public async Task NullRepliesStillConsumeSixEligibleAttempts()
    {
        using var scenario = new QuotaScenario(InterspersedLines(), null);

        Assert.Empty(await scenario.Service.ProcessAsync(scenario.Frame, CancellationToken.None));
        Assert.Empty(scenario.Provider.Calls);
        Assert.Empty(await scenario.Service.ProcessAsync(scenario.Frame with { FrameId = 42 }, CancellationToken.None));

        Assert.Equal(new[] { ("Start", "en"), ("はじめる", "ja"), ("Options", "en"),
            ("つづける", "ja"), ("Load", "en"), ("おわる", "ja") }, scenario.Provider.Calls);
    }

    private static IReadOnlyList<OcrBox> InterspersedLines() => [
        new(100, 0, 100, 20, "123", 0.95f),
        new(100, 30, 100, 20, "Start", 0.95f),
        new(100, 60, 100, 20, "中文", 0.95f),
        new(100, 90, 100, 20, "はじめる", 0.95f),
        new(100, 120, 100, 20, "456", 0.95f),
        new(100, 150, 100, 20, "Options", 0.95f),
        new(100, 180, 100, 20, "つづける", 0.95f),
        new(100, 210, 100, 20, "Load", 0.95f),
        new(100, 240, 100, 20, "おわる", 0.95f),
        new(100, 270, 100, 20, "Credits", 0.95f)];

    private sealed class QuotaScenario : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ocr-quota-" + Guid.NewGuid());
        private readonly TranslationStore _store;
        public RecordingProvider Provider { get; }
        public List<string> Latencies { get; } = [];
        public OcrFrame Frame { get; } = new(41, "ignored", 640, 480, []);
        public OcrTranslationService Service { get; }

        public QuotaScenario(IReadOnlyList<OcrBox> boxes, string? reply)
        {
            _store = new TranslationStore(Path.Combine(_directory, "translations.db"));
            Provider = new RecordingProvider(reply);
            var coordinator = new TranslationCoordinator(_store, Provider) { Latency = Latencies.Add };
            Service = new OcrTranslationService(new FixedRecognizer(boxes), coordinator);
        }

        public void Dispose()
        {
            _store.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FixedRecognizer(IReadOnlyList<OcrBox> boxes) : IOcrRecognizer
    {
        public Task<IReadOnlyList<OcrBox>> RecognizeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(boxes);
    }

    private sealed class RecordingProvider(string? reply) : IOnlineTranslator
    {
        public List<(string Source, string Language)> Calls { get; } = [];
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken)
        {
            Calls.Add((source, sourceLanguage));
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task StableUncoveredBoxGetsTranslated()
    {
        var db = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var store = new TranslationStore(db);
            var service = new OcrTranslationService(new FakeRecognizer(),
                new TranslationCoordinator(store, new FakeProvider()));
            var frame = new OcrFrame(1, "ignored", 640, 480,
                [new OcrRect(0, 0, 100, 30)]);
            Assert.Empty(await service.ProcessAsync(frame, CancellationToken.None));
            Assert.Single(await service.ProcessAsync(frame with { FrameId = 2 }, CancellationToken.None));
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    private sealed class FakeRecognizer : IOcrRecognizer
    {
        public Task<IReadOnlyList<OcrBox>> RecognizeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OcrBox>>([
                new OcrBox(0, 0, 100, 30, "Covered", 0.9f),
                new OcrBox(200, 50, 100, 30, "Start", 0.9f),
                new OcrBox(400, 50, 100, 30, "Noise", 0.4f)]);
    }

    private sealed class FakeProvider : IOnlineTranslator
    {
        public Task<string?> TranslateAsync(string source, string sourceLanguage, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("开始");
    }
}
