using System.Runtime.InteropServices;
using System.Text.Json;

namespace UnityTranslator.Core;

public enum AppTheme
{
    Light,
    Dark,
    System
}

public enum TranslationMode
{
    Free,
    Api
}

public enum FreeTranslationProvider
{
    Bing,
    Baidu
}

/// <summary>加解密一小段文本。真实实现用 Windows DPAPI 按当前用户保护。</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedBase64);
}

/// <summary>
/// DPAPI（当前用户范围），直接调用 Windows 自带的 crypt32.dll，不引入额外 NuGet 依赖。
/// 密钥只对本机当前 Windows 账户可解，复制到别的电脑或别的账户下都无法还原。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public string Protect(string plainText) =>
        Convert.ToBase64String(Transform(System.Text.Encoding.UTF8.GetBytes(plainText), protect: true));

    public string Unprotect(string protectedBase64) =>
        System.Text.Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedBase64), protect: false));

    private static byte[] Transform(byte[] input, bool protect)
    {
        var source = ToBlob(input);
        var result = default(DataBlob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref result);
            if (!ok)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows 数据保护接口调用失败");
            var output = new byte[result.Length];
            Marshal.Copy(result.Data, output, 0, result.Length);
            return output;
        }
        finally
        {
            if (source.Data != IntPtr.Zero) Marshal.FreeHGlobal(source.Data);
            if (result.Data != IntPtr.Zero) LocalFree(result.Data);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.Data, data.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}

/// <summary>
/// 翻译服务与外观的本机配置。密钥经过 <see cref="ISecretProtector"/> 保护，
/// 外观偏好与服务地址保存在同一份配置中，密钥不落明文。
/// </summary>
public sealed class AppSettings
{
    private const string DefaultEndpoint = "https://api.deepseek.com/chat/completions";
    private const string DefaultModel = "deepseek-flash";

    private readonly string _filePath;
    private readonly ISecretProtector _protector;
    private string _protectedApiKey = string.Empty;
    private bool _loaded;

    public AppSettings(string filePath, ISecretProtector protector)
    {
        _filePath = Path.GetFullPath(filePath);
        _protector = protector;
    }

    public string Endpoint { get; private set; } = DefaultEndpoint;

    public string Model { get; private set; } = DefaultModel;

    public string ApiKey { get; private set; } = string.Empty;

    public AppTheme Theme { get; private set; } = AppTheme.Light;

    public TranslationMode Mode { get; private set; } = TranslationMode.Free;

    public FreeTranslationProvider FreeProvider { get; private set; } = FreeTranslationProvider.Bing;

    public void Load()
    {
        _loaded = true;
        Endpoint = DefaultEndpoint;
        Model = DefaultModel;
        ApiKey = string.Empty;
        _protectedApiKey = string.Empty;
        Theme = AppTheme.Light;
        Mode = TranslationMode.Free;
        FreeProvider = FreeTranslationProvider.Bing;
        if (!File.Exists(_filePath)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_filePath));
            if (stored is null) return;
            Endpoint = Choice(stored.Endpoint, DefaultEndpoint);
            Model = Choice(stored.Model, DefaultModel);
            _protectedApiKey = stored.ProtectedApiKey ?? string.Empty;
            ApiKey = Unprotect(_protectedApiKey);
            Theme = ReadTheme(stored.Theme);
            Mode = stored.Mode.ValueKind == JsonValueKind.Undefined
                ? (_protectedApiKey.Length > 0 ? TranslationMode.Api : TranslationMode.Free)
                : ReadChoice(stored.Mode, TranslationMode.Free);
            FreeProvider = ReadChoice(stored.FreeProvider, FreeTranslationProvider.Bing);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception or FormatException or ArgumentException)
        {
            // 配置损坏或无法解密（例如换了 Windows 账户）时回退到默认值，不影响启动
            Endpoint = DefaultEndpoint;
            Model = DefaultModel;
            ApiKey = string.Empty;
            _protectedApiKey = string.Empty;
            Theme = AppTheme.Light;
            Mode = TranslationMode.Free;
            FreeProvider = FreeTranslationProvider.Bing;
        }
    }

    public void Save(string endpoint, string model, string apiKey)
    {
        if (!_loaded) Load();
        Save(endpoint, model, apiKey, Mode, FreeProvider);
    }

    public void Save(string endpoint, string model, string apiKey,
        TranslationMode mode, FreeTranslationProvider freeProvider)
    {
        if (!_loaded) Load();
        var nextEndpoint = Choice(endpoint, DefaultEndpoint);
        var nextModel = Choice(model, DefaultModel);
        var nextApiKey = apiKey ?? string.Empty;
        var nextProtectedApiKey = nextApiKey.Length == 0 ? string.Empty : _protector.Protect(nextApiKey);
        var nextMode = Enum.IsDefined(mode) ? mode : TranslationMode.Free;
        var nextFreeProvider = Enum.IsDefined(freeProvider) ? freeProvider : FreeTranslationProvider.Bing;
        var candidate = new Stored
        {
            Endpoint = nextEndpoint,
            Model = nextModel,
            ProtectedApiKey = nextProtectedApiKey,
            Theme = JsonSerializer.SerializeToElement(Theme.ToString()),
            Mode = JsonSerializer.SerializeToElement(nextMode.ToString()),
            FreeProvider = JsonSerializer.SerializeToElement(nextFreeProvider.ToString())
        };

        Persist(candidate);
        Endpoint = nextEndpoint;
        Model = nextModel;
        ApiKey = nextApiKey;
        _protectedApiKey = nextProtectedApiKey;
        Mode = nextMode;
        FreeProvider = nextFreeProvider;
    }

    public void SaveTranslationChoice(TranslationMode mode, FreeTranslationProvider freeProvider)
    {
        if (!_loaded) Load();
        var nextMode = Enum.IsDefined(mode) ? mode : TranslationMode.Free;
        var nextFreeProvider = Enum.IsDefined(freeProvider) ? freeProvider : FreeTranslationProvider.Bing;
        var candidate = new Stored
        {
            Endpoint = Endpoint,
            Model = Model,
            ProtectedApiKey = _protectedApiKey,
            Theme = JsonSerializer.SerializeToElement(Theme.ToString()),
            Mode = JsonSerializer.SerializeToElement(nextMode.ToString()),
            FreeProvider = JsonSerializer.SerializeToElement(nextFreeProvider.ToString())
        };

        Persist(candidate);
        Mode = nextMode;
        FreeProvider = nextFreeProvider;
    }

    public void SaveTheme(AppTheme theme)
    {
        if (!_loaded) Load();
        var nextTheme = Enum.IsDefined(theme) ? theme : AppTheme.Light;
        var candidate = new Stored
        {
            Endpoint = Endpoint,
            Model = Model,
            ProtectedApiKey = _protectedApiKey,
            Theme = JsonSerializer.SerializeToElement(nextTheme.ToString()),
            Mode = JsonSerializer.SerializeToElement(Mode.ToString()),
            FreeProvider = JsonSerializer.SerializeToElement(FreeProvider.ToString())
        };

        Persist(candidate);
        Theme = nextTheme;
    }

    private void Persist(Stored candidate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var payload = JsonSerializer.Serialize(candidate);
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, payload);
        File.Move(temporary, _filePath, overwrite: true);
    }

    private string Unprotect(string? protectedApiKey)
    {
        if (string.IsNullOrEmpty(protectedApiKey)) return string.Empty;
        try
        {
            return _protector.Unprotect(protectedApiKey);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FormatException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string Choice(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static AppTheme ReadTheme(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        Enum.TryParse<AppTheme>(value.GetString(), ignoreCase: true, out var theme) && Enum.IsDefined(theme) &&
        string.Equals(value.GetString(), theme.ToString(), StringComparison.OrdinalIgnoreCase)
            ? theme
            : AppTheme.Light;

    private static T ReadChoice<T>(JsonElement value, T fallback) where T : struct, Enum =>
        value.ValueKind == JsonValueKind.String &&
        Enum.TryParse<T>(value.GetString(), ignoreCase: true, out var choice) && Enum.IsDefined(choice) &&
        string.Equals(value.GetString(), choice.ToString(), StringComparison.OrdinalIgnoreCase)
            ? choice
            : fallback;

    private sealed class Stored
    {
        public string? Endpoint { get; set; }
        public string? Model { get; set; }
        public string? ProtectedApiKey { get; set; }
        public JsonElement Theme { get; set; }
        public JsonElement Mode { get; set; }
        public JsonElement FreeProvider { get; set; }
    }
}
