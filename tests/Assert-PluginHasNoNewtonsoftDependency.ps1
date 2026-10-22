param([Parameter(Mandatory=$true)][string]$PluginDll)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PluginDll -PathType Leaf)) {
    throw "Plugin file not found: $PluginDll"
}

# Cambion does not ship Newtonsoft.Json.dll. A reference to it can make
# Unity Mono fail to load the message reader and stop translation replies.
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PluginDll).Path)
$metadata = [System.Text.Encoding]::UTF8.GetString($bytes)
if ($metadata.IndexOf('Newtonsoft.Json', [StringComparison]::Ordinal) -ge 0) {
    throw 'Plugin still references Newtonsoft.Json.'
}

Write-Output 'Plugin has no Newtonsoft.Json assembly dependency.'
