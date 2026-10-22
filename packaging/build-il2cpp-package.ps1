# 构建并打包 IL2CPP 组件包（packages\il2cpp-bepinex6）。
#
# 为什么需要单独一个脚本：
#   `packaging\build.ps1` 只构建 unity6-mono / unity2022-mono / legacy-mono 三个 Mono 包，
#   **完全没有 IL2CPP 这一路** —— 交付目录里的 `packages\il2cpp-bepinex6`
#   一直是**手工放进去的**。这意味着下次重新打包会**静默漏掉整个 IL2CPP 支持**：
#   桌面端会把 IL2CPP 游戏算成"可以翻译"，然后找不到组件包，用户装不上。
#   这个脚本把那一块补成可复现的构建步骤。
#
# IL2CPP 包与 Mono 包的三点不同（所以不能照抄 Mono 那套）：
#   1. 加载器是 **BepInEx 6 的 IL2CPP 版**（不是 Mono 版），另外还需要一份 CoreCLR 运行时
#      （归档里的 `dotnet\` 目录）；
#   2. 插件要用 `Il2CppPlugin.csproj` 构建，并且**必须**指向某个游戏的 interop 目录 ——
#      互操作程序集是每款游戏各自生成的，不能随包分发，只用于编译期拿到 UnityEngine 基础类型；
#   3. 插件本体是 `UnityTranslator.Plugin.Il2Cpp.dll`（Mono 包是 `UnityTranslator.Plugin.dll`）。
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$DotNetExe,
    [string]$BepInExIl2CppArchive,
    [string]$BepInExIl2CppDirectory,
    # 任意一款 IL2CPP 游戏的 BepInEx\interop 目录（编译期引用 UnityEngine 基础类型用）
    [Parameter(Mandatory=$true)][string]$Il2CppInteropDir,
    # 归档校验值。写死是为了防止"换了个加载器却没人发现"。
    [string]$ExpectedArchiveHash = 'F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)

if ([string]::IsNullOrEmpty($BepInExIl2CppArchive) -eq [string]::IsNullOrEmpty($BepInExIl2CppDirectory)) {
    throw '必须指定一个 IL2CPP 加载器归档或已有的发行包目录。'
}
if ($BepInExIl2CppArchive) {
    if (-not (Test-Path -LiteralPath $BepInExIl2CppArchive)) { throw "加载器归档不存在：$BepInExIl2CppArchive" }
    $actual = (Get-FileHash -LiteralPath $BepInExIl2CppArchive -Algorithm SHA256).Hash
    if ($actual -ne $ExpectedArchiveHash) { throw "BepInEx IL2CPP 归档校验失败：$actual" }
    Write-Host "BepInEx IL2CPP 归档校验通过：$actual"
} elseif (-not (Test-Path -LiteralPath (Join-Path $BepInExIl2CppDirectory 'BepInEx/core/BepInEx.Unity.IL2CPP.dll')) -or
          -not (Test-Path -LiteralPath (Join-Path $BepInExIl2CppDirectory 'dotnet'))) {
    throw 'IL2CPP 发行包目录缺少加载器或运行时。'
}

if (-not (Test-Path -LiteralPath $Il2CppInteropDir)) { throw "找不到 interop 目录：$Il2CppInteropDir" }
Write-Host ("interop 目录：{0}（{1} 个 dll）" -f $Il2CppInteropDir, (Get-ChildItem $Il2CppInteropDir -Filter '*.dll').Count)

# ── 1) 先展开实际交付的加载器，编译引用必须与包内 core 完全一致 ──
$package = Join-Path $output 'il2cpp-bepinex6'
if (Test-Path -LiteralPath $package) { throw "组件包目录已经存在，不能覆盖：$package" }
New-Item -ItemType Directory -Path $package -Force | Out-Null
if ($BepInExIl2CppArchive) {
    Expand-Archive -LiteralPath $BepInExIl2CppArchive -DestinationPath $package
} else {
    Copy-Item -Path (Join-Path $BepInExIl2CppDirectory '*') -Destination $package -Recurse
}
$loaderCore = Join-Path $package 'BepInEx/core'
foreach ($coreFile in @('BepInEx.Core.dll', 'BepInEx.Unity.IL2CPP.dll', 'Il2CppInterop.Runtime.dll', '0Harmony.dll')) {
    $corePath = Join-Path $loaderCore $coreFile
    if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) { throw "IL2CPP 发行包缺少必需的 core 文件：$corePath" }
}

# ── 2) 构建 IL2CPP 插件；游戏 interop 只用于编译，不复制进包 ──
& $DotNetExe build (Join-Path $root 'il2cpp/Il2CppPlugin.csproj') -c Release `
    -o (Join-Path $root 'il2cpp/out-v05') `
    "-p:CoreDir=$(Join-Path $root 'src/UnityTranslator.Core')" `
    "-p:LoaderCore=$loaderCore" `
    "-p:InteropDir=$Il2CppInteropDir"
if ($LASTEXITCODE -ne 0) { throw 'IL2CPP 插件编译失败。' }

$builtDll = Join-Path $root 'il2cpp/out-v05/UnityTranslator.Plugin.Il2Cpp.dll'
if (-not (Test-Path -LiteralPath $builtDll)) { throw "插件输出缺失：$builtDll" }
$pluginHash = (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash
Write-Host "IL2CPP 插件构建完成：SHA256 $pluginHash"

# ── 3) 放入按此加载器构建的插件 ──
$plugins = Join-Path $package 'BepInEx/plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
Copy-Item -LiteralPath $builtDll -Destination (Join-Path $plugins 'UnityTranslator.Plugin.Il2Cpp.dll')

# 随包附带的安装说明（如果源码树里有就一起带上，保证交付包自解释）
$readme = Join-Path $root 'il2cpp/安装说明-IL2CPP.md'
if (Test-Path -LiteralPath $readme) { Copy-Item -LiteralPath $readme -Destination $package }

$files = Get-ChildItem -LiteralPath $package -Recurse -File
Write-Host ("包已生成：{0} 个文件，{1:N1} MB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))
Write-Host "  BepInEx\plugins\UnityTranslator.Plugin.Il2Cpp.dll  SHA256=$pluginHash"
