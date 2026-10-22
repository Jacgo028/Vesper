param(
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
$lines = Get-Content -LiteralPath $LogPath -Encoding UTF8
$menu = @($lines | Where-Object { $_ -match 'STYLE component=.*parent=MenuItem\(' })
if ($menu.Count -lt 5) { throw "Expected five translated menu measurements, got $($menu.Count)" }
foreach ($line in $menu) {
    if ($line -notmatch 'preferred=\(([0-9.]+), ([0-9.]+)\) rect=\(([0-9.]+), ([0-9.]+)\)') {
        throw "Unparseable TMP measurement: $line"
    }
    $preferredWidth = [float]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    $preferredHeight = [float]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
    $rectWidth = [float]::Parse($Matches[3], [Globalization.CultureInfo]::InvariantCulture)
    $rectHeight = [float]::Parse($Matches[4], [Globalization.CultureInfo]::InvariantCulture)
    if ($preferredWidth -gt $rectWidth + 0.5 -or $preferredHeight -gt $rectHeight + 0.5) {
        throw "Translated text exceeds its UI rectangle: $line"
    }
}

"PASS: $($menu.Count) translated menu measurements fit their UI rectangles"
