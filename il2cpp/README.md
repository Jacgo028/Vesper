# Vesper IL2CPP 插件

`TranslatorPlugin.cs`和`PipeClient.cs`是游戏内IL2CPP翻译插件，`Il2CppPlugin.csproj`通过MSBuild属性引用共享源码、BepInEx IL2CPP和目标游戏interop。它不是旧临时实验目录的预编译输出。

按仓库根目录[BUILD.md](../BUILD.md)准备依赖并传入`CoreDir`、`LoaderCore`、`InteropDir`后构建；本目录不附带游戏互操作程序集或预编译插件。完整组件包由`packaging/build-il2cpp-package.ps1`产生，并由桌面端按实际游戏和组件版本管理。

支持IL2CPP路径不等于所有游戏都兼容；图片文字的外部OCR覆盖由桌面端负责，不等于在IL2CPP内部集成了Mono嵌入式OCR。
