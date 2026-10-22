using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class ExternalOcrMainWindowTests
{
    [Theory]
    [InlineData("2022.3", "IL2CPP", true)]
    [InlineData("5.6.7", "Mono", true)]
    [InlineData("2022.3", "Mono", false)]
    public void Normal_session_routes_only_external_packages(string version, string backend, bool external) => OnUi(f =>
    {
        var game = f.Game(version, backend);
        f.Activate(game);
        Assert.Equal(external ? 1 : 0, f.Factory.Creates);
        if (external)
        {
            var binding = Assert.Single(f.Controller.Starts);
            Assert.Equal(new ExternalOcrSession("normal-session", game.GameId, 42,
                backend == "IL2CPP" ? "il2cpp-bepinex6" : "legacy-mono"), binding.Session);
            Assert.True(binding.Enabled);
            Assert.NotSame(binding.Service(), binding.Service());
            Assert.Null(Get(f.Window, "_ocrService"));
        }
    });

    [Fact]
    public void Idle_window_and_unestablished_binding_do_not_create_controller() => OnUi(f =>
    {
        Assert.Equal(0, f.Factory.Creates);
        Bind(f.Window, f.Game(), f.Coordinator);
        Assert.Equal(0, f.Factory.Creates);
    });

    [Theory]
    [InlineData("disabled")]
    [InlineData("api-invalid")]
    [InlineData("test-launch")]
    public void Launch_without_real_translation_identity_never_starts_external_work(string scenario) => OnUi(f =>
    {
        var game = f.InstalledGame();
        if (scenario == "disabled") f.Window.TranslationEnabledBox.IsChecked = false;
        if (scenario == "api-invalid")
        {
            typeof(MainWindow).GetMethod("SelectTranslationMode", Hidden)!.Invoke(f.Window, [TranslationMode.Api]);
            f.Window.ApiKeyBox.Password = "";
        }
        ITranslationDispatcher? launched = null;
        f.Window.LaunchGameForTest = (_, dispatcher, _) => launched = dispatcher;
        f.Window.LaunchForTest(new Button { Tag = game });
        Assert.NotNull(launched);
        Assert.Equal(scenario == "test-launch", launched is TranslationCoordinator);
        Assert.Null(f.Window.CheatSessionId);
        Assert.Equal(0, f.Factory.Creates);
    });

    [Fact]
    public void Process_loss_then_takeover_reuses_controller_with_new_pid_service_and_epoch() => OnUi(f =>
    {
        var game = f.Game(); f.Activate(game);
        var first = Assert.Single(f.Controller.Starts); var epoch = f.Controller.Epoch;
        f.Window.NotifyGameExitedForTest();
        Assert.Contains(ExternalOcrStopReason.ProcessLost, f.Controller.Invalidations);
        Assert.Same(f.Coordinator, Get(f.Window, "_activeTranslationCoordinator"));
        f.Window.RunningProcessesForTest = _ => [new GameProcessInfo(84, game.ExePath, null)];
        Assert.True(f.Window.TakeOverImmediatelyForTest());
        Assert.Equal(1, f.Factory.Creates); Assert.Equal(2, f.Controller.Starts.Count);
        var next = f.Controller.Starts[1];
        Assert.Equal("normal-session", next.Session.SessionId); Assert.Equal(84, next.Session.ProcessId);
        Assert.NotSame(first.Service(), next.Service()); Assert.True(f.Controller.Epoch > epoch);
    });

    [Fact]
    public void Numeric_attach_clears_translation_eligibility_even_after_normal_session() => OnUi(f =>
    {
        var game = f.Game(); f.Activate(game);
        Set(f.Window, "_host", new NamedPipeHost("unused-external-fixture", f.Coordinator));
        f.Window.ConnectSpecificProcessForTest(game, 84, null!);
        Assert.Null(Get(f.Window, "_activeTranslationCoordinator"));
        Assert.Contains(ExternalOcrStopReason.SessionEnded, f.Controller.Invalidations);
        f.Window.NotifyGameExitedForTest();
        f.Window.RunningProcessesForTest = _ => [new GameProcessInfo(126, game.ExePath, null)];
        Assert.True(f.Window.TakeOverImmediatelyForTest());
        Assert.Single(f.Controller.Starts);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void External_availability_controls_explanation_and_preserves_wrapping(bool available) => OnUi(f =>
    {
        f.Factory.Available = available; f.Select(f.Game());
        var hint = (TextBlock)f.Window.FindName("OcrPreferenceHint");
        Assert.Equal(available, f.Window.EnableOcrBox.IsEnabled);
        Assert.Equal(available, f.Window.EnableOcrBox.IsChecked);
        Assert.Contains(available ? "将使用外部覆盖" : "fixture unavailable", hint.Text);
        Assert.Contains("不支持嵌入式", hint.Text); Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
        Assert.Equal(hint.Text, System.Windows.Automation.AutomationProperties.GetHelpText(f.Window.EnableOcrBox));
        Assert.Equal(0, f.Factory.Creates);
    });

    [Fact]
    public void Changing_library_selection_keeps_running_target_and_f9_updates_retained_preference() => OnUi(f =>
    {
        var game = f.Game(); f.Activate(game); f.Select(f.Game("2022.3", "Unknown"));
        f.Controller.EmitEnabled(false); Drain();
        Assert.False((bool)Get(f.Window, "_ocrEnabled")!);
        Assert.Single(f.Controller.Starts); Assert.Equal(game.GameId, f.Controller.Starts[0].Session.GameId);
        f.Select(game); Assert.False(f.Window.EnableOcrBox.IsChecked);
        var epoch = f.Controller.Epoch;
        f.Window.EnableOcrBox.IsChecked = true;
        Assert.True(f.Controller.Epoch > epoch); Assert.True(f.Controller.Enabled);
        f.Controller.EmitStatus("current external status"); Drain();
        Assert.Equal("current external status", f.Window.RunStatusTextForTest());
    });

    [Theory]
    [InlineData("epoch")]
    [InlineData("pid")]
    [InlineData("session")]
    [InlineData("controller")]
    [InlineData("end")]
    [InlineData("lost")]
    public void Queued_status_and_f9_recheck_identity_at_UI_execution(string change) => OnUi(f =>
    {
        var game = f.Game(); f.Activate(game);
        Assert.Single(f.Controller.Starts);
        f.Controller.EmitStatus("stale external status"); f.Controller.EmitEnabled(false);
        if (change == "epoch") f.Controller.Invalidate(ExternalOcrStopReason.TargetChanged);
        if (change == "pid") Property(f.Window, "CheatProcessId", 84);
        if (change == "session") Property(f.Window, "CheatSessionId", "replacement");
        if (change == "controller") Set(f.Window, "_externalOcrController", new RecordingExternalController());
        if (change == "end") Invoke(f.Window, "EndGameSession");
        if (change == "lost") f.Window.NotifyGameExitedForTest();
        var status = f.Window.RunStatusTextForTest(); Drain();
        Assert.Equal(status, f.Window.RunStatusTextForTest());
        Assert.True((bool)Get(f.Window, "_ocrEnabled")!);
    });

    [Fact]
    public void Optional_factory_failure_does_not_break_normal_session() => OnUi(f =>
    {
        f.Factory.FailCreate = true; var game = f.Game(); f.Activate(game);
        Assert.Equal("normal-session", f.Window.CheatSessionId);
        Assert.Equal(game, f.Window.ActiveGameForTest());
        Assert.Contains("fixture create failure", f.Window.RunStatusTextForTest());
        Assert.Null(f.Store.Find("en", "missing"));
    });

    [Fact]
    public void Rebinding_without_external_capability_stops_previous_loop() => OnUi(f =>
    {
        var game = f.Game(); f.Activate(game); f.Factory.Available = false;
        Bind(f.Window, game, f.Coordinator);
        Assert.Equal(ExternalOcrStopReason.SessionEnded, Assert.Single(f.Controller.Invalidations));
        Assert.Single(f.Controller.Starts);
    });

    [Theory]
    [InlineData("IL2CPP", false)]
    [InlineData("Mono", true)]
    public void Missing_optional_API_keeps_existing_translation_and_reports_unavailability(string backend, bool embedded) => OnUi(f =>
    {
        f.Factory.FailAvailability = true;
        f.Activate(f.Game("2022.3", backend));
        Assert.Equal(embedded, f.Window.EnableOcrBox.IsEnabled);
        Assert.Equal(embedded, Get(f.Window, "_ocrService") is OcrTranslationService);
        Assert.Equal(0, f.Factory.Creates); Assert.Same(f.Coordinator, Get(f.Window, "_activeTranslationCoordinator"));
        if (!embedded) Assert.Contains("fixture API unavailable", ((TextBlock)f.Window.FindName("OcrPreferenceHint")).Text);
        Assert.Null(f.Store.Find("en", "missing"));
    });

    [Fact]
    public void UI_off_on_revokes_queued_status_and_f9_without_changing_running_target() => OnUi(f =>
    {
        var game = f.Game(); f.Activate(game);
        f.Controller.EmitStatus("obsolete"); f.Controller.EmitEnabled(false);
        var before = f.Controller.Epoch;
        f.Window.EnableOcrBox.IsChecked = false; f.Window.EnableOcrBox.IsChecked = true;
        var status = f.Window.RunStatusTextForTest(); Drain();
        Assert.Equal(status, f.Window.RunStatusTextForTest()); Assert.True(f.Window.EnableOcrBox.IsChecked);
        Assert.True(f.Controller.Enabled); Assert.Equal(before + 2, f.Controller.Epoch);
        Assert.Single(f.Controller.Starts); Assert.Equal(game.GameId, f.Controller.Starts[0].Session.GameId);
    });

    [Fact]
    public void No_controller_close_is_synchronous() => OnUi(f =>
    {
        f.Window.Close(); Assert.Equal(1, f.Closed); f.AssertResourcesDisposed();
        Assert.Equal(0, f.Factory.Creates);
    });

    [Fact]
    public void Completed_drain_posts_second_close_instead_of_reentering_first_close() => OnUi(f =>
    {
        f.Activate(f.Game());
        f.Window.Close(); Assert.Equal(0, f.Closed); Assert.Equal(1, f.Controller.Disposals);
        f.AssertResourcesAlive(); Drain();
        Assert.Equal(1, f.Closed); f.AssertResourcesDisposed();
    });

    [Fact]
    public void Blocked_drain_keeps_store_and_HTTP_alive_until_completion() => OnUi(f =>
    {
        f.Activate(f.Game()); f.Controller.Drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Window.Close(); f.Window.Close(); Drain();
        Assert.Equal(0, f.Closed); Assert.False(f.Window.IsVisible); f.AssertResourcesAlive();
        Assert.True((bool)Get(f.Window, "_closing")!); Assert.Equal(1, f.Controller.Disposals);
        f.Controller.Drain.SetResult(); Until(() => f.Closed == 1);
        f.AssertResourcesDisposed(); Assert.Equal(1, f.Controller.Disposals);
    });

    [Fact]
    public void Faulted_drain_is_observed_after_wait_and_still_finishes_close_once() => OnUi(f =>
    {
        f.Activate(f.Game()); f.Controller.Drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Window.Close(); Drain(); Assert.Equal(0, f.Closed); f.AssertResourcesAlive();
        f.Controller.Drain.SetException(new AggregateException(new IOException("fixture cleanup failure")));
        Until(() => f.Closed == 1); f.AssertResourcesDisposed();
        Assert.Equal(1, f.Controller.Disposals);
        Assert.Contains("fixture cleanup failure", File.ReadAllText(Path.Combine(f.Root, "app.log")));
    });

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(object o, string name) => o.GetType().GetField(name, Hidden)?.GetValue(o);
    private static void Set(object o, string name, object? value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
    private static void Property(object o, string name, object? value) => o.GetType().GetProperty(name)!.SetValue(o, value);
    private static void Invoke(object o, string name) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, null);
    private static void Bind(MainWindow window, GameProfile game, TranslationCoordinator coordinator) =>
        window.BindTranslationOcrSession(game, coordinator);
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> done)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < limit) { Drain(); Thread.Sleep(1); }
        Assert.True(done(), "Dispatcher operation did not complete");
    }
    private static void OnUi(Action<Fixture> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { MainWindow.PrepareTestDispatcherForTest(); using var fixture = new Fixture(); body(fixture); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "external-window-" + Guid.NewGuid().ToString("N"));
        public RecordingExternalFactory Factory { get; } = new();
        public RecordingExternalController Controller => Factory.Controller;
        public MainWindow Window { get; }
        public TranslationStore Store => (TranslationStore)Get(Window, "_translations")!;
        public TranslationCoordinator Coordinator { get; }
        public int Closed { get; private set; }
        public Fixture()
        {
            Window = new MainWindow(Root, Factory);
            Window.Closed += (_, _) => Closed++;
            Window.RunningProcessesForTest = _ => [];
            Coordinator = new(Store, new LocalTranslator());
        }
        public GameProfile Game(string version = "2022.3", string backend = "IL2CPP") =>
            new(Guid.NewGuid().ToString("N"), Root, Path.Combine(Root, "game.exe"), version, backend, "x64", true, "");
        public void Select(GameProfile game) { Window.Games.Add(game); Window.GameLibraryList.SelectedItem = game; }
        public void Activate(GameProfile game)
        {
            Select(game); Set(Window, "_activeGame", game); Property(Window, "CheatSessionId", "normal-session");
            Property(Window, "CheatProcessId", 42); Bind(Window, game, Coordinator);
        }
        public GameProfile InstalledGame()
        {
            var game = Game(); File.WriteAllBytes(game.ExePath, [1]);
            PackageFile[] package = [new("BepInEx/plugins/UnityTranslator.Plugin.dll", [2])];
            InstallPlanner.Apply(InstallPlanner.Plan(game, package, ComponentNotice.CurrentVersion));
            Window.ComponentPackageForTest = _ => package; Window.MessageBoxHandler = _ => MessageBoxResult.None;
            Select(game); return game;
        }
        public void AssertResourcesAlive()
        {
            Assert.Null(Store.Find("en", "missing"));
            ((HttpClient)Get(Window, "_httpClient")!).Timeout = TimeSpan.FromSeconds(24);
        }
        public void AssertResourcesDisposed()
        {
            Assert.ThrowsAny<Exception>(() => Store.Find("en", "missing"));
            Assert.Throws<ObjectDisposedException>(() => ((HttpClient)Get(Window, "_httpClient")!).Timeout = TimeSpan.FromSeconds(24));
        }
        public void Dispose()
        {
            Controller.Drain?.TrySetResult();
            if (Closed == 0) { Window.Close(); Until(() => Closed > 0); }
            Directory.Delete(Root, true);
        }
    }
    private sealed class LocalTranslator : IOnlineTranslator
    { public Task<string?> TranslateAsync(string source, string language, CancellationToken ct) => Task.FromResult<string?>("本地测试"); }
}

internal sealed class RecordingExternalFactory : IExternalOcrControllerFactory
{
    public bool Available { get; set; } = true;
    public bool FailCreate { get; set; }
    public bool FailAvailability { get; set; }
    public int Creates { get; private set; }
    public RecordingExternalController Controller { get; } = new();
    public ExternalOcrAvailability GetAvailability() => FailAvailability
        ? throw new PlatformNotSupportedException("fixture API unavailable")
        : new(Available, Available ? "" : "fixture unavailable");
    public IExternalOcrController Create()
    { Creates++; if (FailCreate) throw new InvalidOperationException("fixture create failure"); return Controller; }
}

internal sealed class RecordingExternalController : IExternalOcrController
{
    public List<(ExternalOcrSession Session, Func<OcrTranslationService> Service, bool Enabled)> Starts { get; } = [];
    public List<ExternalOcrStopReason> Invalidations { get; } = [];
    public long Epoch { get; private set; }
    public bool Enabled { get; private set; }
    public int Disposals { get; private set; }
    public TaskCompletionSource? Drain { get; set; }
    public event Action<ExternalOcrStatus>? StatusChanged;
    public event Action<ExternalOcrEnabledChange>? EnabledChanged;
    public void Start(ExternalOcrSession session, Func<OcrTranslationService> createService, bool enabled)
    { Starts.Add((session, createService, enabled)); Enabled = enabled; Epoch++; }
    public void SetEnabled(bool enabled) { Enabled = enabled; Epoch++; }
    public void Invalidate(ExternalOcrStopReason reason) { Invalidations.Add(reason); Epoch++; }
    public ValueTask DisposeAsync() { Disposals++; Epoch++; return new(Drain?.Task ?? Task.CompletedTask); }
    public void EmitStatus(string message)
    { var session = Starts.LastOrDefault().Session; if (session is not null) StatusChanged?.Invoke(new(session.SessionId, session.ProcessId, Epoch, message)); }
    public void EmitEnabled(bool enabled)
    { Enabled = enabled; var session = Starts.LastOrDefault().Session; if (session is not null) EnabledChanged?.Invoke(new(session.SessionId, session.ProcessId, Epoch, enabled)); }
}
