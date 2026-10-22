param(
    [Parameter(Mandatory=$true)][string]$PluginPath,
    [Parameter(Mandatory=$true)][string]$RunName,
    [string]$GameRoot = 'D:\ut-verify\cambion-bepinex5-entrypoint'
)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($GameRoot)
if ($target -ne 'D:\ut-verify\cambion-bepinex5-entrypoint') { throw 'Probe must run in the isolated Cambion clone.' }
$running = @(Get-CimInstance Win32_Process -Filter "name='cambion.exe'" | Where-Object { $_.ExecutablePath -eq (Join-Path $target 'cambion.exe') })
if ($running.Count -gt 0) { throw 'Isolated clone is already running.' }
$logs = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$hostLog = Join-Path $logs ($RunName + '-host.log')
$gameLog = Join-Path $logs ($RunName + '-player.log')
$resultLog = Join-Path $logs ($RunName + '-bepinex.log')
$hostScript = Join-Path $PSScriptRoot 'ReplyHost.ps1'
$pipeName = 'ut-layout-' + [Guid]::NewGuid().ToString('N')
$oldPipe = $env:UT_PIPE_NAME
$oldOcr = $env:UT_OCR
$oldRun = $env:UT_LAYOUT_PROBE_RUN
$env:UT_PIPE_NAME = $pipeName
$env:UT_OCR = '0'
$env:UT_LAYOUT_PROBE_RUN = $pipeName
$game = $null
$hostProcess = $null
try {
    Copy-Item -LiteralPath $PluginPath -Destination (Join-Path $target 'BepInEx\plugins\UnityTranslator.Plugin.dll') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\netstandard2.1\UnityTranslator.RuntimeLayoutProbe.dll') -Destination (Join-Path $target 'BepInEx\plugins\UnityTranslator.RuntimeLayoutProbe.dll') -Force
    $hostProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $hostScript + '"'),'-PipeName',$pipeName,'-LogPath',('"' + $hostLog + '"'),'-TranslationText','"A deliberately long menu translation"') -WindowStyle Hidden -PassThru
    $game = Start-Process -FilePath (Join-Path $target 'cambion.exe') -WorkingDirectory $target -ArgumentList @('-batchmode','-noaudio','-logFile',('"' + $gameLog + '"')) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(135)
    $logPath = Join-Path $target 'BepInEx\LogOutput.log'
    do {
        Start-Sleep -Seconds 2
        $lines = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 300 } else { @() }
        if ($lines -match ("PROBE (RESULT|ERROR) run=" + $pipeName)) { break }
        if ($game.HasExited) { throw 'Game process exited before probe completion.' }
    } while ([DateTime]::UtcNow -lt $deadline)
    Copy-Item -LiteralPath $logPath -Destination $resultLog -Force
    Get-Content -LiteralPath $resultLog | Where-Object { $_ -match 'PROBE ' }
    if (-not ($lines -match ("PROBE RESULT run=" + $pipeName + " failures=0"))) { throw ('Runtime layout probe failed; evidence: ' + $resultLog) }
}
finally {
    if ($game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force }
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force }
    Remove-Item -LiteralPath (Join-Path $target 'BepInEx\plugins\UnityTranslator.RuntimeLayoutProbe.dll') -Force -ErrorAction SilentlyContinue
    $env:UT_PIPE_NAME = $oldPipe
    $env:UT_OCR = $oldOcr
    $env:UT_LAYOUT_PROBE_RUN = $oldRun
}
