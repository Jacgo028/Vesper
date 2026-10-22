using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public partial class MainWindow
{
    private bool _appearanceInitialized;
    private bool _appearanceSubscribed;
    private volatile bool _appearanceDisposed;
    private AppTheme _selectedAppearance;

    private static readonly (string Key, string Light, string Dark)[] AppearancePalette =
    [
        ("BgWindow", "#FFFFFF", "#1B1C1E"),
        ("BgSidebar", "#F8F9FA", "#202124"),
        ("BgCard", "#FFFFFF", "#202124"),
        ("BgSubtle", "#F8F9FA", "#292A2D"),
        ("BgInput", "#FFFFFF", "#292A2D"),
        ("BgPopup", "#FFFFFF", "#292A2D"),
        ("BgPopupHover", "#F1F3F4", "#3C4043"),
        ("BgPopupSelected", "#E8F0FE", "#253B57"),
        ("LibraryActionHover", "#E8F0FE", "#303F54"),
        ("LibraryActionPressed", "#D2E3FC", "#223147"),
        ("LibraryRemoveHover", "#B3261E", "#B3261E"),
        ("LibraryRemovePressed", "#8C1D18", "#8C1D18"),
        ("BgHeader", "#F8F9FA", "#202124"),
        ("BgHover", "#F1F3F4", "#303134"),
        ("BgSelected", "#E8F0FE", "#253B57"),
        ("BorderSoft", "#E0E3E7", "#3C4043"),
        ("BorderStrong", "#DADCE0", "#5F6368"),
        ("BorderAccent", "#185ABC", "#A8C7FA"),
        ("PageFocus", "#185ABC", "#A8C7FA"),
        ("SwitchThumbOff", "#FFFFFF", "#DADCE0"),
        ("SwitchTrackOff", "#68717A", "#5F6368"),
        ("TextPrimary", "#202124", "#F1F3F4"),
        ("TextSecondary", "#4F555B", "#C2C7CE"),
        ("TextMuted", "#5F6368", "#B0B6BE"),
        ("TextOnInput", "#202124", "#F1F3F4"),
        ("TextOnInputSecondary", "#4F555B", "#C2C7CE"),
        ("TextOnInputMuted", "#5F6368", "#B0B6BE"),
        ("InputIcon", "#5F6368", "#C2C7CE"),
        ("TextOnAccent", "#FFFFFF", "#FFFFFF"),
        ("Accent", "#0B57D0", "#0B57D0"),
        ("AccentHover", "#0842A0", "#0842A0"),
        ("AccentPressed", "#062E6F", "#062E6F"),
        ("AccentSoft", "#E8F0FE", "#253B57"),
        ("AccentText", "#185ABC", "#A8C7FA"),
        ("Success", "#137333", "#81C995"),
        ("SuccessSoft", "#E6F4EA", "#1C3327"),
        ("SuccessBorder", "#B7D9C1", "#456A55"),
        ("Warning", "#8A4F00", "#F7C66E"),
        ("WarningSoft", "#FEF7E0", "#382C17"),
        ("WarningBorder", "#E8D8AB", "#75623A"),
        ("Danger", "#B3261E", "#F2B8B5"),
        ("DangerSoft", "#FCE8E6", "#472729"),
        ("DangerBorder", "#E7BFBB", "#8A4B4E"),
        ("OverlayScrim", "#66202124", "#BB000000")
    ];

    private void InitializeAppearance()
    {
        _appearanceInitialized = false;
        _selectedAppearance = _settings.Theme;
        ThemeModeBox.SelectedItem = ThemeModeBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _settings.Theme.ToString(), StringComparison.Ordinal));
        ApplyAppearance(_settings.Theme);
        _appearanceInitialized = true;
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        _appearanceSubscribed = true;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceInitialized || _appearanceDisposed ||
            ThemeModeBox.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<AppTheme>(item.Tag?.ToString(), out var theme) || !Enum.IsDefined(theme)) return;

        _selectedAppearance = theme;
        ApplyAppearance(theme);
        try
        {
            _settings.SaveTheme(theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("外观已经切换，但未能记住这次选择：" + ex.Message);
            SetRunStatus("外观已经切换，但未能记住这次选择。", RunState.Warning,
                "本次使用不受影响；下次打开可能恢复原来的外观。", ex.Message);
        }
    }

    private void ApplyAppearance(AppTheme selected)
    {
        var dark = (selected == AppTheme.System ? ReadWindowsTheme() : selected) == AppTheme.Dark;
        // State fills, rather than whole-control fading, keep white action labels readable.
        Resources["ButtonHoverOpacity"] = 1d;
        Resources["ButtonPressedOpacity"] = 1d;
        foreach (var entry in AppearancePalette)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? entry.Dark : entry.Light);
            Resources[entry.Key + "Color"] = color;
            Resources[entry.Key] = new SolidColorBrush(color);
        }
        OnAppearanceChanged();
    }

    private static AppTheme ReadWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return AppTheme.Light;
        }
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_appearanceDisposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess())
        {
            RefreshSystemAppearance();
            return;
        }
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RefreshSystemAppearance));
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            // Windows can finish delivering a preference notification while the UI is shutting down.
        }
    }

    private void RefreshSystemAppearance()
    {
        if (!_appearanceDisposed && _appearanceInitialized && _selectedAppearance == AppTheme.System)
            ApplyAppearance(AppTheme.System);
    }

    private void DisposeAppearance()
    {
        _appearanceDisposed = true;
        if (!_appearanceSubscribed) return;
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        _appearanceSubscribed = false;
    }

    partial void OnAppearanceChanged();
}
