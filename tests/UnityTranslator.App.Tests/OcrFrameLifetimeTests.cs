using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class OcrFrameLifetimeTests
{
    // The game owns these captures until it validates (or discards) the overlay.
    // Deleting in the App callback breaks that contract even for empty/error replies.
    [Theory]
    [InlineData("empty")]
    [InlineData("disabled")]
    [InlineData("missing-worker")]
    [InlineData("unavailable-worker")]
    [InlineData("missing-service")]
    [InlineData("error")]
    [InlineData("cancelled")]
    public void Callback_preserves_game_owned_capture_on_every_exit(string scenario) => OnWindow(fixture =>
    {
        fixture.Configure(scenario);
        var frame = fixture.NewFrame();
        var token = scenario == "cancelled" ? new CancellationToken(true) : CancellationToken.None;

        if (scenario == "error")
            Assert.Throws<IOException>(() => Pump(fixture.Host.OcrFrameReceived!(frame, token)));
        else if (scenario == "cancelled")
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(fixture.Host.OcrFrameReceived!(frame, token)));
        else
            Assert.Empty(Pump(fixture.Host.OcrFrameReceived!(frame, token)));

        Assert.True(File.Exists(frame.ImagePath),
            $"The App deleted the game's capture on the {scenario} exit; the game still owns cleanup.");
    });

    [Fact]
    public void Nonempty_overlay_keeps_capture_available_when_actual_pipe_reply_is_written() => OnWindow(fixture =>
    {
        fixture.Configure("text");
        Assert.Empty(Pump(fixture.Host.OcrFrameReceived!(fixture.NewFrame(), CancellationToken.None)));
        var frame = fixture.NewFrame();
        using var writer = new CaptureObservingWriter(frame.ImagePath);
        SetField(fixture.Host, "_writer", writer);
        try
        {
            Pump((Task)Invoke(fixture.Host, "DispatchAsync", JsonSerializer.Serialize(new
            {
                Version = 1, Kind = "OcrFrame", frame.FrameId, frame.ImagePath,
                frame.Width, frame.Height, KnownUiBoxes = Array.Empty<object>()
            }))!);
        }
        finally { SetField(fixture.Host, "_writer", null); }

        var reply = Assert.Single(writer.Replies);
        Assert.Equal("OcrOverlay", reply.Kind);
        Assert.Equal(1, reply.BoxCount);
        Assert.True(reply.CaptureExists,
            "The nonempty overlay reached the pipe after the App deleted the capture required by game validation.");
        Assert.True(File.Exists(frame.ImagePath));
    });

    [Fact]
    public void Callback_success_does_not_claim_the_game_has_displayed_an_unacknowledged_overlay() => OnWindow(fixture =>
    {
        fixture.Configure("text");
        Assert.Empty(Pump(fixture.Host.OcrFrameReceived!(fixture.NewFrame(), CancellationToken.None)));
        Assert.Single(Pump(fixture.Host.OcrFrameReceived!(fixture.NewFrame(), CancellationToken.None)));
        // Settle the queued status update. No game ACK has been sent in this test.
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);

        var status = fixture.Window.RunStatusTextForTest();
        Assert.False(Regex.IsMatch(status, @"(?:已经|已).*(?:画面|屏幕).*(?:替换|显示)|(?:已经|已).*(?:替换|显示).*(?:画面|屏幕)"),
            $"The App claimed an on-screen result before game acknowledgement: {status}");
    });

    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, Hidden)!.Invoke(target, arguments);

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }

    private static void OnWindow(Action<Fixture> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                MainWindow.PrepareTestDispatcherForTest();
                using var fixture = new Fixture();
                check(fixture);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Isolated OCR callback test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ocr-lifetime-test-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _captures = [];
        private readonly TranslationStore _store;
        private readonly TranslationCoordinator _coordinator;
        public MainWindow Window { get; }
        public NamedPipeHost Host { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Window = new MainWindow(Path.Combine(_root, "app-data"), new RecordingExternalFactory { Available = false });
            _store = new TranslationStore(Path.Combine(_root, "translations.db"));
            _coordinator = new TranslationCoordinator(_store, new ControlledTranslator());
            Host = new NamedPipeHost("unused-ocr-lifetime-" + Guid.NewGuid().ToString("N"), _coordinator);
            // This fixture exercises capture ownership inside a real supported
            // session context; capability denial is covered separately.
            var game = new GameProfile("ocr-lifetime", _root,
                Path.Combine(_root, "unused.exe"), "2022.3", "Mono", "x64", true, "");
            SetField(Window, "_activeGame", game);
            SetField(Window, "_host", Host);
            typeof(MainWindow).GetProperty("CheatSessionId")!.SetValue(Window, "ocr-lifetime-session");
            Invoke(Window, "PublishOcrSessionCapability", game);
            Invoke(Window, "AttachOcrHandlers", Host);
        }

        public void Configure(string scenario)
        {
            var runtime = Path.Combine(_root, "availability-markers");
            Directory.CreateDirectory(Path.Combine(runtime, "tessdata"));
            // Only the availability guard reads these files. The fake recognizer below
            // replaces expensive external OCR; no executable/process is started.
            File.WriteAllBytes(Path.Combine(runtime, "tesseract.exe"), []);
            File.WriteAllBytes(Path.Combine(runtime, "tessdata", "jpn.traineddata"), []);
            File.WriteAllBytes(Path.Combine(runtime, "tessdata", "eng.traineddata"), []);
            SetField(Window, "_ocrEnabled", scenario != "disabled");
            SetField(Window, "_ocrWorker", scenario == "missing-worker" ? null :
                new OcrWorkerHost(scenario == "unavailable-worker" ? Path.Combine(_root, "missing-runtime") : runtime));
            SetField(Window, "_ocrService", scenario == "missing-service" ? null :
                new OcrTranslationService(new ControlledRecognizer(scenario), _coordinator));
        }

        public OcrFrame NewFrame()
        {
            var captureDirectory = Path.Combine(Path.GetTempPath(), "UnityTranslator");
            Directory.CreateDirectory(captureDirectory);
            var path = Path.Combine(captureDirectory, "ocr-lifetime-test-" + Guid.NewGuid().ToString("N") + ".png");
            _captures.Add(path);
            const int width = 320, height = 120;
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                new byte[width * height * 4], width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return new OcrFrame(_captures.Count, path, width, height, []);
        }

        public void Dispose()
        {
            Window.Close();
            _store.Dispose();
            // Test fixture, not App code, releases only the exact captures it created.
            foreach (var path in _captures) if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ControlledRecognizer(string scenario) : IOcrRecognizer
    {
        public Task<IReadOnlyList<OcrBox>> RecognizeAsync(string path, CancellationToken token)
        {
            if (scenario == "error") return Task.FromException<IReadOnlyList<OcrBox>>(new IOException("Controlled OCR failure"));
            if (scenario == "cancelled") return Task.FromCanceled<IReadOnlyList<OcrBox>>(token);
            return Task.FromResult<IReadOnlyList<OcrBox>>(scenario == "text"
                ? [new OcrBox(20, 30, 150, 30, "Continue", .99f)] : []);
        }
    }

    private sealed class ControlledTranslator : IOnlineTranslator
    {
        public Task<string?> TranslateAsync(string source, string language, CancellationToken token) =>
            Task.FromResult<string?>("继续");
    }

    private sealed record ReplyObservation(string? Kind, int BoxCount, bool CaptureExists);
    private sealed class CaptureObservingWriter(string imagePath) : StreamWriter(new MemoryStream(), new UTF8Encoding(false))
    {
        public List<ReplyObservation> Replies { get; } = [];
        public override Task WriteLineAsync(string? value)
        {
            using var json = JsonDocument.Parse(value!);
            Replies.Add(new ReplyObservation(json.RootElement.GetProperty("Kind").GetString(),
                json.RootElement.GetProperty("Boxes").GetArrayLength(), File.Exists(imagePath)));
            return base.WriteLineAsync(value);
        }
    }
}
