using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class LibraryTableLayoutTests
{
    // Catches doubled input padding stretching the entire toolbar beyond the source's compact controls.
    [Theory]
    [InlineData(900)]
    [InlineData(1200)]
    public void ToolbarKeepsCompactControlsWithUsableSearchText(double width) => OnUi(window =>
    {
        window.LibrarySearchBox.Text = "cambion";
        Layout(window, width);
        var toolbar = Named<Grid>(window, "LibraryToolbar");
        foreach (var control in Visuals(toolbar).OfType<Control>().Where(c => c is TextBox or Button))
            Assert.InRange(control.ActualHeight, 34, 38);
        var host = Assert.IsType<ScrollViewer>(window.LibrarySearchBox.Template.FindName("PART_ContentHost", window.LibrarySearchBox));
        Assert.True(host.ViewportHeight >= 17, $"Search text viewport too short: {host.ViewportHeight}");
    });

    // Catches independent star columns shifting the technical heading away from the actual cells.
    [Theory]
    [InlineData(900)]
    [InlineData(1200)]
    public void TechnicalHeadingAlignsWithItsRenderedCells(double width) => OnUi(window =>
    {
        var game = Profile("cambion"); window.Games.Add(game); Layout(window, width);
        var header = Visuals(Named<Grid>(window, "LibraryTableHeader")).OfType<TextBlock>()
            .Single(t => t.Text == "引擎 / 后端 / 架构");
        var row = (ListBoxItem)window.GameLibraryList.ItemContainerGenerator.ContainerFromItem(game);
        var technical = Visuals(row).OfType<TextBlock>().Single(t => t.Text.StartsWith("Unity "));
        Assert.InRange(Math.Abs(Bounds(window, header).Left - Bounds(window, technical).Left), 0, 2);
    });

    // Catches full detected versions wrapping into denser rows; the tooltip must retain all metadata.
    [Theory]
    [InlineData(900)]
    [InlineData(1200)]
    public void FullDetectionRemainsOneLineWithAccessibleUnabridgedText(double width) => OnUi(window =>
    {
        var game = Profile("cambion") with { UnityVersion = "2022.3.62f2", Backend = "IL2CPP" };
        window.Games.Add(game); Layout(window, width);
        var row = (ListBoxItem)window.GameLibraryList.ItemContainerGenerator.ContainerFromItem(game);
        var technical = Visuals(row).OfType<TextBlock>().Single(t => t.Text.StartsWith("Unity "));
        Assert.True(technical.ActualHeight <= 20, $"Metadata wrapped to {technical.ActualHeight} DIP");
        Assert.Equal(TextTrimming.CharacterEllipsis, technical.TextTrimming);
        Assert.Contains(game.UnityVersion, technical.ToolTip?.ToString());
        Assert.Contains(game.Backend, technical.ToolTip?.ToString());
        Assert.Contains(game.Architecture, technical.ToolTip?.ToString());
    });

    // Catches navigation text being wider than its content slot in the fixed-width sidebar.
    [Fact]
    public void SidebarLabelsFitTheirVisibleContentSlots() => OnUi(window =>
    {
        Layout(window, 1200);
        foreach (var item in window.NavList.Items.OfType<ListBoxItem>().Where(i => i.Visibility == Visibility.Visible))
        {
            var label = Assert.Single(Visuals(item).OfType<TextBlock>());
            var presenter = Visuals(item).OfType<ContentPresenter>().First();
            AssertInside(Bounds(window, presenter), Bounds(window, label));
        }
    });

    [Fact]
    public void RowMenuHighlightResolvesTheCurrentThemeBrush() => OnUi(window =>
    {
        var game = Profile("cambion"); window.Games.Add(game); Layout(window, 1200);
        var more = RowMore(window, game);
        var menu = more.ContextMenu;
        menu.PlacementTarget = more;
        // Exercise the real item template without depending on the popup's asynchronous container styling.
        menu.Resources["BgPopupHover"] = window.FindResource("BgPopupHover");
        var item = (MenuItem)menu.Items[0];
        item.Style = (Style)window.FindResource("LibraryRowMenuItem");
        item.Measure(new Size(200, 40));
        item.Arrange(new Rect(0, 0, 200, 40));
        item.ApplyTemplate();
        item.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
        Assert.True(item.IsHighlighted);
        var surface = (Border)item.Template.FindName("MenuSurface", item);
        Assert.Equal(((SolidColorBrush)window.FindResource("BgPopupHover")).Color,
            Assert.IsType<SolidColorBrush>(surface.Background).Color);
        Assert.False(menu.IsOpen);
    });

    // Catches restoring the old narrow selector or letting list/toolbar overflow the detail pane.
    [Theory]
    [InlineData("Light", 900)]
    [InlineData("Dark", 900)]
    [InlineData("Light", 1200)]
    [InlineData("Dark", 1200)]
    public void TableIsThePrimaryPaneWithoutHidingTheDetailAction(string theme, double width) => OnUi(window =>
    {
        ChooseTheme(window, theme);
        var game = Profile("cambion"); window.Games.Add(game);
        Layout(window, width);
        var table = Named<FrameworkElement>(window, "LibraryTablePanel");
        var detail = Named<FrameworkElement>(window, "GameDetailPanel");
        var tableBounds = Bounds(window, table); var detailBounds = Bounds(window, detail);
        Assert.True(tableBounds.Width > detailBounds.Width, "游戏库应为主列表，而不是原先窄侧栏");
        Assert.True(detailBounds.Width >= 280);
        Assert.True(tableBounds.Right <= detailBounds.Left + 0.1);
        AssertInside(tableBounds, Bounds(window, window.LibrarySearchBox));
        AssertInside(detailBounds, Bounds(window, window.SelectedGameActionButton));
        Assert.Same(game, window.SelectedGameActionButton.Tag);
    });

    [Fact]
    public void TableRowShowsActualPathAndDetectionRatherThanMockMetadata() => OnUi(window =>
    {
        var game = Profile("真实游戏") with { UnityVersion = "2021.3.31", Backend = "IL2CPP" };
        window.Games.Add(game); Layout(window, 1200);
        var row = Assert.IsType<ListBoxItem>(window.GameLibraryList.ItemContainerGenerator.ContainerFromItem(game));
        var texts = Visuals(row).OfType<TextBlock>().ToArray();
        Assert.Contains(texts, t => t.Text == game.RootPath);
        Assert.Contains(texts, t => t.Text.Contains("2021.3.31") && t.Text.Contains("IL2CPP"));
    });

    [Fact]
    public void RowRenameMenuTargetsItsOwnGameInsteadOfThePreviousSelection() => OnUi(window =>
    {
        var first = Profile("first"); var second = Profile("second");
        window.Games.Add(first); window.Games.Add(second); Layout(window, 1200);
        window.GameLibraryList.SelectedItem = first;
        var more = RowMore(window, second);
        var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        menu.PlacementTarget = more;
        Assert.Equal(second, more.Tag);
        var rename = Assert.IsType<MenuItem>(menu.Items[0]);
        rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(second, window.GameLibraryList.SelectedItem);
        Assert.Equal("second", window.LibraryRenameBox.Text);
        Assert.Equal(Visibility.Visible, window.LibraryRenamePanel.Visibility);
    });

    [Fact]
    public void RowRemoveMenuStillRequiresTheRecordOnlyConfirmation() => OnUi(window =>
    {
        var first = Profile("first"); var second = Profile("second");
        window.Games.Add(first); window.Games.Add(second); Layout(window, 1200);
        window.GameLibraryList.SelectedItem = first;
        var more = RowMore(window, second);
        more.ContextMenu.PlacementTarget = more;
        var remove = Assert.IsType<MenuItem>(more.ContextMenu.Items[2]);
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(second, window.GameLibraryList.SelectedItem);
        Assert.Equal(Visibility.Visible, window.ConfirmOverlay.Visibility);
        Assert.Contains("second", window.ConfirmMessage.Text);
        window.ConfirmCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, window.Games.Count);
    });

    [Fact]
    public void LongTitleAndProviderDraftStayWithinTheRightPane() => OnUi(window =>
    {
        window.Games.Add(Profile(new string('游', 80)));
        window.ApiSettingsButton.IsChecked = true;
        window.EndpointBox.Text = "https://a-very-long-provider-name.example.invalid/chat/completions";
        window.WorkspaceTabs.SelectedIndex = 0;
        Layout(window, 900);
        var detail = Bounds(window, Named<FrameworkElement>(window, "GameDetailPanel"));
        AssertInside(detail, Bounds(window, window.SelectedGameTitle));
        AssertInside(detail, Bounds(window, window.TranslationProviderSummary));
        Assert.True(window.TranslationProviderSummary.TextWrapping == TextWrapping.Wrap);
        Assert.Equal(TextTrimming.CharacterEllipsis, window.SelectedGameTitle.TextTrimming);
    });

    private static Button RowMore(MainWindow window, GameProfile game)
    {
        var row = (ListBoxItem)window.GameLibraryList.ItemContainerGenerator.ContainerFromItem(game);
        return Assert.Single(Visuals(row).OfType<Button>().Where(b => b.ContextMenu is not null));
    }
    private static void AssertInside(Rect container, Rect child)
    {
        Assert.True(child.Width > 0 && child.Height > 0);
        Assert.True(child.Left >= container.Left - 1 && child.Right <= container.Right + 1,
            $"控件超出所属面板：container={container}, child={child}");
    }
    private static Rect Bounds(MainWindow window, FrameworkElement element) => element.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(element.RenderSize));
    private static T Named<T>(MainWindow window, string name) where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Visuals(child)) yield return nested;
        }
    }
    private static void Layout(MainWindow window, double width)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, 800)); content.Arrange(new Rect(0, 0, width, 800)); content.UpdateLayout();
    }
    private static void ChooseTheme(MainWindow window, string theme)
    {
        var box = window.ThemeModeBox;
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, theme));
    }
    private static GameProfile Profile(string name) => new(name, Path.Combine(Path.GetTempPath(), "vesper-table-" + name),
        Path.Combine(Path.GetTempPath(), name + ".exe"), "2022.3", "Mono", "x64", true, "");
    private static void OnUi(Action<MainWindow> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "vesper-table-ui-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow.PrepareTestDispatcherForTest(); var window = new MainWindow(root, new RecordingExternalFactory { Available = false });
            window.MessageBoxHandler = _ => MessageBoxResult.None;
            window.RunningProcessesForTest = _ => [];
            try { body(window); } catch (Exception ex) { failure = ex; } finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
