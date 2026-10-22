using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

/// <summary>
/// 安装确认框以前会把两百多个文件名整整齐齐列出来，用户的原话是
/// "我们不需要显示具体需要安装什么，只需要显示需要安装多少个就好了"。
/// 这里把文案钉住，避免以后又退回去。
/// </summary>
public sealed class InstallDialogWordingTests
{
    [Fact]
    public void ReportsCountsOnlyAndNeverListsFileNames()
    {
        var files = Enumerable.Range(0, 230)
            .Select(index => new PackageFile($"BepInEx/plugins/file{index}.dll", [1])).ToArray();
        var plan = new InstallPlan(@"C:\game", @"C:\game\.unity-translator-install.json",
            files,
            [@"C:\game\winhttp.dll"],
            [@"C:\game\BepInEx\plugins\UnityTranslator.Plugin.dll"],
            [@"C:\game\BepInEx\core\BepInEx.Core.dll"],
            ["XUnity.AutoTranslator"]);

        var message = MainWindow.DescribeInstall(plan);

        Assert.Contains("230 个文件", message);
        Assert.Contains("新增 228 个", message);                       // 230 - 1 替换 - 1 更新
        Assert.Contains("替换游戏目录里已有的同名文件 1 个", message);
        Assert.Contains("更新本工具以前装过的 1 个", message);
        Assert.Contains("跳过不写", message);
        Assert.DoesNotContain("file0.dll", message);                   // 一个文件名都不许出现
        Assert.DoesNotContain("BepInEx/plugins", message);
    }

    /// <summary>
    /// 游戏目录里本来就有别的汉化时，必须主动说明 —— 否则用户会以为自己的汉化被无声换掉了。
    /// </summary>
    [Fact]
    public void WarnsWhenTheGameAlreadyHasAnotherTranslationComponent()
    {
        var plan = new InstallPlan(@"C:\game", @"C:\game\.unity-translator-install.json",
            [new PackageFile("BepInEx/plugins/UnityTranslator.Plugin.Il2Cpp.dll", [1])],
            [], [], [], ["XUnity.AutoTranslator", "AutoTranslator 汉化配置"]);

        var message = MainWindow.DescribeInstall(plan);

        Assert.Contains("已经装了别的汉化组件", message);
        Assert.Contains("XUnity.AutoTranslator", message);
        Assert.Contains("AutoTranslator 汉化配置", message);
        Assert.Contains("卸载时会还原", message);
    }

    /// <summary>全新安装（目录里什么都没有）时不该出现"替换/跳过"这些吓人的话。</summary>
    [Fact]
    public void SaysNothingAboutReplacingOnACleanInstall()
    {
        var plan = new InstallPlan(@"C:\game", @"C:\game\.unity-translator-install.json",
            [new PackageFile("BepInEx/plugins/UnityTranslator.Plugin.dll", [1])],
            [], [], [], []);

        var message = MainWindow.DescribeInstall(plan);

        Assert.Contains("1 个文件", message);
        Assert.DoesNotContain("替换", message);
        Assert.DoesNotContain("跳过", message);
        Assert.DoesNotContain("别的汉化组件", message);
    }
}
