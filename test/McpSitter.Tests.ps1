#requires -Version 7.0
# Pester v5 integration tests for mcp-sitter.
#
# Exercises the full MCP handshake against the sitter-with-FakeMcp pipeline:
# tools/list merging, tool forwarding, sitter_kill + lazy respawn, binary
# watcher, and the sitter_child_stderr ring buffer.
#
# Prerequisites:
#   dotnet build -c Debug
#   dotnet build test/FakeMcp/FakeMcp.csproj -c Debug
# Run:
#   Invoke-Pester test/McpSitter.Tests.ps1

BeforeAll {
    $ext = if ($IsWindows) { '.exe' } else { '' }
    $script:relayExe = Join-Path $PSScriptRoot "../bin/Debug/net9.0/mcp-sitter$ext"
    $script:childExe = Join-Path $PSScriptRoot "FakeMcp/bin/Debug/net9.0/FakeMcp$ext"

    if (-not (Test-Path $script:relayExe)) {
        throw "mcp-sitter not found at $script:relayExe. Run: dotnet build -c Debug"
    }
    if (-not (Test-Path $script:childExe)) {
        throw "FakeMcp not found at $script:childExe. Run: dotnet build test/FakeMcp/FakeMcp.csproj -c Debug"
    }

    function Start-Sitter {
        param([string[]]$SitterArgs = @())
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $script:relayExe
        foreach ($a in $SitterArgs) { $psi.ArgumentList.Add($a) }
        $psi.ArgumentList.Add($script:childExe)
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
        $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
        $p = [System.Diagnostics.Process]::new()
        $p.StartInfo = $psi
        $null = $p.Start()
        return $p
    }

    function Stop-Sitter {
        param($p)
        if (-not $p) { return }
        try { $p.StandardInput.Close() } catch {}
        $null = $p.WaitForExit(3000)
        if (-not $p.HasExited) { try { $p.Kill($true) } catch {} }
    }

    function Send-Msg {
        param($p, $obj)
        $json = $obj | ConvertTo-Json -Depth 20 -Compress
        $p.StandardInput.WriteLine($json)
        $p.StandardInput.Flush()
    }

    function Recv-Until-Id {
        param($p, $id, [int]$timeoutSec = 10)
        $deadline = (Get-Date).AddSeconds($timeoutSec)
        while ((Get-Date) -lt $deadline) {
            $readTask = $p.StandardOutput.ReadLineAsync()
            $waitMs = [int](($deadline - (Get-Date)).TotalMilliseconds)
            if ($waitMs -le 0) { throw "timeout waiting for id=$id" }
            if (-not $readTask.Wait($waitMs)) {
                throw "timeout waiting for id=$id"
            }
            $line = $readTask.Result
            if ($null -eq $line) { throw "stdout closed while waiting for id=$id" }
            $obj = $line | ConvertFrom-Json
            if ($null -ne $obj.id -and $obj.id -eq $id) { return $obj }
        }
        throw "timeout waiting for id=$id"
    }

    function Invoke-Handshake {
        param($p)
        Send-Msg $p @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; clientInfo=@{ name='pester'; version='0' }; capabilities=@{} } }
        $null = Recv-Until-Id $p 1
        Send-Msg $p @{ jsonrpc='2.0'; method='notifications/initialized' }
    }

    function Call-Tool {
        param($p, $id, $name, $toolArgs, [int]$timeoutSec = 10)
        Send-Msg $p @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$name; arguments=$toolArgs } }
        return Recv-Until-Id $p $id $timeoutSec
    }
}

Describe "mcp-sitter core" {
    BeforeAll {
        $script:p = Start-Sitter
        Invoke-Handshake $script:p
        $script:nextId = 100
    }
    AfterAll {
        Stop-Sitter $script:p
    }

    It "advertises built-in sitter_* tools alongside child tools" {
        Send-Msg $script:p @{ jsonrpc='2.0'; id=($script:nextId++); method='tools/list'; params=@{} }
        $r = Recv-Until-Id $script:p ($script:nextId - 1)
        $names = $r.result.tools | ForEach-Object { $_.name }
        $names | Should -Contain 'fake_echo'
        $names | Should -Contain 'fake_upper'
        $names | Should -Contain 'fake_log'
        $names | Should -Contain 'sitter_status'
        $names | Should -Contain 'sitter_kill'
        $names | Should -Contain 'sitter_binary_info'
        $names | Should -Contain 'sitter_child_stderr'
    }

    It "relays fake_echo to the child" {
        $r = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'hello' }
        $r.result.content[0].text | Should -Be 'echo: hello'
    }

    It "reports a healthy child via sitter_status before any kill" {
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_status' @{}
        $status = $r.result.content[0].text | ConvertFrom-Json
        $status.childAlive | Should -BeTrue
        $status.childReady | Should -BeTrue
        $status.killCount | Should -Be 0
        $status.spawnCount | Should -Be 1
    }

    It "sitter_kill drops the child and the next tool/call lazily respawns with a [mcp-sitter] restart notice" {
        $null = Call-Tool $script:p ($script:nextId++) 'sitter_kill' @{}
        $r = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'after kill' } 15
        $texts = @($r.result.content | ForEach-Object { $_.text })
        $texts | Should -Contain 'echo: after kill'
        ($texts | Where-Object { $_ -match '\[mcp-sitter\] server restarted' }) |
            Should -Not -BeNullOrEmpty -Because 'restart notice must be injected into the first post-respawn tool/call response'
    }

    It "reflects the respawn in sitter_status" {
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_status' @{}
        $status = $r.result.content[0].text | ConvertFrom-Json
        $status.spawnCount | Should -BeGreaterOrEqual 2
        $status.killCount | Should -BeGreaterOrEqual 1
        $status.childReady | Should -BeTrue
    }
}

Describe "mcp-sitter file watcher" {
    BeforeAll {
        $script:p = Start-Sitter @('--debounce','500')
        Invoke-Handshake $script:p
        $script:nextId = 200
    }
    AfterAll {
        Stop-Sitter $script:p
    }

    It "starts with killCount=0" {
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_status' @{}
        ($r.result.content[0].text | ConvertFrom-Json).killCount | Should -Be 0
    }

    It "kills the child after a binary touch" {
        (Get-Item $script:childExe).LastWriteTime = Get-Date
        Start-Sleep -Seconds 3
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_status' @{} 15
        $status = $r.result.content[0].text | ConvertFrom-Json
        $status.killCount | Should -BeGreaterOrEqual 1
        $status.lastKillReason | Should -Match 'binary'
    }

    It "lazily respawns on the next tool/call after a watcher kill" {
        $r = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'post-touch' } 15
        $texts = @($r.result.content | ForEach-Object { $_.text })
        $texts | Should -Contain 'echo: post-touch'
    }
}

Describe "sitter_child_stderr" {
    BeforeAll {
        $script:p = Start-Sitter
        Invoke-Handshake $script:p
        $script:nextId = 300
    }
    AfterAll {
        Stop-Sitter $script:p
    }

    It "captures the FakeMcp startup line written to stderr" {
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_child_stderr' @{}
        $r.result.content[0].text | Should -Match 'FakeMcp started pid='
    }

    It "captures a mid-lifecycle stderr line emitted via fake_log" {
        $null = Call-Tool $script:p ($script:nextId++) 'fake_log' @{ text = 'MARKER_ONE' }
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_child_stderr' @{}
        $r.result.content[0].text | Should -Match 'MARKER_ONE'
    }

    Context "after sitter_kill + respawn" {
        BeforeAll {
            $null = Call-Tool $script:p ($script:nextId++) 'sitter_kill' @{}
            $null = Call-Tool $script:p ($script:nextId++) 'fake_log' @{ text = 'MARKER_TWO' } 15
        }

        It "filters to the current child generation when since_spawn=true" {
            $r = Call-Tool $script:p ($script:nextId++) 'sitter_child_stderr' @{ since_spawn = $true }
            $r.result.content[0].text | Should -Match 'MARKER_TWO'
            $r.result.content[0].text | Should -Not -Match 'MARKER_ONE'
        }

        It "returns all generations when since_spawn=false" {
            $r = Call-Tool $script:p ($script:nextId++) 'sitter_child_stderr' @{ since_spawn = $false }
            $r.result.content[0].text | Should -Match 'MARKER_ONE'
            $r.result.content[0].text | Should -Match 'MARKER_TWO'
        }

        It "inserts a '----- child respawn (gen N, pid P) -----' delimiter between generations" {
            $r = Call-Tool $script:p ($script:nextId++) 'sitter_child_stderr' @{ since_spawn = $false }
            $text = $r.result.content[0].text
            $text | Should -Match '----- child respawn \(gen 2, pid \d+\) -----'
            # MARKER_ONE from gen 1 must appear before the delimiter; MARKER_TWO from gen 2 after.
            $idxOne = $text.IndexOf('MARKER_ONE')
            $idxSep = $text.IndexOf('child respawn (gen 2')
            $idxTwo = $text.IndexOf('MARKER_TWO')
            $idxOne | Should -BeLessThan $idxSep
            $idxSep | Should -BeLessThan $idxTwo
        }
    }
}
