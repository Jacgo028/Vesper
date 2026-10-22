using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-tests", Guid.NewGuid().ToString("N"));
    private string NewPath(string name) => Path.Combine(_root, name);
    // 每个测试类实例都有自己的 _root（含 GUID），所以固定文件名之间不会互相干扰
    private string File => NewPath("settings.json");

    [Fact]
    public void ApiKeySurvivesRestartAndIsNotWrittenInPlainText()
    {
        const string key = "sk-live-1234567890-ABCDEFGHIJ";
        var protector = new FakeProtector();
        new AppSettings(File, protector).Save("https://api.example.com/v1/chat/completions", "gpt-4o-mini", key);

        // 模拟程序退出后重新打开
        var reopened = new AppSettings(File, protector);
        reopened.Load();

        Assert.Equal(key, reopened.ApiKey);
        Assert.Equal("https://api.example.com/v1/chat/completions", reopened.Endpoint);
        Assert.Equal("gpt-4o-mini", reopened.Model);
        Assert.DoesNotContain(key, System.IO.File.ReadAllText(File));
        // 接口地址是明文存的，只有密钥加密；这里确认密钥确实不在文件里
        Assert.Contains("api.example.com", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void MissingFileKeepsDeepSeekDefaults()
    {
        var settings = new AppSettings(File, new FakeProtector());
        settings.Load();

        Assert.Equal("https://api.deepseek.com/chat/completions", settings.Endpoint);
        Assert.Equal("deepseek-flash", settings.Model);
        Assert.Equal(string.Empty, settings.ApiKey);
    }

    [Fact]
    public void BlankFieldsFallBackToDefaultsInsteadOfPersistingEmptyValues()
    {
        var settings = new AppSettings(File, new FakeProtector());
        settings.Save("   ", "", "sk-key");

        var reopened = new AppSettings(File, new FakeProtector());
        reopened.Load();

        Assert.Equal("https://api.deepseek.com/chat/completions", reopened.Endpoint);
        Assert.Equal("deepseek-flash", reopened.Model);
        Assert.Equal("sk-key", reopened.ApiKey);
    }

    [Fact]
    public void CorruptFileDoesNotBreakStartup()
    {
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(File, "{ this is not json");

        var settings = new AppSettings(File, new FakeProtector());
        settings.Load();

        Assert.Equal("https://api.deepseek.com/chat/completions", settings.Endpoint);
        Assert.Equal(string.Empty, settings.ApiKey);
    }

    [Fact]
    public void UndecryptableKeyIsTreatedAsMissing()
    {
        // 密钥是用另一台电脑或另一个 Windows 账户加密的，本机解不开；
        // 用独立文件，避免读到同一测试类里其它测试写下的内容
        var path = NewPath("foreign-settings.json");
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(path,
            """{"Endpoint":"https://api.example.com/chat/completions","Model":"m","ProtectedApiKey":"bm90LXJlYWxseS1lbmNyeXB0ZWQ="}""");

        var settings = new AppSettings(path, new FakeProtector());
        settings.Load();

        Assert.Equal(string.Empty, settings.ApiKey);
        Assert.Equal("https://api.example.com/chat/completions", settings.Endpoint);
    }

    [Fact]
    public void DpapiProtectorRoundTripsAndStaysOffDiskInPlainText()
    {
        var protector = new DpapiSecretProtector();
        const string key = "sk-密钥-测试-42";
        new AppSettings(File, protector).Save("https://api.deepseek.com/chat/completions", "deepseek-flash", key);

        var reopened = new AppSettings(File, protector);
        reopened.Load();

        Assert.Equal(key, reopened.ApiKey);
        var raw = System.IO.File.ReadAllText(File);
        Assert.DoesNotContain(key, raw);
        Assert.DoesNotContain("sk-", raw);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// 替身，行为与真实保护器一致：可逆、不落明文，并且**拒绝不是自己产生的数据**
    /// （真实 DPAPI 遇到别处加密的数据也会失败）。
    /// </summary>
    private sealed class FakeProtector : ISecretProtector
    {
        private const string Marker = "FAKE1:";

        public string Protect(string plainText) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                Marker + new string(plainText.Reverse().ToArray())));

        public string Unprotect(string protectedBase64)
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedBase64));
            if (!decoded.StartsWith(Marker, StringComparison.Ordinal))
                throw new System.ComponentModel.Win32Exception(13, "数据不是由本机当前用户加密的");
            return new string(decoded[Marker.Length..].Reverse().ToArray());
        }
    }
}
