using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class ThemeAppearanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-theme-ui-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Light", false, false)]
    [InlineData("Light", false, true)]
    [InlineData("Light", true, false)]
    [InlineData("Light", true, true)]
    [InlineData("Dark", false, false)]
    [InlineData("Dark", false, true)]
    [InlineData("Dark", true, false)]
    [InlineData("Dark", true, true)]
    public void FocusingPreferenceSwitchPreservesItsCompleteCircularThumb(string theme, bool enabled, bool hovered)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            SelectOcrCapableGame(window);
            var root = Assert.IsType<Grid>(window.Content);
            // These exact DIP assertions describe 100%; keep the product's layout rounding enabled.
            using var controlledDpi = new ControlledVisualDpi(root, 1);
            foreach (var toggle in new[] { window.TranslationEnabledBox, window.EnableOcrBox })
            {
                try
                {
                    toggle.IsChecked = enabled;
                    toggle.ApplyTemplate();
                    ForceState(toggle, "IsMouseOver", hovered);
                    ForceState(toggle, "IsKeyboardFocused", false);
                    root.Measure(new Size(900, 640));
                    root.Arrange(new Rect(0, 0, 900, 640));
                    root.UpdateLayout();
                    var track = Assert.IsType<Border>(toggle.Template.FindName("SwitchTrack", toggle));
                    var thumb = Assert.IsType<Border>(toggle.Template.FindName("SwitchThumb", toggle));
                    var restingBounds = thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize));

                    ForceState(toggle, "IsKeyboardFocused", true);
                    Assert.True(toggle.IsKeyboardFocused);
                    root.UpdateLayout();
                    DrainDispatcher();
                    var focusedBounds = thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize));
                    var clip = LayoutInformation.GetLayoutClip(thumb);
                    Assert.True(clip is null || clip.FillContainsWithDetail(new RectangleGeometry(new Rect(thumb.RenderSize))) == IntersectionDetail.FullyContains,
                        $"{toggle.Name} focused thumb must remain a complete circle; render={thumb.RenderSize}, clip={clip?.Bounds}.");
                    Assert.Equal(restingBounds, focusedBounds);
                    Assert.Equal(18, thumb.ActualWidth);
                    Assert.Equal(18, thumb.ActualHeight);
                    Assert.True(new Rect(track.RenderSize).Contains(focusedBounds), "The complete thumb must remain within the track.");
                    Assert.Contains(VisualDescendants(toggle).OfType<Border>(), border =>
                        border.BorderThickness.Top > 0 && border.BorderBrush is SolidColorBrush { Color.A: > 0 } &&
                        border.ActualWidth >= track.ActualWidth && border.ActualHeight >= track.ActualHeight);
                    ForceState(toggle, "IsKeyboardFocused", false);
                    root.UpdateLayout();
                    Assert.Equal(restingBounds, thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize)));
                }
                finally
                {
                    ForceState(toggle, "IsKeyboardFocused", false);
                    ForceState(toggle, "IsMouseOver", false);
                }
            }
        });
    }

    // Controlled WPF visual DPI, not physical monitor/input evidence.
    // Break caught: clipping/changing the focused thumb or forgetting pixel rounding at fractional DPI.
    [Theory]
    [InlineData(1.0, 18)]
    [InlineData(1.5, 27)]
    [InlineData(1.75, 32)]
    [InlineData(2.0, 36)]
    public void PreferenceSwitchPreservesCompleteThumbAtControlledDpi(double dpiScale, double physicalThumbSize)
    {
        OnWindow(window =>
        {
            SelectOcrCapableGame(window);
            var root = Assert.IsType<Grid>(window.Content);
            using var controlledDpi = new ControlledVisualDpi(root, dpiScale);
            foreach (var theme in new[] { "Light", "Dark" })
            foreach (var enabled in new[] { false, true })
            foreach (var hovered in new[] { false, true })
            {
                Choose(ThemeChooser(window), theme);
                foreach (var toggle in new[] { window.TranslationEnabledBox, window.EnableOcrBox })
                {
                    try
                    {
                        toggle.IsChecked = enabled;
                        toggle.ApplyTemplate();
                        ForceState(toggle, "IsMouseOver", hovered);
                        ForceState(toggle, "IsKeyboardFocused", false);
                        root.Measure(new Size(900, 640));
                        root.Arrange(new Rect(0, 0, 900, 640));
                        root.UpdateLayout();
                        var track = Assert.IsType<Border>(toggle.Template.FindName("SwitchTrack", toggle));
                        var thumb = Assert.IsType<Border>(toggle.Template.FindName("SwitchThumb", toggle));
                        var restingBounds = thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize));
                        ForceState(toggle, "IsKeyboardFocused", true);
                        Assert.True(toggle.IsKeyboardFocused);
                        root.UpdateLayout();
                        DrainDispatcher();
                        var focusedBounds = thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize));
                        var clip = LayoutInformation.GetLayoutClip(thumb);
                        Assert.True(clip is null || clip.FillContainsWithDetail(new RectangleGeometry(new Rect(thumb.RenderSize))) == IntersectionDetail.FullyContains,
                            $"{toggle.Name} thumb must remain complete at controlled {dpiScale * 100}% DPI; render={thumb.RenderSize}, clip={clip?.Bounds}.");
                        Assert.Equal(restingBounds, focusedBounds);
                        Assert.True(thumb.UseLayoutRounding);
                        var dpi = VisualTreeHelper.GetDpi(thumb);
                        Assert.Equal(dpiScale, dpi.DpiScaleX);
                        Assert.Equal(dpiScale, dpi.DpiScaleY);
                        Assert.Equal(physicalThumbSize, thumb.ActualWidth * dpi.DpiScaleX);
                        Assert.Equal(physicalThumbSize, thumb.ActualHeight * dpi.DpiScaleY);
                        Assert.Equal(thumb.ActualWidth, thumb.ActualHeight);
                        Assert.True(new Rect(track.RenderSize).Contains(focusedBounds), "The complete thumb must remain within the track.");
                        Assert.Contains(VisualDescendants(toggle).OfType<Border>(), border =>
                            border.BorderThickness.Top > 0 && border.BorderBrush is SolidColorBrush { Color.A: > 0 } &&
                            border.ActualWidth >= track.ActualWidth && border.ActualHeight >= track.ActualHeight);
                        ForceState(toggle, "IsKeyboardFocused", false);
                        root.UpdateLayout();
                        Assert.Equal(restingBounds, thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize)));
                    }
                    finally
                    {
                        ForceState(toggle, "IsKeyboardFocused", false);
                        ForceState(toggle, "IsMouseOver", false);
                    }
                }
            }
        });
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    [InlineData("Dark", true)]
    public void PreferenceSwitchThumbRemainsVisibleAgainstTrack(string theme, bool enabled)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            SelectOcrCapableGame(window);
            foreach (var toggle in new[] { window.TranslationEnabledBox, window.EnableOcrBox })
            {
                toggle.IsChecked = enabled;
                toggle.ApplyTemplate();
                var track = Assert.IsType<Border>(toggle.Template.FindName("SwitchTrack", toggle));
                var thumb = Assert.IsType<Border>(toggle.Template.FindName("SwitchThumb", toggle));
                Assert.Equal(enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left, thumb.HorizontalAlignment);
                AssertContrast(thumb.Background, track.Background, 3);
            }
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void SelectedSidebarItemKeepsItsVisibleSelectionMarker(string theme)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            window.NavList.SelectedIndex = 3;
            var root = Assert.IsType<Grid>(window.Content);
            root.Measure(new Size(1200, 800));
            root.Arrange(new Rect(0, 0, 1200, 800));
            root.UpdateLayout();
            var item = Assert.IsType<ListBoxItem>(window.FindName("TranslationServicesNavItem"));
            item.ApplyTemplate();
            var marker = Assert.IsType<Border>(item.Template.FindName("SelectionMarker", item));
            var surface = Assert.IsType<Border>(item.Template.FindName("ItemSurface", item));
            AssertContrast(marker.Background, surface.Background, 3);
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void NotStartedGamePromptUsesNeutralSurfaceInsteadOfWarningPanel(string theme)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            window.NavList.SelectedIndex = 1;
            var prompt = Assert.IsType<Border>(window.FindName("ScanEmptyState"));
            var title = Assert.IsType<TextBlock>(window.FindName("ScanEmptyTitle"));
            Assert.Equal("还没有启动游戏", title.Text);
            Assert.Equal(ColorOf((Brush)window.FindResource("BgSubtle")), ColorOf(prompt.Background));
            Assert.Equal(ColorOf((Brush)window.FindResource("BorderSoft")), ColorOf(prompt.BorderBrush));
            Assert.Equal(ColorOf((Brush)window.FindResource("TextPrimary")), ColorOf(title.Foreground));
            AssertReadable(title.Foreground, prompt.Background);
            AssertReadable(window.ScanEmptyAdvice.Foreground, prompt.Background);
            Assert.True(window.ScanGoToGameButton.IsEnabled);
        });
    }

    [Theory]
    [InlineData("Light", "#FFFFFF", "#F8F9FA", "#0B57D0")]
    [InlineData("Dark", "#1B1C1E", "#202124", "#0B57D0")]
    public void ReferenceAppearanceRecolorsActualWorkspaceAndReadablePrimaryActions(
        string theme, string canvas, string sidebar, string accent)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            var workspace = Assert.IsType<Grid>(window.Content);
            var navigation = Assert.IsType<Border>(window.FindName("NavigationSidebar"));
            Assert.Equal((Color)ColorConverter.ConvertFromString(canvas), ColorOf(workspace.Background));
            Assert.Equal((Color)ColorConverter.ConvertFromString(sidebar), ColorOf(navigation.Background));

            window.NavList.SelectedIndex = 1;
            Assert.IsType<RadioButton>(window.FindName("ApiSettingsButton")).IsChecked = true;
            var save = Assert.IsType<Button>(window.FindName("SaveTranslationSettingsButton"));
            save.ApplyTemplate();
            Assert.True(save.IsEnabled);
            Assert.Equal((Color)ColorConverter.ConvertFromString(accent), ColorOf(save.Background));
            AssertReadable(save.Foreground, save.Background);
            AssertReadable(window.EndpointBox.Foreground, window.EndpointBox.Background);
            AssertReadable(window.ScanResultList.Foreground, window.ScanResultList.Background);
            foreach (var text in new[] { "TextPrimary", "TextSecondary", "TextMuted" })
            foreach (var surface in new[] { "BgWindow", "BgSidebar", "BgCard", "BgSubtle", "BgHover", "BgSelected" })
                AssertReadable((Brush)window.FindResource(text), (Brush)window.FindResource(surface));
            foreach (var state in new[] { "Success", "Warning", "Danger" })
            {
                AssertReadable((Brush)window.FindResource(state), workspace.Background);
                AssertReadable((Brush)window.FindResource(state), (Brush)window.FindResource(state + "Soft"));
            }
        });
    }

    [Fact]
    public void DefaultThemeAlreadyRendersReferenceDaySurfacesBeforeRuntimeOverrides()
    {
        OnWindow(window =>
        {
            var preview = new Grid();
            preview.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/UnityTranslator.App;component/Theme.xaml")
            });
            var action = new Button { Style = (Style)preview.FindResource("PrimaryButton"), Content = "保存" };
            var card = new Border { Style = (Style)preview.FindResource("Card"), Child = action };
            preview.Children.Add(card);
            preview.Measure(new Size(300, 100));
            preview.Arrange(new Rect(0, 0, 300, 100));
            preview.UpdateLayout();

            Assert.Equal((Color)ColorConverter.ConvertFromString("#0B57D0"), ColorOf(action.Background));
            Assert.Equal(Colors.White, ColorOf(action.Foreground));
            Assert.Equal(Colors.White, ColorOf(card.Background));
            AssertReadable(action.Foreground, action.Background);
        });
    }

    [Fact]
    public void ChangingThemeRecolorsExistingInputGridAndPopup()
    {
        OnWindow(window =>
        {
            var chooser = ThemeChooser(window);
            window.WorkspaceTabs.SelectedIndex = 1;
            var input = window.EndpointBox;
            var grid = window.ScanResultList;
            var originalInput = ColorOf(input.Background);
            var originalGrid = ColorOf(grid.Background);
            chooser.ApplyTemplate();
            var popup = Assert.IsType<Popup>(chooser.Template.FindName("PART_Popup", chooser));
            var popupBorder = Assert.IsType<Border>(popup.Child);

            Choose(chooser, "Dark");

            Assert.Same(input, window.EndpointBox);
            Assert.Same(grid, window.ScanResultList);
            Assert.NotEqual(originalInput, ColorOf(input.Background));
            Assert.NotEqual(originalGrid, ColorOf(grid.Background));
            Assert.Equal((Color)ColorConverter.ConvertFromString("#292A2D"), ColorOf(input.Background));
            Assert.Equal((Color)ColorConverter.ConvertFromString("#202124"), ColorOf(grid.Background));
            Assert.Equal((Color)ColorConverter.ConvertFromString("#292A2D"), ColorOf(popupBorder.Background));
            AssertReadable(input.Foreground, input.Background);
            AssertReadable(grid.Foreground, grid.Background);
            AssertReadable(window.ConfirmTitle.Foreground, (Brush)window.FindResource("BgCard"));

            Choose(chooser, "Light");

            Assert.Equal(originalInput, ColorOf(input.Background));
            Assert.Equal(originalGrid, ColorOf(grid.Background));
        });
    }

    [Fact]
    public void ThemeSelectionIsSavedImmediatelyAndRestoredWithApiConfiguration()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        new AppSettings(settingsPath, new DpapiSecretProtector()).Save("https://example.com/chat", "remembered-model", "remembered-key");
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), "Dark");
            var savedWhileOpen = new AppSettings(settingsPath, new DpapiSecretProtector());
            savedWhileOpen.Load();
            Assert.Equal("Dark", savedWhileOpen.Theme.ToString());
            Assert.Equal("remembered-key", savedWhileOpen.ApiKey);
        });

        OnWindow(window =>
        {
            Assert.Equal("Dark", ((ComboBoxItem)ThemeChooser(window).SelectedItem).Tag);
            Assert.Equal("https://example.com/chat", window.EndpointBox.Text);
            Assert.Equal("remembered-model", window.ModelBox.Text);
            Assert.Equal("remembered-key", window.ApiKeyBox.Password);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#292A2D"), ColorOf(window.EndpointBox.Background));
            AssertReadable(window.EndpointBox.Foreground, window.EndpointBox.Background);
        });
    }

    [Fact]
    public void SwitchingAppearancePreservesSessionResultsSelectionFreezeAndUnsubmittedInput()
    {
        OnWindow(window =>
        {
            var memory = new ThemeMemory();
            var game = new GameProfile("theme-game", _root, Path.Combine(_root, "Game.exe"), "2022.3", "Mono", "x64", true, "");
            window.StartCheatSessionForTest(game, memory);
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(53)));
            window.SelectFirstHitForTest();
            window.ScanWriteValueBox.Text = "53";
            window.ScanFreezeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.ScanValueBox.Text = "unsubmitted search";
            window.ScanWriteValueBox.Text = "unsubmitted replacement";
            window.EndpointBox.Text = "unsubmitted endpoint";
            window.ApiKeyBox.Password = "unsubmitted key";
            window.CorrectionBox.Text = "unsubmitted correction";
            window.NavList.SelectedIndex = 1;
            var session = window.CheatSessionForTest();
            var selected = window.ScanResultList.SelectedItem;
            var freezeLabel = window.ScanFreezeButton.Content;

            Choose(ThemeChooser(window), "Dark");
            Choose(ThemeChooser(window), "Light");

            Assert.Same(session, window.CheatSessionForTest());
            Assert.Same(game, window.ActiveGameForTest());
            Assert.Equal(1, window.HitCountForTest());
            Assert.Same(selected, window.ScanResultList.SelectedItem);
            Assert.Equal("停止锁定", freezeLabel);
            Assert.Equal(freezeLabel, window.ScanFreezeButton.Content);
            Assert.Equal("unsubmitted search", window.ScanValueBox.Text);
            Assert.Equal("unsubmitted replacement", window.ScanWriteValueBox.Text);
            Assert.Equal("unsubmitted endpoint", window.EndpointBox.Text);
            Assert.Equal("unsubmitted key", window.ApiKeyBox.Password);
            Assert.Equal("unsubmitted correction", window.CorrectionBox.Text);
            Assert.Equal(1, window.NavList.SelectedIndex);
            var saved = new AppSettings(Path.Combine(_root, "settings.json"), new DpapiSecretProtector());
            saved.Load();
            Assert.Equal(string.Empty, saved.ApiKey);
            Assert.Equal("https://api.deepseek.com/chat/completions", saved.Endpoint);
        });
    }

    [Fact]
    public void SystemModeUsesWindowsPreferenceAndHandlesBackgroundNotifications()
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), "System");
            Assert.Equal(WindowsUsesDarkMode(), IsDark((Brush)window.FindResource("BgWindow")));
            window.Resources["BgWindow"] = Brushes.Magenta;

            Exception? failure = null;
            var notificationThread = new Thread(() => failure = Record.Exception(() => NotifySystemThemeChanged(window)))
            {
                IsBackground = true
            };
            notificationThread.Start();
            Assert.True(notificationThread.Join(TimeSpan.FromSeconds(5)), "The Windows preference notification did not return.");
            Assert.Null(failure);
            DrainDispatcher();

            Assert.Equal(WindowsUsesDarkMode(), IsDark((Brush)window.FindResource("BgWindow")));
            Assert.NotEqual(Colors.Magenta, ColorOf((Brush)window.FindResource("BgWindow")));
        });
    }

    [Fact]
    public void UnsavedSystemSelectionStillRespondsToWindowsPreferenceNotifications()
    {
        OnWindow(window =>
        {
            var chooser = ThemeChooser(window);
            Choose(chooser, "Dark");
            var path = Path.Combine(_root, "settings.json");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Choose(chooser, "System");
            }
            var saved = new AppSettings(path, new DpapiSecretProtector());
            saved.Load();
            Assert.Equal(AppTheme.Dark, saved.Theme);
            Assert.Equal("System", ((ComboBoxItem)chooser.SelectedItem).Tag);
            window.Resources["BgWindow"] = Brushes.Magenta;

            NotifySystemThemeChanged(window);
            DrainDispatcher();

            Assert.NotEqual(Colors.Magenta, ColorOf((Brush)window.FindResource("BgWindow")));
            Assert.Equal(WindowsUsesDarkMode(), IsDark((Brush)window.FindResource("BgWindow")));
            Assert.Contains("未能记住", File.ReadAllText(Path.Combine(_root, "app.log")));
        });
    }

    [Fact]
    public void SystemNotificationsDoNotOverrideAnExplicitSelectionOrAClosedWindow()
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), "Dark");
            var selectedBackground = ColorOf((Brush)window.FindResource("BgWindow"));
            NotifySystemThemeChanged(window);
            DrainDispatcher();
            Assert.Equal(selectedBackground, ColorOf((Brush)window.FindResource("BgWindow")));

            Choose(ThemeChooser(window), "System");
            window.Close();
            window.Resources["BgWindow"] = Brushes.Magenta;
            NotifySystemThemeChanged(window);
            DrainDispatcher();
            Assert.Equal(Colors.Magenta, ColorOf((Brush)window.FindResource("BgWindow")));
        });
    }

    private static ComboBox ThemeChooser(MainWindow window) => Assert.IsType<ComboBox>(window.FindName("ThemeModeBox"));

    [Theory]
    [InlineData("Light", "Dark", "黑夜", "\uE708", 8)]
    [InlineData("Dark", "Light", "白天", "\uE706", 8)]
    [InlineData("Dark", "System", "跟随系统", "\uE770", 7)]
    [InlineData("System", "Dark", "黑夜", "\uE708", 8)]
    [InlineData("Light", "System", "跟随系统", "\uE770", 7)]
    public void ThemeSelectionShowsItsCompleteLabelOnTheFirstHomeFrame(string from, string to, string label, string glyph, int gap)
    {
        OnWindow(window =>
        {
            var chooser = ThemeChooser(window);
            Choose(chooser, from);
            var root = Assert.IsType<Grid>(window.Content);
            root.Measure(new Size(1200, 800));
            root.Arrange(new Rect(0, 0, 1200, 800));
            root.UpdateLayout();
            DrainDispatcher();
            Choose(chooser, to);
            root.UpdateLayout();
            DrainDispatcher();
            Assert.Equal(0, window.NavList.SelectedIndex);
            var site = Assert.IsType<ContentPresenter>(chooser.Template.FindName("ContentSite", chooser));
            Assert.True(site.ActualWidth > 0 && site.ActualHeight > 0, "The first home frame must draw the selected theme without a navigation or refresh.");
            var drawnContent = chooser.SelectionBoxItem is System.Windows.Shapes.Rectangle { Fill: VisualBrush { Visual: Visual visual } }
                ? visual : site;
            var labels = VisualDescendants(drawnContent).OfType<TextBlock>().ToArray();
            var title = Assert.Single(labels.Where(text => text.Text == label));
            var icon = Assert.Single(labels.Where(text => text.Text == glyph));
            Assert.True(title.ActualWidth > 0 && icon.ActualWidth > 0);
            Assert.True(site.ActualWidth + 1 / VisualTreeHelper.GetDpi(site).DpiScaleX >= title.ActualWidth + icon.ActualWidth + gap,
                $"Visible selection width={site.ActualWidth}; text={title.ActualWidth}; icon={icon.ActualWidth}; gap={gap}");
            AssertReadable(title.Foreground, chooser.Background);
            Assert.Equal(gap, icon.Margin.Right);
        });
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    [InlineData("Dark", true)]
    public void HoveredPreferenceSwitchKeepsCompositedIndicatorContrast(string theme, bool enabled)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            SelectOcrCapableGame(window);
            foreach (var toggle in new[] { window.TranslationEnabledBox, window.EnableOcrBox })
            {
                toggle.IsChecked = enabled;
                toggle.ApplyTemplate();
                ForceState(toggle, "IsMouseOver", true);
                Assert.True(toggle.IsMouseOver);
                var track = Assert.IsType<Border>(toggle.Template.FindName("SwitchTrack", toggle));
                var thumb = Assert.IsType<Border>(toggle.Template.FindName("SwitchThumb", toggle));
                var backdrop = ColorOf((Brush)window.FindResource("BgWindow"));
                var visibleTrack = Composite(ColorOf(track.Background), backdrop, track.Opacity);
                var visibleThumb = Composite(ColorOf(thumb.Background), backdrop, track.Opacity * thumb.Opacity);
                AssertContrast(new SolidColorBrush(visibleThumb), new SolidColorBrush(visibleTrack), 3);
            }
        });
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    [InlineData("Dark", true)]
    public void PageKeyboardFocusContrastsWithItsSurfaceWhileInputFocusStaysReadable(string theme, bool selected)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            var workspace = Assert.IsType<Grid>(window.Content);
            workspace.Measure(new Size(1200, 800));
            workspace.Arrange(new Rect(0, 0, 1200, 800));
            workspace.UpdateLayout();
            var nav = Assert.IsType<ListBoxItem>(window.FindName("TranslationServicesNavItem"));
            nav.IsSelected = selected;
            nav.ApplyTemplate();
            ForceState(nav, "IsKeyboardFocused", true);
            Assert.True(nav.IsKeyboardFocused);
            nav.ApplyTemplate();
            var surface = Assert.IsType<Border>(nav.Template.FindName("ItemSurface", nav));
            AssertContrast(surface.BorderBrush, selected ? surface.Background : (Brush)window.FindResource("BgSidebar"), 3);
            var mode = Assert.IsType<RadioButton>(window.FindName("ApiSettingsButton"));
            mode.IsChecked = selected;
            mode.ApplyTemplate();
            ForceState(mode, "IsKeyboardFocused", true);
            var segment = Assert.IsType<Border>(mode.Template.FindName("Segment", mode));
            AssertContrast(segment.BorderBrush, selected ? segment.Background : (Brush)window.FindResource("BgWindow"), 3);
            window.EndpointBox.ApplyTemplate();
            ForceState(window.EndpointBox, "IsKeyboardFocused", true);
            var input = Assert.IsType<Border>(window.EndpointBox.Template.FindName("Bd", window.EndpointBox));
            AssertContrast(input.BorderBrush, input.Background, 3);
            foreach (var button in LogicalDescendants(window).OfType<Button>().Where(button => button.IsEnabled))
            {
                button.ApplyTemplate();
                ForceState(button, "IsKeyboardFocused", true);
                if (button.Template?.FindName("Bd", button) is Border buttonSurface)
                {
                    var background = ColorOf(buttonSurface.Background).A == 0
                        ? (Brush)window.FindResource("BgWindow") : buttonSurface.Background;
                    try { AssertContrast(buttonSurface.BorderBrush, background, 3); }
                    catch (Exception ex) { throw new InvalidOperationException($"Focus on {button.Name}/{button.Content}: {ColorOf(buttonSurface.BorderBrush)} over {ColorOf(background)}", ex); }
                }
            }
        });
    }

    private static void ForceState(UIElement element, string name, bool state)
    {
        // WPF's read-only input getters consult device/cache state instead of the DP local value.
        // Set those test-only states so the real template triggers execute without a shown window.
        if (name == "IsKeyboardFocused")
        {
            var focus = typeof(System.Windows.Input.KeyboardDevice).GetField("_focus", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(focus);
            focus!.SetValue(System.Windows.Input.Keyboard.PrimaryDevice, state ? element : null);
        }
        else if (name == "IsMouseOver" || name == "IsKeyboardFocusWithin")
        {
            var write = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(write);
            write!.Invoke(element, [Enum.Parse(write.GetParameters()[0].ParameterType, name + "Cache"), state]);
        }
        var owner = name == "IsPressed" ? typeof(ButtonBase) : typeof(UIElement);
        var field = owner.GetField(name + "PropertyKey", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        element.SetValue(Assert.IsType<DependencyPropertyKey>(field!.GetValue(null)), state);
    }

    private static Color Composite(Color foreground, Color background, double opacity)
    {
        var alpha = foreground.A / 255d * opacity;
        return Color.FromRgb((byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void EveryEnabledDarkButtonKeepsCompositedTextAndFocusReadable(bool hover, bool pressed, bool focused)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), "Dark");
            var buttons = LogicalDescendants(window).OfType<Button>().ToArray();
            Assert.True(buttons.Length > 20);
            foreach (var button in buttons)
            {
                button.IsEnabled = true;
                button.ApplyTemplate();
                button.Measure(new Size(500, 100));
                ForceState(button, "IsMouseOver", hover);
                ForceState(button, "IsPressed", pressed);
                ForceState(button, "IsKeyboardFocused", focused);
                // Foreground changes propagate to icon/content bindings on the
                // dispatcher. Inspect the same settled frame that is rendered.
                button.Arrange(new Rect(new Point(), button.DesiredSize));
                button.UpdateLayout();
                DrainDispatcher();
                Assert.Equal(hover, button.IsMouseOver);
                Assert.Equal(pressed, button.IsPressed);
                var surface = Assert.IsType<Border>(button.Template.FindName("Bd", button));
                var backdrop = ButtonBackdrop(button, window);
                var background = Composite(ColorOf(surface.Background), backdrop, surface.Opacity);
                try
                {
                    AssertReadable(new SolidColorBrush(Composite(ColorOf(button.Foreground), backdrop, surface.Opacity)), new SolidColorBrush(background));
                    foreach (var text in VisualDescendants(surface).OfType<TextBlock>())
                        AssertContrast(new SolidColorBrush(Composite(ColorOf(text.Foreground), backdrop, surface.Opacity)),
                            new SolidColorBrush(background), text.FontFamily.Source == "Segoe MDL2 Assets" ? 3 : 4.5);
                    if (focused)
                        AssertContrast(new SolidColorBrush(Composite(ColorOf(surface.BorderBrush), backdrop, surface.Opacity)), new SolidColorBrush(background), 3);
                }
                catch (Exception ex) { throw new InvalidOperationException($"Button {button.Name}/{button.Content}: hover={hover}, pressed={pressed}, focus={focused}, surface opacity={surface.Opacity}", ex); }
            }
        });
    }

    [Theory]
    [InlineData(false, false, 1d)]
    [InlineData(true, false, 1d)]
    [InlineData(false, true, 1d)]
    [InlineData(true, true, 1d)]
    public void LightLibraryAndPageFeedbackKeepOpaqueLabelsInAllInteractionStates(bool hover, bool pressed, double opacity)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), "Dark");
            Choose(ThemeChooser(window), "Light");
            foreach (var button in LogicalDescendants(window).OfType<Button>())
            {
                button.IsEnabled = true;
                button.ApplyTemplate();
                ForceState(button, "IsMouseOver", hover);
                ForceState(button, "IsPressed", pressed);
                var surface = Assert.IsType<Border>(button.Template.FindName("Bd", button));
                Assert.Equal(opacity, surface.Opacity);
            }
        });
    }

    private static Color ButtonBackdrop(Button button, MainWindow window)
    {
        DependencyObject? parent = button;
        while ((parent = (parent is Visual ? VisualTreeHelper.GetParent(parent) : null) ?? LogicalTreeHelper.GetParent(parent)) != null)
        {
            var brush = parent switch { Border border => border.Background, Panel panel => panel.Background, Control control => control.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A == 255) return solid.Color;
        }
        return ColorOf((Brush)window.FindResource("BgWindow"));
    }

    // Catches global dark foregrounds leaking into silver fields, including off-page controls.
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void ActualTextAndPasswordFieldsKeepReadableTextAndCaretsAcrossPages(string theme)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            var fields = LogicalDescendants(window).OfType<Control>()
                .Where(control => control is TextBox or PasswordBox).ToArray();
            Assert.True(fields.Length >= 10, "All existing pages must participate.");
            foreach (var field in fields)
            {
                field.ApplyTemplate();
                AssertReadable(field.Foreground, field.Background);
                var caret = field is TextBox text ? text.CaretBrush : ((PasswordBox)field).CaretBrush;
                AssertContrast(caret ?? field.Foreground, field.Background, 3);
                if (field is TextBox selectedText) AssertReadable(selectedText.Foreground, selectedText.SelectionBrush);
                if (theme == "Dark") Assert.Equal(ColorOf((Brush)window.FindResource("TextPrimary")), ColorOf(window.ConfirmTitle.Foreground));
                if (theme == "Dark") Assert.Equal((Color)ColorConverter.ConvertFromString("#292A2D"), ColorOf(field.Background));
            }
        });
    }

    // Catches selected UIElement clones retaining a white popup foreground on a silver closed picker.
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void ActualPickersKeepSelectedLabelsArrowsAndPopupItemsReadable(string theme)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            var pickers = LogicalDescendants(window).OfType<ComboBox>().ToArray();
            Assert.True(pickers.Length >= 5);
            foreach (var picker in pickers)
            {
                picker.ApplyTemplate();
                AssertReadable(picker.Foreground, picker.Background);
                var toggle = Assert.IsType<ToggleButton>(picker.Template.FindName("Toggle", picker));
                toggle.ApplyTemplate();
                foreach (var arrow in VisualDescendants(toggle).OfType<System.Windows.Shapes.Path>())
                    AssertContrast(arrow.Stroke, picker.Background, 3);
                var popup = Assert.IsType<Popup>(picker.Template.FindName("PART_Popup", picker));
                var surface = Assert.IsType<Border>(popup.Child);
                foreach (var item in picker.Items.OfType<ComboBoxItem>())
                {
                    // These windows/popups are intentionally never shown on the user's desktop.
                    // Connect the detached item to the same live resources as the opened popup.
                    item.Resources.MergedDictionaries.Add(window.Resources);
                    item.Style ??= picker.ItemContainerStyle;
                    item.ApplyTemplate();
                    var border = Assert.IsType<Border>(item.Template.FindName("Bd", item));
                    foreach (var selected in new[] { false, true })
                    {
                        if (picker != ThemeChooser(window)) item.IsSelected = selected;
                        var background = ColorOf(border.Background).A == 0 ? surface.Background : border.Background;
                        try { AssertReadable(item.Foreground, background); }
                        catch (Exception ex) { throw new InvalidOperationException($"Picker item {picker.Name}: '{item.Content}', foreground {ColorOf(item.Foreground)}, background {ColorOf(background)}, selected={selected}.", ex); }
                        if (item.Content is DependencyObject content)
                            foreach (var text in LogicalDescendants(content).OfType<TextBlock>())
                                if (text.FontFamily.Source != "Segoe MDL2 Assets")
                                {
                                    try { AssertReadable(text.Foreground, background); }
                                    catch (Exception ex) { throw new InvalidOperationException($"Picker {picker.Name}, label '{text.Text}', foreground {ColorOf(text.Foreground)}, background {ColorOf(background)}, selected={selected}.", ex); }
                                }
                                else if (theme == "Dark") AssertContrast(text.Foreground, background, 3);
                    }
                }
            }
        });
    }

    // Catches body headings becoming black again while inverse controls stay legible.
    [Fact]
    public void DarkBodyAndDropInstructionsStayReadableOnNeutralSurfaces()
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), "Dark");
            Assert.Equal(ColorOf((Brush)window.FindResource("TextPrimary")), ColorOf(window.ConfirmTitle.Foreground));
            AssertReadable(window.ConfirmTitle.Foreground, (Brush)window.FindResource("BgCard"));
            var drop = Assert.IsType<Border>(window.FindName("DropZone"));
            foreach (var text in LogicalDescendants(window.DropHintPanel).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible))
                AssertReadable(text.Foreground, drop.Background);
        });
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    [InlineData("Dark", true)]
    public void ActualGridCellsHeadersAndNavigationStayReadableWhenSelected(string theme, bool selected)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            window.NavList.SelectedIndex = 1;
            var game = new GameProfile("theme-grid", _root, Path.Combine(_root, "Game.exe"), "2022.3", "Mono", "x64", true, "");
            window.StartCheatSessionForTest(game, new ThemeMemory());
            window.SeedHitForTest(new ScanHit(0x1000, NumericKind.Int32, BitConverter.GetBytes(53)));
            var workspace = Assert.IsType<Grid>(window.Content);
            workspace.Measure(new Size(1200, 800));
            workspace.Arrange(new Rect(0, 0, 1200, 800));
            workspace.UpdateLayout();
            var grid = window.ScanResultList;
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
            row.IsSelected = selected;
            row.ApplyTemplate();
            row.UpdateLayout();
            var cells = VisualDescendants(row).OfType<DataGridCell>().ToArray();
            Assert.NotEmpty(cells);
            foreach (var cell in cells)
            {
                AssertReadable(cell.Foreground, row.Background);
                foreach (var label in VisualDescendants(cell).OfType<TextBlock>())
                {
                    AssertReadable(label.Foreground, row.Background);
                    if (theme == "Dark") Assert.Equal(ColorOf((Brush)window.FindResource("TextPrimary")), ColorOf(label.Foreground));
                }
            }
            var headers = VisualDescendants(grid).OfType<DataGridColumnHeader>().Where(h => h.Content != null).ToArray();
            Assert.NotEmpty(headers);
            foreach (var header in headers) AssertReadable(header.Foreground, header.Background);
            var item = Assert.IsType<ListBoxItem>(window.FindName("TranslationServicesNavItem"));
            item.IsSelected = selected;
            item.ApplyTemplate();
            var surface = Assert.IsType<Border>(item.Template.FindName("ItemSurface", item));
            var background = selected ? surface.Background : (Brush)window.FindResource("BgSidebar");
            AssertReadable(item.Foreground, background);
            if (theme == "Dark") Assert.Equal(ColorOf((Brush)window.FindResource(selected ? "AccentText" : "TextSecondary")), ColorOf(item.Foreground));
        });
    }

    // Catches primary/danger actions fading their text or having no visible pressed state.
    [Theory]
    [InlineData("Light", false)]
    [InlineData("Dark", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", true)]
    public void PrimaryAndDangerActionHoverAndPressStayOpaqueReadableAndVisiblyDistinct(string theme, bool danger)
    {
        OnWindow(window =>
        {
            Choose(ThemeChooser(window), theme);
            var button = danger ? window.ConfirmOkButton : window.ScanStartButton;
            button.IsEnabled = true; button.ApplyTemplate();
            var surface = Assert.IsType<Border>(button.Template.FindName("Bd", button));
            var normal = ColorOf(surface.Background);
            ForceState(button, "IsMouseOver", true);
            var hover = ColorOf(surface.Background);
            Assert.Equal(1d, surface.Opacity);
            Assert.NotEqual(normal, hover);
            AssertReadable(button.Foreground, surface.Background);
            ForceState(button, "IsPressed", true);
            Assert.Equal(1d, surface.Opacity);
            Assert.True(Luminance(ColorOf(surface.Background)) < Luminance(hover));
            AssertReadable(button.Foreground, surface.Background);
            ForceState(button, "IsPressed", false); ForceState(button, "IsMouseOver", false);
            Assert.Equal(normal, ColorOf(surface.Background));
        });
    }

    private sealed class ControlledVisualDpi : IDisposable
    {
        private readonly Grid _root;
        private readonly DpiScale _original;

        internal ControlledVisualDpi(Grid root, double scale)
        {
            _root = root;
            _original = VisualTreeHelper.GetDpi(root);
            // Materialize template visuals first. SetRootDpi alone does not invalidate cached layout.
            Arrange();
            VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
            foreach (var element in VisualDescendants(root).Prepend(root).OfType<UIElement>())
            {
                element.InvalidateMeasure();
                element.InvalidateArrange();
            }
            Arrange();
        }

        private void Arrange()
        {
            _root.Measure(new Size(900, 640));
            _root.Arrange(new Rect(0, 0, 900, 640));
            _root.UpdateLayout();
        }

        public void Dispose() => VisualTreeHelper.SetRootDpi(_root, _original);
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in LogicalDescendants(child)) yield return nested;
        }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in VisualDescendants(child)) yield return nested;
        }
    }

    private static void Choose(ComboBox chooser, string tag) =>
        chooser.SelectedItem = chooser.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, tag));

    private static void SelectOcrCapableGame(MainWindow window)
    {
        var root = Path.Combine(Path.GetTempPath(), "vesper-theme-ocr-context");
        var game = new GameProfile("theme-ocr-context", root, Path.Combine(root, "unused.exe"), "2022.3", "Mono", "x64", true, "");
        window.Games.Add(game);
        window.GameLibraryList.SelectedItem = game;
    }

    private static void NotifySystemThemeChanged(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("OnSystemThemeChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(window, [null, new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)]);
    }

    private static bool WindowsUsesDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static bool IsDark(Brush brush) => Luminance(ColorOf(brush)) < 0.25;
    private static void AssertDark(Brush brush) => Assert.True(IsDark(brush), "The surface should use the dark palette.");

    private static void AssertReadable(Brush foreground, Brush background) => AssertContrast(foreground, background, 4.5);

    private static void AssertContrast(Brush foreground, Brush background, double minimum)
    {
        var first = Luminance(ColorOf(foreground));
        var second = Luminance(ColorOf(background));
        var ratio = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(ratio >= minimum, $"Actual foreground/background contrast {ratio:F2}:1 should be at least {minimum}:1.");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private void OnWindow(Action<MainWindow> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow.PrepareTestDispatcherForTest();
            MainWindow? window = null;
            try { window = new MainWindow(_root, new RecordingExternalFactory { Available = false }); body(window); }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The appearance test did not finish.");
        if (failure is not null) throw new InvalidOperationException("Appearance UI test failed.", failure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ThemeMemory : IProcessMemory
    {
        private int _value = 53;
        public IReadOnlyList<MemoryRegion> Regions() => [new(0x1000, 4, true, true)];
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address != 0x1000 || destination.Length != 4) return false;
            BitConverter.GetBytes(_value).CopyTo(destination);
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address != 0x1000 || source.Length != 4) return false;
            _value = BitConverter.ToInt32(source);
            return true;
        }
        public bool IsAlive => true;
        public int ProcessId => 41;
        public long StartedUtcTicks => 42;
    }
}
