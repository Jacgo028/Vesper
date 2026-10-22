using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class BrandIdentityTests
{
    [Fact]
    public void WindowAndVisibleHeaderIdentifyTheProductAsVesper()
    {
        OnWindow(window =>
        {
            Assert.Equal("Vesper暮星", window.Title);
            var header = Descendants<TextBlock>(window).Single(text => text.FontSize == 20 && text.FontWeight == FontWeights.SemiBold);
            Assert.Equal("Vesper暮星", header.Text);
            Assert.Equal("Vesper暮星", new TextBlockAutomationPeer(header).GetName());
        });
    }

    [Theory]
    [InlineData(900, 640)]
    [InlineData(1200, 800)]
    public void SelectedLogoLoadsAtBoundedSizeWithinHeader(double width, double height)
    {
        OnWindow(window =>
        {
            var logo = Assert.IsType<Image>(window.FindName("BrandMarkImage"));
            AssertDecodedBitmap(logo.Source);
            Assert.Equal(32, logo.Width);
            Assert.Equal(32, logo.Height);
            Assert.Equal(Stretch.Uniform, logo.Stretch);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            Assert.Equal(32, logo.ActualWidth);
            Assert.Equal(32, logo.ActualHeight);
            var bounds = logo.TransformToAncestor(content).TransformBounds(new Rect(logo.RenderSize));
            Assert.InRange(bounds.Left, 0, 190);
            Assert.InRange(bounds.Right, 0, 212);
            Assert.InRange(bounds.Top, 0, 60);
            Assert.InRange(bounds.Bottom, 0, 60);
            var header = ((Panel)logo.Parent).Children.OfType<TextBlock>().Single();
            Assert.Equal("Vesper暮星", new TextBlockAutomationPeer(header).GetName());
            var wordmarkBounds = header.TransformToAncestor(content).TransformBounds(new Rect(header.RenderSize));
            Assert.InRange(wordmarkBounds.Right, 0, 212);
            Assert.True(wordmarkBounds.Left >= bounds.Right);
        });
    }

    [Fact]
    public void WindowIconLoadsARealBitmapForTheSelectedBrand()
    {
        OnWindow(window => AssertDecodedBitmap(window.Icon));
    }

    private static void AssertDecodedBitmap(ImageSource source)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(source);
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
        var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        Assert.Contains(pixels, value => value != 0);
    }

    [Fact]
    public void CompiledProductMetadataUsesVesperWithoutChangingInternalIdentity()
    {
        var assembly = typeof(MainWindow).Assembly;
        var metadata = FileVersionInfo.GetVersionInfo(assembly.Location);
        Assert.Equal("Vesper暮星", metadata.ProductName);
        Assert.Equal("Vesper暮星", metadata.FileDescription);
        Assert.Equal("Vesper暮星", metadata.CompanyName);
        Assert.Equal("Vesper暮星", metadata.LegalCopyright);
        Assert.Equal("UnityTranslator.App", assembly.GetName().Name);
        Assert.Equal(assembly.GetName().Version, new Version(metadata.FileMajorPart,
            metadata.FileMinorPart, metadata.FileBuildPart, metadata.FilePrivatePart));
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-game.exe")]
    public void LibraryImageUsesFluentGameArtworkWhenExecutableHasNoIcon(string executable)
    {
        OnWindow(window =>
        {
            var game = new GameProfile("missing-icon", Path.GetTempPath(), executable, "2022.3", "Mono", "x64", true, "");
            var row = (FrameworkElement)window.GameLibraryList.ItemTemplate.LoadContent();
            row.DataContext = game;
            ((Panel)window.Content).Children.Add(row);
            row.Measure(new Size(320, 100));
            row.Arrange(new Rect(0, 0, 320, 100));
            row.UpdateLayout();
            var image = Assert.Single(Descendants<Image>(row));
            Assert.Same(window.FindResource("IconGame"), image.Source);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void OnWindow(Action<MainWindow> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ut-brand-ui-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                MainWindow.PrepareTestDispatcherForTest();
                window = new MainWindow(root, new RecordingExternalFactory { Available = false });
                check(window);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (failure is not null) throw failure;
    }
}
