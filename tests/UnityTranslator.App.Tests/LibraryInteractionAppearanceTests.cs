using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class LibraryInteractionAppearanceTests
{
    // Restoring the platform ContextMenu template reintroduces its pale icon gutter.
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void RowMenuRendersOneUniformSurfaceAcrossTheFormerIconGutter(string theme) => OnUi(window =>
    {
        Setup(window, theme);
        var more = Visuals(window.GameLibraryList).OfType<Button>().Single(b => b.ContextMenu is not null);
        var menu = more.ContextMenu!;
        menu.PlacementTarget = more;
        // Resource lookup through a detached Popup is deferred until it opens. Supply the
        // same live window resources explicitly while keeping every test off the desktop.
        menu.Resources.MergedDictionaries.Add(window.Resources);
        menu.Measure(new Size(210, 200)); menu.Arrange(new Rect(0, 0, 210, menu.DesiredSize.Height)); menu.UpdateLayout();
        var bitmap = Render(menu);
        var expected = ColorOf((Brush)window.FindResource("BgPopup"));
        Assert.Equal(expected, Pixel(bitmap, 8, 12));
        Assert.Equal(expected, Pixel(bitmap, bitmap.PixelWidth - 9, 12));
        Assert.Equal(expected, Pixel(bitmap, 28, bitmap.PixelHeight - 9));
        Assert.False(menu.IsOpen);
    });

    // Missing input triggers leave all these controls visually unchanged in the dark palette.
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void LibraryButtonsVisiblyRespondRestoreAndKeepReadableStableGeometry(string theme) => OnUi(window =>
    {
        Setup(window, theme);
        var controls = Visuals((DependencyObject)window.FindName("LibraryToolbar")).OfType<Button>()
            .Concat(Visuals(window.GameLibraryList).OfType<Button>())
            .Concat(new[] { window.SelectedGameActionButton, window.RenameLibraryGameButton,
                window.OpenLibraryFolderButton, window.RemoveLibraryGameButton }).ToArray();
        Assert.Equal(7, controls.Length);
        foreach (var button in controls)
        {
            button.IsEnabled = true; button.ApplyTemplate();
            var surface = Assert.IsType<Border>(button.Template.FindName("Bd", button));
            var normal = Pixel(Render(button), 7, 7);
            var bounds = button.RenderSize;
            var font = button.FontSize;
            var accessibleName = new ButtonAutomationPeer(button).GetName();
            Assert.False(string.IsNullOrWhiteSpace(accessibleName));
            Force(button, "IsMouseOver", true);
            var hover = Pixel(Render(button), 7, 7);
            Assert.NotEqual(normal, hover);
            AssertReadable(button.Foreground, surface.Background);
            foreach (var text in Visuals(surface).OfType<TextBlock>())
                AssertReadable(text.Foreground, surface.Background);
            Assert.Equal(1d, surface.Opacity);
            var outline = Assert.IsType<Border>(button.Template.FindName("InteractionOutline", button));
            Assert.True(ColorOf(outline.BorderBrush).A > 0);
            Assert.True(outline.BorderThickness.Left >= 1);
            Assert.Equal(Visibility.Visible, outline.Visibility);
            AssertContrast(outline.BorderBrush, surface.Background, 3);
            Force(button, "IsPressed", true);
            var pressed = Pixel(Render(button), 7, 7);
            Assert.NotEqual(hover, pressed);
            Assert.True(Luminance(pressed) < Luminance(hover));
            AssertReadable(button.Foreground, surface.Background);
            Force(button, "IsPressed", false);
            Assert.Equal(hover, Pixel(Render(button), 7, 7));
            Force(button, "IsMouseOver", false);
            Assert.Equal(normal, Pixel(Render(button), 7, 7));
            Force(button, "IsKeyboardFocused", true);
            button.UpdateLayout();
            Assert.True(ColorOf(outline.BorderBrush).A > 0);
            Assert.Equal(Visibility.Visible, outline.Visibility);
            Assert.Equal(bounds, button.RenderSize);
            Assert.Equal(font, button.FontSize);
            Assert.Equal(accessibleName, new ButtonAutomationPeer(button).GetName());
            Assert.True(button.Focusable);
            Force(button, "IsKeyboardFocused", false);
            button.IsEnabled = false;
            var disabled = Pixel(Render(button), 7, 7);
            Force(button, "IsMouseOver", true);
            Force(button, "IsPressed", true);
            Assert.Equal(disabled, Pixel(Render(button), 7, 7));
            Force(button, "IsPressed", false); Force(button, "IsMouseOver", false);
        }
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void RemoveActionsUseReadableRedHighlightAndRestoreTheirOriginalSurface(string theme) => OnUi(window =>
    {
        Setup(window, theme);
        var button = window.RemoveLibraryGameButton;
        Force(button, "IsMouseOver", true); button.ApplyTemplate();
        var surface = Assert.IsType<Border>(button.Template.FindName("Bd", button));
        AssertRed(surface.Background); Assert.Equal(Colors.White, ColorOf(button.Foreground));
        AssertReadable(button.Foreground, surface.Background);
        var menu = Visuals(window.GameLibraryList).OfType<Button>().Single(b => b.ContextMenu is not null).ContextMenu!;
        menu.Resources.MergedDictionaries.Add(window.Resources);
        var item = (MenuItem)menu.Items[2];
        item.Style ??= menu.ItemContainerStyle;
        item.Measure(new Size(200, 50)); item.Arrange(new Rect(0, 0, 200, 50)); item.ApplyTemplate();
        item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        var itemSurface = Assert.IsType<Border>(item.Template.FindName("MenuSurface", item));
        Assert.True(item.Focusable);
        Assert.Equal("移出游戏库", new MenuItemAutomationPeer(item).GetName());
        AssertRed(itemSurface.Background); AssertReadable(item.Foreground, itemSurface.Background);
        var hover = Pixel(Render(item), 7, 7);
        Force(item, "IsPressed", true);
        var pressed = Pixel(Render(item), 7, 7);
        Assert.True(Luminance(pressed) < Luminance(hover), "Pressed remove-menu fill must deepen.");
        AssertRed(itemSurface.Background); AssertReadable(item.Foreground, itemSurface.Background);
        Force(item, "IsPressed", false);
        Assert.Equal(hover, Pixel(Render(item), 7, 7));
        item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        Assert.False(item.IsHighlighted);
        Assert.Equal(0, ColorOf(itemSurface.Background).A);
        Assert.Equal(ColorOf((Brush)window.FindResource("TextOnInput")), ColorOf(item.Foreground));
        item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        item.IsEnabled = false;
        Force(item, "IsPressed", true);
        Assert.False(ColorOf(itemSurface.Background).R > ColorOf(itemSurface.Background).G * 1.5 && ColorOf(itemSurface.Background).A > 0);
        Force(item, "IsPressed", false);
    });

    private static void Setup(MainWindow window, string theme)
    {
        window.ThemeModeBox.SelectedItem = window.ThemeModeBox.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, theme));
        var game = new GameProfile("hover-fixture", Path.Combine(Path.GetTempPath(), "vesper-hover"),
            Path.Combine(Path.GetTempPath(), "vesper-hover.exe"), "2022.3", "Mono", "x64", true, "");
        window.Games.Add(game); window.GameLibraryList.SelectedItem = game;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1200, 800)); content.Arrange(new Rect(0, 0, 1200, 800)); content.UpdateLayout();
    }
    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(drawing); return bitmap;
    }
    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var bytes = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static void AssertRed(Brush brush)
    {
        var color = ColorOf(brush); Assert.True(color.R > color.G * 1.5 && color.A == 255, $"Expected opaque red highlight, got {color}.");
    }
    private static void AssertReadable(Brush foreground, Brush background)
        => AssertContrast(foreground, background, 4.5);
    private static void AssertContrast(Brush foreground, Brush background, double minimum)
    {
        var a = Luminance(ColorOf(foreground)); var b = Luminance(ColorOf(background));
        Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= minimum, $"Unreadable {foreground} on {background}.");
    }
    private static double Luminance(Color c)
    {
        static double Linear(byte n) => n / 255d <= .04045 ? n / 255d / 12.92 : Math.Pow((n / 255d + .055) / 1.055, 2.4);
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }
    private static void Force(UIElement element, string name, bool value)
    {
        if (name == "IsKeyboardFocused")
            typeof(KeyboardDevice).GetField("_focus", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Keyboard.PrimaryDevice, value ? element : null);
        else if (name == "IsMouseOver")
        {
            var write = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.NonPublic | BindingFlags.Instance)!;
            write.Invoke(element, [Enum.Parse(write.GetParameters()[0].ParameterType, name + "Cache"), value]);
        }
        var owner = name == "IsPressed" ? element is MenuItem ? typeof(MenuItem) : typeof(ButtonBase) : typeof(UIElement);
        var key = (DependencyPropertyKey)owner.GetField(name + "PropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        element.SetValue(key, value);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Visuals(child)) yield return nested;
        }
    }
    private static void OnUi(Action<MainWindow> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "vesper-hover-ui-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow.PrepareTestDispatcherForTest(); var window = new MainWindow(root, new RecordingExternalFactory { Available = false });
            window.MessageBoxHandler = _ => MessageBoxResult.None; window.RunningProcessesForTest = _ => [];
            try { body(window); } catch (Exception ex) { failure = ex; } finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
