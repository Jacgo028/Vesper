using System.Windows;
using System.Windows.Controls;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public partial class MainWindow
{
    private bool _providerSettingsInitialized;
    private TranslationMode _selectedTranslationMode = TranslationMode.Free;
    private FreeTranslationProvider _selectedFreeTranslationProvider = FreeTranslationProvider.Bing;
    private bool _translationSaveFailed;
    private TranslationMode _failedTranslationMode;
    private FreeTranslationProvider _failedFreeTranslationProvider;

    internal TranslationMode SelectedTranslationMode => _selectedTranslationMode;

    internal FreeTranslationProvider SelectedFreeTranslationProvider => _selectedFreeTranslationProvider;

    private void InitializeProviderSettings()
    {
        _providerSettingsInitialized = false;
        _selectedTranslationMode = _settings.Mode;
        _selectedFreeTranslationProvider = _settings.FreeProvider;
        FreeProviderBox.SelectedItem = FreeProviderBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _settings.FreeProvider.ToString(), StringComparison.Ordinal));
        UpdateTranslationModePresentation();
        _providerSettingsInitialized = true;
        RefreshFreeProviderStatus();
    }

    private void SaveSelectedTranslationSettings()
    {
        try
        {
            if (SelectedTranslationMode == TranslationMode.Free)
                _settings.SaveTranslationChoice(TranslationMode.Free, SelectedFreeTranslationProvider);
            else
                _settings.Save(EndpointBox.Text, ModelBox.Text, ApiKeyBox.Password,
                    TranslationMode.Api, SelectedFreeTranslationProvider);
        }
        catch
        {
            _translationSaveFailed = true;
            _failedTranslationMode = SelectedTranslationMode;
            _failedFreeTranslationProvider = SelectedFreeTranslationProvider;
            if (SelectedTranslationMode == TranslationMode.Free)
            {
                FreeProviderStatus.Text = "保存失败，当前选择尚未写入磁盘；尚未联网验证。";
                FreeProviderStatus.Visibility = Visibility.Visible;
            }
            UpdateTranslationProviderSummary();
            throw;
        }

        _translationSaveFailed = false;
        if (SelectedTranslationMode == TranslationMode.Free)
        {
            RefreshFreeProviderStatus();
        }
        UpdateTranslationProviderSummary();
    }

    private void SelectTranslationMode(TranslationMode mode)
    {
        if (_selectedTranslationMode != mode)
        {
            TranslationSaveStatus.Text = string.Empty;
            TranslationSaveStatus.Visibility = Visibility.Collapsed;
        }
        _selectedTranslationMode = mode;
        UpdateTranslationModePresentation();
        UpdateTranslationProviderSummary();
    }

    private void UpdateTranslationModePresentation()
    {
        var free = SelectedTranslationMode == TranslationMode.Free;
        var initialized = _providerSettingsInitialized;
        _providerSettingsInitialized = false;
        FreeTranslationPanel.Visibility = free ? Visibility.Visible : Visibility.Collapsed;
        ApiTranslationPanel.Visibility = free ? Visibility.Collapsed : Visibility.Visible;
        FreeTranslationButton.IsChecked = free;
        ApiSettingsButton.IsChecked = !free;
        _providerSettingsInitialized = initialized;
        SaveTranslationSettingsButton.IsEnabled = !free || SelectedFreeTranslationProvider == FreeTranslationProvider.Bing;
        TranslationPrivacyNotice.Text = free
            ? "游戏文字会发送到必应翻译；免费网页服务可能限流或调整。"
            : "开启后文字会发送到本页填写的翻译服务，关闭后不发送联网翻译请求。";
    }

    private void OnOpenFreeTranslation(object sender, RoutedEventArgs e)
    {
        if (!_providerSettingsInitialized) return;
        SelectTranslationMode(TranslationMode.Free);
        OpenTranslationServicesPage();
    }

    private void OnSelectApiSettings(object sender, RoutedEventArgs e)
    {
        if (!_providerSettingsInitialized) return;
        SelectTranslationMode(TranslationMode.Api);
        OnOpenApiSettings(sender, e);
    }

    private void OnEditTranslationSettings(object sender, RoutedEventArgs e)
    {
        OpenTranslationServicesPage();
    }

    private void OnFreeProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_providerSettingsInitialized || FreeProviderBox.SelectedItem is not ComboBoxItem { Tag: "Bing" }) return;
        _selectedFreeTranslationProvider = FreeTranslationProvider.Bing;
        RefreshFreeProviderStatus();
        SelectTranslationMode(TranslationMode.Free);
    }

    private void OnSaveTranslationSettings(object sender, RoutedEventArgs e)
    {
        var saved = SaveSettings();
        if (SelectedTranslationMode == TranslationMode.Api) _apiSaveFailed = !saved;
        TranslationSaveStatus.Text = saved
            ? "已保存，下次启动游戏时生效。"
            : "保存失败，当前选择尚未写入磁盘。";
        TranslationSaveStatus.SetResourceReference(TextBlock.ForegroundProperty, saved ? "TextSecondary" : "Warning");
        TranslationSaveStatus.Visibility = Visibility.Visible;
        UpdateApiProviderSummary();
    }

    private void OnApiDraftChanged(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsInitialized) UpdateTranslationProviderSummary();
    }

    private void UpdateTranslationProviderSummary()
    {
        if (!_providerSettingsInitialized || !_libraryPresentationReady) return;
        var selected = DescribeTranslationChoice(SelectedTranslationMode, SelectedFreeTranslationProvider, EndpointBox.Text);
        var apiDraftChanged = !string.Equals(EndpointBox.Text.Trim(), _settings.Endpoint, StringComparison.Ordinal) ||
            !string.Equals(ModelBox.Text.Trim(), _settings.Model, StringComparison.Ordinal) ||
            !string.Equals(ApiKeyBox.Password, _settings.ApiKey, StringComparison.Ordinal);
        var changed = SelectedTranslationMode != _settings.Mode ||
            (SelectedTranslationMode == TranslationMode.Free && SelectedFreeTranslationProvider != _settings.FreeProvider) ||
            (SelectedTranslationMode == TranslationMode.Api && apiDraftChanged);
        var saveFailed = _translationSaveFailed && SelectedTranslationMode == _failedTranslationMode &&
            (SelectedTranslationMode == TranslationMode.Api || SelectedFreeTranslationProvider == _failedFreeTranslationProvider);
        TranslationProviderSummary.SetResourceReference(TextBlock.ForegroundProperty,
            SelectedTranslationMode == TranslationMode.Free && SelectedFreeTranslationProvider != FreeTranslationProvider.Bing
                ? "Warning" : "TextPrimary");
        if (changed || saveFailed)
        {
            var committed = DescribeTranslationChoice(_settings.Mode, _settings.FreeProvider, _settings.Endpoint);
            var state = saveFailed ? "未保存，保存失败" : "未保存";
            TranslationProviderSummary.Text = $"当前选择：{selected}（{state}）；原设置：{committed}";
        }
        else TranslationProviderSummary.Text = "当前翻译：" + selected;
        TranslationServiceSummary.Text = selected + (saveFailed ? "（未保存，保存失败）" : changed ? "（未保存）" : string.Empty);
        TranslationServiceSummaryLabel.Text = changed || saveFailed ? "当前选择：" : "当前使用：";
        TranslationServiceSummary.ToolTip = TranslationProviderSummary.Text;
    }

    private static string DescribeTranslationChoice(TranslationMode mode, FreeTranslationProvider provider, string endpoint)
    {
        if (mode == TranslationMode.Free)
            return "免费翻译 · " + (provider == FreeTranslationProvider.Baidu ? "百度（当前版本不可用）" : "必应 Bing");
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "待填写服务地址";
        return "API · " + host;
    }

    private void RefreshFreeProviderStatus()
    {
        var supported = SelectedFreeTranslationProvider == FreeTranslationProvider.Bing;
        SaveTranslationSettingsButton.IsEnabled = SelectedTranslationMode == TranslationMode.Api || supported;
        FreeProviderStatus.Text = supported
            ? string.Empty
            : "原设置中的百度免费翻译在当前版本不可用。请选择必应 Bing 并保存后重试。";
        FreeProviderStatus.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        FreeProviderStatus.SetResourceReference(TextBlock.ForegroundProperty, supported ? "TextSecondary" : "Warning");
    }
}
