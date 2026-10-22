param(
    [Parameter(Mandatory = $true)][string]$LogPath,
    [Parameter(Mandatory = $true)][string]$OriginalFontName
)

$ErrorActionPreference = 'Stop'
$lines = Get-Content -LiteralPath $LogPath -Encoding UTF8
$menu = @($lines | Where-Object { $_ -match '\[层级\].*TitleManager/MenuLayout/MenuItem\(.*字体=' })
if ($menu.Count -eq 0) {
    $menu = @($lines | Where-Object { $_ -match 'STYLE component=.*parent=MenuItem\(' })
}
if ($menu.Count -lt 5) { throw "Expected five translated menu components, got $($menu.Count)" }
foreach ($line in $menu) {
    if (-not ($line.Contains("字体=$OriginalFontName") -or $line.Contains("font=$OriginalFontName"))) {
        throw "Translated menu lost its original font: $line"
    }
}

"PASS: $($menu.Count) translated menu components retained $OriginalFontName"
