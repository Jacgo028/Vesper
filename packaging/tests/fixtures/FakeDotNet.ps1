$ErrorActionPreference = 'Stop'
try {
    Set-Content -LiteralPath $env:MANGO_PACKAGING_TEST_LOG -Value '{}' -Encoding ASCII
    if ($args[0] -ne 'build') { throw 'Expected a dotnet build invocation.' }
    $outputIndex = [Array]::IndexOf($args, '-o')
    if ($outputIndex -lt 0) { throw 'Build output argument was not supplied.' }
    $outputDirectory = $args[$outputIndex + 1]
    # Windows PowerShell -File expands a native -p:Name=value token to -p, Name=value.
    $loaderArgument = @($args | Where-Object { $_ -like 'LoaderCore=*' -or $_ -like '-p:LoaderCore=*' })
    $interopArgument = @($args | Where-Object { $_ -like 'InteropDir=*' -or $_ -like '-p:InteropDir=*' })
    if ($loaderArgument.Count -ne 1 -or $interopArgument.Count -ne 1) {
        throw ('Expected exactly one LoaderCore and InteropDir argument. Received: ' + ($args -join ' | '))
    }
    $loaderCore = $loaderArgument[0].Substring($loaderArgument[0].IndexOf('=') + 1)
    $interopDirectory = $interopArgument[0].Substring($interopArgument[0].IndexOf('=') + 1)
    $coreFiles = @('BepInEx.Core.dll', 'BepInEx.Unity.IL2CPP.dll', 'Il2CppInterop.Runtime.dll', '0Harmony.dll')
    $coreHashes = @($coreFiles | ForEach-Object {
        $path = Join-Path $loaderCore $_
        [pscustomobject]@{ Name = $_; Hash = if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $null } }
    })
    [pscustomobject]@{
        LoaderCore = [IO.Path]::GetFullPath($loaderCore)
        InteropDir = [IO.Path]::GetFullPath($interopDirectory)
        CoreHashes = $coreHashes
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $env:MANGO_PACKAGING_TEST_LOG -Encoding UTF8
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $outputDirectory 'UnityTranslator.Plugin.Il2Cpp.dll') -Value 'fixture-built-plugin' -Encoding ASCII
    exit 0
} catch {
    Write-Error $_
    exit 1
}
