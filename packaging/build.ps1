# 注意：本文件必须保存为「带 BOM 的 UTF-8」。
# 不带 BOM 时 Windows PowerShell 会按 ANSI 解析，中文字符串会变成乱码并导致语法错误。
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$DotNetExe,
    [Parameter(Mandatory=$true)][string]$BepInEx5Archive,
    [Parameter(Mandatory=$true)][string]$BepInEx6Archive,
    [Parameter(Mandatory=$true)][string]$TesseractDirectory,
    [Parameter(Mandatory=$true)][string]$Unity6ManagedDir,
    [Parameter(Mandatory=$true)][string]$Unity2022ManagedDir,
    [Parameter(Mandatory=$true)][string]$Unity2021ManagedDir,
    [Parameter(Mandatory=$true)][string]$Unity2020ManagedDir,
    [Parameter(Mandatory=$true)][string]$LegacyManagedDir,
    [Parameter(Mandatory=$true)][string]$Il2CppLoaderDirectory,
    [Parameter(Mandatory=$true)][string]$Il2CppInteropDir,
    [switch]$SkipApp
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

# 版本号唯一来源：仓库根目录的 Version.props。
# 打包时读出来写进说明文件，保证“软件内显示的版本”和“交付物标注的版本”一致。
$versionProps = Join-Path $root 'Version.props'
if (-not (Test-Path -LiteralPath $versionProps)) { throw '缺少 Version.props，无法确定版本号。' }
$versionMatch = [regex]::Match((Get-Content -LiteralPath $versionProps -Raw), '<AppVersion>([^<]+)</AppVersion>')
if (-not $versionMatch.Success) { throw 'Version.props 里没有 AppVersion。' }
$appVersion = 'V' + $versionMatch.Groups[1].Value.Trim()
Write-Host "打包版本：$appVersion"
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if ((Get-ChildItem -LiteralPath $output -Force | Measure-Object).Count -ne 0) {
        throw '输出目录必须为空，以免覆盖已有文件。'
    }
} else { New-Item -ItemType Directory -Path $output | Out-Null }

$archive5Hash = (Get-FileHash -LiteralPath $BepInEx5Archive -Algorithm SHA256).Hash
$archive6Hash = (Get-FileHash -LiteralPath $BepInEx6Archive -Algorithm SHA256).Hash
if ($archive5Hash -ne '41A089E5B1B1F0713B331346BAF6677B1184C69EABEBF51101097954E854C749' -or
    $archive6Hash -ne 'D7455185E8A0A01EB122947C59229F3FFCCEED853FEBD4707C08219743D8F477') {
    throw 'BepInEx 归档文件校验失败。'
}
$loaderRoot = Join-Path $output '.loader-staging'
$loader5 = Join-Path $loaderRoot 'bepinex5'
$loader6 = Join-Path $loaderRoot 'bepinex6'
Expand-Archive -LiteralPath $BepInEx5Archive -DestinationPath $loader5
Expand-Archive -LiteralPath $BepInEx6Archive -DestinationPath $loader6

if (-not $SkipApp) {
    & $DotNetExe publish (Join-Path $root 'src/UnityTranslator.App/UnityTranslator.App.csproj') `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false -p:RestoreLockedMode=true -o $output
    if ($LASTEXITCODE -ne 0) { throw 'App 发布失败。' }
    Move-Item -LiteralPath (Join-Path $output 'UnityTranslator.App.exe') -Destination (Join-Path $output 'Vesper暮星.exe')
}

$pluginVariants = @()
foreach ($item in @(
    @{ Name='unity6-mono'; Managed=$Unity6ManagedDir; Loader=$loader5; Major=5; Framework='netstandard2.1'; Slim=$false },
    @{ Name='unity2022-mono'; Managed=$Unity2022ManagedDir; Loader=$loader6; Major=6; Framework='netstandard2.1'; Slim=$false },
    @{ Name='unity2022-mono-bepinex5'; Managed=$Unity2022ManagedDir; Loader=$loader5; Major=5; Framework='netstandard2.1'; Slim=$false },
    @{ Name='unity2021-mono'; Managed=$Unity2021ManagedDir; Loader=$loader5; Major=5; Framework='netstandard2.1'; Slim=$false },
    @{ Name='unity2020-mono'; Managed=$Unity2020ManagedDir; Loader=$loader5; Major=5; Framework='net46'; Slim=$false },
    @{ Name='legacy-mono'; Managed=$LegacyManagedDir; Loader=$loader5; Major=5; Framework='net35'; Slim=$true }
)) {
    # BepInEx 主版本和游戏 Managed 引用会在每轮变化；必须跳过增量缓存，确保
    # 实际打入各包的 DLL 都按该轮引用重新生成。
    & $DotNetExe build (Join-Path $root 'src/UnityTranslator.Plugin/UnityTranslator.Plugin.csproj') -t:Rebuild `
        -c Release "-p:BepInExDir=$($item.Loader)" "-p:BepInExMajor=$($item.Major)" `
        "-p:GameManagedDir=$($item.Managed)" "-p:PluginTargetFramework=$($item.Framework)" `
        "-p:SlimVariant=$($item.Slim.ToString().ToLowerInvariant())"
    if ($LASTEXITCODE -ne 0) { throw "插件编译失败：$($item.Name)" }
    $builtDll = Join-Path $root "src/UnityTranslator.Plugin/bin/Release/$($item.Framework)/UnityTranslator.Plugin.dll"
    if (-not (Test-Path -LiteralPath $builtDll)) { throw "插件输出缺失：$builtDll" }

    $package = Join-Path $output "packages/$($item.Name)"
    New-Item -ItemType Directory -Path $package | Out-Null
    Copy-Item -Path (Join-Path $item.Loader '*') -Destination $package -Recurse
    $plugins = Join-Path $package 'BepInEx/plugins'
    New-Item -ItemType Directory -Path $plugins -Force | Out-Null
    $packagedDll = Join-Path $plugins 'UnityTranslator.Plugin.dll'
    Copy-Item -LiteralPath $builtDll -Destination $packagedDll
    $pluginVariants += [pscustomobject]@{
        Name = $item.Name
        Major = $item.Major
        Path = $packagedDll
        Expected = if ($item.Major -eq 5) { 'BepInEx' } else { 'BepInEx.Core' }
        # BepInEx5 的产物里不该出现 BepInEx6 的程序集名
        Excluded = if ($item.Major -eq 5) { 'BepInEx.Core' } else { $null }
        SharedBuild = $false
        Framework = switch ($item.Framework) {
            # CLR 2.0 predates TargetFrameworkAttribute; its metadata runtime
            # marker is the usable check when compiling against Unity's BCL.
            'net35' { 'v2.0.50727' }
            'netstandard2.0' { '.NETStandard,Version=v2.0' }
            'net46' { '.NETFramework,Version=v4.6' }
            default { '.NETStandard,Version=v2.1' }
        }
    }
}

# IL2CPP 与 Mono 一起交付，避免一次重新打包后静默丢失这条支持路径。
& (Join-Path $PSScriptRoot 'build-il2cpp-package.ps1') -OutputDirectory (Join-Path $output 'packages') `
    -DotNetExe $DotNetExe -BepInExIl2CppDirectory $Il2CppLoaderDirectory -Il2CppInteropDir $Il2CppInteropDir
if ($LASTEXITCODE -ne 0) { throw 'IL2CPP 组件构建失败。' }

# 校验放在两次构建都结束之后。用 ReflectionOnlyLoadFrom 有两个坑：会锁住文件，
# 且同名程序集不能从两个路径加载，所以这里直接在 dll 字节里找程序集引用名，
# 不加载、不加锁。校验对象是真正打进包里的 dll。
$seenHashes = @{}
foreach ($variant in $pluginVariants) {
    $hash = (Get-FileHash -LiteralPath $variant.Path -Algorithm SHA256).Hash
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($variant.Path))
    if ($text.IndexOf($variant.Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "插件版本错配：$($variant.Name) 的 dll 里找不到对 $($variant.Expected) 的引用"
    }
    if ($variant.Excluded -and $text.IndexOf($variant.Excluded, [StringComparison]::Ordinal) -ge 0) {
        throw "插件版本错配：$($variant.Name) 的 dll 里出现了不该有的 $($variant.Excluded) 引用"
    }
    if ($text.IndexOf('Newtonsoft.Json', [StringComparison]::Ordinal) -ge 0) {
        throw "插件依赖错配：$($variant.Name) 引用了游戏可能没有的 Newtonsoft.Json，翻译回包会失效"
    }
    if ($text.IndexOf($variant.Framework, [StringComparison]::Ordinal) -lt 0) {
        throw "插件框架错配：$($variant.Name) 不包含预期的 $($variant.Framework)"
    }
    # 不同 Unity 版本若公开引用身份相同，独立 Rebuild 可以合法产生同样的 IL。
    # 校验真实框架/加载器引用，不能把相同哈希本身当成缓存污染。
    $seenHashes[$hash] = $variant.Name
    Write-Host "  插件 $($variant.Name)：引用 $($variant.Expected)，SHA256 $hash"
}

$ocrExe = Join-Path $TesseractDirectory 'tesseract.exe'
$ocrJapanese = Join-Path $TesseractDirectory 'tessdata/jpn.traineddata'
$ocrEnglish = Join-Path $TesseractDirectory 'tessdata/eng.traineddata'
if ((Get-FileHash -LiteralPath $ocrExe -Algorithm SHA256).Hash -ne 'CCD044D6CF16EAAAD151260E1FCC5E3E1504CD1B8644E940B4F7AE3E315DD0D3' -or
    (Get-FileHash -LiteralPath $ocrJapanese -Algorithm SHA256).Hash -ne '1F5DE9236D2E85F5FDF4B3C500F2D4926F8D9449F28F5394472D9E8D83B91B4D' -or
    (Get-FileHash -LiteralPath $ocrEnglish -Algorithm SHA256).Hash -ne '7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2') {
    throw '本机 OCR 程序或语言文件校验失败。'
}
$ocrOutput = Join-Path $output 'ocr'
$ocrData = Join-Path $ocrOutput 'tessdata'
New-Item -ItemType Directory -Path $ocrData -Force | Out-Null
Copy-Item -LiteralPath $ocrExe -Destination $ocrOutput
Get-ChildItem -LiteralPath $TesseractDirectory -Filter '*.dll' -File | Copy-Item -Destination $ocrOutput
Copy-Item -LiteralPath $ocrJapanese,$ocrEnglish -Destination $ocrData
Copy-Item -LiteralPath (Join-Path $TesseractDirectory 'tessdata/configs') -Destination $ocrData -Recurse
Copy-Item -LiteralPath (Join-Path $TesseractDirectory 'doc/LICENSE') -Destination (Join-Path $ocrOutput 'LICENSE.txt')

$resolvedLoader = [IO.Path]::GetFullPath($loaderRoot)
if (-not $resolvedLoader.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedLoader) -ne '.loader-staging') {
    throw '临时文件夹位置异常，已停止清理。'
}
Remove-Item -LiteralPath $resolvedLoader -Recurse -Force

# 插件分版本构建用的临时目录，同样只允许删输出目录内的 .plugin-staging
$resolvedPlugins = [IO.Path]::GetFullPath((Join-Path $output '.plugin-staging'))
if ($resolvedPlugins.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetFileName($resolvedPlugins) -eq '.plugin-staging' -and
    (Test-Path -LiteralPath $resolvedPlugins)) {
    Remove-Item -LiteralPath $resolvedPlugins -Recurse -Force
}
@"
版本：$appVersion
生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

Unity Translator includes BepInEx 5.4.23.3 and 6.0.0-be.788 (LGPL-2.1), and UnityDoorstop (LGPL-2.1).
Official source and license: https://github.com/BepInEx/BepInEx and https://github.com/NeighTools/UnityDoorstop
BepInEx 5 archive: https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip
SHA-256: 41A089E5B1B1F0713B331346BAF6677B1184C69EABEBF51101097954E854C749
BepInEx 6 archive: https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.Mono-win-x64-6.0.0-be.788%2B5b766a3.zip
SHA-256: D7455185E8A0A01EB122947C59229F3FFCCEED853FEBD4707C08219743D8F477
Offline OCR: Tesseract 5.5.0.20241111 (Apache-2.0), Windows installer:
https://github.com/tesseract-ocr/tesseract/releases/download/5.5.0/tesseract-ocr-w64-setup-5.5.0.20241111.exe
Installer SHA-256: F3FC4236425B690C8BE756F35793F77394EE004BE0A6460A440C754D892F68BC
OCR models from https://github.com/tesseract-ocr/tessdata_fast (Apache-2.0):
jpn SHA-256: 1F5DE9236D2E85F5FDF4B3C500F2D4926F8D9449F28F5394472D9E8D83B91B4D
eng SHA-256: 7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2

External window capture: Vortice.Direct3D11/DXGI/DirectX 3.6.2, Vortice.Mathematics 1.9.2 (MIT),
SharpGen.Runtime/Runtime.COM 2.2.0-beta (MIT), Microsoft.Windows.SDK.NET.Ref 10.0.19041.56
(Windows SDK terms, including its supplied Microsoft.Windows.SDK.NET and WinRT.Runtime).
Full texts and provenance: licenses/external-ocr/. See docs/external-ocr-dependencies.md.
"@ | Set-Content -LiteralPath (Join-Path $output ('THIRD-PARTY-NOTICES-' + $appVersion + '.txt')) -Encoding UTF8
$externalLicenses = Join-Path $PSScriptRoot 'licenses/external-ocr'
if (-not (Test-Path -LiteralPath $externalLicenses)) { throw 'Missing external OCR license texts.' }
New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force | Out-Null
Copy-Item -LiteralPath $externalLicenses -Destination (Join-Path $output 'licenses') -Recurse
