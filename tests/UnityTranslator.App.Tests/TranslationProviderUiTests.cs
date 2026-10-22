using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class TranslationProviderUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-provider-ui-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings.json");

    // A new user must be able to choose free translation without API credentials.
    [Fact]
    public void NewWindowOffersOnlyTheAnonymousBingProvider()
    {
        OnWindow(window =>
        {
            Assert.Equal("免费翻译", Find<RadioButton>(window, "FreeTranslationButton").Content);
            Assert.Equal("API 翻译", window.ApiSettingsButton.Content);
            var providers = Find<ComboBox>(window, "FreeProviderBox");
            Assert.Equal(new[] { "Bing" }, providers.Items.OfType<ComboBoxItem>().Select(item => item.Tag?.ToString()));
            Assert.Equal("Bing", ((ComboBoxItem)providers.SelectedItem).Tag);
            var summary = Find<TextBlock>(window, "TranslationProviderSummary").Text;
            Assert.Contains("免费", summary);
            Assert.Contains("Bing", summary);
        });
    }

    // Opening a provider entry must control the route used by the next launch.
    [Fact]
    public void FreeEntrySelectsFreeModeAndOpensItsPanel()
    {
        SeedApi();
        OnWindow(window =>
        {
            Click(window, "ApiSettingsButton");
            Click(window, "FreeTranslationButton");

            Assert.Equal("Free", SelectedMode(window));
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "FreeTranslationPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "ApiTranslationPanel").Visibility);
            var summary = Find<TextBlock>(window, "TranslationProviderSummary").Text;
            Assert.Contains("免费", summary);
            Assert.Contains("未保存", summary);
            Assert.Contains("API", summary);
        });
    }

    // A stale or invalid API draft must not block saving the keyless service choice.
    [Fact]
    public void SavingFreeProviderPreservesApiCiphertextAndUnsubmittedDraft()
    {
        SeedApi();
        var ciphertext = StoredCiphertext();
        OnWindow(window =>
        {
            window.EndpointBox.Text = "unfinished API address";
            window.ModelBox.Text = "unfinished-model";
            window.ApiKeyBox.Password = "unsubmitted-local-key";
            Click(window, "FreeTranslationButton");
            Choose(window, "Bing");
            Click(window, "SaveTranslationSettingsButton");

            var saved = ReadSettings();
            Assert.Equal(TranslationMode.Free, saved.Mode);
            Assert.Equal(FreeTranslationProvider.Bing, saved.FreeProvider);
            Assert.Equal("https://saved.example.com/chat", saved.Endpoint);
            Assert.Equal("saved-model", saved.Model);
            Assert.Equal("saved-key", saved.ApiKey);
            Assert.Equal(ciphertext, StoredCiphertext());
            Assert.Equal("unfinished API address", window.EndpointBox.Text);
            Assert.Equal("unsubmitted-local-key", window.ApiKeyBox.Password);
            Assert.Contains("Bing", Find<TextBlock>(window, "TranslationProviderSummary").Text);
            Assert.DoesNotContain("未保存", Find<TextBlock>(window, "TranslationProviderSummary").Text);
            Assert.DoesNotContain("通过", Find<TextBlock>(window, "FreeProviderStatus").Text);
        });
        OnWindow(window =>
        {
            Assert.Equal("Free", SelectedMode(window));
            Assert.Equal("Bing", ((ComboBoxItem)Find<ComboBox>(window, "FreeProviderBox").SelectedItem).Tag);
        });
    }

    // Choosing API must not silently continue launching through the free provider.
    [Fact]
    public void ApiEntrySelectsApiAndItsSaveKeepsTheFreeProviderPreference()
    {
        new AppSettings(SettingsPath, new DpapiSecretProtector()).SaveTranslationChoice(TranslationMode.Free, FreeTranslationProvider.Baidu);
        OnWindow(window =>
        {
            Click(window, "FreeTranslationButton");
            Click(window, "ApiSettingsButton");
            Assert.Equal("Api", SelectedMode(window));
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "ApiTranslationPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "FreeTranslationPanel").Visibility);
            window.EndpointBox.Text = "https://new.example.com/chat";
            window.ModelBox.Text = "new-model";
            window.ApiKeyBox.Password = "new-local-key";
            Click(window, "SaveTranslationSettingsButton");

            var saved = ReadSettings();
            Assert.Equal(TranslationMode.Api, saved.Mode);
            Assert.Equal(FreeTranslationProvider.Baidu, saved.FreeProvider);
            Assert.Equal("new-model", saved.Model);
            Assert.Equal("new-local-key", saved.ApiKey);
            Assert.Contains("API", Find<TextBlock>(window, "TranslationProviderSummary").Text);
            Assert.Contains("new.example.com", window.ApiProviderSummary.Text);
        });
    }

    // A failed save must distinguish the currently selected provider from the disk choice.
    [Fact]
    public void FailedFreeSaveKeepsTheCommittedChoiceAndShowsThePendingChoiceAfterThemeChange()
    {
        SeedApi();
        OnWindow(window =>
        {
            Click(window, "FreeTranslationButton");
            using (File.Open(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Click(window, "SaveTranslationSettingsButton");
                Find<ComboBox>(window, "ThemeModeBox").SelectedIndex = 1;
            }

            Assert.Equal(TranslationMode.Api, ReadSettings().Mode);
            Assert.Equal("Free", SelectedMode(window));
            Assert.Equal("Bing", ((ComboBoxItem)Find<ComboBox>(window, "FreeProviderBox").SelectedItem).Tag);
            var summary = Find<TextBlock>(window, "TranslationProviderSummary").Text;
            Assert.Contains("免费", summary);
            Assert.Contains("API", summary);
            Assert.Contains("Bing", summary);
            Assert.Contains("未保存", summary);
            Assert.Contains("保存失败", Find<TextBlock>(window, "FreeProviderStatus").Text);
        });
    }

    // Changing selection must update the active route without prematurely persisting it.
    [Fact]
    public void ChoosingBingForAnUnsupportedStoredProviderRequiresAnExplicitSave()
    {
        new AppSettings(SettingsPath, new DpapiSecretProtector()).SaveTranslationChoice(TranslationMode.Free, FreeTranslationProvider.Baidu);
        OnWindow(window =>
        {
            Click(window, "FreeTranslationButton");
            Choose(window, "Bing");

            Assert.Equal(FreeTranslationProvider.Baidu, ReadSettings().FreeProvider);
            Assert.Equal(FreeTranslationProvider.Bing, window.SelectedFreeTranslationProvider);
            var summary = Find<TextBlock>(window, "TranslationProviderSummary").Text;
            Assert.Contains("Bing", summary);
            Assert.Contains("未保存", summary);
        });
    }

    // Unsupported old choices must be visible and must not silently become Bing.
    [Fact]
    public void UnsupportedStoredProviderIsReportedWithoutASilentFallback()
    {
        new AppSettings(SettingsPath, new DpapiSecretProtector()).SaveTranslationChoice(TranslationMode.Free, FreeTranslationProvider.Baidu);
        OnWindow(window =>
        {
            Assert.Equal(FreeTranslationProvider.Baidu, window.SelectedFreeTranslationProvider);
            Assert.Null(Find<ComboBox>(window, "FreeProviderBox").SelectedItem);
            Assert.Contains("不可用", Find<TextBlock>(window, "TranslationProviderSummary").Text);
            Assert.Contains("不可用", Find<TextBlock>(window, "FreeProviderStatus").Text);
        });
    }

    // A failure in the other mode must not mislabel an already committed choice.
    [Fact]
    public void ReturningToCommittedFreeChoiceDoesNotInheritAnApiSaveFailure()
    {
        new AppSettings(SettingsPath, new DpapiSecretProtector()).SaveTranslationChoice(TranslationMode.Free, FreeTranslationProvider.Bing);
        OnWindow(window =>
        {
            Click(window, "ApiSettingsButton");
            window.ApiKeyBox.Password = "unsubmitted-local-key";
            using (File.Open(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Click(window, "SaveTranslationSettingsButton");
            Assert.Contains("保存失败", Find<TextBlock>(window, "TranslationProviderSummary").Text);

            Click(window, "FreeTranslationButton");

            var summary = Find<TextBlock>(window, "TranslationProviderSummary").Text;
            Assert.Contains("Bing", summary);
            Assert.DoesNotContain("未保存", summary);
            Assert.DoesNotContain("保存失败", summary);
        });
    }

    // Managing services is navigation, so it must not switch the active mode.
    [Fact]
    public void HomeManagementEntryOpensTheDedicatedServicePageWithoutChangingMode()
    {
        SeedApi();
        OnWindow(window =>
        {
            var page = Find<TabItem>(window, "TranslationServicesTab");
            var manage = Find<Button>(window, "ManageTranslationServicesButton");
            window.ApiKeyBox.Password = "unsubmitted-navigation-draft";

            manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Same(page, window.WorkspaceTabs.SelectedItem);
            Assert.Equal("翻译服务", ((ListBoxItem)window.NavList.SelectedItem).Content);
            Assert.Equal(TranslationMode.Api, window.SelectedTranslationMode);
            Assert.Equal("unsubmitted-navigation-draft", window.ApiKeyBox.Password);
            Assert.Contains("API", Find<TextBlock>(window, "TranslationServiceSummary").Text);
            Assert.Same(page, OwningPage(window.EndpointBox));
            Assert.Same(page, OwningPage(Find<ComboBox>(window, "FreeProviderBox")));
            Assert.Same(page, OwningPage(window.ApiSettingsButton));
            Assert.Same(page, OwningPage(Find<RadioButton>(window, "FreeTranslationButton")));
            Assert.NotSame(page, OwningPage(manage));
        });
    }

    // Page changes and mode changes must retain both independent configurations.
    [Fact]
    public void NavigatingBetweenPagesKeepsTheApiDraftAndSelectedFreeMode()
    {
        SeedApi();
        OnWindow(window =>
        {
            Click(window, "ManageTranslationServicesButton");
            window.EndpointBox.Text = "https://draft.example.com/chat";
            window.ModelBox.Text = "draft-model";
            window.ApiKeyBox.Password = "unsubmitted-navigation-draft";
            Click(window, "FreeTranslationButton");
            window.NavList.SelectedIndex = 0;
            Click(window, "ManageTranslationServicesButton");

            Assert.Equal(TranslationMode.Free, window.SelectedTranslationMode);
            Assert.Equal("https://draft.example.com/chat", window.EndpointBox.Text);
            Assert.Equal("draft-model", window.ModelBox.Text);
            Assert.Equal("unsubmitted-navigation-draft", window.ApiKeyBox.Password);
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "FreeTranslationPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "ApiTranslationPanel").Visibility);
            Assert.Equal(TranslationMode.Api, ReadSettings().Mode);
        });
    }

    // Old API settings should open the API option rather than changing to free mode.
    [Fact]
    public void LegacyApiConfigurationOpensTheMatchingServiceOptionFromTheSidebar()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
        {
            Endpoint = "https://legacy.example.com/chat",
            Model = "legacy-model",
            ProtectedApiKey = new DpapiSecretProtector().Protect("legacy-local-key"),
            Theme = "Dark"
        }));
        OnWindow(window =>
        {
            var item = Assert.Single(window.NavList.Items.OfType<ListBoxItem>().Where(candidate => Equals(candidate.Content, "翻译服务")));
            window.NavList.SelectedItem = item;

            Assert.Same(Find<TabItem>(window, "TranslationServicesTab"), window.WorkspaceTabs.SelectedItem);
            Assert.Equal(TranslationMode.Api, window.SelectedTranslationMode);
            Assert.Equal("legacy-local-key", window.ApiKeyBox.Password);
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "ApiTranslationPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "FreeTranslationPanel").Visibility);
            Assert.Contains("legacy.example.com", Find<TextBlock>(window, "TranslationServiceSummary").Text);
        });
    }

    // Both accessible segments must feed the same primary action, without losing the other configuration.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimarySaveEnablesTheModeChosenThroughAccessibleSegments(bool api)
    {
        SeedApi();
        OnWindow(window =>
        {
            Click(window, "ManageTranslationServicesButton");
            window.EndpointBox.Text = "https://draft.example.com/chat";
            window.ModelBox.Text = "draft-model";
            window.ApiKeyBox.Password = "draft-local-key";
            var option = Find<RadioButton>(window, api ? "ApiSettingsButton" : "FreeTranslationButton");
            Assert.Equal(api ? "API 翻译" : "免费翻译", AutomationProperties.GetName(option));
            SelectSegment(option);
            var save = Find<Button>(window, "SaveTranslationSettingsButton");
            Assert.True(save.IsEnabled);
            Assert.Equal("保存并启用", AutomationProperties.GetName(save));

            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var settings = ReadSettings();
            Assert.Equal(api ? TranslationMode.Api : TranslationMode.Free, settings.Mode);
            Assert.Equal(api ? "draft-model" : "saved-model", settings.Model);
            Assert.Equal(api ? "draft-local-key" : "saved-key", settings.ApiKey);
            Assert.True(option.IsChecked);
            Assert.DoesNotContain("未保存", Find<TextBlock>(window, "TranslationProviderSummary").Text);
            Assert.Contains("已保存", Find<TextBlock>(window, "TranslationSaveStatus").Text);
        });
    }

    // Selecting a mode must expose its form directly, without another disclosure click.
    [Fact]
    public void ModeSegmentsRevealFlatConfigurationAndKeepTheApiDraft()
    {
        OnWindow(window =>
        {
            Click(window, "ManageTranslationServicesButton");
            var api = Find<RadioButton>(window, "ApiSettingsButton");
            var free = Find<RadioButton>(window, "FreeTranslationButton");
            SelectSegment(api);
            window.EndpointBox.Text = "https://draft.example.com/chat";
            window.ModelBox.Text = "draft-model";
            window.ApiKeyBox.Password = "unsubmitted-segment-draft";
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "ApiTranslationPanel").Visibility);
            Assert.Null(OwningDisclosure(window.ApiKeyBox));

            SelectSegment(free);
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "FreeTranslationPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "ApiTranslationPanel").Visibility);
            Assert.Null(OwningDisclosure(Find<ComboBox>(window, "FreeProviderBox")));
            SelectSegment(api);

            Assert.Equal("https://draft.example.com/chat", window.EndpointBox.Text);
            Assert.Equal("draft-model", window.ModelBox.Text);
            Assert.Equal("unsubmitted-segment-draft", window.ApiKeyBox.Password);
            Assert.False(free.IsChecked);
            Assert.True(api.IsChecked);
        });
    }

    // A margin outside the cloned selection visual can clip its last letters despite a wide field.
    [Fact]
    public void BingSelectionReservesSpaceForTheCompleteProviderNameAndLogo()
    {
        OnWindow(window =>
        {
            Click(window, "ManageTranslationServicesButton");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1200, 800));
            content.Arrange(new Rect(0, 0, 1200, 800));
            content.UpdateLayout();
            var picker = Find<ComboBox>(window, "FreeProviderBox");
            var selection = Assert.IsType<ContentPresenter>(picker.Template.FindName("ContentSite", picker));
            var label = new FormattedText("必应 Bing", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(picker.FontFamily, picker.FontStyle, picker.FontWeight, picker.FontStretch),
                picker.FontSize, Brushes.Black, 1);
            var requiredWidth = 28 + 16 + 10 + label.WidthIncludingTrailingWhitespace;

            Assert.True(selection.ActualWidth >= requiredWidth - 1,
                $"Selected provider content has {selection.ActualWidth:F1}px but the complete logo and name need {requiredWidth:F1}px.");
        });
    }

    // Every API draft field can change the pending provider without changing the committed configuration.
    [Theory]
    [InlineData("Endpoint")]
    [InlineData("Model")]
    [InlineData("ApiKey")]
    public void ApiDraftChangesRemainExplicitlyUnsavedAcrossThemeAndModeChanges(string field)
    {
        SeedApi();
        OnWindow(window =>
        {
            Click(window, "ManageTranslationServicesButton");
            if (field == "Endpoint") window.EndpointBox.Text = "https://draft.example.com/chat";
            else if (field == "Model") window.ModelBox.Text = "draft-model";
            else window.ApiKeyBox.Password = "unsubmitted-draft-key";

            Assert.Contains("未保存", Find<TextBlock>(window, "TranslationServiceSummary").Text);
            Assert.Contains("saved.example.com", window.ApiProviderSummary.Text);
            Find<ComboBox>(window, "ThemeModeBox").SelectedIndex = 1;
            Click(window, "FreeTranslationButton");
            Click(window, "ApiSettingsButton");

            Assert.Contains("未保存", Find<TextBlock>(window, "TranslationServiceSummary").Text);
            Assert.Contains("未保存", Find<TextBlock>(window, "TranslationProviderSummary").Text);
            Assert.DoesNotContain("unsubmitted-draft-key", Find<TextBlock>(window, "TranslationProviderSummary").Text);
            Assert.Equal("https://saved.example.com/chat", ReadSettings().Endpoint);
            Assert.Equal("saved-model", ReadSettings().Model);
            Assert.Equal("saved-key", ReadSettings().ApiKey);
            Click(window, "SaveTranslationSettingsButton");
            Assert.DoesNotContain("未保存", Find<TextBlock>(window, "TranslationServiceSummary").Text);
        });
    }

    // Closing must discard drafts instead of silently replacing the last explicit save.
    [Theory]
    [InlineData(TranslationMode.Api, FreeTranslationProvider.Bing)]
    [InlineData(TranslationMode.Free, FreeTranslationProvider.Baidu)]
    public void ClosingWithUnsavedTranslationDraftKeepsCommittedSettings(
        TranslationMode savedMode, FreeTranslationProvider savedProvider)
    {
        new AppSettings(SettingsPath, new DpapiSecretProtector()).Save(
            "https://saved.example.com/chat", "saved-model", "saved-key", savedMode, savedProvider);
        var before = File.ReadAllBytes(SettingsPath);
        var ciphertext = StoredCiphertext();
        var closed = false;

        OnWindow(window =>
        {
            window.Closed += (_, _) => closed = true;
            window.EndpointBox.Text = "https://draft.example.com/chat";
            window.ModelBox.Text = "draft-model";
            window.ApiKeyBox.Password = "unsubmitted-close-draft";
            Choose(window, "Bing");
            Click(window, savedMode == TranslationMode.Api ? "FreeTranslationButton" : "ApiSettingsButton");
            Assert.Contains("未保存", Find<TextBlock>(window, "TranslationProviderSummary").Text);
        }); // OnWindow calls the real Window.Close without showing a desktop window.

        Assert.True(closed);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Equal(ciphertext, StoredCiphertext());
        OnWindow(window =>
        {
            Assert.Equal(savedMode, window.SelectedTranslationMode);
            Assert.Equal(savedProvider, window.SelectedFreeTranslationProvider);
            Assert.Equal("https://saved.example.com/chat", window.EndpointBox.Text);
            Assert.Equal("saved-model", window.ModelBox.Text);
            Assert.Equal("saved-key", window.ApiKeyBox.Password);
        });
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    // Releasing a failed save's file lock must not authorize an automatic retry on close.
    [Theory]
    [InlineData(TranslationMode.Api, FreeTranslationProvider.Bing)]
    [InlineData(TranslationMode.Free, FreeTranslationProvider.Baidu)]
    public void ClosingAfterFailedTranslationSaveDoesNotRetry(
        TranslationMode savedMode, FreeTranslationProvider savedProvider)
    {
        new AppSettings(SettingsPath, new DpapiSecretProtector()).Save(
            "https://saved.example.com/chat", "saved-model", "saved-key", savedMode, savedProvider);
        var before = File.ReadAllBytes(SettingsPath);
        var ciphertext = StoredCiphertext();
        var closed = false;

        OnWindow(window =>
        {
            window.Closed += (_, _) => closed = true;
            window.EndpointBox.Text = "https://draft.example.com/chat";
            window.ModelBox.Text = "draft-model";
            window.ApiKeyBox.Password = "failed-close-draft";
            Choose(window, "Bing");
            Click(window, savedMode == TranslationMode.Api ? "FreeTranslationButton" : "ApiSettingsButton");
            using (File.Open(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Click(window, "SaveTranslationSettingsButton");
            Assert.Contains("保存失败", Find<TextBlock>(window, "TranslationSaveStatus").Text);
        });

        Assert.True(closed);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Equal(ciphertext, StoredCiphertext());
        OnWindow(window =>
        {
            Assert.Equal(savedMode, window.SelectedTranslationMode);
            Assert.Equal(savedProvider, window.SelectedFreeTranslationProvider);
            Assert.Equal("https://saved.example.com/chat", window.EndpointBox.Text);
            Assert.Equal("saved-model", window.ModelBox.Text);
            Assert.Equal("saved-key", window.ApiKeyBox.Password);
        });
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    // An explicit save remains authoritative through shutdown, without re-encrypting it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitTranslationSaveSurvivesCloseAndReopen(bool api)
    {
        SeedApi();
        byte[]? committed = null;
        string? ciphertext = null;
        var closed = false;

        OnWindow(window =>
        {
            window.Closed += (_, _) => closed = true;
            window.EndpointBox.Text = "https://draft.example.com/chat";
            window.ModelBox.Text = "draft-model";
            window.ApiKeyBox.Password = "explicit-save-local-key";
            Click(window, api ? "ApiSettingsButton" : "FreeTranslationButton");
            Click(window, "SaveTranslationSettingsButton");
            Assert.Contains("已保存", Find<TextBlock>(window, "TranslationSaveStatus").Text);
            committed = File.ReadAllBytes(SettingsPath);
            ciphertext = StoredCiphertext();
        });

        Assert.True(closed);
        Assert.NotNull(committed);
        Assert.Equal(committed, File.ReadAllBytes(SettingsPath));
        Assert.Equal(ciphertext, StoredCiphertext());
        OnWindow(window =>
        {
            Assert.Equal(api ? TranslationMode.Api : TranslationMode.Free, window.SelectedTranslationMode);
            Assert.Equal(FreeTranslationProvider.Bing, window.SelectedFreeTranslationProvider);
            Assert.Equal(api ? "https://draft.example.com/chat" : "https://saved.example.com/chat", window.EndpointBox.Text);
            Assert.Equal(api ? "draft-model" : "saved-model", window.ModelBox.Text);
            Assert.Equal(api ? "explicit-save-local-key" : "saved-key", window.ApiKeyBox.Password);
        });
        Assert.Equal(committed, File.ReadAllBytes(SettingsPath));
    }

    private void SeedApi() => new AppSettings(SettingsPath, new DpapiSecretProtector()).Save(
        "https://saved.example.com/chat", "saved-model", "saved-key", TranslationMode.Api, FreeTranslationProvider.Bing);

    private AppSettings ReadSettings()
    {
        var settings = new AppSettings(SettingsPath, new DpapiSecretProtector());
        settings.Load();
        return settings;
    }

    private string? StoredCiphertext()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        return json.RootElement.GetProperty("ProtectedApiKey").GetString();
    }

    private static T Find<T>(MainWindow window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static void Click(MainWindow window, string name)
    {
        if (window.FindName(name) is RadioButton option) SelectSegment(option);
        else Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Choose(MainWindow window, string provider)
    {
        var box = Find<ComboBox>(window, "FreeProviderBox");
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, provider));
    }

    private static string SelectedMode(MainWindow window) => window.SelectedTranslationMode.ToString();

    private static TabItem? OwningPage(DependencyObject element)
    {
        for (var parent = LogicalTreeHelper.GetParent(element); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is TabItem page) return page;
        return null;
    }

    private static Expander? OwningDisclosure(DependencyObject element)
    {
        for (var parent = LogicalTreeHelper.GetParent(element); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is Expander expander) return expander;
        return null;
    }

    private static void SelectSegment(RadioButton option)
    {
        var peer = new RadioButtonAutomationPeer(option);
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
    }

    private void OnWindow(Action<MainWindow> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow.PrepareTestDispatcherForTest();
            var window = new MainWindow(_root, new RecordingExternalFactory { Available = false });
            try { check(window); }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Provider settings UI test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
