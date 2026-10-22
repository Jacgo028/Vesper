# 第三方组件与来源

项目级GPL-3.0-only不替代第三方许可；保留原有声明。源码包主要提供项目自身代码和编译引用，不包含游戏或完整运行发行包。

- Fluent UI System Icons：MIT。原许可、固定提交和图标路径在 `src/UnityTranslator.App/Assets/Fluent/`。WPF图标使用原路径数据及主题颜色。
- BepInEx、UnityDoorstop：加载器／插件运行依赖。原发行说明记录为LGPL-2.1，来源分别为 https://github.com/BepInEx/BepInEx 和 https://github.com/NeighTools/UnityDoorstop 。本源码包不内置其二进制；分发运行包须保留匹配版本的完整许可与对应义务。
- Tesseract、tessdata_fast：本机OCR和语言模型，发行说明记录为Apache-2.0。模型和OCR二进制未打入源码包。来源为 https://github.com/tesseract-ocr/tesseract 和 https://github.com/tesseract-ocr/tessdata_fast 。
- Vortice.Windows／Mathematics、SharpGen：外部窗口捕获依赖。固定版本锁文件、MIT许可文本及来源位于 `packaging/licenses/external-ocr/`。
- Microsoft.Windows.SDK.NET／WinRT.Runtime：由Windows SDK引用包提供，按其SDK条款处理，不统一改标MIT或GPL。所记录的SDK许可、再分发清单及出处同上。
- Microsoft.Data.Sqlite、Microsoft.NET.Test.Sdk、xUnit、coverlet等：通过NuGet还原，版本以项目和锁文件为准，各依赖保留自身许可。
- 必应网页翻译协议参考 plainheart/bing-translate-api 的Edge实现（MIT），固定参考版本为 `f37b17b8bc7848b3be38f4440bec5285ad1fd3e2`：https://github.com/plainheart/bing-translate-api/blob/f37b17b8bc7848b3be38f4440bec5285ad1fd3e2/src/met/index.js 。程序是独立C#实现，未打包该JavaScript项目。非官方通路不构成微软授权或服务保证。

本软件没有打包Cheat Engine本体／驱动，也没有集成LunaTranslator代码。引用项目作可行性研究不等于把它纳入本仓库。

6.1运行包沿用的历史声明原文保留在 `docs/third-party/runtime-notices-0.5.9.txt`，其中日期、候选状态与历史版本是原记录，不代替本次发行说明。第三方原文中的历史候选表述也不表示当前主程序版本回退。

游戏的Managed、interop、资源、商业字体和存档不在源码中。构建时使用本机系统／游戏引用不授予这些材料的再分发权。公开二进制或更改依赖前需另行核对适用条款；这份清单不是对所有组合发行方式的法律认证。
