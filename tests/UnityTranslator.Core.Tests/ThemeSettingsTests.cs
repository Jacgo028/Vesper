using System.Text.Json;
using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class ThemeSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-theme-settings", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings.json");

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.System)]
    public void SavedThemeSurvivesRestart(AppTheme theme)
    {
        var settings = new AppSettings(SettingsPath, new DpapiSecretProtector());
        settings.SaveTheme(theme);

        var reopened = new AppSettings(SettingsPath, new DpapiSecretProtector());
        reopened.Load();

        Assert.Equal(theme, reopened.Theme);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Theme\":\"unknown\"}")]
    [InlineData("{\"Theme\":99}")]
    [InlineData("{\"Theme\":null}")]
    [InlineData("{\"Theme\":{}}")]
    [InlineData("{\"Theme\":\"1\"}")]
    [InlineData("{\"Theme\":\"Light, Dark\"}")]
    [InlineData("not json")]
    public void OldOrInvalidThemeFallsBackToLight(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, json);
        var settings = new AppSettings(SettingsPath, new DpapiSecretProtector());

        settings.Load();

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public void MissingSettingsStartInLightMode()
    {
        var settings = new AppSettings(SettingsPath, new DpapiSecretProtector());
        settings.Load();

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("{}")]
    [InlineData("\"unrecognized\"")]
    public void InvalidThemeDoesNotDiscardSavedApiConfiguration(string themeJson)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath,
            $$"""{"Endpoint":"https://example.com/chat","Model":"saved-model","ProtectedApiKey":"","Theme":{{themeJson}}}""");
        var settings = new AppSettings(SettingsPath, new DpapiSecretProtector());

        settings.Load();

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.Equal("https://example.com/chat", settings.Endpoint);
        Assert.Equal("saved-model", settings.Model);
    }

    [Fact]
    public void SavingThemePreservesEncryptedApiConfiguration()
    {
        const string key = "theme-test-secret-key";
        var protector = new DpapiSecretProtector();
        var settings = new AppSettings(SettingsPath, protector);
        settings.Save("https://example.com/chat", "sample-model", key);
        var encryptedBefore = StoredSecret();

        settings.SaveTheme(AppTheme.Dark);

        var reopened = new AppSettings(SettingsPath, protector);
        reopened.Load();
        Assert.Equal(AppTheme.Dark, reopened.Theme);
        Assert.Equal("https://example.com/chat", reopened.Endpoint);
        Assert.Equal("sample-model", reopened.Model);
        Assert.Equal(key, reopened.ApiKey);
        Assert.Equal(encryptedBefore, StoredSecret());
        Assert.DoesNotContain(key, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void SavingApiConfigurationPreservesRememberedThemeWithoutPriorLoad()
    {
        new AppSettings(SettingsPath, new DpapiSecretProtector()).SaveTheme(AppTheme.System);

        new AppSettings(SettingsPath, new DpapiSecretProtector()).Save("https://example.com/chat", "model-two", "new-key");

        var reopened = new AppSettings(SettingsPath, new DpapiSecretProtector());
        reopened.Load();
        Assert.Equal(AppTheme.System, reopened.Theme);
        Assert.Equal("model-two", reopened.Model);
        Assert.Equal("new-key", reopened.ApiKey);
    }

    [Fact]
    public void SavingThemePreservesForeignEncryptedKeyWithoutPriorLoad()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, """{"Endpoint":"https://example.com/chat","Model":"remote-model","ProtectedApiKey":"foreign-encrypted-key"}""");

        new AppSettings(SettingsPath, new DpapiSecretProtector()).SaveTheme(AppTheme.Dark);

        Assert.Equal("foreign-encrypted-key", StoredSecret());
        var reopened = new AppSettings(SettingsPath, new DpapiSecretProtector());
        reopened.Load();
        Assert.Equal("https://example.com/chat", reopened.Endpoint);
        Assert.Equal("remote-model", reopened.Model);
        Assert.Equal(AppTheme.Dark, reopened.Theme);
    }

    [Fact]
    public void FailedApiSaveCannotLeakDraftConfigurationIntoTheNextThemeSave()
    {
        var settings = new AppSettings(SettingsPath, new DpapiSecretProtector());
        settings.Save("https://saved.example.com/chat", "saved-model", "saved-key");
        settings.SaveTheme(AppTheme.System);
        var savedJson = File.ReadAllText(SettingsPath);
        var savedCiphertext = StoredSecret();
        using (File.Open(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = Record.Exception(() => settings.Save("https://draft.example.com/chat", "draft-model", "draft-key"));
            Assert.True(failure is IOException or UnauthorizedAccessException,
                "Replacing an exclusively locked settings file must fail.");
        }
        Assert.Equal(savedJson, File.ReadAllText(SettingsPath));

        settings.SaveTheme(AppTheme.Dark);

        var reopened = new AppSettings(SettingsPath, new DpapiSecretProtector());
        reopened.Load();
        Assert.Equal("https://saved.example.com/chat", reopened.Endpoint);
        Assert.Equal("saved-model", reopened.Model);
        Assert.Equal("saved-key", reopened.ApiKey);
        Assert.Equal(savedCiphertext, StoredSecret());
        Assert.Equal(AppTheme.Dark, reopened.Theme);
    }

    [Fact]
    public void ProtectionFailureRetainsCommittedMemoryAndEncryptedConfiguration()
    {
        var protector = new RejectingProtector();
        var settings = new AppSettings(SettingsPath, protector);
        settings.Save("https://saved.example.com/chat", "saved-model", "saved-key");
        settings.SaveTheme(AppTheme.System);
        var savedJson = File.ReadAllText(SettingsPath);
        var savedCiphertext = StoredSecret();
        protector.RejectProtection = true;

        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            settings.Save("https://draft.example.com/chat", "draft-model", "draft-key"));

        Assert.Equal("https://saved.example.com/chat", settings.Endpoint);
        Assert.Equal("saved-model", settings.Model);
        Assert.Equal("saved-key", settings.ApiKey);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(savedJson, File.ReadAllText(SettingsPath));

        settings.SaveTheme(AppTheme.Dark);
        var reopened = new AppSettings(SettingsPath, new DpapiSecretProtector());
        reopened.Load();
        Assert.Equal("https://saved.example.com/chat", reopened.Endpoint);
        Assert.Equal("saved-model", reopened.Model);
        Assert.Equal("saved-key", reopened.ApiKey);
        Assert.Equal(savedCiphertext, StoredSecret());
        Assert.Equal(AppTheme.Dark, reopened.Theme);
    }

    [Fact]
    public void FailedThemeSaveRetainsTheLastCommittedPreferenceAndApiConfiguration()
    {
        var settings = new AppSettings(SettingsPath, new DpapiSecretProtector());
        settings.Save("https://saved.example.com/chat", "saved-model", "saved-key");
        settings.SaveTheme(AppTheme.System);
        var savedJson = File.ReadAllText(SettingsPath);
        using (File.Open(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = Record.Exception(() => settings.SaveTheme(AppTheme.Dark));
            Assert.True(failure is IOException or UnauthorizedAccessException,
                "Replacing an exclusively locked settings file must fail.");
        }

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal("https://saved.example.com/chat", settings.Endpoint);
        Assert.Equal("saved-model", settings.Model);
        Assert.Equal("saved-key", settings.ApiKey);
        Assert.Equal(savedJson, File.ReadAllText(SettingsPath));
    }

    private string? StoredSecret()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        return json.RootElement.GetProperty("ProtectedApiKey").GetString();
    }

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
