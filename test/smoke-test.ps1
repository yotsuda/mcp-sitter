#requires -Version 7.0
$ErrorActionPreference = 'Stop'

$relayExe = Join-Path $PSScriptRoot '..\bin\Debug\net9.0\mcp-sitter.exe'
$childExe = Join-Path $PSScriptRoot 'FakeMcp\bin\Debug\net9.0\FakeMcp.exe'

if (-not (Test-Path $relayExe)) { throw "mcp-sitter.exe not found: $relayExe" }
if (-not (Test-Path $childExe)) { throw "FakeMcp.exe not found: $childExe" }

Write-Host "Sitter: $relayExe"
Write-Host "child: $childExe"

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $relayExe
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
$script:proc = $p
Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -Action {
    if ($EventArgs.Data) { $script:stderrLines.Enqueue($EventArgs.Data) }
} | Out-Null
$null = $p.Start()
$p.BeginErrorReadLine()

function Send-Msg($obj) {
    $json = $obj | ConvertTo-Json -Depth 20 -Compress
    Write-Host ">>> $json" -ForegroundColor DarkCyan
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
        Write-Host "<<< $line" -ForegroundColor DarkGray
        $obj = $line | ConvertFrom-Json
        if ($null -ne $obj.id -and $obj.id -eq $id) { return $obj }
        if ($null -ne $obj.method) {
            Write-Host "    (notification: $($obj.method))" -ForegroundColor Yellow
        }
    }
    throw "timeout waiting for id=$id"
}

try {
    # 1. initialize
    Send-Msg @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; clientInfo=@{ name='smoke-test'; version='0' }; capabilities=@{} } }
    $init = Recv-Until-Id 1
    Write-Host "[init] server: $($init.result.serverInfo.name) v$($init.result.serverInfo.version)" -ForegroundColor Green

    # 2. initialized notification
    Send-Msg @{ jsonrpc='2.0'; method='notifications/initialized' }

    # 3. tools/list
    Send-Msg @{ jsonrpc='2.0'; id=2; method='tools/list'; params=@{} }
    $tl = Recv-Until-Id 2
    $toolNames = $tl.result.tools | ForEach-Object { $_.name }
    Write-Host "[tools/list] $($tl.result.tools.Count): $($toolNames -join ', ')" -ForegroundColor Green
    $expected = @('fake_echo','fake_upper','sitter_status','sitter_kill')
    $missing = $expected | Where-Object { $_ -notin $toolNames }
    if ($missing) { throw "missing tools: $($missing -join ',')" }

    # 4. fake_echo call
    Send-Msg @{ jsonrpc='2.0'; id=3; method='tools/call'; params=@{ name='fake_echo'; arguments=@{ text='hello world' } } }
    $call = Recv-Until-Id 3
    Write-Host "[fake_echo] $($call.result.content[0].text)" -ForegroundColor Green
    if ($call.result.content[0].text -ne 'echo: hello world') { throw "unexpected echo result" }

    # 5. sitter_status
    Send-Msg @{ jsonrpc='2.0'; id=4; method='tools/call'; params=@{ name='sitter_status'; arguments=@{} } }
    $st = Recv-Until-Id 4
    Write-Host "[sitter_status]" -ForegroundColor Green
    Write-Host $st.result.content[0].text
    $statusObj = $st.result.content[0].text | ConvertFrom-Json
    if (-not $statusObj.childAlive) { throw "childAlive should be true" }
    if (-not $statusObj.childReady) { throw "childReady should be true" }
    if ($statusObj.killCount -ne 0) { throw "killCount should be 0" }

    # 6. sitter_kill -> lazy respawn via fake_echo
    Send-Msg @{ jsonrpc='2.0'; id=5; method='tools/call'; params=@{ name='sitter_kill'; arguments=@{} } }
    $kl = Recv-Until-Id 5
    Write-Host "[sitter_kill] $($kl.result.content[0].text)" -ForegroundColor Green

    # 7. fake_echo triggers lazy respawn
    Send-Msg @{ jsonrpc='2.0'; id=6; method='tools/call'; params=@{ name='fake_echo'; arguments=@{ text='after kill' } } }
    $call2 = Recv-Until-Id 6 15
    Write-Host "[fake_echo after kill] $($call2.result.content[0].text)" -ForegroundColor Green
    if ($call2.result.content[0].text -ne 'echo: after kill') { throw "echo after kill failed" }

    # 8. sitter_status should show kill and respawn
    Send-Msg @{ jsonrpc='2.0'; id=7; method='tools/call'; params=@{ name='sitter_status'; arguments=@{} } }
    $st2 = Recv-Until-Id 7
    Write-Host "[sitter_status after kill+respawn]" -ForegroundColor Green
    Write-Host $st2.result.content[0].text
    $statusObj2 = $st2.result.content[0].text | ConvertFrom-Json
    if ($statusObj2.killCount -lt 1) { throw "killCount should be >= 1, got $($statusObj2.killCount)" }
    if ($statusObj2.spawnCount -lt 2) { throw "spawnCount should be >= 2, got $($statusObj2.spawnCount)" }
    if (-not $statusObj2.childReady) { throw "child should be ready after lazy respawn" }

    Write-Host "`nALL CHECKS PASSED" -ForegroundColor Green
}
finally {
    try { $p.StandardInput.Close() } catch {}
    $p.WaitForExit(3000) | Out-Null
    if (-not $p.HasExited) { $p.Kill($true) }
    Get-EventSubscriber | Where-Object { $_.SourceObject -eq $p } | Unregister-Event
    Write-Host "`n--- stderr (mcp-sitter + child) ---" -ForegroundColor Magenta
    while ($script:stderrLines.TryDequeue([ref]$null)) {}
    foreach ($l in $script:stderrLines.ToArray()) { Write-Host $l -ForegroundColor DarkMagenta }
}
