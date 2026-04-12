#requires -Version 7.0
$ErrorActionPreference = 'Stop'

$relayExe = Join-Path $PSScriptRoot '..\bin\Debug\net9.0\mcp-sitter.exe'
$childExe = Join-Path $PSScriptRoot 'FakeMcp\bin\Debug\net9.0\FakeMcp.exe'

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $relayExe
$psi.ArgumentList.Add('--debounce')
$psi.ArgumentList.Add('500')
$psi.ArgumentList.Add($childExe)
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)

$script:stderrLines = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
$p = [System.Diagnostics.Process]::new()
$p.StartInfo = $psi
$p.EnableRaisingEvents = $true
Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -Action {
    if ($EventArgs.Data) { $script:stderrLines.Enqueue($EventArgs.Data) }
} | Out-Null
$null = $p.Start()
$p.BeginErrorReadLine()

function Send-Msg($obj) {
    $json = $obj | ConvertTo-Json -Depth 20 -Compress
    $p.StandardInput.WriteLine($json)
    $p.StandardInput.Flush()
}

function Recv-Until-Id($id, [int]$timeoutSec = 10) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $readTask = $p.StandardOutput.ReadLineAsync()
        while (-not $readTask.IsCompleted) {
            if ((Get-Date) -ge $deadline) { throw "timeout waiting for id=$id" }
            Start-Sleep -Milliseconds 50
        }
        $line = $readTask.Result
        if ($null -eq $line) { throw "stdout closed while waiting for id=$id" }
        $obj = $line | ConvertFrom-Json
        if ($null -ne $obj.id -and $obj.id -eq $id) { return $obj }
    }
    throw "timeout waiting for id=$id"
}

try {
    Send-Msg @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; clientInfo=@{ name='watch-test'; version='0' }; capabilities=@{} } }
    $null = Recv-Until-Id 1
    Send-Msg @{ jsonrpc='2.0'; method='notifications/initialized' }
    Send-Msg @{ jsonrpc='2.0'; id=2; method='tools/list'; params=@{} }
    $null = Recv-Until-Id 2

    Send-Msg @{ jsonrpc='2.0'; id=3; method='tools/call'; params=@{ name='sitter_status'; arguments=@{} } }
    $st = Recv-Until-Id 3
    $before = ($st.result.content[0].text | ConvertFrom-Json).killCount
    Write-Host "killCount before touch: $before" -ForegroundColor Green
    if ($before -ne 0) { throw "expected killCount=0" }

    Write-Host "touching $childExe ..." -ForegroundColor Cyan
    (Get-Item $childExe).LastWriteTime = Get-Date
    Start-Sleep -Seconds 3

    Send-Msg @{ jsonrpc='2.0'; id=4; method='tools/call'; params=@{ name='sitter_status'; arguments=@{} } }
    $st2 = Recv-Until-Id 4 15
    $statusAfter = $st2.result.content[0].text | ConvertFrom-Json
    Write-Host "killCount after touch: $($statusAfter.killCount)" -ForegroundColor Green
    Write-Host "lastKillReason: $($statusAfter.lastKillReason)" -ForegroundColor Green
    if ($statusAfter.killCount -lt 1) { throw "expected killCount >= 1 after touch" }

    # fake_echo triggers lazy respawn after watcher kill
    Send-Msg @{ jsonrpc='2.0'; id=5; method='tools/call'; params=@{ name='fake_echo'; arguments=@{ text='post-touch' } } }
    $call = Recv-Until-Id 5 15
    if ($call.result.content[0].text -ne 'echo: post-touch') { throw "post-touch echo failed" }
    Write-Host "[fake_echo after watcher kill + lazy respawn] $($call.result.content[0].text)" -ForegroundColor Green

    Write-Host "`nWATCH KILL + LAZY RESPAWN: PASS" -ForegroundColor Green
}
finally {
    try { $p.StandardInput.Close() } catch {}
    $p.WaitForExit(3000) | Out-Null
    if (-not $p.HasExited) { $p.Kill($true) }
    Get-EventSubscriber | Where-Object { $_.SourceObject -eq $p } | Unregister-Event
    Write-Host "`n--- stderr ---" -ForegroundColor Magenta
    foreach ($l in $script:stderrLines.ToArray()) { Write-Host $l -ForegroundColor DarkMagenta }
}
