using System.Windows;
using System.Windows.Controls;

namespace UnityTranslator.App;

public partial class MainWindow
{
    private void OnOpenApiSettings(object sender, RoutedEventArgs e)
    {
        OpenTranslationServicesPage();
    }

    private void OpenTranslationServicesPage()
    {
        NavList.SelectedItem = TranslationServicesNavItem;
        WorkspaceTabs.SelectedItem = TranslationServicesTab;
        TranslationServicesScrollViewer.UpdateLayout();
        TranslationServicesScrollViewer.ScrollToTop();
    }

    private void OnNumbersShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || FindName(name) is not FrameworkElement section) return;
        NumbersScrollViewer.UpdateLayout();
        if (NumbersScrollViewer.Content is not FrameworkElement content) return;
        var top = section.TranslatePoint(new Point(0, 0), content).Y;
        NumbersScrollViewer.ScrollToVerticalOffset(Math.Max(0, top - 12));
    }
}
