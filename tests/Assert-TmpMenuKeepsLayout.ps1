param(
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
$lines = Get-Content -LiteralPath $LogPath -Encoding UTF8
$sized = @($lines | Where-Object { $_ -match '\[字号归一化\].*原字号=([0-9.]+).*新字号=([0-9.]+)' })
if ($sized.Count -lt 4) {
    throw "Expected at least four translated menu size samples, got $($sized.Count)"
}

foreach ($line in $sized) {
    if ($line -notmatch '原字号=([0-9.]+).*新字号=([0-9.]+)') { throw "Unparseable size: $line" }
    $original = [float]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    $applied = [float]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
    if ($applied -gt $original + 0.01) {
        throw "Translated menu grew from $original to ${applied}: $line"
    }
}

$preferred = @($lines | Where-Object { $_ -match '\[尺寸推算\].*推算尺寸=([0-9.]+)x([0-9.]+).*组件rect=([0-9.]+)x([0-9.]+)' })
if ($preferred.Count -lt 3) { throw "Expected three layout measurements, got $($preferred.Count)" }
foreach ($line in $preferred) {
    if ($line -notmatch '推算尺寸=([0-9.]+)x([0-9.]+).*组件rect=([0-9.]+)x([0-9.]+)') { throw "Unparseable layout: $line" }
    $height = [float]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
    $rectHeight = [float]::Parse($Matches[4], [Globalization.CultureInfo]::InvariantCulture)
    if ($height -gt $rectHeight + 0.1) {
        throw "Translated menu exceeds its rect height ($height > ${rectHeight}): $line"
    }
}

"PASS: $($sized.Count) translated sizes did not grow; $($preferred.Count) measured heights fit"
