using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UnityTranslator.Core;
using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly GameLibrary _library;
    private readonly TranslationStore _translations;
    private readonly AppSettings _settings;
    // 翻译请求用的 HTTP 客户端。
    //
    // 为什么要显式配 SocketsHttpHandler，而不是 `new HttpClient()`：
    //   第 100 轮实测发现"等待"里有一部分是**我们自己的**（不只是模型）：
    //     发现→发送 P50 曾高达 10 秒（那是我自己的限流 bug，已修），
    //     而发送→回包 P50=43ms —— 说明连接本身没问题，但不代表首连没成本。
    //   默认的 HttpClient 在**第一次**请求时要现做 TCP + TLS 握手，那几百毫秒
    //   恰好落在"游戏里第一句话"上，正是用户最容易察觉的地方。
    //   显式池化 + KeepAlive 让后续请求直接复用连接；下面再不发一个预热请求
    //   （见 WarmUpConnectionAsync），把这一次握手挪到用户看不见的时候。
    //
    // 超时仍保持 25 秒不变：它是"服务不回"时的保护，不能因为要快而放宽。
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 64,
        ConnectTimeout = TimeSpan.FromSeconds(6)
    })
    { Timeout = TimeSpan.FromSeconds(25) };
    private NamedPipeHost? _host;
    private OcrWorkerHost? _ocrWorker;
    private OcrTranslationService? _ocrService;
    private Process? _gameProcess;
    private TranslateRequest? _lastRequest;
    private volatile bool _ocrEnabled = true;
    private DispatcherTimer? _dropFeedbackTimer;

    /// <summary>数据目录：设置、游戏列表、译文库、快捷项与日志都放在这里。</summary>
    private readonly string _appDataDirectory = string.Empty;

    public ObservableCollection<GameProfile> Games { get; } = [];

    /// <summary>
    /// 已经自动回退过加载器的游戏（每款只自动换一次，避免来回换）。
    /// 与界面无关，所以用 GameId 而不是 <see cref="GameProfile"/> —— 列表刷新会换新实例。
    /// </summary>
    private readonly HashSet<string> _autoFallbackAttempted = new(StringComparer.Ordinal);

    /// <summary>正在等"用户退出游戏"才好换加载器的游戏。</summary>
    private readonly HashSet<string> _pendingLoaderFallback = new(StringComparer.Ordinal);

    /// <summary>
    /// 按游戏列表重刷界面上的卡片。回退会改列表里的加载器偏好，
    /// 卡片不重刷的话，下一次"自检/安装"还会按旧偏好算包，等于白换。
    /// </summary>
    private void RefreshGameList()
    {
        var selectedId = SelectedLibraryGame?.GameId;
        _suppressLibrarySelection = true;
        Games.Clear();
        foreach (var game in _library.List()) Games.Add(game);
        _suppressLibrarySelection = false;
        GameLibraryList.SelectedItem = GameLibraryList.Items.OfType<GameProfile>().FirstOrDefault(game => game.GameId == selectedId)
            ?? GameLibraryList.Items.OfType<GameProfile>().FirstOrDefault();
        UpdateLibraryPresentation();
    }

    /// <summary>测试替身；为 null 时使用真正的消息框。</summary>
    internal Func<string, MessageBoxResult>? MessageBoxHandler { get; set; }

    internal Func<GameProfile, IReadOnlyList<PackageFile>?>? ComponentPackageForTest { get; set; }

    internal Action<GameProfile, ITranslationDispatcher, string>? LaunchGameForTest { get; set; }


    private void ShowMessage(string message, string title, MessageBoxButton button, MessageBoxImage icon)
    {
        if (MessageBoxHandler is not null) { MessageBoxHandler(message); return; }
        MessageBox.Show(this, message, title, button, icon);
    }

    public MainWindow() : this(null) { }

    /// <param name="appDataDirectory">
    /// 数据目录；传 null 时使用 %LOCALAPPDATA%\UnityTranslator。
    /// 测试可传入临时目录，避免读写用户真实的数据。
    /// </param>
    internal MainWindow(string? appDataDirectory) : this(appDataDirectory, new ExternalOcrControllerFactory()) { }

    internal MainWindow(string? appDataDirectory, IExternalOcrControllerFactory externalOcrFactory)
    {
        _externalOcrFactory = externalOcrFactory ?? throw new ArgumentNullException(nameof(externalOcrFactory));
        InitializeComponent();
        var dropTargets = EnableDropOnEveryChild(GameLibraryPage);
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityTranslator");
        _appDataDirectory = dir;
        _library = new GameLibrary(Path.Combine(dir, "games.json"));
        _translations = new TranslationStore(Path.Combine(dir, "translations.db"));
        _settings = new AppSettings(Path.Combine(dir, "settings.json"), new DpapiSecretProtector());
        _settings.Load();
        EndpointBox.Text = _settings.Endpoint;
        ModelBox.Text = _settings.Model;
        ApiKeyBox.Password = _settings.ApiKey;
        InitializeProviderSettings();
        foreach (var game in _library.List()) Games.Add(game);
        DataContext = this;
        InitializeLibraryPresentation();
        InitializeAppearance();
        VersionLabel.Text = "版本 " + AppVersion.Display;
        NavList.SelectedIndex = 0;
        WorkspaceTabs.SelectedIndex = 0;
        TopPageTitle.Text = "游戏与翻译";
        // 还没有游戏会话：查找、修改、锁定与查询都必须保持不可用。
        RefreshWorkspaceState();
        SetRunStatus("还没有启动游戏。先在上面把游戏拖进来，然后点“安装翻译组件”。", RunState.Idle);
        Log($"启动：窗口已创建，拖放区子元素已开启拖放 {dropTargets} 个");
    }

    private bool SaveSettings()
    {
        try
        {
            SaveSelectedTranslationSettings();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception)
        {
            var explained = UiText.Explain(ex, "保存翻译服务设置");
            SetRunStatus(explained.Message, RunState.Warning, explained.Advice, explained.Technical);
            return false;
        }
    }

    internal void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameProfile game }) return;
        if (!game.IsCompatible)
        {
            Log($"启动游戏：被拒绝，{game.DisplayName} 不兼容（{game.CompatibilityReason}）");
            MessageBox.Show(this, game.CompatibilityReason, "这款游戏暂时不能启动",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!File.Exists(Path.Combine(game.RootPath, ".unity-translator-install.json")))
        {
            // 组件没装：但如果游戏其实已经在跑，接上去比弹这个提示更有用。
            // 这个顺序是实测踩出来的：玩家自己启动了游戏、只想改数值时，
            // 先弹“还没有安装组件”会把人挡在门外，而连接已运行的游戏并不需要重新安装。
            var running = RunningProcesses(game);
            if (running.Count > 0)
            {
                Log($"启动游戏：{game.DisplayName} 没装组件，但已经在运行（PID={running[0].ProcessId}），"
                    + "改为直接连接到这个进程");
                AttachToRunning(game, running[0], null);
                return;
            }
            Log($"启动游戏：被拒绝，{game.DisplayName} 还没有安装翻译组件");
            MessageBox.Show(this, "请先点“安装翻译组件”，装好之后再启动游戏。", "还没有安装组件",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_gameProcess is { HasExited: false })
        {
            Log($"启动游戏：被拒绝，已经跟踪着进程 {_gameProcess.Id}（{_activeGame?.DisplayName}）");
            MessageBox.Show(this, "已经有一款游戏在运行了，请先退出它。", "游戏正在运行",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 已在运行的游戏只连接，不修改它正在使用的组件文件。
        var alreadyRunning = RunningProcesses(game);
        if (alreadyRunning.Count > 0)
        {
            Log($"启动游戏：{game.DisplayName} 已经在运行（PID={alreadyRunning[0].ProcessId}），"
                + "改为直接连接到这个进程，不再启动第二个");
            AttachToRunning(game, alreadyRunning[0], null);
            return;
        }

        // 已安装但不是当前组件时，启动必须停在更新选择上。
        // 更新成功后重新走启动入口；取消、安装失败或复查失败都不会启动。
        var componentCheck = CheckComponent(game);
        if (componentCheck is { NeedsUpdate: true })
        {
            var notice = ComponentNotice.UpdateNotice(componentCheck);
            Log($"启动游戏：{game.DisplayName} 的组件需要更新"
                + $"（已装={componentCheck.InstalledVersion ?? "未知"}，当前={componentCheck.CurrentVersion}，"
                + $"原因={componentCheck.Reason}）");
            SetRunStatus($"{game.DisplayName} 的翻译组件需要更新，游戏尚未启动。", RunState.Warning,
                "选择“一键更新并启动”，或取消本次启动。");
            AskConfirm(notice.Title, notice.Body, $"目标游戏：{game.DisplayName}\n目录：{game.RootPath}",
                () => UpdateComponentAndLaunch(game), "一键更新并启动");
            return;
        }
        if (!SaveSettings()) Log("翻译服务设置未能保存，本次启动仍使用界面中的当前选择。下次启动软件时需要重新选择。");
        Log($"启动游戏：准备启动 {game.DisplayName}，exe={game.ExePath}");

        // 免费翻译不依赖 API 配置。某个服务不可用时不擅自切换供应商，
        // 游戏与数值工具仍可启动，文字保留原文。
        var translationRequested = TranslationEnabledBox.IsChecked == true;
        ITranslationDispatcher dispatcher = new DisabledTranslation();
        string? translationProblem = null;
        if (translationRequested)
        {
            try
            {
                var configured = TranslationProviderFactory.Create(_httpClient, SelectedTranslationMode,
                    SelectedFreeTranslationProvider, EndpointBox.Text, ModelBox.Text, ApiKeyBox.Password);
                // 只预热玩家明确选择的 API；免费模式绝不访问保存的 API 地址或发送其密钥。
                if (configured.Translator is ChatTranslationClient chat)
                    _ = chat.WarmUpConnectionAsync(new Uri(EndpointBox.Text.Trim()));
                dispatcher = new TranslationCoordinator(_translations, configured.Translator, configured.CacheScope)
                {
                    Diagnostics = Log,
                    Latency = Log
                };
                Log($"翻译服务：{configured.DisplayName}；缓存范围={configured.CacheScope}（本次游戏会话固定，设置修改下次启动生效）");
            }
            catch (InvalidOperationException ex)
            {
                translationProblem = ex.Message;
            }
        }
        // 这三条日志是排查"游戏里没有翻译"的第一落点：
        // 如果这里记的是"未启用"，那问题在设置，不在插件或游戏。
        Log(translationRequested
            ? $"翻译：已请求联网翻译，结果={(translationProblem is null ? "已创建翻译调度器" : "未创建（" + translationProblem + "）")}"
            : "翻译：用户关闭了“启用联网翻译”，本次启动不会翻译任何文字（数值工具不受影响）");

        try
        {
            _host?.Dispose();
            _host = null;
            _ocrWorker = new OcrWorkerHost(Path.Combine(AppContext.BaseDirectory, "ocr"));
            var supportsOcr = PackageCatalog.SupportsEmbeddedOcr(game, PreferredLoaderFamily(game));
            // 画面识别依赖联网翻译，翻译关闭时不创建，作弊功能不受影响。
            _ocrService = supportsOcr && dispatcher is TranslationCoordinator coordinator
                ? new OcrTranslationService(_ocrWorker, coordinator)
                : null;

            var status = translationProblem is null && translationRequested
                ? (!supportsOcr || _ocrWorker.IsAvailable
                    ? "游戏已启动，正在等游戏里的文字。游戏内按 F8 暂停或恢复翻译。"
                    : "游戏已启动。没有找到本机的画面识别组件，所以只能翻译能直接读到的文字。")
                : $"游戏已启动，数值工具可以使用（{translationProblem ?? "已关闭联网翻译"}）。";
            var advice = translationProblem is null
                ? null
                : "要翻译文字，请到“翻译服务”页面检查当前选择；只用数值工具则不需要填写翻译配置。";
            if (LaunchGameForTest is { } launchForTest) launchForTest(game, dispatcher, status);
            else StartGameSession(game, dispatcher, status);
            if (advice is not null) SetRunStatus(status, RunState.Warning, advice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidOperationException or ArgumentException)
        {
            _host?.Dispose();
            _host = null;
            Log($"启动游戏：失败，{ex.GetType().Name}：{ex.Message}");
            var explained = UiText.Explain(ex, "启动游戏");
            SetRunStatus(explained.Message, RunState.Error, explained.Advice, explained.Technical);
            ShowMessage(explained.Message + "\n\n" + explained.Advice, "启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>测试钩子：走真实的“启动游戏”入口，验证它不会在游戏已运行时再起一个进程。</summary>
    internal void LaunchForTest(object sender) => OnLaunch(sender, new RoutedEventArgs());

    private void UpdateComponentAndLaunch(GameProfile game)
    {
        try
        {
            var package = ComponentPackageForTest?.Invoke(game)
                ?? PackageCatalog.Load(AppContext.BaseDirectory, game, PreferredLoaderFamily(game));
            if (package is null || package.Count == 0)
                throw new InvalidOperationException("没有找到这款游戏适用的翻译组件包");

            var plan = InstallPlanner.Plan(game, package, ComponentNotice.CurrentVersion);
            InstallPlanner.Apply(plan);
            var result = InstallPlanner.Check(game.RootPath, package, ComponentNotice.CurrentVersion);
            if (result.State != ComponentState.UpToDate)
                throw new InvalidOperationException("更新后组件自检仍未通过");

            Log($"启动游戏：{game.DisplayName} 的组件已更新并通过自检，继续启动");
            OnLaunch(new Button { Tag = game }, new RoutedEventArgs());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or InvalidOperationException)
        {
            Log($"启动游戏：更新失败，不启动 {game.DisplayName}：{ex.GetType().Name} {ex.Message}");
            SetRunStatus($"{game.DisplayName} 的翻译组件更新失败，游戏未启动。", RunState.Error,
                "请检查组件包和游戏目录后重试。", ex.Message);
            ShowMessage("更新翻译组件失败，游戏没有启动。\n\n"
                + "请检查组件包和游戏目录后重试。\n\n技术详情：" + ex.Message, "更新失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveCorrection(object sender, RoutedEventArgs e)
    {
        if (_lastRequest is null)
        {
            MessageBox.Show(this, "还没有可以修改的译文。等游戏里出现一句话之后再试。", "暂时不能修改",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _translations.SaveCorrection(_lastRequest.SourceLanguage, _lastRequest.Source, CorrectionBox.Text);
            SetRunStatus("修改已保存。以后遇到同样的原文会优先用你改过的说法。", RunState.Ok);
        }
        catch (ArgumentException ex)
        {
            var explained = UiText.Explain(ex, "保存译文修改");
            SetRunStatus(explained.Message, RunState.Warning, explained.Advice, explained.Technical);
            ShowMessage(explained.Message + "\n\n" + explained.Advice, "译文格式不完整",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _dropFeedbackTimer?.Stop();
        if (_dropFeedbackTimer is not null) _dropFeedbackTimer.Tick -= OnDropFeedbackElapsed;
        GameLibraryPage.LayoutUpdated -= OnLibraryDropLayoutUpdated;
        // 先标记关闭：游戏进程的退出事件可能在关闭过程中到达，
        // 那时不应该再去“等新进程接管”（窗口已经没了，接管毫无意义）。
        _closing = true;
        StopTakeOverWaitOnClose();
        Log("关闭：窗口正在关闭，会话与游戏进程句柄将被释放");
        // 关闭不提交翻译草稿，也不重试用户已看到失败的保存。
        DisposeAppearance();
        Games.CollectionChanged -= OnLibraryGamesChanged;
        EndGameSession();
        _scanWorkspace.Dispose();
        _host?.Dispose();
        _httpClient.Dispose();
        _translations.Dispose();
        _gameProcess?.Dispose();
        base.OnClosed(e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLibraryDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) ResetDropFeedback();
        OnDragOver(sender, e);
    }

    private void OnLibraryDropLayoutUpdated(object? sender, EventArgs e)
    {
        // Templates and virtualized rows are created after the constructor.
        // WPF native hit-testing checks the exact element, not its AllowDrop parent.
        EnableDropOnEveryChild(GameLibraryPage);
    }

    // WPF 只把拖放事件发给鼠标正下方那一个元素；该元素 AllowDrop 为 false 时
    // DragDrop.GetCurrentTarget 会把目标置为 null，既不触发 DragOver 也不触发 Drop，
    // 并且不会回退到父级。拖放区里的 StackPanel、TextBlock、Button 默认都是 false，
    // 所以必须逐个打开，否则只有拖到文字之间的空隙才有效。
    private static int EnableDropOnEveryChild(DependencyObject parent)
    {
        var enabled = 0;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is UIElement { AllowDrop: false } element)
            {
                element.AllowDrop = true;
                enabled++;
            }
            enabled += EnableDropOnEveryChild(child);
        }
        return enabled;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Copy;
        string[] paths;
        try
        {
            paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        }
        catch (Exception ex)
        {
            Log("拖入：读取文件列表失败 " + ex);
            ShowDropFailure("没能读取拖进来的内容", "请再拖一次，或点击游戏库上方的“添加”。");
            return;
        }
        Log($"拖入：收到 {paths.Length} 个路径 -> {string.Join(" | ", paths)}");
        if (paths.Length == 0)
        {
            ShowDropFailure("拖进来的东西里没有游戏", "请拖入游戏的 .exe，或整个游戏文件夹。");
            return;
        }
        foreach (var path in paths) AddGame(path);
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "游戏程序 (*.exe)|*.exe", CheckFileExists = true };
        var result = dialog.ShowDialog(this);
        Log($"选择程序：对话框返回 {result?.ToString() ?? "null"}，文件名='{dialog.FileName}'");
        if (result == true) AddGame(dialog.FileName);
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Unity 游戏文件夹" };
        var result = dialog.ShowDialog(this);
        Log($"选择文件夹：对话框返回 {result?.ToString() ?? "null"}，文件夹='{dialog.FolderName}'");
        if (result == true) AddGame(dialog.FolderName);
    }

    internal void AddGame(string path)
    {
        Log($"添加游戏：'{path}'  文件存在={File.Exists(path)}  目录存在={Directory.Exists(path)}");
        try
        {
            var profile = _library.Add(path);
            Log($"  检测结果：兼容={profile.IsCompatible} 后端={profile.Backend} 架构={profile.Architecture} " +
                $"Unity={profile.UnityVersion} 原因='{profile.CompatibilityReason}'");
            // Games 绑定在界面上：Remove + Insert 会先触发一次集合变更通知，
            // 通知期间列表内容可能被界面逻辑改动，随后的 Insert/RemoveAt 就会越界。
            // 这里改为按索引一次性替换，只有一次变更通知，也不存在中间状态。
            var old = Games.FirstOrDefault(game => game.GameId == profile.GameId);
            var existingIndex = old is null ? -1 : Games.IndexOf(old);
            if (existingIndex >= 0) Games[existingIndex] = profile;
            else Games.Insert(0, profile);
            UpdateGameCardHint();
            LibrarySearchBox.Clear();
            GameLibraryList.SelectedItem = profile;
            Log($"  已加入列表，当前共 {Games.Count} 项");

            // 卡片在窗口下方，用户看不到；如果这一块没反应，会以为拖入失败。
            var name = profile.DisplayName;
            if (!profile.IsCompatible)
                ShowDropFailure("已加入列表，但暂时不能安装", $"{name}：{profile.CompatibilityReason} 若拖入的是启动器，请改选实际游戏的 .exe 或完整文件夹。");
            else if (old is not null)
                ShowDropSuccess("已更新", name, "游戏卡片已刷新。下一步：点“安装翻译组件”。");
            else
                ShowDropSuccess("已识别并加入列表", name, "下一步：点游戏卡片上的“安装翻译组件”。");
        }
        // 这里必须捕获所有异常：以前只捕获 IO/权限/参数异常，其余异常会被静默吞掉，
        // 表现就是"拖进去/选了文件之后什么都没发生"，无法排查。
        catch (Exception ex)
        {
            Log("  失败：" + ex);
            var explained = UiText.Explain(ex, "读取这个游戏");
            ShowDropFailure(explained.Message, explained.Advice);
            ShowMessage(explained.Message + "\n\n" + explained.Advice + "\n\n" + explained.Technical,
                "没法读取这个游戏", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>成功提示：几秒后自动淡回默认界面。</summary>
    private void ShowDropSuccess(string title, string gameName, string detail)
    {
        SetDropFeedback("SuccessBorder", "SuccessSoft", "Success", "", title, gameName, detail);
        RestartDropFeedbackTimer(TimeSpan.FromSeconds(2.6));
    }

    /// <summary>失败提示留出阅读时间，可手动关闭；不会永久占用拖入栏。</summary>
    private void ShowDropFailure(string title, string detail)
    {
        SetDropFeedback("DangerBorder", "DangerSoft", "Danger", "", title, string.Empty, detail);
        RestartDropFeedbackTimer(TimeSpan.FromSeconds(8));
    }

    private void OnDismissDropFeedback(object sender, RoutedEventArgs e) => ResetDropFeedback();

    private void SetDropFeedback(string borderColor, string background, string accent, string icon,
        string title, string gameName, string detail)
    {
        DropZone.SetResourceReference(Border.BorderBrushProperty, borderColor);
        DropZone.BorderThickness = new Thickness(2);
        DropZone.SetResourceReference(Border.BackgroundProperty, background);
        DropResultIcon.Text = icon;
        DropResultIcon.SetResourceReference(TextBlock.ForegroundProperty, accent);
        DropResultTitle.Text = string.IsNullOrEmpty(gameName) ? title : title + "：" + gameName;
        DropResultTitle.SetResourceReference(TextBlock.ForegroundProperty, accent);
        DropResultDetail.Text = detail;
        DropResultDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        DropResultPanel.Visibility = Visibility.Visible;
        DropResultPanel.IsHitTestVisible = true;
        Fade(DropHintPanel, 0);
        Fade(DropResultPanel, 1);
    }

    private void ResetDropFeedback()
    {
        _dropFeedbackTimer?.Stop();
        DropZone.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        DropZone.BorderThickness = new Thickness(0);
        DropZone.SetResourceReference(Border.BackgroundProperty, "BgWindow");
        if (DropResultPanel.IsKeyboardFocusWithin) LibrarySearchBox.Focus();
        DropResultPanel.IsHitTestVisible = false;
        DropResultPanel.Visibility = Visibility.Collapsed;
        DropResultPanel.BeginAnimation(OpacityProperty, null);
        DropResultPanel.Opacity = 0;
        Fade(DropHintPanel, 1);
    }

    private static void Fade(UIElement target, double to)
    {
        // 先落基础值再挂动画：即使动画因为某些原因不推进（例如界面未在渲染），
        // 最终状态也仍然是期望的可见性，不会出现"提示没显示出来"。
        target.Opacity = to;
        target.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(200))
            {
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private void RestartDropFeedbackTimer(TimeSpan delay)
    {
        _dropFeedbackTimer ??= new DispatcherTimer();
        _dropFeedbackTimer.Stop();
        _dropFeedbackTimer.Interval = delay;
        _dropFeedbackTimer.Tick -= OnDropFeedbackElapsed;
        _dropFeedbackTimer.Tick += OnDropFeedbackElapsed;
        _dropFeedbackTimer.Start();
    }

    private void OnDropFeedbackElapsed(object? sender, EventArgs e)
    {
        _dropFeedbackTimer?.Stop();
        ResetDropFeedback();
    }

    /// <summary>
    /// 排错用日志，写在数据目录里的 app.log。
    /// 使用注入的数据目录而不是硬编码的 %LOCALAPPDATA%：
    /// 否则并发运行的测试会同时写同一个文件，互相阻塞并造成偶发失败。
    /// </summary>
    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_appDataDirectory);
            File.AppendAllText(Path.Combine(_appDataDirectory, "app.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    internal void OnInstall(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameProfile game }) return;
        if (!game.IsCompatible)
        {
            MessageBox.Show(this, game.CompatibilityReason, "暂时不能安装",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 与 CheckComponent 同理：装的时候必须装"这款游戏实测能跑的那个加载器"，
        // 否则启动时自检提示更新 → 用户点安装 → 又把能跑的换回起不来的。
        try
        {
            var package = ComponentPackageForTest?.Invoke(game)
                ?? PackageCatalog.Load(AppContext.BaseDirectory, game, PreferredLoaderFamily(game));
            if (package is null || package.Count == 0)
            {
                ShowMessage("这款游戏的翻译组件还没有准备好，暂时不能安装。", "暂时不能安装",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // 把当前版本写进安装清单 —— 下次启动游戏时的自检就靠它判断"装的是不是这一版"。
            var plan = InstallPlanner.Plan(game, package, ComponentNotice.CurrentVersion);
            AskConfirm("安装翻译组件", DescribeInstall(plan),
                $"目标游戏：{game.DisplayName}\n目录：{game.RootPath}",
                () => ApplyInstall(plan, game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or InvalidOperationException)
        {
            var explained = UiText.Explain(ex, "安装");
            SetRunStatus(explained.Message, RunState.Error, explained.Advice, explained.Technical);
            ShowMessage(explained.Message + "\n\n" + explained.Advice, "安装失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 对一款游戏做组件自检。拿不到组件包时返回 null（调用方当作"无法判断"处理，不提示）。
    /// </summary>
    private ComponentCheck? CheckComponent(GameProfile game)
    {
        // ★ 必须带上"这款游戏实测能跑哪个加载器"。
        //   不带的话：预加载器失败 → 自动回退到 BepInEx 5 → 用户下次启动时，
        //   这里又拿 BepInEx 6 的包去比对，于是每次都提示"组件需要更新"，
        //   用户一点"安装"，就把刚回退好的东西又换回起不来的那一版。
        //   （这个坑上一轮在别的路径上踩过一次，不再踩第二次。）
        try
        {
            // 即使随程序附带的组件包缺失，清单里的旧版本仍能识别，
            // 从而阻止一次明知组件过期的启动。
            var package = ComponentPackageForTest?.Invoke(game)
                ?? PackageCatalog.Load(AppContext.BaseDirectory, game, PreferredLoaderFamily(game));
            return InstallPlanner.Check(game.RootPath, package ?? [], ComponentNotice.CurrentVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A broken bundle must not abort library/startup or bypass updating.
            return new ComponentCheck(ComponentState.NeedsUpdate, UpdateReason.UnknownVersion, null, ComponentNotice.CurrentVersion);
        }
    }

    /// <summary>
    /// 这款游戏该用哪个加载器家族安装/自检 —— 由"上次实测哪个能跑"决定。
    /// 记录值只认 `"BepInEx5"`（唯一的回退档）；其余（含空串）一律 `Auto`。
    /// </summary>
    private LoaderFamily PreferredLoaderFamily(GameProfile game) =>
        _library.PreferredLoaderFor(game.GameId) == GameProfile.BepInEx5Loader
            ? LoaderFamily.BepInEx5
            : LoaderFamily.Auto;

    /// <summary>
    /// 安装确认文案。只说数量，不罗列文件名——两百多行的清单没人看得下去。
    /// 但"会覆盖游戏原有文件"和"这里本来就有别的汉化"必须讲清楚，否则用户不知道自己的汉化去哪了。
    /// </summary>
    internal static string DescribeInstall(InstallPlan plan)
    {
        var lines = new List<string> { $"将向游戏目录写入 {plan.Files.Count} 个文件。" };
        if (plan.AddedCount > 0) lines.Add($"· 新增 {plan.AddedCount} 个");
        if (plan.Upgraded.Count > 0) lines.Add($"· 更新本工具以前装过的 {plan.Upgraded.Count} 个");
        if (plan.Replaced.Count > 0)
            lines.Add($"· 替换游戏目录里已有的同名文件 {plan.Replaced.Count} 个（替换前会先备份，卸载时自动还原）");
        if (plan.Identical.Count > 0)
            lines.Add($"· 有 {plan.Identical.Count} 个文件的内容和现有文件完全一样，跳过不写");

        if (plan.HasOtherTranslationComponent)
            lines.Add("\n注意：这个游戏目录里已经装了别的汉化组件（" +
                      string.Join("、", plan.ExistingTranslationComponents) +
                      "）。安装后画面文本改由本工具翻译；被替换掉的文件已经备份好，卸载时会还原。");

        lines.Add("\n确认继续吗？");
        return string.Join("\n", lines);
    }

    /// <param name="removeStaleFilesFrom">
    /// 非 null 时，安装前先按清单清掉"这个包**没有**"的旧文件（换加载器专用）。
    /// 不传就是原来的行为：只写不改，绝不动清单以外的东西。
    /// </param>
    private void ApplyInstall(InstallPlan plan, GameProfile game, IReadOnlyList<PackageFile>? removeStaleFilesFrom = null)
    {
        try
        {
            if (removeStaleFilesFrom is not null)
            {
                // ★ 清理范围要减掉"这次要写入的文件"。理论上两者不相交（清理只处理清单里的旧文件），
                //   但真出现交集时后果是"先删掉、再写入"变成"删掉、写入失败 → 缺文件"，
                //   而这里少删一个文件毫无代价。所以按白名单再挡一层。
                var writing = new HashSet<string>(
                    plan.Files.Select(file => file.RelativePath.Replace('/', '\\')),
                    StringComparer.OrdinalIgnoreCase);
                var removed = InstallPlanner.RemoveStaleFiles(plan.GameRoot, removeStaleFilesFrom)
                    .Where(path => !writing.Contains(Path.GetRelativePath(plan.GameRoot, path)))
                    .ToArray();
                if (removed.Length > 0)
                {
                    Log($"安装：先清掉旧加载器的 {removed.Length} 个残留文件（例如 "
                        + string.Join("、", removed.Take(3).Select(Path.GetFileName)) + "）");
                }
            }
            InstallPlanner.Apply(plan);
            SetRunStatus($"{game.DisplayName} 的翻译组件已安装。现在可以点“启动游戏”。", RunState.Ok,
                "下一步：点这款游戏卡片上的“启动游戏”。");
            // 走 ShowMessage 而不是直接 MessageBox.Show：自动回退会用到这条路径，
            // 而回退是**无人值守**的 —— 直接弹真对话框会让自动化验证卡在那里，
            // 也会让"验证自动回退"这件事根本没法做。
            ShowMessage("翻译组件已安装。现在可以点“启动游戏”了。", "安装完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or InvalidOperationException)
        {
            var explained = UiText.Explain(ex, "安装");
            SetRunStatus(explained.Message, RunState.Error, explained.Advice, explained.Technical);
            ShowMessage(explained.Message + "\n\n" + explained.Advice, "安装失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _libraryCheckStamp = null;
            UpdateLibraryPresentation();
        }
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameProfile game }) return;
        var manifest = Path.Combine(game.RootPath, ".unity-translator-install.json");
        if (!File.Exists(manifest))
        {
            MessageBox.Show(this, "这个游戏没有本工具的安装记录，不需要卸载。", "无需卸载",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AskConfirm("卸载翻译组件",
            $"将从 {game.DisplayName} 删除本工具安装的文件。\n\n" +
            "只删除本工具自己装进去、并且没有被改动过的文件；你改过的文件会保留下来。游戏存档不受影响。",
            $"目录：{game.RootPath}",
            () => ApplyUninstall(game, manifest));
    }

    private void ApplyUninstall(GameProfile game, string manifest)
    {
        try
        {
            var report = InstallPlanner.Uninstall(manifest);
            var restoreNote = report.Restored.Count > 0
                ? $"\n\n游戏原有的 {report.Restored.Count} 个文件已经还原成安装前的样子。"
                : string.Empty;
            if (report.Conflicts.Count == 0)
            {
                SetRunStatus($"{game.DisplayName} 的翻译组件已卸载干净。", RunState.Ok);
                ShowMessage("翻译组件已卸载。" + restoreNote, "卸载完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var kept = string.Join("\n", report.Conflicts);
            SetRunStatus($"{game.DisplayName} 有 {report.Conflicts.Count} 个文件被保留，没有删除。", RunState.Warning,
                "这些文件的内容已经和安装时不一样，为了不误删你自己的东西而保留。");
            ShowMessage(
                "有些文件已经被改动过，为了不误删你的东西，这些文件保留了下来：\n\n" + kept + restoreNote,
                "卸载完成（部分文件保留）", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or InvalidOperationException)
        {
            var explained = UiText.Explain(ex, "卸载");
            SetRunStatus(explained.Message, RunState.Error, explained.Advice, explained.Technical);
            ShowMessage(explained.Message + "\n\n" + explained.Advice, "卸载失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _libraryCheckStamp = null;
            UpdateLibraryPresentation();
        }
    }
}
