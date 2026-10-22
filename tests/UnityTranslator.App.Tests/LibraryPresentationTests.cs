using System.Windows;
using System.Windows.Controls;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class LibraryPresentationTests
{
    // A missing selection handler would leave the previous game's action target
    // and title behind while highlighting a different row.
    [Fact]
    public void SelectingLibraryRowUpdatesTitleAndActionWithoutResettingApiInput()
    {
        OnUi(window =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("GameLibraryList"));
            var first = Profile("first"); var second = Profile("second");
            window.Games.Add(first); window.Games.Add(second);
            window.EndpointBox.Text = "https://example.invalid/chat/completions";
            window.ApiKeyBox.Password = "unsaved-local-input";
            list.SelectedItem = second;
            var title = Assert.IsType<TextBlock>(window.FindName("SelectedGameTitle"));
            var action = Assert.IsType<Button>(window.FindName("SelectedGameActionButton"));
            Assert.Equal("second", title.Text);
            Assert.Equal(second, action.Tag);
            Assert.True(action.IsEnabled);
            Assert.Contains("安装", action.Content?.ToString());
            Assert.Equal("unsaved-local-input", window.ApiKeyBox.Password);
            list.SelectedItem = first;
            Assert.Equal("first", title.Text);
            Assert.Equal(first, action.Tag);
            Assert.Equal("https://example.invalid/chat/completions", window.EndpointBox.Text);
        });
    }

    [Fact]
    public void IncompatibleSelectedGameCannotInvokePrimaryAction()
    {
        OnUi(window =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("GameLibraryList"));
            var game = Profile("unsupported") with { IsCompatible = false, CompatibilityReason = "不支持的运行时" };
            window.Games.Add(game); list.SelectedItem = game;
            var action = Assert.IsType<Button>(window.FindName("SelectedGameActionButton"));
            Assert.False(action.IsEnabled);
            Assert.Contains("不支持", Assert.IsType<TextBlock>(window.FindName("SelectedComponentStatus")).Text);
        });
    }

    [Fact]
    public void SaveApiButtonPersistsFieldsBeforeWindowCloses()
    {
        OnUi(window =>
        {
            window.ApiSettingsButton.IsChecked = true;
            var save = Assert.IsType<Button>(window.FindName("SaveTranslationSettingsButton"));
            window.EndpointBox.Text = "https://example.invalid/chat/completions";
            window.ModelBox.Text = "test-local-model";
            window.ApiKeyBox.Password = "";
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var directory = (string)typeof(MainWindow).GetField("_appDataDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var settings = new AppSettings(Path.Combine(directory, "settings.json"), new DpapiSecretProtector());
            settings.Load();
            Assert.Equal("test-local-model", settings.Model);
            Assert.Equal("https://example.invalid/chat/completions", settings.Endpoint);
            Assert.Contains("待填写", Assert.IsType<TextBlock>(window.FindName("ApiProviderSummary")).Text);
        });
    }

    private static GameProfile Profile(string name) => new(name, Path.Combine(Path.GetTempPath(), "ut-no-game-" + name),
        Path.Combine(Path.GetTempPath(), name + ".exe"), "2022.3", "Mono", "x64", true, "");

    [Fact]
    public void ThemeSwitchDoesNotDescribeAnUnsubmittedKeyAsSaved()
    {
        OnUi(window =>
        {
            window.ApiKeyBox.Password = "unsubmitted-only";
            var chooser = Assert.IsType<ComboBox>(window.FindName("ThemeModeBox"));
            chooser.SelectedIndex = 1;
            Assert.Contains("待填写", Assert.IsType<TextBlock>(window.FindName("ApiProviderSummary")).Text);
        });
    }

    [Fact]
    public void FailedApiSaveDoesNotReportUnsavedSettingsAsSaved()
    {
        OnUi(window =>
        {
            var directory = (string)typeof(MainWindow).GetField("_appDataDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            Directory.CreateDirectory(Path.Combine(directory, "settings.json"));
            window.ApiKeyBox.Password = "local-test-only-key";
            window.ApiSettingsButton.IsChecked = true;
            Assert.IsType<Button>(window.FindName("SaveTranslationSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain("已保存", Assert.IsType<TextBlock>(window.FindName("ApiProviderSummary")).Text);
            Assert.Contains("保存", window.ServiceStatus.Text);
        });
    }

    [Fact]
    public void SelectedGameUpdateActionRequiresConfirmationBeforeLaunching()
    {
        OnUi(window =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ut-selected-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var exe = Path.Combine(root, "Selected.exe"); File.WriteAllBytes(exe, [1]);
                var game = new GameProfile("selected-update", root, exe, "2022.3", "Mono", "x64", true, "");
                IReadOnlyList<PackageFile> package = [new("BepInEx/plugins/UnityTranslator.Plugin.dll", [2])];
                InstallPlanner.Apply(InstallPlanner.Plan(game, package, "0.0.1"));
                window.ComponentPackageForTest = _ => package;
                window.RunningProcessesForTest = _ => [];
                window.MessageBoxHandler = _ => MessageBoxResult.None;
                var launches = 0;
                window.LaunchGameForTest = (_, _, _) => launches++;
                window.Games.Add(game);
                var list = Assert.IsType<ListBox>(window.FindName("GameLibraryList")); list.SelectedItem = game;
                var button = Assert.IsType<Button>(window.FindName("SelectedGameActionButton"));
                Assert.Contains("更新", button.Content?.ToString());
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, window.ConfirmOverlay.Visibility);
                Assert.Equal(0, launches);
                window.ConfirmCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, launches);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, launches);
                Assert.Equal(ComponentState.UpToDate, InstallPlanner.Check(root, package, ComponentNotice.CurrentVersion).State);
            }
            finally { Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void ProminentActionRefreshesAfterInstallAndUninstall()
    {
        OnUi(window =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ut-library-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var exe = Path.Combine(root, "Game.exe"); File.WriteAllBytes(exe, [1]);
                var game = new GameProfile("library-install", root, exe, "2022.3", "Mono", "x64", true, "");
                window.ComponentPackageForTest = _ => [new PackageFile("BepInEx/plugins/UnityTranslator.Plugin.dll", [2])];
                window.MessageBoxHandler = _ => MessageBoxResult.None;
                window.Games.Add(game);
                var button = Assert.IsType<Button>(window.FindName("SelectedGameActionButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("启动游戏", button.Content);
                typeof(MainWindow).GetMethod("OnSelectedGameUninstall", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, [button, new RoutedEventArgs(Button.ClickEvent)]);
                window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("安装翻译组件", button.Content);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void UnreadableBundledPackageDoesNotBreakLibrarySelection()
    {
        OnUi(window =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ut-locked-package-" + Guid.NewGuid().ToString("N"));
            var plugin = Path.Combine(root, "packages", "unity2022-mono", "BepInEx", "plugins", "UnityTranslator.Plugin.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!); File.WriteAllBytes(plugin, [1]);
            Exception? failure;
            try
            {
                using var held = File.Open(plugin, FileMode.Open, FileAccess.Read, FileShare.None);
                var game = Profile("locked-package");
                window.ComponentPackageForTest = profile => PackageCatalog.Load(root, profile);
                var messages = new List<string>();
                window.MessageBoxHandler = text => { messages.Add(text); return MessageBoxResult.None; };
                failure = Record.Exception(() =>
                {
                    window.Games.Add(game);
                    Assert.IsType<Button>(window.FindName("SelectedGameActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                if (failure is null) Assert.NotEmpty(messages);
            }
            finally
            {
                window.ComponentPackageForTest = null; window.Games.Clear();
                Directory.Delete(root, true);
            }
            Assert.Null(failure);
        });
    }

    [Fact]
    public void SmallWindowRebalancesLibraryWithoutChangingSelection()
    {
        OnUi(window =>
        {
            var game = Profile("responsive"); window.Games.Add(game);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(900, 700)); content.Arrange(new Rect(0, 0, 900, 700)); content.UpdateLayout();
            var column = Assert.IsType<ColumnDefinition>(window.FindName("GameLibraryColumn"));
            // The approved table-first design gives the library the larger pane.
            Assert.InRange(column.ActualWidth, 350, 420);
            content.Measure(new Size(1200, 800)); content.Arrange(new Rect(0, 0, 1200, 800)); content.UpdateLayout();
            Assert.InRange(column.ActualWidth, 500, 600);
            Assert.Same(game, Assert.IsType<ListBox>(window.FindName("GameLibraryList")).SelectedItem);
        });
    }

    private static void OnUi(Action<MainWindow> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ut-library-ui-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow.PrepareTestDispatcherForTest();
            var window = new MainWindow(root, new RecordingExternalFactory { Available = false });
            try { check(window); }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (failure is not null) throw failure;
    }
}
