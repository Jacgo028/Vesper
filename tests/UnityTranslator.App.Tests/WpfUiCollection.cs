using System.Windows;

namespace UnityTranslator.App.Tests;

/// <summary>
/// 所有会真正构造 WPF 窗口的测试共用一个集合，串行执行。
///
/// 两个原因：
/// 1. 多个线程并发调用 <see cref="Application.LoadComponent"/> 加载同一份 BAML 时，
///    .NET 内部 System.IO.Packaging.PackagePart 的共享流列表不是线程安全的
///    （会抛 ArgumentOutOfRangeException: List.RemoveAt），并发创建窗口会偶发失败。
///    真实程序只在唯一的界面线程上加载一次 XAML，不受这个缺陷影响。
/// 2. MainWindow.xaml 使用 StaticResource 引用主题令牌，这些令牌由 App.xaml 合并的
///    Theme.xaml 提供。测试进程默认没有 Application 实例，必须先建一个并载入主题，
///    否则窗口构造会抛“无法找到名为 BgWindow 的资源”。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection : IDisposable
{
    public const string Name = "WPF UI (串行)";

    public WpfUiCollection() => EnsureApplicationOnStaThread();

    /// <summary>
    /// 在独立的 STA 线程上建立 Application 并载入主题资源。
    /// 资源字典在这里被冻结，因此其它线程（各测试自己的 STA 线程）读取是安全的。
    /// </summary>
    private static void EnsureApplicationOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var resources = Application.Current!.Resources;
                if (resources.MergedDictionaries.Count == 0)
                    resources.MergedDictionaries.Add(ThemeDictionary());
                if (!resources.Contains("BgWindow"))
                    throw new InvalidOperationException(
                        $"主题资源没有加载成功：字典数={resources.MergedDictionaries.Count}，键数={resources.Count}，" +
                        $"探测 TextPrimary={resources.Contains("TextPrimary")}");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (failure is not null)
            throw new InvalidOperationException("无法为界面测试准备主题资源", failure);
    }

    private static ResourceDictionary ThemeDictionary() => new ResourceDictionary
    {
        Source = new Uri("pack://application:,,,/UnityTranslator.App;component/Theme.xaml", UriKind.Absolute)
    };

    public void Dispose() { }
}
