using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityTranslator.Core;
using UnityTranslator.App.Cheats;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class OcrCapabilityTests
{
    private const string Unsupported = "当前游戏版本不支持嵌入式识图翻译";

    [Theory]
    [InlineData(LoaderFamily.Auto, "unity2022-mono")]
    [InlineData(LoaderFamily.BepInEx5, "unity2022-mono-bepinex5")]
    public void Full_Mono_OCR_is_supported_for_each_resolved_loader_family(LoaderFamily loader, string package)
    {
        var game = Profile("2022.3", "Mono");
        Assert.Equal(package, PackageCatalog.ResolvePackageName(game, loader));
        Assert.True(PackageCatalog.SupportsEmbeddedOcr(game, loader));
    }

    [Theory]
    [InlineData("2022.3", "IL2CPP", true, false)]
    [InlineData("6000.0", "IL2CPP", true, false)]
    [InlineData("5.6.7", "Mono", true, false)]
    [InlineData("Unknown", "Mono", true, false)]
    [InlineData("2022.3", "Unknown", true, false)]
    [InlineData("2022.3", "Mono", false, false)]
    [InlineData("2019.4", "Mono", true, true)]
    [InlineData("2020.1", "Mono", true, true)]
    [InlineData("2021.3", "Mono", true, true)]
    [InlineData("2022.3", "Mono", true, true)]
    [InlineData("6000.0", "Mono", true, true)]
    public void Selected_package_controls_actual_OCR_switch(string version, string backend, bool compatible, bool supported) => OnUi(f =>
    {
        f.Select(Profile(version, backend, compatible));
        Assert.Equal(supported, f.Window.EnableOcrBox.IsEnabled);
        Assert.Equal(supported, f.Window.EnableOcrBox.IsChecked);
        var unavailable = compatible && (backend == "IL2CPP" || backend == "Mono")
            ? Unsupported + "；外部覆盖不可用：fixture unavailable" : Unsupported;
        Assert.Equal(supported ? "使用本地 OCR 识别游戏内的图片文字，游戏内按 F9 切换" : unavailable, f.Hint.Text);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Temporarily_unsupported_selection_restores_prior_user_preference(bool preference) => OnUi(f =>
    {
        var supported = Profile("2022.3", "Mono");
        f.Select(supported); f.Window.EnableOcrBox.IsChecked = preference;
        f.Select(Profile("5.6.7", "Mono"));
        Assert.False(f.Window.EnableOcrBox.IsEnabled); Assert.False(f.Window.EnableOcrBox.IsChecked);
        f.Window.EnableOcrBox.IsChecked = true; // Programmatic changes cannot bypass disabled capability.
        Assert.False(f.Window.EnableOcrBox.IsChecked);
        f.Select(supported);
        Assert.True(f.Window.EnableOcrBox.IsEnabled); Assert.Equal(preference, f.Window.EnableOcrBox.IsChecked);
    });

    [Fact]
    public void No_selection_does_not_advertise_an_available_OCR_mode() => OnUi(f =>
    {
        Assert.False(f.Window.EnableOcrBox.IsEnabled); Assert.False(f.Window.EnableOcrBox.IsChecked);
        Assert.Contains("选择", f.Hint.Text); Assert.Contains("游戏", f.Hint.Text); Assert.NotEqual(Unsupported, f.Hint.Text);
    });

    [Theory]
    [InlineData("2022.3", "IL2CPP", false)]
    [InlineData("5.6.7", "Mono", false)]
    [InlineData("2022.3", "Mono", true)]
    public void Launch_prepares_OCR_service_only_for_capable_package(string version, string backend, bool supported) => OnUi(f =>
    {
        var game = f.InstalledGame(version, backend); f.Select(game);
        var invoked = false;
        f.Window.LaunchGameForTest = (_, dispatcher, _) =>
        {
            invoked = true; Assert.IsType<TranslationCoordinator>(dispatcher);
            Assert.Equal(supported, Get(f.Window, "_ocrService") is OcrTranslationService);
        };
        f.Window.LaunchForTest(new Button { Tag = game }); Assert.True(invoked);
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Unsupported_reason_stays_opaque_and_wraps_in_narrow_library(string theme) => OnUi(f =>
    {
        f.Window.ThemeModeBox.SelectedItem = f.Window.ThemeModeBox.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, theme));
        f.Select(Profile("2022.3", "IL2CPP"));
        var root = (FrameworkElement)f.Window.Content; root.Measure(new Size(900, 640)); root.Arrange(new Rect(0, 0, 900, 640)); root.UpdateLayout();
        Assert.Equal(Unsupported + "；外部覆盖不可用：fixture unavailable", f.Hint.Text); Assert.Equal(TextWrapping.Wrap, f.Hint.TextWrapping);
        Assert.Equal(1, f.Window.EnableOcrBox.Opacity); Assert.True(f.Hint.ActualWidth > 20);
        Assert.True(f.Hint.DesiredSize.Width <= f.Hint.ActualWidth + 1);
    });

    [Theory]
    [InlineData("none")]
    [InlineData("no-session")]
    [InlineData("il2cpp")]
    [InlineData("legacy")]
    [InlineData("unknown")]
    [InlineData("lost")]
    public void Stale_service_cannot_process_frames_without_supported_active_session(string kind) => OnUi(f =>
    {
        var supported = Profile("2022.3", "Mono"); f.Select(supported);
        if (kind == "no-session") Set(f.Window, "_activeGame", supported);
        else if (kind != "none") f.Activate(kind switch { "il2cpp" => Profile("2022.3", "IL2CPP"), "legacy" => Profile("5.6.7", "Mono"), "unknown" => Profile("2022.3", "Unknown"), _ => supported });
        if (kind == "lost") Set(f.Window, "_processLost", true);
        var frame = f.Frame(); Assert.Empty(Pump(f.Callback(frame)));
        Assert.Equal(0, f.Recognizer.Calls); Assert.True(File.Exists(frame.ImagePath));
    });

    [Fact]
    public void Detached_host_cannot_deliver_frames_into_another_active_session() => OnUi(f =>
    {
        var game = Profile("2022.3", "Mono"); f.Select(game); f.Activate(game);
        using var oldHost = new NamedPipeHost("unused-detached-host", f.Coordinator);
        Invoke(f.Window, "AttachOcrHandlers", oldHost);
        var frame = f.Frame(); Assert.Empty(Pump(oldHost.OcrFrameReceived!(frame, CancellationToken.None)));
        Assert.Equal(0, f.Recognizer.Calls); Assert.True(File.Exists(frame.ImagePath));
    });

    [Fact]
    public void Selecting_unsupported_row_does_not_disable_supported_running_session() => OnUi(f =>
    {
        var supported = Profile("2022.3", "Mono"); f.Select(supported); f.Activate(supported);
        Assert.Empty(Pump(f.Callback(f.Frame())));
        f.Select(Profile("2022.3", "IL2CPP"));
        Assert.False(f.Window.EnableOcrBox.IsChecked); Assert.False(f.Window.EnableOcrBox.IsEnabled);
        Assert.Single(Pump(f.Callback(f.Frame())));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Background_frame_uses_established_session_capability_without_reading_mutable_library(bool supported) => OnUi(f =>
    {
        var game = Profile(supported ? "2022.3" : "5.6.7", "Mono"); f.Select(game);
        var service = Get(f.Window, "_ocrService");
        // Establish through the real UI-side session path; no process is opened
        // and the existing fixture host means EnsureHost starts no transports.
        f.Window.ConnectSpecificProcessForTest(game, 42, new CapabilityMemory());
        Set(f.Window, "_ocrService", service);
        Assert.Empty(Pump(f.Callback(f.Frame())));
        var library = Get(f.Window, "_library")!;
        var games = (List<GameProfile>)Get(library, "_games")!;
        var prior = games.ToArray();
        IReadOnlyList<OcrOverlayItem>? result = null;
        try
        {
            // Deterministically reproduce the transient null slot List.Find can
            // see during an unsynchronised UI Commit Clear/AddRange. The actual
            // library instance is used, so any background lookup fails here.
            games.Clear(); games.Add(null!);
            var frame = f.Frame();
            result = CompleteBackground(Task.Run(() => f.Callback(frame)));
            Assert.True(File.Exists(frame.ImagePath));
        }
        finally { games.Clear(); games.AddRange(prior); }
        if (supported) Assert.Single(result!); else Assert.Empty(result!);
    });

    [Theory]
    [InlineData("disable")]
    [InlineData("off-on")]
    [InlineData("end")]
    [InlineData("new-session")]
    [InlineData("unsupported-session")]
    [InlineData("service-replaced")]
    [InlineData("closing")]
    public void Awaited_result_is_discarded_after_preference_or_session_change(string change) => OnUi(f =>
    {
        var game = Profile("2022.3", "Mono"); f.Select(game); f.Activate(game);
        Assert.Empty(Pump(f.Callback(f.Frame())));
        f.Recognizer.Pending = new TaskCompletionSource<IReadOnlyList<OcrBox>>();
        var frame = f.Frame(); var request = f.Callback(frame); Assert.False(request.IsCompleted);
        if (change is "disable" or "off-on") { f.Window.EnableOcrBox.IsChecked = false; if (change == "off-on") f.Window.EnableOcrBox.IsChecked = true; }
        else if (change == "end") Invoke(f.Window, "EndGameSession");
        else if (change == "service-replaced") Set(f.Window, "_ocrService", null);
        else if (change == "closing") f.Window.MarkClosingForTest();
        else f.Activate(change == "new-session" ? game : Profile("2022.3", "IL2CPP"));
        f.Recognizer.Pending.SetResult(Recognizer.Boxes);
        Assert.Empty(Pump(request)); Assert.True(File.Exists(frame.ImagePath));
    });

    [Theory]
    [InlineData("end")]
    [InlineData("disable")]
    [InlineData("off-on")]
    public void Already_queued_status_cannot_report_old_OCR_after_state_change(string change) => OnUi(f =>
    {
        var game = Profile("2022.3", "Mono"); f.Select(game); f.Activate(game);
        Assert.Empty(Pump(f.Callback(f.Frame())));
        var reply = f.Callback(f.Frame()); Assert.True(reply.IsCompletedSuccessfully); Assert.Single(Pump(reply));
        if (change == "end") Invoke(f.Window, "EndGameSession");
        else { f.Window.EnableOcrBox.IsChecked = false; if (change == "off-on") f.Window.EnableOcrBox.IsChecked = true; }
        var status = f.Window.RunStatusTextForTest();
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        Assert.Equal(status, f.Window.RunStatusTextForTest());
    });

    [Fact]
    public void Session_reset_invalidates_frames_without_clearing_newly_prepared_service() => OnUi(f =>
    {
        var service = Get(f.Window, "_ocrService"); Invoke(f.Window, "EndGameSession");
        Assert.Same(service, Get(f.Window, "_ocrService"));
    });

    private static GameProfile Profile(string version, string backend, bool compatible = true) => new(Guid.NewGuid().ToString("N"), "C:\\synthetic-capability", "C:\\synthetic-capability\\game.exe", version, backend, "x64", compatible, "fixture");
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(object owner, string name) => owner.GetType().GetField(name, Hidden)!.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
    private static void Pump(Task task)
    {
        if (!task.IsCompleted) { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); }
        task.GetAwaiter().GetResult();
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.Result; }
    private static T CompleteBackground<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static void OnUi(Action<Fixture> body)
    {
        Exception? failure = null; var thread = new Thread(() =>
        { try { MainWindow.PrepareTestDispatcherForTest(); using var fixture = new Fixture(); body(fixture); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ocr-capability-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _frames = [];
        private readonly TranslationStore _store;
        public MainWindow Window { get; }
        public NamedPipeHost Host { get; }
        public TranslationCoordinator Coordinator { get; }
        public Recognizer Recognizer { get; } = new();
        public TextBlock Hint => Assert.IsType<TextBlock>(Window.FindName("OcrPreferenceHint"));
        public Fixture()
        {
            Window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false }); Window.RunningProcessesForTest = _ => [];
            _store = new TranslationStore(Path.Combine(_root, "translations.db"));
            var coordinator = Coordinator = new TranslationCoordinator(_store, new Translator());
            var runtime = Path.Combine(_root, "availability-only"); Directory.CreateDirectory(Path.Combine(runtime, "tessdata"));
            foreach (var name in new[] { "tesseract.exe", "tessdata/jpn.traineddata", "tessdata/eng.traineddata" }) File.WriteAllBytes(Path.Combine(runtime, name), []);
            Set(Window, "_ocrWorker", new OcrWorkerHost(runtime)); Set(Window, "_ocrService", new OcrTranslationService(Recognizer, coordinator));
            Host = new NamedPipeHost("unused-capability-" + Guid.NewGuid().ToString("N"), coordinator); Set(Window, "_host", Host); Invoke(Window, "AttachOcrHandlers", Host);
        }
        public void Select(GameProfile game) { if (!Window.Games.Contains(game)) Window.Games.Add(game); Window.GameLibraryList.SelectedItem = game; }
        public GameProfile InstalledGame(string version, string backend)
        {
            var directory = Path.Combine(_root, "installed-game"); Directory.CreateDirectory(directory);
            var exe = Path.Combine(directory, "game.exe"); File.WriteAllBytes(exe, [1]);
            var game = new GameProfile("launch-capability", directory, exe, version, backend, "x64", true, "");
            PackageFile[] package = [new("BepInEx/plugins/UnityTranslator.Plugin.dll", [2])];
            InstallPlanner.Apply(InstallPlanner.Plan(game, package, ComponentNotice.CurrentVersion));
            Window.ComponentPackageForTest = _ => package; Window.MessageBoxHandler = _ => MessageBoxResult.None;
            return game;
        }
        public void Activate(GameProfile game)
        {
            Set(Window, "_activeGame", game);
            typeof(MainWindow).GetProperty("CheatSessionId")!.SetValue(Window, Guid.NewGuid().ToString("N"));
            Invoke(Window, "PublishOcrSessionCapability", game);
        }
        public Task<IReadOnlyList<OcrOverlayItem>> Callback(OcrFrame frame) => Host.OcrFrameReceived!(frame, CancellationToken.None);
        public OcrFrame Frame()
        {
            var directory = Path.Combine(Path.GetTempPath(), "UnityTranslator"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "ocr-capability-" + Guid.NewGuid().ToString("N") + ".png"); _frames.Add(path);
            var image = BitmapSource.Create(320, 120, 96, 96, PixelFormats.Bgra32, null, new byte[320 * 120 * 4], 320 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(path)) encoder.Save(stream);
            return new OcrFrame(_frames.Count, path, 320, 120, []);
        }
        public void Dispose() { Window.Close(); _store.Dispose(); foreach (var frame in _frames) if (File.Exists(frame)) File.Delete(frame); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class Recognizer : IOcrRecognizer
    {
        public static readonly IReadOnlyList<OcrBox> Boxes = [new OcrBox(20, 30, 150, 30, "Continue", .99f)];
        public int Calls { get; private set; }
        public TaskCompletionSource<IReadOnlyList<OcrBox>>? Pending { get; set; }
        public Task<IReadOnlyList<OcrBox>> RecognizeAsync(string path, CancellationToken token) { Calls++; return Pending?.Task ?? Task.FromResult(Boxes); }
    }
    private sealed class Translator : IOnlineTranslator
    { public Task<string?> TranslateAsync(string source, string language, CancellationToken token) => Task.FromResult<string?>("继续"); }
    private sealed class CapabilityMemory : IProcessMemory
    {
        public IReadOnlyList<MemoryRegion> Regions() => [];
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
        public bool IsAlive => true;
        public int ProcessId => 42;
        public long StartedUtcTicks => 1;
    }
}
