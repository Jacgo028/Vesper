using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public partial class MainWindow
{
    private bool _libraryPresentationReady;
    private bool _suppressLibrarySelection;
    private string? _libraryCheckStamp;
    private ComponentCheck? _libraryCheck;
    private bool _apiSaveFailed;
    private ICollectionView? _libraryView;
    private string? _renamingGameId;

    private GameProfile? SelectedLibraryGame => GameLibraryList.SelectedItem as GameProfile;

    private void InitializeLibraryPresentation()
    {
        // Tab content receives inherited DataContext only when realized. Bind
        // this persistent library explicitly so initial selection is not tied
        // to showing a window or opening a particular page first.
        _libraryView = CollectionViewSource.GetDefaultView(Games);
        _libraryView.Filter = item => item is GameProfile game &&
            game.DisplayName.Contains(LibrarySearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        GameLibraryList.ItemsSource = _libraryView;
        _libraryPresentationReady = true;
        Games.CollectionChanged += OnLibraryGamesChanged;
        if (GameLibraryList.SelectedIndex < 0 && Games.Count > 0) GameLibraryList.SelectedIndex = 0;
        UpdateLibraryPresentation();
        UpdateApiProviderSummary();
    }

    private void OnLibraryGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_libraryPresentationReady || _suppressLibrarySelection) return;
        if (SelectedLibraryGame is null && GameLibraryList.Items.Count > 0) GameLibraryList.SelectedIndex = 0;
        UpdateLibraryPresentation();
    }

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_libraryPresentationReady || _suppressLibrarySelection) return;
        if (_renamingGameId != SelectedLibraryGame?.GameId) CancelLibraryRename();
        UpdateLibraryPresentation();
    }

    private void OnLibrarySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_libraryPresentationReady) return;
        _libraryView?.Refresh();
        if (SelectedLibraryGame is null && GameLibraryList.Items.Count > 0) GameLibraryList.SelectedIndex = 0;
        UpdateLibraryPresentation();
    }

    private void UpdateLibraryPresentation()
    {
        if (!_libraryPresentationReady) return;
        var game = SelectedLibraryGame;
        UpdateOcrPreferencePresentation(game);
        GameListEmptyHint.Visibility = GameLibraryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GameListEmptyHint.Text = Games.Count == 0 ? "添加一款游戏，开始翻译。" : "没有匹配的游戏，清空搜索可显示全部。";
        RenameLibraryGameButton.IsEnabled = game is not null;
        OpenLibraryFolderButton.IsEnabled = game is not null;
        RemoveLibraryGameButton.IsEnabled = game is not null && _activeGame?.GameId != game.GameId;
        SelectedGameTitle.Text = game?.DisplayName ?? "选择一款游戏，开始翻译";
        SelectedGameInfo.Text = game is null ? "从左侧添加或选择你的本地游戏" : $"Unity {game.UnityVersion} · {game.Backend} · {game.Architecture}";
        SelectedGameTechnical.Text = game is null ? "" : game.TechnicalSummary + "\n" + game.ExePath;
        SelectedGameActionButton.Tag = game;
        SelectedGameActionButton.IsEnabled = game?.IsCompatible == true;
        if (game is null)
        {
            SelectedComponentStatus.Text = "还没有选择游戏";
            SelectedGameActionButton.Content = "启动游戏";
            return;
        }
        if (!game.IsCompatible)
        {
            SelectedComponentStatus.Text = game.CompatibilityReason;
            SelectedComponentStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            SelectedGameActionButton.Content = "暂不支持";
            return;
        }
        // Reuse the existing component checker, but do not re-read the complete
        // loader package on every scan-result/status refresh.
        var stamp = game.GameId + "|" + game.PreferredLoader;
        foreach (var relative in new[] { InstallPlanner.ManifestName, "BepInEx/plugins/UnityTranslator.Plugin.dll", "BepInEx/plugins/UnityTranslator.Plugin.Il2Cpp.dll" })
        {
            try
            {
                var file = new FileInfo(Path.Combine(game.RootPath, relative));
                stamp += file.Exists ? $"|{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "|-";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stamp += "|unreadable"; }
        }
        if (stamp != _libraryCheckStamp)
        {
            _libraryCheck = CheckComponent(game);
            _libraryCheckStamp = stamp;
        }
        var installed = File.Exists(Path.Combine(game.RootPath, InstallPlanner.ManifestName));
        var state = installed ? (_libraryCheck?.State ?? ComponentState.NeedsUpdate) : ComponentState.NotInstalled;
        SelectedGameActionButton.Content = state switch
        {
            ComponentState.NotInstalled => "安装翻译组件",
            ComponentState.NeedsUpdate => "一键更新并启动",
            _ => "启动游戏"
        };
        SelectedComponentStatus.Text = state switch
        {
            ComponentState.NotInstalled => "首次使用，需要安装翻译组件",
            ComponentState.NeedsUpdate => "组件需要更新，确认后再启动",
            _ => "组件已就绪"
        };
        SelectedComponentStatus.SetResourceReference(TextBlock.ForegroundProperty, state == ComponentState.UpToDate ? "Success" : "Warning");
    }

    private void OnSelectedGameAction(object sender, RoutedEventArgs e)
    {
        if (SelectedLibraryGame is not { IsCompatible: true } game) return;
        if (!File.Exists(Path.Combine(game.RootPath, InstallPlanner.ManifestName))) OnInstall(new Button { Tag = game }, e);
        else OnLaunch(new Button { Tag = game }, e);
    }

    private void OnSelectedGameInstall(object sender, RoutedEventArgs e)
    {
        if (SelectedLibraryGame is { IsCompatible: true } game) OnInstall(new Button { Tag = game }, e);
    }

    private void OnSelectedGameUninstall(object sender, RoutedEventArgs e)
    {
        if (SelectedLibraryGame is { } game) OnUninstall(new Button { Tag = game }, e);
    }

    private void OnLibraryRowMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameProfile game, ContextMenu: { } menu } button) return;
        GameLibraryList.SelectedItem = game;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private bool SelectLibraryMenuGame(object sender)
    {
        if (sender is not MenuItem item) return false;
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu ?? item.Parent as ContextMenu;
        if (menu?.PlacementTarget is not FrameworkElement { Tag: GameProfile game } || !Games.Contains(game)) return false;
        GameLibraryList.SelectedItem = game;
        return true;
    }

    private void OnLibraryRowRename(object sender, RoutedEventArgs e)
    {
        if (SelectLibraryMenuGame(sender)) OnRenameLibraryGame(sender, e);
    }

    private void OnLibraryRowOpenFolder(object sender, RoutedEventArgs e)
    {
        if (SelectLibraryMenuGame(sender)) OnOpenLibraryFolder(sender, e);
    }

    private void OnLibraryRowRemove(object sender, RoutedEventArgs e)
    {
        if (SelectLibraryMenuGame(sender)) OnRemoveLibraryGame(sender, e);
    }

    private void OnRenameLibraryGame(object sender, RoutedEventArgs e)
    {
        if (SelectedLibraryGame is not { } game) return;
        _renamingGameId = game.GameId;
        LibraryRenameBox.Text = game.DisplayName;
        LibraryRenamePanel.Visibility = Visibility.Visible;
        LibraryRenameBox.Focus();
        LibraryRenameBox.SelectAll();
    }

    private void CancelLibraryRename()
    {
        _renamingGameId = null;
        LibraryRenamePanel.Visibility = Visibility.Collapsed;
    }

    private void OnCancelLibraryName(object sender, RoutedEventArgs e) => CancelLibraryRename();

    private void OnSaveLibraryName(object sender, RoutedEventArgs e)
    {
        if (_renamingGameId is not { } id || SelectedLibraryGame?.GameId != id) return;
        try
        {
            _library.Rename(id, LibraryRenameBox.Text);
            CancelLibraryRename();
            RefreshGameList();
            if (_activeGame?.GameId == id)
            {
                _activeGame = _library.List().FirstOrDefault(game => game.GameId == id);
                RefreshWorkspaceState();
            }
            ShowDropSuccess("名称已保存", _library.List().FirstOrDefault(game => game.GameId == id)?.DisplayName ?? "", "只修改列表名称，不改动游戏文件。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowLibraryManagementFailure("保存名称失败", ex);
        }
    }

    private void OnOpenLibraryFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedLibraryGame is not { } game) return;
        try
        {
            if (!Directory.Exists(game.RootPath))
            {
                ShowDropFailure("找不到游戏文件夹", "游戏可能已被移动或删除。可移出旧记录，再拖入新的完整游戏文件夹。");
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = game.RootPath, UseShellExecute = true, Verb = "open" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowLibraryManagementFailure("打开文件夹失败", ex);
        }
    }

    private bool CanRemoveLibraryGame(GameProfile game)
    {
        if (_pendingLoaderFallback.Contains(game.GameId))
        {
            ShowDropFailure("翻译组件正在等待切换", "请先退出游戏，并等待加载器切换完成，再移出游戏库。");
            return false;
        }
        if (_activeGame?.GameId == game.GameId)
        {
            ShowDropFailure("游戏正在运行或等待重新连接", "请先退出这款游戏，并等待当前会话结束，再移出游戏库。");
            return false;
        }
        bool? running;
        try { running = LibraryProcessIsRunning(game); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            Log("移除前无法确认游戏进程：" + ex);
            running = null;
        }
        if (running is null)
        {
            ShowDropFailure("无法确认游戏是否已退出", "读取同名进程的信息失败。请先关闭这款游戏，再重试移出；本次没有移除记录。");
            return false;
        }
        if (running == true)
        {
            ShowDropFailure("游戏正在运行", "请先退出这款游戏，再移出游戏库。");
            return false;
        }
        return true;
    }

    private bool? LibraryProcessIsRunning(GameProfile game)
    {
        if (RunningProcessesForTest is not null) return RunningProcessesForTest(game).Count > 0;
        if (string.IsNullOrWhiteSpace(game.ExePath)) return false;
        // Only inspect matching names, and fail closed if a matching process is elevated
        // or unreadable. The launcher's best-effort discovery intentionally differs.
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExePath));
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.HasExited) continue;
                var path = candidate.MainModule?.FileName;
                if (path is null) return null;
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(game.ExePath), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }

    private void OnRemoveLibraryGame(object sender, RoutedEventArgs e)
    {
        if (SelectedLibraryGame is not { } game || !CanRemoveLibraryGame(game)) return;
        AskConfirm("移出游戏库？", $"只移除“{game.DisplayName}”的列表记录，不删除游戏、存档、译文缓存或已安装的翻译组件。",
            "移出后可重新拖入；该条目的名称和已记录的加载器偏好会一并移除。", () =>
            {
                if (!CanRemoveLibraryGame(game)) return;
                try
                {
                    if (!_library.Remove(game.GameId)) return;
                    CancelLibraryRename();
                    RefreshGameList();
                    ShowDropSuccess("已移出游戏库", game.DisplayName, "游戏和存档仍保留在原位置。");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ShowLibraryManagementFailure("移出游戏库失败", ex);
                }
            }, "仅移出列表");
    }

    private void ShowLibraryManagementFailure(string title, Exception ex)
    {
        Log(title + "：" + ex);
        var explained = UiText.Explain(ex, "管理游戏库");
        ShowDropFailure(title, explained.Message + " " + explained.Advice);
    }

    private void OnSaveApiSettings(object sender, RoutedEventArgs e)
    {
        SelectTranslationMode(TranslationMode.Api);
        _apiSaveFailed = !SaveSettings();
        UpdateApiProviderSummary();
    }

    private void UpdateApiProviderSummary()
    {
        if (!_libraryPresentationReady) return;
        UpdateTranslationProviderSummary();
        if (_apiSaveFailed)
        {
            ApiProviderSummary.Text = "保存失败，配置尚未写入磁盘";
            return;
        }
        var host = Uri.TryCreate(_settings.Endpoint, UriKind.Absolute, out var uri) ? uri.Host : "翻译服务";
        ApiProviderSummary.Text = string.IsNullOrWhiteSpace(_settings.ApiKey) ? "待填写翻译服务密钥" : host + " · 已保存配置";
    }

    partial void OnAppearanceChanged() => UpdateApiProviderSummary();
}
