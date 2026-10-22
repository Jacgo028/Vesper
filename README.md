# Vesper暮星

Vesper 是面向 Windows x64 Unity 游戏的翻译器，帮助玩家减少语言障碍、享受游戏。此仓库为 Vesper-6.1 的产品源码，包含桌面程序、Mono／IL2CPP 插件与自动化测试。

## 功能

- 游戏库：拖入游戏程序或目录，搜索、重命名、打开文件夹和移出记录；移出不删除游戏。
- 文字翻译：Mono 与 IL2CPP 路径，优先保留原生控件的字号、布局和字体；安全测量失败时保留原文。
- 翻译服务：独立免费入口（必应网页通路）与用户自配的兼容聊天补全 API；配置分别保留。
- 图片文字：本机 OCR，根据游戏组件与环境使用嵌入式识图或外部窗口覆盖；不支持时明确提示。
- 数值工具：精确／未知初值／范围扫描、变化筛选、地址表、读写、锁定，以及受支持的 CE `.CT` 数值条目导入导出。使用本项目自己的实现，不内置 Cheat Engine 本体、驱动或完整调试器。
- A 版界面，支持白天／黑夜模式。

不保证任意 Unity 版本、游戏或自定义渲染文字都兼容。外部识图覆盖主要面向窗口化／无边框窗口；独占全屏、复杂背景、艺术字、小字和快速变化内容可能无法可靠识别。数值工具只用于你有权操作的离线游戏，不提供反作弊绕过。

## 快速开始

在 Windows 上安装 .NET 8 SDK（此版本使用 8.0.425 验证），从仓库根目录执行：

```powershell
dotnet restore UnityTranslator.sln --locked-mode
dotnet test UnityTranslator.sln -c Release --no-restore
dotnet run --project src/UnityTranslator.App/UnityTranslator.App.csproj -c Release
```

源码能独立构建桌面主程序，但不附带预编译游戏组件、OCR 引擎／模型或游戏程序集。完整运行包的准备方法见 [构建说明](BUILD.md)。仅编译桌面端不等于已经得到完整的可翻译游戏发行包。

## 目录

```text
src/UnityTranslator.App/       WPF 桌面界面、翻译服务、OCR 和数值工具
src/UnityTranslator.Core/      检测、布局、存储、协议等共享逻辑
src/UnityTranslator.Plugin/    Mono 游戏插件，包括旧版 Slim 变体
il2cpp/                       IL2CPP 游戏插件
tests/                        自动化测试与测试辅助项目
packaging/                    构建、单文件发布及验收脚本
```

## 数据与翻译服务

正式用户数据保存在 `%LOCALAPPDATA%/UnityTranslator`，不在源码仓库中。API 密钥在本机由当前 Windows 账户加密保存；开启联网翻译后，游戏文字会发到用户选择的服务。

免费必应入口是非官方网页通路，可能限流、变化或停止；首次翻译预计可能等待约30秒，并非固定时延或服务可用性承诺。实时翻译速度将会取决于您选用的服务商以及您的网络。

提交问题前请先移除密钥、日志中的私人文字、存档及个人路径。详见 [隐私与问题报告](SECURITY.md)。

## 验证范围

6.1 界面交付前完整回归为 Core 471 + App 835，共1306项通过，另检查了实际发布程序后台启动和正常退出。本次源码导出的验证结果见 [导出说明](EXPORT.md)。这些检查不代表所有游戏、在线供应商、DPI 或显示环境都已通过；6.1 没有把6.0旧游戏证据当作新EXE实测。

## 许可证

本项目自有代码采用 **GNU General Public License v3.0 only**，SPDX 标识 `GPL-3.0-only`，见 [LICENSE](LICENSE)。不自动授权改用未来版本的 GPL。保留文件中的原有版权和许可声明。

第三方代码、图标、依赖和工具按其各自许可提供，不因本项目选用 GPL 而被重新许可。参见 [第三方声明](THIRD-PARTY-NOTICES.md)。分发衍生版本时应履行适用的源码和许可义务；发行二进制时也需核对所附第三方组件的再分发条件。
