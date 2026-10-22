using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class UiAVisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vesper-ui-a-" + Guid.NewGuid().ToString("N"));

    // Catches the previous blue/slate canvas and pale primary buttons leaking into any page.
    [Theory]
    [InlineData("Light", 0)]
    [InlineData("Dark", 0)]
    [InlineData("Light", 1)]
    [InlineData("Dark", 1)]
    [InlineData("Light", 3)]
    [InlineData("Dark", 3)]
    public void SelectedAThemeKeepsNeutralCanvasAndReadableBluePrimaryActions(string theme, int page) => OnUi(window =>
    {
        window.ThemeModeBox.SelectedItem = window.ThemeModeBox.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, theme));
        window.NavList.SelectedIndex = page;
        var root = (Grid)window.Content;
        root.Measure(new Size(1200, 800)); root.Arrange(new Rect(0, 0, 1200, 800)); root.UpdateLayout();
        var canvas = ((SolidColorBrush)root.Background).Color;
        Assert.True(Math.Max(canvas.R, Math.Max(canvas.G, canvas.B)) - Math.Min(canvas.R, Math.Min(canvas.G, canvas.B)) <= 8,
            $"A版 requires a neutral page, not a blue/slate wash: {canvas}.");
        Assert.True(theme == "Dark" ? canvas.R < 40 : canvas.R > 245);
        var action = page switch { 0 => window.SelectedGameActionButton, 1 => window.ScanStartButton, _ => window.SaveTranslationSettingsButton };
        // Inspect the enabled action; unavailable-session actions deliberately use muted foregrounds.
        action.IsEnabled = true; action.ApplyTemplate(); root.UpdateLayout();
        var blue = ((SolidColorBrush)action.Background).Color;
        Assert.True(blue.B > blue.R + 70 && blue.B > blue.G * 1.3, $"Primary action must be visibly blue: {blue}.");
        Assert.True(Contrast(((SolidColorBrush)action.Foreground).Color, blue) >= 4.5);
    });

    // Catches replacing/recreating page controls and losing selection or an unsubmitted API draft.
    [Fact]
    public void NavigationAndThemeChangesKeepSelectionDraftAndFunctionalControls()
    {
        OnUi(window =>
        {
            var game = new GameProfile("SymbiontZeroType", _root, Path.Combine(_root, "Game.exe"), "2020.3.48f1", "IL2CPP", "x64", true, "") { CustomName = "SymbiontZeroType" };
            window.Games.Add(game);
            window.Games.Add(game with { GameId = "dungeon-ui-fixture", CustomName = "Project Dungeon", ExePath = Path.Combine(_root, "Dungeon.exe"), UnityVersion = "6000.0.25f1" });
            window.Games.Add(game with { GameId = "decoy-ui-fixture", CustomName = "DECOY", ExePath = Path.Combine(_root, "Decoy.exe"), UnityVersion = "2022.3.62f2" });
            window.GameLibraryList.SelectedItem = game;
            window.EndpointBox.Text = "https://example.invalid/chat/completions";
            window.ModelBox.Text = "unsubmitted-model";
            window.ApiKeyBox.Password = "fixture-key-not-a-secret";
            var endpoint = window.EndpointBox;
            window.StartCheatSessionForTest(game, new UiMemory());
            foreach (var theme in new[] { "Light", "Dark" })
            {
                window.ThemeModeBox.SelectedItem = window.ThemeModeBox.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, theme));
                window.NavList.SelectedIndex = 0;
                window.SelectedGameActionButton.Content = "一键更新并启动";
                window.SelectedComponentStatus.Text = "组件需要更新";
                Capture(window, "library-" + theme);
                window.NavList.SelectedIndex = 1;
                Capture(window, "numbers-" + theme);
                window.NavList.SelectedIndex = 3;
                window.FreeTranslationButton.IsChecked = true;
                Assert.Equal(Visibility.Visible, window.FreeTranslationPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, window.ApiTranslationPanel.Visibility);
                Capture(window, "service-free-" + theme);
                window.ApiSettingsButton.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, window.FreeTranslationPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.ApiTranslationPanel.Visibility);
                Capture(window, "service-api-" + theme);
                Assert.Same(endpoint, window.EndpointBox);
                Assert.Equal("https://example.invalid/chat/completions", endpoint.Text);
                Assert.Equal("unsubmitted-model", window.ModelBox.Text);
                Assert.Equal("fixture-key-not-a-secret", window.ApiKeyBox.Password);
                Assert.Same(game, window.GameLibraryList.SelectedItem);
            }
        });
    }

    private static void Capture(MainWindow window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("VESPER_UI_A_EVIDENCE");
        if (string.IsNullOrEmpty(folder)) return;
        var root = (Grid)window.Content;
        var previous = VisualTreeHelper.GetDpi(root);
        VisualTreeHelper.SetRootDpi(root, new DpiScale(1, 1));
        try
        {
            root.Measure(new Size(1100, 750)); root.Arrange(new Rect(0, 0, 1100, 750)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 750, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(folder);
            using var stream = File.Create(Path.Combine(folder, name + ".png")); png.Save(stream);
        }
        finally { VisualTreeHelper.SetRootDpi(root, previous); }
    }

    private sealed class UiMemory : IProcessMemory
    {
        public IReadOnlyList<MemoryRegion> Regions() => [];
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
        public bool IsAlive => true;
        public int ProcessId => 41;
        public long StartedUtcTicks => 42;
    }

    // Catches reintroducing the two stacked initial-value field rows, or placing buttons inside them.
    [Theory]
    [InlineData(1200)]
    [InlineData(900)]
    public void InitialScanFieldsShareOneRowAndActionsStayBelowWithoutClipping(double width) => OnUi(window =>
    {
        window.NavList.SelectedIndex = 1;
        var root = (Grid)window.Content;
        root.Measure(new Size(width, 800)); root.Arrange(new Rect(0, 0, width, 800)); root.UpdateLayout();
        var fields = new FrameworkElement[] { window.ScanInitialModeBox, window.ScanKindBox, window.ScanValueBox, window.ScanUpperValueBox };
        var bounds = fields.Select(f => f.TransformToAncestor(root).TransformBounds(new Rect(f.RenderSize))).ToArray();
        Assert.True(bounds.Max(b => b.Top) - bounds.Min(b => b.Top) <= 1, "Initial scan fields must share one baseline.");
        Assert.True(bounds.Max(b => b.Height) - bounds.Min(b => b.Height) <= 1,
            "Text fields must not double-apply padding and become taller than their neighboring pickers.");
        for (var i = 0; i < bounds.Length; i++)
        {
            Assert.True(bounds[i].Width >= 90, "The field must remain usable at the minimum window width.");
            Assert.InRange(bounds[i].Left, 0, width);
            Assert.True(bounds[i].Right <= width, "A field must not disappear beyond the viewport.");
            if (i > 0) Assert.True(bounds[i].Left >= bounds[i - 1].Right, "Fields must not overlap.");
        }
        var start = window.ScanStartButton.TransformToAncestor(root).TransformBounds(new Rect(window.ScanStartButton.RenderSize));
        Assert.True(start.Top >= bounds.Max(b => b.Bottom), "Start action belongs below the fields, not between them.");
    });

    // Catches losing the approved free-provider delay notice or showing it for unrelated API services.
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void FreeProviderKeepsItsProminentFirstTranslationDelayNotice(string theme) => OnUi(window =>
    {
        window.ThemeModeBox.SelectedItem = window.ThemeModeBox.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, theme));
        window.NavList.SelectedIndex = 3; window.FreeTranslationButton.IsChecked = true;
        var notice = Assert.IsType<Border>(window.FindName("FreeTranslationDelayNotice"));
        Assert.Equal(Visibility.Visible, notice.Visibility);
        var text = Assert.IsType<TextBlock>(((Grid)notice.Child).Children.OfType<TextBlock>().Single());
        Assert.Contains("首次翻译预计需要等待30秒", text.Text);
        window.ApiSettingsButton.IsChecked = true;
        Assert.Equal(Visibility.Collapsed, notice.Visibility);
    });

    // Catches excess vertical padding hiding the service page's main action in the reference viewport.
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ApiSaveActionFitsReferenceViewportWithoutScrolling(string theme) => OnUi(window =>
    {
        window.ThemeModeBox.SelectedItem = window.ThemeModeBox.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, theme));
        window.NavList.SelectedIndex = 3; window.ApiSettingsButton.IsChecked = true;
        var root = (Grid)window.Content;
        root.Measure(new Size(1100, 750)); root.Arrange(new Rect(0, 0, 1100, 750)); root.UpdateLayout();
        var bounds = window.SaveTranslationSettingsButton.TransformToAncestor(root).TransformBounds(new Rect(window.SaveTranslationSettingsButton.RenderSize));
        Assert.True(bounds.Top >= 60 && bounds.Bottom <= 730, $"The main save action is outside the initial viewport: {bounds}.");
    });

    private void OnUi(Action<MainWindow> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            MainWindow.PrepareTestDispatcherForTest();
            MainWindow? window = null;
            try { window = new MainWindow(_root, new RecordingExternalFactory { Available = false }); body(window); }
            catch (Exception ex) { error = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) throw new InvalidOperationException("A版 UI assertion failed.", error);
    }
    private static double Contrast(Color first, Color second)
    {
        static double L(Color color)
        {
            static double C(byte c) { var v = c / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        var a = L(first); var b = L(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
