param(
    [Parameter(Mandatory=$true)][string]$PipeName,
    [Parameter(Mandatory=$true)][string]$LogPath,
    [string]$TranslationText = 'A deliberately long menu translation'
)
$ErrorActionPreference = 'Stop'
while ($true) {
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($PipeName,[IO.Pipes.PipeDirection]::InOut,1,
        [IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
    $reader = $null
    $writer = $null
    try {
        $pipe.WaitForConnection()
        $reader = [IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,1024,$true)
        $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),1024,$true)
        $writer.AutoFlush = $true
        while ($pipe.IsConnected) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            $request = $line | ConvertFrom-Json
            if (-not $request.Source) { continue }
            $writer.WriteLine((@{ Version=1; RequestId=$request.RequestId; ComponentId=$request.ComponentId;
                Generation=$request.Generation; Translated=$TranslationText } | ConvertTo-Json -Compress))
            Add-Content -LiteralPath $LogPath -Value ('REPLIED ' + $request.Source)
        }
    } finally {
        if ($writer) { $writer.Dispose() }
        if ($reader) { $reader.Dispose() }
        $pipe.Dispose()
    }
}
