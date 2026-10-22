# Vesper 构建说明

## 桌面程序与测试

使用 Windows x64、.NET 8 SDK。此源码使用8.0.425验证；外部窗口捕获路径需要相应 Windows Graphics Capture 能力，不代表旧版 Windows 支持已验证。

```powershell
dotnet restore UnityTranslator.sln --locked-mode
dotnet build UnityTranslator.sln -c Release --no-restore
dotnet test UnityTranslator.sln -c Release --no-build
```

解决方案包含桌面程序、Core 和两套主测试，游戏插件另行按目标运行时构建。WPF／Windows原生测试不适合直接在 Linux 无头环境运行。首次还原需要能访问 NuGet；所有已有锁文件都应保留。

桌面单文件发布：

```powershell
dotnet publish src/UnityTranslator.App/UnityTranslator.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
  -p:EnableCompressionInSingleFile=false -p:RestoreLockedMode=true `
  -o artifacts/app
```

此命令只发布桌面程序。保留产生的原生依赖和许可文件，不要只复制一个EXE就当成完整发行包。`Version.props` 是产品版本的唯一来源。

## Mono 插件

准备目标游戏的 `<游戏名>_Data/Managed` 目录和匹配的 BepInEx 发行目录。下例路径是占位，必须替换成本机有权使用的文件：

```powershell
dotnet build src/UnityTranslator.Plugin/UnityTranslator.Plugin.csproj `
  -c Release -t:Rebuild `
  '-p:GameManagedDir=D:/Games/Example/Example_Data/Managed' `
  '-p:BepInExDir=D:/Dependencies/BepInExMono' `
  -p:BepInExMajor=5 -p:PluginTargetFramework=netstandard2.1
```

不能用一份插件DLL代替所有运行时。`packaging/build.ps1` 按配置分别构建 Unity6／2022／2021／2020／旧版Slim等变体，加载器和框架以该脚本的映射为准。旧版可能使用 `net46` 或 `net35` 并引用游戏自身的基础类库；不要盲目套用上例参数。

## IL2CPP 插件

准备匹配的 BepInEx IL2CPP 发行包和目标游戏生成的 `BepInEx/interop`。这些游戏文件不在源码中，也不应上传到仓库。

```powershell
dotnet build il2cpp/Il2CppPlugin.csproj -c Release `
  "-p:CoreDir=$PWD/src/UnityTranslator.Core" `
  '-p:LoaderCore=D:/Dependencies/BepInExIL2CPP/BepInEx/core' `
  '-p:InteropDir=D:/Games/Example/BepInEx/interop'
```

插件目标为 .NET6 游戏运行时；构建机需要还原对应引用。IL2CPP 插件不在主解决方案中，以免没有游戏引用时破坏普通桌面构建。不要将某个游戏生成的 interop 当作通用SDK分发。

## 完整发行包

`packaging/build.ps1` 接受以下外部输入，参数均为调用者提供的本机路径：

- `OutputDirectory`：新的空输出目录。
- `DotNetExe`：SDK入口。
- `BepInEx5Archive`、`BepInEx6Archive`：脚本锁定版本与SHA的Mono加载器归档。
- `TesseractDirectory`：含OCR程序、依赖、许可和 `tessdata/jpn.traineddata`／`eng.traineddata` 的目录。
- `Unity6ManagedDir`、`Unity2022ManagedDir`、`Unity2021ManagedDir`、`Unity2020ManagedDir`、`LegacyManagedDir`：相应真实游戏／运行时引用。
- `Il2CppLoaderDirectory`、`Il2CppInteropDir`：IL2CPP加载器和互操作引用。

完整脚本还会调用 `packaging/build-il2cpp-package.ps1`，生成游戏组件目录。它会校验固定归档和OCR文件哈希，不能把任意不同版本重命名后绕过校验。准备好上述输入并核对第三方许可后再运行；本源码包不含这些二进制输入。

`packaging/verify-external-ocr.ps1` 是原始严格验收检查器，要求真实运行证据；缺少证据时退出非零是预期行为，不要伪造通过记录或直接复制旧版本证据。`packaging/publish-input-freeze.targets` 可冻结同次单文件发布的实际载荷用于核对。

对本次导出只验证桌面方案的构建／测试，不借此宣称所有Mono／IL2CPP插件已重新构建或完成真实游戏验收。
