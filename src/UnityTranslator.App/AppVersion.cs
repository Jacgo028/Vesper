using System.Reflection;
using System.Windows;

namespace UnityTranslator.App;

/// <summary>
/// 版本号来自构建时写入的程序集信息（见仓库根目录 Version.props），
/// 界面、可执行文件属性、打包脚本因此共用同一个版本号，不会出现内外不一致。
/// </summary>
internal static class AppVersion
{
    /// <summary>完整版本，例如 V0.2。</summary>
    public static string Display { get; } = ReadDisplay();

    private static string ReadDisplay()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink 之类的工具会附加 +commit 后缀，只保留版本部分。
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        var version = assembly.GetName().Version;
        return version is null ? "V0.1" : $"V{version.Major}.{version.Minor}";
    }
}