param(
    [Parameter(Mandatory=$true)][string]$ReleaseRoot,
    [Parameter(Mandatory=$true)][string]$PreviousRelease,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Pure checker: no process launch, build, download, settings read, or desktop replacement.
function Assert-Path([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "Absolute path required: $Path" }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $ancestor = $full
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse path rejected: $ancestor" }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $full
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
function Write-NewJson([string]$Name, $Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 30))
    $file = [IO.File]::Open((Join-Path $EvidenceRoot $Name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes,0,$bytes.Length) } finally { $file.Dispose() }
}
function Files([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Missing directory: $Root" }
    $rows = @(Get-ChildItem -LiteralPath $Root -Recurse -Force -File | ForEach-Object {
        $null = Assert-Path $_.FullName
        @{ Path=$_.FullName.Substring($Root.Length+1).Replace('\','/'); Length=$_.Length; Sha256=(Hash $_.FullName) }
    } | Sort-Object { $_.Path })
    return ,$rows
}
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action; $checks.Add(@{ Name=$Name; Status='Passed' }) }
    catch { $checks.Add(@{ Name=$Name; Status='Failed'; Detail=$_.Exception.Message }) }
}
$ReleaseRoot = Assert-Path $ReleaseRoot
$PreviousRelease = Assert-Path $PreviousRelease
$EvidenceRoot = Assert-Path $EvidenceRoot
foreach ($root in @($ReleaseRoot,$PreviousRelease)) {
    if ($EvidenceRoot -eq $root -or $EvidenceRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must be outside release trees.' }
}
if ($ReleaseRoot -eq $PreviousRelease) { throw 'Candidate and previous release must differ.' }
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Evidence directory must be new; reports are never overwritten.' }
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$required = [Collections.Generic.List[object]]::new()
$bundle = $null; $exeHash = $null; $ledger = @{RequiredEvidence=@()}
$exe = Join-Path $ReleaseRoot 'Vesper暮星.exe'
Check 'Bundle6UncompressedInventory' {
    Add-Type -Path (Join-Path $PSScriptRoot 'BundleInventory.cs')
    $script:bundle = [Vesper.Packaging.BundleInventory]::Read($exe)
    $script:exeHash = $bundle.ExeSha256
    Write-NewJson 'bundle.json' $bundle
}
Check 'ReleaseEvidenceLedger' {
    $script:ledger = Read-Json (Join-Path $ReleaseRoot 'release-evidence.json')
    if ($ledger.SchemaVersion -ne 1 -or $ledger.CandidateExeSha256 -ne $exeHash -or -not $exeHash) { throw 'Ledger schema or candidate hash mismatch.' }
}
Check 'ExactPublishInputs' {
    if (-not $bundle -or -not $ledger.FrozenInputRoot) { throw 'No bundle or same-invocation frozen input root.' }
    $freeze = Assert-Path $ledger.FrozenInputRoot
    $map = @{}
    foreach ($line in (Get-Content -LiteralPath (Join-Path $freeze 'publish-inputs.tsv'))) {
        $parts = $line.Split("`t")
        if ($parts.Count -ne 3 -or $parts[1] -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Malformed frozen input row.' }
        $name = [Vesper.Packaging.BundleInventory]::NormalizePath($parts[0])
        if ($map.ContainsKey($name)) { throw "Duplicate frozen input: $name" }
        $map[$name] = $parts[1]
        $path = Assert-Path (Join-Path $freeze $name)
        if ((Hash $path) -ne $parts[1]) { throw "Frozen bytes changed: $name" }
    }
    foreach ($entry in $bundle.Entries) {
        if (-not $map.ContainsKey($entry.Path) -or $map[$entry.Path] -ne $entry.Sha256) { throw "Bundle/input mismatch: $($entry.Path)" }
        if (Test-Path -LiteralPath (Join-Path $ReleaseRoot $entry.Path)) { throw "Bundled payload also exists as sidecar: $($entry.Path)" }
    }
    foreach ($name in @('UnityTranslator.App.dll','UnityTranslator.Core.dll','Vortice.Direct3D11.dll','Vortice.DXGI.dll','Vortice.DirectX.dll','Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll')) {
        if (-not @($bundle.Entries | Where-Object Path -eq $name).Count) { throw "Required payload missing: $name" }
    }
    # Every SDK input is either an actual bundle entry or a byte-identical physical sidecar.
    $bundledNames = @($bundle.Entries.Path)
    foreach ($name in $map.Keys) {
        if ($name -eq 'UnityTranslator.App.exe' -or $name -in $bundledNames) { continue }
        if ((Hash (Join-Path $ReleaseRoot $name)) -ne $map[$name]) { throw "Publish sidecar mismatch: $name" }
    }
}
Check 'UnchangedComponentsOcrAssets' {
    $comparisons = @()
    foreach ($name in @('packages/il2cpp-bepinex6','packages/legacy-mono','packages/unity2020-mono','packages/unity2021-mono','packages/unity2022-mono','packages/unity2022-mono-bepinex5','packages/unity6-mono','ocr','Assets')) {
        $old = Files (Join-Path $PreviousRelease $name)
        $new = Files (Join-Path $ReleaseRoot $name)
        if ($old.Count -eq 0 -or $old.Count -ne $new.Count) { throw "Tree count mismatch: $name" }
        for ($i=0; $i -lt $old.Count; $i++) {
            if ($old[$i].Path -cne $new[$i].Path -or $old[$i].Length -ne $new[$i].Length -or $old[$i].Sha256 -ne $new[$i].Sha256) { throw "Tree file mismatch: $name/$($old[$i].Path)" }
            $comparisons += @{ Path="$name/$($old[$i].Path)"; Sha256=$new[$i].Sha256; Length=$new[$i].Length; Unchanged=$true }
        }
    }
    Write-NewJson 'reused-files.json' $comparisons
}
Check 'FullPackageManifest' {
    $actual = @((Files $ReleaseRoot) | Where-Object { $_.Path -ne 'release-manifest.json' })
    $expected = @(Read-Json (Join-Path $ReleaseRoot 'release-manifest.json'))
    if ($actual.Count -ne $expected.Count) { throw 'Package manifest file count mismatch.' }
    $byName = @{}
    foreach ($row in $expected) { if ($byName.ContainsKey($row.Path)) { throw 'Duplicate manifest path.' }; $byName[$row.Path]=$row }
    foreach ($row in $actual) {
        if (-not $byName.ContainsKey($row.Path) -or $byName[$row.Path].Sha256 -ne $row.Sha256 -or $byName[$row.Path].Length -ne $row.Length) { throw "Package manifest mismatch: $($row.Path)" }
    }
    Write-NewJson 'all-files.json' (Files $ReleaseRoot)
}
Check 'LicenseTexts' {
    $known = @{
        'Vortice.Windows-LICENSE.txt'='82BD4E84D144BEBBB519ED05899F0CEAEAF2E0F95160AC32FD8CC388710C76D0'
        'Vortice.Mathematics-LICENSE.txt'='CF72351FA2E9F2EB181AF7FD508DD73DA1D84BA43575CB98F7AF22F26A81AF5E'
        'SharpGenTools-LICENSE.txt'='1CC7ABD1E97575838535484BEF6E4AB14E81681DDF7837672ACEDE5FCA1D4E30'
        'Windows-SDK-LICENSE.rtf'='DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62'
        'Windows-SDK-REDIST.md'='FF4DC82936EF9E8449927A3D06EE1FCA801933C1464CEEE60FA14003EB656DD0'
    }
    # SDK exact downloaded text; completeness/redistribution review is a separate required report.
    foreach ($name in $known.Keys) { if ((Hash (Join-Path $ReleaseRoot "licenses/external-ocr/$name")) -ne $known[$name]) { throw "License changed/missing: $name" } }
    foreach ($name in @('licenses/external-ocr/PROVENANCE.md','THIRD-PARTY-NOTICES-V0.5.9.txt','packages.lock.external-ocr.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ReleaseRoot $name) -PathType Leaf)) { throw "Missing notice/lock: $name" }
    }
}
$names = @('FinalSolutionTests','NativeClientCaptureAndOcclusion','NativeResizeAndClose','NativeOverlayLifecycle','RemoteOverlayVisibilityAndTargetIsolation','PhysicalInputAndF9','NaturalGameImageTextTwoIl2CppVersions','SlimAndFullMonoRegression','PublishedDefaultStartup','PublishedCaptureDependencyResolution','PublishedUnavailableStartup','OrdinaryTranslationAndNumericalTools','RealConfigurationUnchanged','OwnedResourceCleanup','NativeIconSevenFrames','LicenseCompleteness','DpiAndMonitorTopology','PhysicalDeviceLoss','Pre19041Startup')
foreach ($name in $names) {
    $record = @{ Name=$name; Status='NotAttempted'; Detail='Required evidence absent.' }
    try {
        $matches = @($ledger.RequiredEvidence | Where-Object { $_.Name -eq $name })
        if ($matches.Count -gt 1) { throw 'Duplicate evidence row.' }
        if ($matches.Count -eq 1) {
            $item=$matches[0]; $record.Status=$item.Status; $record.Detail=$item.Detail
            if ($item.Status -eq 'Passed') {
                if (-not $exeHash -or $item.CandidateExeSha256 -ne $exeHash -or @($item.Artifacts).Count -eq 0) { throw 'Evidence lacks final candidate binding/artifacts.' }
                foreach ($artifact in $item.Artifacts) {
                    $artifactPath=Assert-Path $artifact.Path
                    if ($artifact.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Hash $artifactPath) -ne $artifact.Sha256) { throw 'Evidence artifact changed/missing.' }
                }
                $record.Artifacts=$item.Artifacts
            }
            elseif ($item.Status -notin @('Failed','NotAttempted','EnvironmentUnverified','Blocked')) { throw 'Unknown evidence status.' }
        }
    } catch { $record.Status='Failed'; $record.Detail=$_.Exception.Message }
    $required.Add($record)
}
# No boolean waiver, synthetic factory result, or helper success substitutes for required evidence.
$passed = & (Join-Path $PSScriptRoot 'Test-ExternalReleaseDecision.ps1') -StaticChecks @($checks) -RequiredEvidence @($required) -ExpectedEvidenceNames $names
Write-NewJson 'verification.json' @{
    SchemaVersion=1; CreatedUtc=[DateTime]::UtcNow.ToString('o'); Passed=$passed; DeliveryAllowed=$passed
    CandidateExeSha256=$exeHash; ReleaseRoot=$ReleaseRoot; PreviousRelease=$PreviousRelease
    CheckerSha256=(Hash $PSCommandPath); BundleReaderSha256=(Hash (Join-Path $PSScriptRoot 'BundleInventory.cs'))
    DecisionSha256=(Hash (Join-Path $PSScriptRoot 'Test-ExternalReleaseDecision.ps1'))
    StaticChecks=@($checks); RequiredEvidence=@($required)
    EnvironmentLimits=@('Real pre-19041 OS startup','Actual 100/150/200 percent DPI and negative monitor coordinates','Physical device-loss recovery')
    Scope='Read-only static verification and evidence integrity. Runtime claims depend on separately reviewed reports.'
}
Write-Output "Passed=$passed; report=$(Join-Path $EvidenceRoot 'verification.json')"
if (-not $passed) { exit 2 }
