using System.Runtime.CompilerServices;

// 让测试与诊断工具能够直接调用 MainWindow 里的内部成员（例如 AddGame），
// 以便在没有界面交互的情况下复现"拖入/选择之后什么都没发生"这类问题。
[assembly: InternalsVisibleTo("UnityTranslator.App.Tests")]
[assembly: InternalsVisibleTo("ut-vis")]
[assembly: InternalsVisibleTo("ut-layout")]
[assembly: InternalsVisibleTo("ut-feedback")]
[assembly: InternalsVisibleTo("ut-snap")]
// 真机流程验证工具：在没有界面点击能力的环境里，用真实游戏内存驱动整条数值修改流程。
[assembly: InternalsVisibleTo("ut-flowverify")]
// 安装界面验证工具：在**副本 + 隔离数据目录**上驱动真正的安装确认流程，
// 用来证明对话框只说数量、不罗列文件名，而不必去点真实游戏库里的条目。
[assembly: InternalsVisibleTo("ut-installui")]
// 加载器回退验证工具：在**副本 + 隔离数据目录**上让真正的 MainWindow 走一遍
// "启动游戏 → 看门狗发现预加载器崩了 → 自动换成 BepInEx 5 → 记住结论"，
// 用来证明这条**无人值守**的路径真的会自己走完（它出错时用户没有任何线索）。
[assembly: InternalsVisibleTo("ut-loaderfallback-app")]
