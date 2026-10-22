using System.Windows;
using System.Windows.Controls;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class LaunchUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-launch-update", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OutdatedComponentOffersUpdateAndCancelNeverLaunches()
    {
        var result = OnUiThread(() =>
        {
            var (window, game, package) = Prepare();
            var launches = 0;
            window.ComponentPackageForTest = _ => package;
            window.LaunchGameForTest = (_, _, _) => launches++;
            window.RunningProcessesForTest = _ => [];
            window.MessageBoxHandler = _ => MessageBoxResult.None;
            try
            {
                window.LaunchForTest(new Button { Tag = game });
                var offered = window.ConfirmOverlay.Visibility == Visibility.Visible
                    && window.ConfirmOkButton.Content?.ToString() == "一键更新并启动"
                    && window.ConfirmMessage.Text.Contains("已装版本");
                window.ConfirmCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return (offered, launches, window.ConfirmOverlay.Visibility);
            }
            finally { window.Close(); }
        });

        Assert.True(result.offered);
        Assert.Equal(0, result.launches);
        Assert.Equal(Visibility.Collapsed, result.Visibility);
    }

    [Fact]
    public void ConfirmedUpdateLaunchesOnlyAfterComponentIsCurrent()
    {
        var result = OnUiThread(() =>
        {
            var (window, game, package) = Prepare();
            var launches = 0;
            var currentAtLaunch = false;
            window.ComponentPackageForTest = _ => package;
            window.LaunchGameForTest = (_, _, _) =>
            {
                launches++;
                currentAtLaunch = InstallPlanner.Check(game.RootPath, package, ComponentNotice.CurrentVersion)
                    .State == ComponentState.UpToDate;
            };
            window.RunningProcessesForTest = _ => [];
            window.MessageBoxHandler = _ => MessageBoxResult.None;
            try
            {
                window.LaunchForTest(new Button { Tag = game });
                window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return (launches, currentAtLaunch);
            }
            finally { window.Close(); }
        });

        Assert.Equal(1, result.launches);
        Assert.True(result.currentAtLaunch);
    }

    [Fact]
    public void FailedUpdateNeverLaunches()
    {
        var result = OnUiThread(() =>
        {
            var (window, game, package) = Prepare();
            var launches = 0;
            var messages = new List<string>();
            window.ComponentPackageForTest = _ => package;
            window.LaunchGameForTest = (_, _, _) => launches++;
            window.RunningProcessesForTest = _ => [];
            window.MessageBoxHandler = _ => MessageBoxResult.None;
            try
            {
                window.LaunchForTest(new Button { Tag = game });
                window.MessageBoxHandler = message => { messages.Add(message); return MessageBoxResult.None; };
                window.ComponentPackageForTest = _ => [new PackageFile("../outside.dll", [9])];
                window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return (launches, messages, window.RunStatusTextForTest());
            }
            finally { window.Close(); }
        });

        Assert.Equal(0, result.launches);
        Assert.Contains(result.messages, message => message.Contains("更新翻译组件失败")
            && message.Contains("游戏没有启动"));
        Assert.Contains("失败", result.Item3);
    }

    [Fact]
    public void MissingBundledPackageStillBlocksAnOutdatedInstall()
    {
        var result = OnUiThread(() =>
        {
            var (window, game, _) = Prepare();
            var launches = 0;
            window.ComponentPackageForTest = _ => [];
            window.LaunchGameForTest = (_, _, _) => launches++;
            window.RunningProcessesForTest = _ => [];
            window.MessageBoxHandler = _ => MessageBoxResult.None;
            try
            {
                window.LaunchForTest(new Button { Tag = game });
                return (launches, window.ConfirmOverlay.Visibility);
            }
            finally { window.Close(); }
        });

        Assert.Equal(0, result.launches);
        Assert.Equal(Visibility.Visible, result.Visibility);
    }

    private (MainWindow Window, GameProfile Game, IReadOnlyList<PackageFile> Package) Prepare()
    {
        var gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "Game.exe");
        File.WriteAllBytes(exe, [1]);
        var game = new GameProfile("test-game", gameRoot, exe, "2022.3.62f2", "Mono", "x64", true, "");
        IReadOnlyList<PackageFile> package =
        [
            new PackageFile("BepInEx/plugins/UnityTranslator.Plugin.dll", [2])
        ];
        InstallPlanner.Apply(InstallPlanner.Plan(game, package, "0.0.1"));
        return (new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false }), game, package);
    }

    private static T OnUiThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error is not null) throw new InvalidOperationException("UI test failed", error);
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
