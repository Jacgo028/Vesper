using System.Windows;
using System.Windows.Controls;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class TranslationProviderLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-provider-launch-" + Guid.NewGuid().ToString("N"));

    // Catches the old launch-time API-key gate and a missing provider cache scope.
    [Fact]
    public async Task FreeLaunchWithoutApiCredentialsUsesBingScopedCache()
    {
        Task<TranslateReply?>? pending = null;
        OnWindow(TranslationMode.Free, FreeTranslationProvider.Bing, window =>
        {
            window.EndpointBox.Text = "not an API URL";
            window.ModelBox.Text = "";
            window.ApiKeyBox.Password = "";
            using (var store = new TranslationStore(Path.Combine(_root, "appdata", "translations.db")))
            {
                store.SaveGenerated("ja", "はじめから", "旧 API 译文");
                store.SaveGenerated("ja", "はじめから", "从头开始", "free:bing");
            }
            var dispatcher = Launch(window);
            Assert.IsType<TranslationCoordinator>(dispatcher);
            pending = dispatcher.TranslateAsync(new TranslateRequest("1", "はじめから", "ja", 12, 3), default);
        });
        var reply = await Assert.IsAssignableFrom<Task<TranslateReply?>>(pending);
        Assert.Equal("从头开始", reply?.Translated);
        Assert.Equal(12, reply?.ComponentId);
        Assert.Equal(3, reply?.Generation);
    }

    // A retained unavailable choice must never silently become Bing or API.
    [Fact]
    public async Task UnavailableLegacyBaiduKeepsOriginalAndExplainsProblem()
    {
        Task<TranslateReply?>? pending = null;
        OnWindow(TranslationMode.Free, FreeTranslationProvider.Baidu, window =>
        {
            string? status = null;
            var dispatcher = Launch(window, text => status = text);
            Assert.IsNotType<TranslationCoordinator>(dispatcher);
            pending = dispatcher.TranslateAsync(new TranslateRequest("2", "はじめから", "ja", 1, 1), default);
            Assert.Contains("百度", status);
            Assert.Contains("不可用", status);
        });
        Assert.Null(await Assert.IsAssignableFrom<Task<TranslateReply?>>(pending));
    }

    [Fact]
    public void DisabledTranslationNeverCreatesBingDispatcher()
    {
        OnWindow(TranslationMode.Free, FreeTranslationProvider.Bing, window =>
        {
            window.TranslationEnabledBox.IsChecked = false;
            var dispatcher = Launch(window);
            Assert.IsNotType<TranslationCoordinator>(dispatcher);
        });
    }

    [Fact]
    public void MissingApiKeyDoesNotFallBackToFreeTranslation()
    {
        OnWindow(TranslationMode.Api, FreeTranslationProvider.Bing, window =>
        {
            window.ApiKeyBox.Password = "";
            string? status = null;
            var dispatcher = Launch(window, text => status = text);
            Assert.IsNotType<TranslationCoordinator>(dispatcher);
            Assert.Contains("密钥", status);
        });
    }

    private ITranslationDispatcher Launch(MainWindow window, Action<string>? observeStatus = null)
    {
        var gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "Game.exe");
        File.WriteAllBytes(exe, [1]);
        var game = new GameProfile("provider-launch", gameRoot, exe, "2022.3.62f2", "Mono", "x64", true, "");
        PackageFile[] package = [new("BepInEx/plugins/UnityTranslator.Plugin.dll", [2])];
        InstallPlanner.Apply(InstallPlanner.Plan(game, package, ComponentNotice.CurrentVersion));
        window.ComponentPackageForTest = _ => package;
        window.RunningProcessesForTest = _ => [];
        window.MessageBoxHandler = _ => MessageBoxResult.None;
        ITranslationDispatcher? result = null;
        window.LaunchGameForTest = (_, dispatcher, status) => { result = dispatcher; observeStatus?.Invoke(status); };
        window.OnLaunch(new Button { Tag = game }, new RoutedEventArgs());
        return Assert.IsAssignableFrom<ITranslationDispatcher>(result);
    }

    private void OnWindow(TranslationMode mode, FreeTranslationProvider free, Action<MainWindow> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var data = Path.Combine(_root, "appdata");
                var settings = new AppSettings(Path.Combine(data, "settings.json"), new DpapiSecretProtector());
                settings.Save("https://api.example.test/chat", "test-model", "", mode, free);
                var window = new MainWindow(data, new RecordingExternalFactory { Available = false });
                try { body(window); }
                finally { window.Close(); }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
