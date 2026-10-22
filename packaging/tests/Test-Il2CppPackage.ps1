# Runs the real packaging script in an isolated source tree; only the external compiler is replaced.
# Catches compiling against interop-sibling core instead of the core bytes that are delivered.
$ErrorActionPreference = 'Stop'
$packagingScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../build-il2cpp-package.ps1'))
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $temporaryParent ('mango-il2cpp-package-tests-' + [Guid]::NewGuid().ToString('N'))
$previousTestLog = $env:MANGO_PACKAGING_TEST_LOG
$coreFiles = @('BepInEx.Core.dll', 'BepInEx.Unity.IL2CPP.dll', 'Il2CppInterop.Runtime.dll', '0Harmony.dll')
$failures = @()
$passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-PackageFixture([string]$Name, [string]$InputMode, [string]$MissingCore = '', [switch]$WrongArchiveHash) {
    $caseRoot = Join-Path $fixtureRoot $Name
    $sourceRoot = Join-Path $caseRoot 'source'
    $sourcePackaging = Join-Path $sourceRoot 'packaging'
    $loader = Join-Path $caseRoot 'chosen-loader'
    $chosenCore = Join-Path $loader 'BepInEx/core'
    $gameBepInEx = Join-Path $caseRoot 'game/BepInEx'
    $interop = Join-Path $gameBepInEx 'interop'
    $unrelatedCore = Join-Path $gameBepInEx 'core'
    $output = Join-Path $caseRoot 'output'
    New-Item -ItemType Directory -Path $sourcePackaging,$chosenCore,(Join-Path $loader 'dotnet'),$interop,$unrelatedCore,(Join-Path $sourceRoot 'il2cpp') -Force | Out-Null
    Copy-Item -LiteralPath $packagingScript -Destination (Join-Path $sourcePackaging 'build-il2cpp-package.ps1')
    Set-Content -LiteralPath (Join-Path $sourceRoot 'il2cpp/Il2CppPlugin.csproj') -Value '<Project />' -Encoding ASCII
    foreach ($file in $coreFiles) {
        if ($file -ne $MissingCore) { Set-Content -LiteralPath (Join-Path $chosenCore $file) -Value ('delivered-' + $file) -Encoding ASCII }
        Set-Content -LiteralPath (Join-Path $unrelatedCore $file) -Value ('unrelated-game-' + $file) -Encoding ASCII
    }
    Set-Content -LiteralPath (Join-Path $loader 'dotnet/coreclr.dll') -Value 'fixture-runtime' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $interop 'OnlyForCompilation.dll') -Value 'game-interop-must-not-ship' -Encoding ASCII
    $log = Join-Path $caseRoot 'dotnet-invocation.json'
    $env:MANGO_PACKAGING_TEST_LOG = $log
    $parameters = @{
        OutputDirectory = $output
        DotNetExe = Join-Path $PSScriptRoot 'fixtures/FakeDotNet.cmd'
        Il2CppInteropDir = $interop
    }
    if ($InputMode -eq 'archive') {
        $archive = Join-Path $caseRoot 'loader.zip'
        Compress-Archive -Path (Join-Path $loader '*') -DestinationPath $archive
        $parameters.BepInExIl2CppArchive = $archive
        $parameters.ExpectedArchiveHash = if ($WrongArchiveHash) { 'invalid-fixture-hash' } else { (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
    } else {
        $parameters.BepInExIl2CppDirectory = $loader
    }
    $failure = $null
    try { & (Join-Path $sourcePackaging 'build-il2cpp-package.ps1') @parameters 6>$null | Out-Null } catch { $failure = $_ }
    [pscustomobject]@{
        Failure = $failure
        Package = Join-Path $output 'il2cpp-bepinex6'
        Log = $log
        Interop = $interop
    }
}

function Test-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $script:passed++
        Write-Host ('PASS ' + $Name)
    } catch {
        $script:failures += ($Name + ': ' + $_.Exception.Message)
        Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message)
    }
}

try {
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    foreach ($inputMode in @('directory', 'archive')) {
        Test-Case ($inputMode + ' compiles against the exact delivered core') {
            $fixture = New-PackageFixture -Name ($inputMode + '-parity') -InputMode $inputMode
            Assert-True ($null -eq $fixture.Failure) ('Packaging failed: ' + $fixture.Failure)
            Assert-True (Test-Path -LiteralPath $fixture.Log) 'The external compiler was not invoked.'
            $invocation = Get-Content -LiteralPath $fixture.Log -Raw | ConvertFrom-Json
            $expectedCore = [IO.Path]::GetFullPath((Join-Path $fixture.Package 'BepInEx/core'))
            Assert-True ($invocation.LoaderCore -eq $expectedCore) ('LoaderCore path mismatch: expected delivered core ' + $expectedCore + '; got ' + $invocation.LoaderCore)
            foreach ($file in $coreFiles) {
                $compiled = @($invocation.CoreHashes | Where-Object { $_.Name -eq $file })
                $deliveredHash = (Get-FileHash -LiteralPath (Join-Path $expectedCore $file) -Algorithm SHA256).Hash
                Assert-True ($compiled.Count -eq 1 -and $compiled[0].Hash -eq $deliveredHash) ('Compiled and delivered core bytes differ for ' + $file)
            }
            Assert-True ($invocation.InteropDir -eq [IO.Path]::GetFullPath($fixture.Interop)) 'The compiler did not receive the selected game interop directory.'
            $plugin = Join-Path $fixture.Package 'BepInEx/plugins/UnityTranslator.Plugin.Il2Cpp.dll'
            Assert-True ((Get-Content -LiteralPath $plugin -Raw).Trim() -eq 'fixture-built-plugin') 'The built plugin was not delivered.'
            Assert-True (@(Get-ChildItem -LiteralPath $fixture.Package -Recurse -Filter 'OnlyForCompilation.dll').Count -eq 0) 'Game interop was distributed.'
        }
        foreach ($missingCore in $coreFiles) {
            Test-Case ($inputMode + ' rejects missing ' + $missingCore + ' before compiling') {
                $fixture = New-PackageFixture -Name ($inputMode + '-missing-' + $missingCore) -InputMode $inputMode -MissingCore $missingCore
                Assert-True ($null -ne $fixture.Failure) ('Missing core file was accepted: ' + $missingCore)
                Assert-True (-not (Test-Path -LiteralPath $fixture.Log)) ('Compiler ran despite missing delivered core file: ' + $missingCore)
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Package 'BepInEx/plugins/UnityTranslator.Plugin.Il2Cpp.dll'))) 'A plugin was delivered after validation failed.'
            }
        }
    }
    Test-Case 'archive hash mismatch fails before compiling or copying' {
        $fixture = New-PackageFixture -Name 'archive-wrong-hash' -InputMode 'archive' -WrongArchiveHash
        Assert-True ($null -ne $fixture.Failure) 'An archive with a mismatched hash was accepted.'
        Assert-True (-not (Test-Path -LiteralPath $fixture.Log)) 'Compiler ran after archive hash validation failed.'
        Assert-True (-not (Test-Path -LiteralPath $fixture.Package)) 'Archive was copied after hash validation failed.'
    }
} finally {
    $env:MANGO_PACKAGING_TEST_LOG = $previousTestLog
    $resolvedFixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixtureRoot.StartsWith($temporaryParent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedFixtureRoot).StartsWith('mango-il2cpp-package-tests-', [StringComparison]::Ordinal)) {
        throw 'Fixture cleanup target is outside the expected temporary parent.'
    }
    Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
}
if ($failures.Count -gt 0) { throw (($failures.Count.ToString() + ' packaging regression(s) failed:') + [Environment]::NewLine + ($failures -join [Environment]::NewLine)) }
Write-Host ('PASS all ' + $passed + ' IL2CPP packaging regressions')
