using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class LibraryWorkspaceTests
{
    // Catches the old failure branch stopping its timer forever, not just its initial color.
    [Fact]
    public void InvalidImportReturnsToDefaultHintWithoutAnotherImport() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Launcher", complete: false));
        Assert.Equal(1, window.DropResultPanel.Opacity);
        PumpUntil(() => window.DropResultPanel.Opacity == 0, TimeSpan.FromSeconds(9));
        Assert.Equal(1, window.DropHintPanel.Opacity);
        Assert.Same(window.FindResource("BgWindow"), window.DropZone.Background);
    });

    [Fact]
    public void ErrorCanBeDismissedAndNextDragClearsOldError() => OnUi((window, root) =>
    {
        var launcher = CreateGame(root, "Launcher", complete: false);
        window.AddGame(launcher);
        Named<Button>(window, "DismissDropFeedbackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, window.DropResultPanel.Opacity);
        window.AddGame(launcher);
        var target = Named<FrameworkElement>(window, "SelectedGameTitle");
        target.RaiseEvent(Drag(DragDrop.PreviewDragEnterEvent, new DataObject(DataFormats.FileDrop, new[] { launcher })));
        Assert.Equal(0, window.DropResultPanel.Opacity);
    });

    [Fact]
    public void ClosingWindowStopsPendingFeedbackTimer() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Good"));
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("_dropFeedbackTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Assert.True(timer.IsEnabled);
        window.Close();
        Assert.False(timer.IsEnabled);
    });

    // Catches registration limited to DropZone, and duplicate child + parent handlers.
    [Theory]
    [InlineData("GameLibraryPage")]
    [InlineData("GameLibraryList")]
    [InlineData("SelectedGameActionButton")]
    [InlineData("SelectedGameTitle")]
    [InlineData("GameScrollViewer")]
    public void WholeGamePageAcceptsEachDropExactlyOnce(string targetName) => OnUi((window, root) =>
    {
        Layout(window);
        var target = Named<UIElement>(window, targetName);
        Assert.True(target.AllowDrop);
        var args = Drag(DragDrop.PreviewDropEvent, new DataObject(DataFormats.FileDrop, new[] { CreateGame(root, "Good") }));
        target.RaiseEvent(args);
        Assert.True(args.Handled);
        Assert.Single(window.Games);
        Assert.DoesNotContain("已更新", window.DropResultTitle.Text);
    });

    [Fact]
    public void DroppingOnSearchBoxImportsRatherThanReplacingSearchWithAPath() => OnUi((window, root) =>
    {
        var search = Named<TextBox>(window, "LibrarySearchBox");
        search.Text = "old query";
        Layout(window);
        search.RaiseEvent(Drag(DragDrop.PreviewDropEvent, new DataObject(DataFormats.FileDrop, new[] { CreateGame(root, "Good") })));
        Assert.Single(window.Games);
        Assert.Equal("", search.Text);
        Assert.NotNull(window.GameLibraryList.SelectedItem);
    });

    [Fact]
    public void NonFileDropDoesNotChangeTheLibrary() => OnUi((window, _) =>
    {
        Layout(window);
        var target = Named<UIElement>(window, "GameLibraryPage");
        var args = Drag(DragDrop.PreviewDropEvent, new DataObject(DataFormats.UnicodeText, "not a game"));
        target.RaiseEvent(args);
        Assert.Empty(window.Games);
        Assert.Equal(0, window.DropResultPanel.Opacity);
    });

    [Fact]
    public void SearchFiltersNamesCaseInsensitivelyWithoutDeletingRecords() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Alpha")); window.AddGame(CreateGame(root, "Beta"));
        Named<TextBox>(window, "LibrarySearchBox").Text = "ALP";
        Assert.Equal("Alpha", Assert.IsType<GameProfile>(Assert.Single(window.GameLibraryList.Items.Cast<object>())).DisplayName);
        Assert.Equal("Alpha", window.SelectedGameTitle.Text);
        Assert.Equal(2, window.Games.Count);
        Named<TextBox>(window, "LibrarySearchBox").Text = "no match";
        Assert.Empty(window.GameLibraryList.Items);
        Assert.False(window.SelectedGameActionButton.IsEnabled);
        Assert.Equal(Visibility.Visible, window.GameListEmptyHint.Visibility);
        Named<TextBox>(window, "LibrarySearchBox").Clear();
        Assert.Equal(2, window.GameLibraryList.Items.Count);
    });

    [Fact]
    public void RenameUsesVisibleEditorAndSurvivesReopen() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Alpha"));
        Named<Button>(window, "RenameLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "LibraryRenamePanel").Visibility);
        Named<TextBox>(window, "LibraryRenameBox").Text = "暮星冒险";
        Named<Button>(window, "SaveLibraryNameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("暮星冒险", window.SelectedGameTitle.Text);
        var saved = Assert.Single(new GameLibrary(Path.Combine(root, "appdata", "games.json")).List());
        Assert.Equal("暮星冒险", saved.DisplayName);
    });

    [Fact]
    public void RemoveRequiresConfirmationAndOnlyRemovesTheRecord() => OnUi((window, root) =>
    {
        var exe = CreateGame(root, "Alpha"); window.AddGame(exe);
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Visible, window.ConfirmOverlay.Visibility);
        window.ConfirmCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(window.Games);
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(window.Games);
        Assert.Empty(new GameLibrary(Path.Combine(root, "appdata", "games.json")).List());
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Alpha_Data", "Managed", "Assembly-CSharp.dll")));
        Assert.False(window.SelectedGameActionButton.IsEnabled);
    });

    [Fact]
    public void ActiveSessionCannotBeRemovedEvenIfItStartsAfterConfirmation() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Alpha"));
        var game = Assert.Single(window.Games);
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        typeof(MainWindow).GetField("_activeGame", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, game);
        window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(window.Games);
        Assert.Single(new GameLibrary(Path.Combine(root, "appdata", "games.json")).List());
    });

    [Fact]
    public void FailedRemovalSaveLeavesTheVisibleLibraryIntact() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Alpha"));
        using var held = File.Open(Path.Combine(root, "appdata", "games.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(window.Games);
        Assert.Contains("失败", window.DropResultTitle.Text);
    });

    [Fact]
    public void PendingLoaderFallbackCannotLoseItsLibraryRecord() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Alpha"));
        var game = Assert.Single(window.Games);
        var pending = (HashSet<string>)typeof(MainWindow).GetField("_pendingLoaderFallback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        pending.Add(game.GameId);
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(window.Games);
        Assert.Single(new GameLibrary(Path.Combine(root, "appdata", "games.json")).List());
    });

    [Fact]
    public void RenamingFilteredGameRefreshesSearchWithoutLosingTheRecord() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Alpha"));
        Named<TextBox>(window, "LibrarySearchBox").Text = "Alpha";
        Named<Button>(window, "RenameLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Named<TextBox>(window, "LibraryRenameBox").Text = "Beta";
        Named<Button>(window, "SaveLibraryNameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(window.GameLibraryList.Items);
        Assert.False(window.SelectedGameActionButton.IsEnabled);
        Named<TextBox>(window, "LibrarySearchBox").Clear();
        Assert.Equal("Beta", Assert.IsType<GameProfile>(Assert.Single(window.GameLibraryList.Items.Cast<object>())).DisplayName);
    });

    [Fact]
    public void MissingFolderReportsRecoverableErrorRatherThanOpeningSomethingElse() => OnUi((window, root) =>
    {
        var exe = CreateGame(root, "Alpha"); window.AddGame(exe);
        Directory.Move(Path.GetDirectoryName(exe)!, Path.Combine(root, "Moved"));
        Named<Button>(window, "OpenLibraryFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("找不到", window.DropResultTitle.Text);
        Named<Button>(window, "DismissDropFeedbackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, window.DropResultPanel.Opacity);
    });

    [Fact]
    public void HiddenFeedbackDoesNotReserveSpaceOrExposeAnInvisibleKeyboardTarget() => OnUi((window, root) =>
    {
        Assert.Equal(Visibility.Collapsed, window.DropResultPanel.Visibility);
        window.AddGame(CreateGame(root, "Launcher", complete: false));
        Assert.Equal(Visibility.Visible, window.DropResultPanel.Visibility);
        Named<Button>(window, "DismissDropFeedbackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, window.DropResultPanel.Visibility);
    });

    [Fact]
    public void RemovingFilteredSelectionSelectsAnotherVisibleResult() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Other"));
        window.AddGame(CreateGame(root, "Adventure One"));
        window.AddGame(CreateGame(root, "Adventure Two"));
        Named<TextBox>(window, "LibrarySearchBox").Text = "Adventure";
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ConfirmOkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(window.GameLibraryList.Items);
        Assert.NotNull(window.GameLibraryList.SelectedItem);
        Assert.True(window.SelectedGameActionButton.IsEnabled);
    });

    [Fact]
    public void UnreadableRunningProcessStateBlocksRemovalRatherThanFailingOpen() => OnUi((window, root) =>
    {
        window.AddGame(CreateGame(root, "Alpha"));
        window.RunningProcessesForTest = _ => throw new System.ComponentModel.Win32Exception(5);
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(window.Games);
        Assert.Equal(Visibility.Collapsed, window.ConfirmOverlay.Visibility);
        Assert.Contains("确认", window.DropResultTitle.Text);
    });

    [Fact]
    public void ExternallyRunningExecutableCannotBeRemoved() => OnUi((window, _) =>
    {
        window.AddGame(Environment.ProcessPath!);
        Named<Button>(window, "RemoveLibraryGameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(window.Games);
        Assert.Equal(Visibility.Collapsed, window.ConfirmOverlay.Visibility);
    });

    private static T Named<T>(MainWindow window, string name) where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));

    internal static DragEventArgs Drag(RoutedEvent routedEvent, IDataObject data)
    {
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 5);
        return (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, new Grid(), new Point(4, 4)]) is { } args
            ? WithEvent(args, routedEvent) : throw new InvalidOperationException();
    }

    private static DragEventArgs WithEvent(DragEventArgs args, RoutedEvent routedEvent) { args.RoutedEvent = routedEvent; return args; }
    private static void Layout(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1200, 800)); content.Arrange(new Rect(0, 0, 1200, 800)); content.UpdateLayout();
    }
    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var frame = new DispatcherFrame(); var expired = false;
        var check = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) => { if (condition()) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        var deadline = new DispatcherTimer(timeout, DispatcherPriority.Send, (_, _) => { expired = true; frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { check.Stop(); deadline.Stop(); }
        Assert.False(expired, "反馈提示没有在期限内恢复默认状态");
    }
    private static string CreateGame(string root, string name, bool complete = true)
    {
        var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, name + ".exe");
        var bytes = new byte[256]; bytes[0] = 0x4d; bytes[1] = 0x5a; BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = 0x50; bytes[0x81] = 0x45; BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84); File.WriteAllBytes(exe, bytes);
        if (complete)
        {
            Directory.CreateDirectory(Path.Combine(directory, name + "_Data", "Managed"));
            File.WriteAllBytes(Path.Combine(directory, "UnityPlayer.dll"), [1]);
            File.WriteAllBytes(Path.Combine(directory, name + "_Data", "Managed", "Assembly-CSharp.dll"), [1]);
        }
        return exe;
    }
    private static void OnUi(Action<MainWindow, string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "vesper-library-workspace-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow.PrepareTestDispatcherForTest();
            var window = new MainWindow(Path.Combine(root, "appdata"), new RecordingExternalFactory { Available = false }); window.MessageBoxHandler = _ => MessageBoxResult.None;
            try { body(window, root); }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Library UI thread timed out");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
