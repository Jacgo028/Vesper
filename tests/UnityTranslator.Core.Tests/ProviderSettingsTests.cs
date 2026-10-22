using System.Text.Json;
using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class ProviderSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ut-provider-settings-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings.json");

    // Missing defaults would cause an untouched install to request API credentials.
    [Fact]
    public void NewConfigurationPersistsFreeBingAsTheDefaultChoice()
    {
        var settings = NewSettings();
        settings.SaveTheme(AppTheme.Dark);

        AssertStoredChoice("Free", "Bing");
        Assert.Equal(string.Empty, settings.ApiKey);
    }

    // A theme save must not reset the user's chosen translation provider.
    [Theory]
    [InlineData("Free", "Baidu")]
    [InlineData("Api", "Bing")]
    public void ThemeSaveRetainsExplicitTranslationChoice(string mode, string provider)
    {
        WriteConfiguration($$"""{"Mode":"{{mode}}","FreeProvider":"{{provider}}","Theme":"System"}""");
        NewSettings().SaveTheme(AppTheme.Dark);

        AssertStoredChoice(mode, provider);
    }

    // Existing key users must keep API mode when upgrading an old configuration.
    [Theory]
    [InlineData("foreign-encrypted-key", "Api")]
    [InlineData("", "Free")]
    public void LegacyConfigurationInfersModeFromTheSavedKey(string ciphertext, string expectedMode)
    {
        WriteConfiguration($$"""{"Endpoint":"https://saved.example.com/chat","Model":"saved-model","ProtectedApiKey":"{{ciphertext}}","Theme":"Dark"}""");
        NewSettings().SaveTheme(AppTheme.System);

        AssertStoredChoice(expectedMode, "Bing");
        Assert.Equal(ciphertext, ReadStoredString("ProtectedApiKey"));
    }

    // Invalid enum payloads must not enter an unsupported provider branch.
    [Theory]
    [InlineData("99", "{}")]
    [InlineData("\"unknown\"", "\"1\"")]
    [InlineData("null", "\"Bing, Baidu\"")]
    public void InvalidTranslationChoiceFallsBackWithoutDiscardingApiDraft(string mode, string provider)
    {
        WriteConfiguration($$"""{"Mode":{{mode}},"FreeProvider":{{provider}},"Endpoint":"https://saved.example.com/chat","Model":"saved-model","ProtectedApiKey":"foreign-encrypted-key","Theme":"Dark"}""");
        var settings = NewSettings();
        settings.SaveTheme(AppTheme.System);

        AssertStoredChoice("Free", "Bing");
        Assert.Equal("https://saved.example.com/chat", settings.Endpoint);
        Assert.Equal("saved-model", settings.Model);
        Assert.Equal("foreign-encrypted-key", ReadStoredString("ProtectedApiKey"));
    }

    // Saving free mode must not re-encrypt or replace even an unreadable API draft.
    [Fact]
    public void SavingFreeChoicePreservesExactEncryptedApiDraftAndTheme()
    {
        WriteConfiguration("""{"Mode":"Api","FreeProvider":"Bing","Endpoint":"https://saved.example.com/chat","Model":"saved-model","ProtectedApiKey":"foreign-encrypted-key","Theme":"Dark"}""");
        var settings = NewSettings();

        SaveChoice(settings, "Free", "Baidu");

        AssertStoredChoice("Free", "Baidu");
        Assert.Equal("foreign-encrypted-key", ReadStoredString("ProtectedApiKey"));
        Assert.Equal("Dark", ReadStoredString("Theme"));
        var reopened = NewSettings();
        reopened.Load();
        Assert.Equal("https://saved.example.com/chat", reopened.Endpoint);
        Assert.Equal("saved-model", reopened.Model);
        Assert.Equal(string.Empty, reopened.ApiKey);
    }

    // The API save must commit the explicit mode and free-provider preference together.
    [Fact]
    public void ApiSavePersistsTheSelectedModeAndPreservesTheme()
    {
        var settings = NewSettings();
        settings.SaveTheme(AppTheme.Dark);

        SaveApi(settings, "https://saved.example.com/chat", "saved-model", "local-test-key", "Api", "Baidu");

        AssertStoredChoice("Api", "Baidu");
        var reopened = NewSettings();
        reopened.Load();
        Assert.Equal("local-test-key", reopened.ApiKey);
        Assert.Equal(AppTheme.Dark, reopened.Theme);
        Assert.DoesNotContain("local-test-key", File.ReadAllText(SettingsPath));
    }

    // Existing callers updating an API draft must not switch away from free mode.
    [Fact]
    public void ThreeArgumentApiSavePreservesTheCommittedTranslationChoice()
    {
        var settings = NewSettings();
        SaveChoice(settings, "Free", "Baidu");

        settings.Save("https://saved.example.com/chat", "saved-model", "local-test-key");

        AssertStoredChoice("Free", "Baidu");
    }

    // A failed disk replacement must not leak an unsaved choice into a theme save.
    [Fact]
    public void FailedChoiceSaveRetainsCommittedMemoryAndDisk()
    {
        var settings = NewSettings();
        SaveApi(settings, "https://saved.example.com/chat", "saved-model", "saved-key", "Api", "Bing");
        var before = File.ReadAllText(SettingsPath);
        using (File.Open(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = Record.Exception(() => SaveChoice(settings, "Free", "Baidu"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(TranslationMode.Api, settings.Mode);
        Assert.Equal(FreeTranslationProvider.Bing, settings.FreeProvider);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
        settings.SaveTheme(AppTheme.Dark);
        AssertStoredChoice("Api", "Bing");
        Assert.Equal("saved-key", settings.ApiKey);
    }

    // Protection failure must not commit either the API draft or its mode.
    [Fact]
    public void FailedApiProtectionRetainsTheLastChoiceAndCredentials()
    {
        var protector = new RejectingProtector();
        var settings = new AppSettings(SettingsPath, protector);
        SaveApi(settings, "https://saved.example.com/chat", "saved-model", "saved-key", "Free", "Baidu");
        var before = File.ReadAllText(SettingsPath);
        protector.RejectProtection = true;

        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            SaveApi(settings, "https://draft.example.com/chat", "draft-model", "draft-key", "Api", "Bing"));

        Assert.Equal(TranslationMode.Free, settings.Mode);
        Assert.Equal(FreeTranslationProvider.Baidu, settings.FreeProvider);
        Assert.Equal("saved-model", settings.Model);
        Assert.Equal("saved-key", settings.ApiKey);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    private AppSettings NewSettings() => new(SettingsPath, new DpapiSecretProtector());

    private void WriteConfiguration(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, json);
    }

    private string? ReadStoredString(string property)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.True(json.RootElement.TryGetProperty(property, out var value), $"Saved configuration is missing {property}.");
        return value.GetString();
    }

    private void AssertStoredChoice(string mode, string provider)
    {
        Assert.Equal(mode, ReadStoredString("Mode"));
        Assert.Equal(provider, ReadStoredString("FreeProvider"));
    }

    private static void SaveChoice(AppSettings settings, string mode, string provider) =>
        settings.SaveTranslationChoice(Enum.Parse<TranslationMode>(mode), Enum.Parse<FreeTranslationProvider>(provider));

    private static void SaveApi(AppSettings settings, string endpoint, string model, string key, string mode, string provider) =>
        settings.Save(endpoint, model, key, Enum.Parse<TranslationMode>(mode), Enum.Parse<FreeTranslationProvider>(provider));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RejectingProtector : ISecretProtector
    {
        private readonly DpapiSecretProtector _inner = new();
        public bool RejectProtection { get; set; }
        public string Protect(string plainText) => RejectProtection
            ? throw new System.ComponentModel.Win32Exception(5, "Protection failed.")
            : _inner.Protect(plainText);
        public string Unprotect(string protectedBase64) => _inner.Unprotect(protectedBase64);
    }
}
