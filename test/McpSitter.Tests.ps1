#requires -Version 7.0
# Pester v5 integration tests for mcp-sitter.
#
# Exercises the full MCP handshake against the sitter-with-FakeMcp pipeline:
# tools/list merging, tool forwarding, sitter_kill + lazy respawn, and the
# sitter_child_stderr ring buffer.
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
        param([string[]]$SitterArgs = @(), [string]$ChildExe = $script:childExe)
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $script:relayExe
        foreach ($a in $SitterArgs) { $psi.ArgumentList.Add($a) }
        $psi.ArgumentList.Add($ChildExe)
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

    # A private copy of FakeMcp, so a test can replace its binary.
    function New-FakeMcpCopy {
        param([string]$Name)
        $dir = Join-Path $TestDrive $Name
        Copy-Item -Path (Split-Path $script:childExe -Parent) -Destination $dir -Recurse
        return Join-Path $dir (Split-Path $script:childExe -Leaf)
    }

    # Deploys a "new build" the way a build script that works around the
    # Windows lock does: rename the running file aside, copy the new one in.
    function Publish-NewBuild {
        param([string]$Exe)
        $stash = "$Exe.stash-$(Get-Random)"
        Move-Item -LiteralPath $Exe -Destination $stash
        Copy-Item -LiteralPath $stash -Destination $Exe
        (Get-Item -LiteralPath $Exe).LastWriteTimeUtc = [datetime]::UtcNow
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

Describe "in-flight requests when the child exits" {
    BeforeAll {
        $script:p = Start-Sitter
        Invoke-Handshake $script:p
        $script:nextId = 500
    }
    AfterAll {
        Stop-Sitter $script:p
    }

    It "answers a request the child dies on with an error instead of leaving it hanging" {
        $r = Call-Tool $script:p ($script:nextId++) 'fake_crash' @{ text = 'x' } 8
        $r.error | Should -Not -BeNullOrEmpty
        $r.error.message | Should -Match 'exited \(code 3\)'
    }

    It "restarts on the next call and says the previous instance crashed" {
        $r = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'again' } 15
        $texts = @($r.result.content | ForEach-Object { $_.text })
        $texts | Should -Contain 'echo: again'
        ($texts -join "`n") | Should -Match 'crashed \(exit 3\)'
    }

    It "still delivers an answer the child sent just before exiting" {
        $r = Call-Tool $script:p ($script:nextId++) 'fake_exit_after' @{ text = 'x' } 8
        $r.error | Should -BeNullOrEmpty
        $r.result.content[-1].text | Should -Be 'bye: x'
    }
}

Describe "reload on change" {
    BeforeAll {
        $script:exe = New-FakeMcpCopy 'reload'
        $script:p = Start-Sitter -ChildExe $script:exe
        Invoke-Handshake $script:p
        $script:nextId = 700
        $null = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'first' }
    }
    AfterAll {
        Stop-Sitter $script:p
    }

    It "watches the child exe and its .dll" {
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_status' @{}
        $status = $r.result.content[0].text | ConvertFrom-Json
        $status.reloadOnChange | Should -Contain ([IO.Path]::GetFullPath($script:exe))
        $status.binaryChangedSinceSpawn | Should -BeFalse
    }

    It "restarts the child on the new build at the next tool call, without sitter_kill" {
        Set-Content -LiteralPath (Join-Path (Split-Path $script:exe) 'toolset.txt') -Value 'b'
        Publish-NewBuild $script:exe
        $r = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'second' } 20
        $texts = @($r.result.content | ForEach-Object { $_.text })
        $texts | Should -Contain 'echo: second'
        $notice = $texts -join "`n"
        $notice | Should -Match '\[mcp-sitter\] server restarted \(binary changed'
        $notice | Should -Match 'fake_reverse'
    }

    It "does not restart again while the binary stays the same" {
        $r = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'third' }
        @($r.result.content).Count | Should -Be 1
        $status = (Call-Tool $script:p ($script:nextId++) 'sitter_status' @{}).result.content[0].text | ConvertFrom-Json
        $status.spawnCount | Should -Be 2
        $status.lastKillReason | Should -Be 'binary changed'
    }
}

Describe "--no-reload-on-change" {
    BeforeAll {
        $script:exe = New-FakeMcpCopy 'noreload'
        $script:p = Start-Sitter -SitterArgs @('--no-reload-on-change') -ChildExe $script:exe
        Invoke-Handshake $script:p
        $script:nextId = 900
        $null = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'first' }
    }
    AfterAll {
        Stop-Sitter $script:p
    }

    It "keeps the running child when the binary changes" {
        Publish-NewBuild $script:exe
        $r = Call-Tool $script:p ($script:nextId++) 'fake_echo' @{ text = 'second' }
        @($r.result.content).Count | Should -Be 1
        $status = (Call-Tool $script:p ($script:nextId++) 'sitter_status' @{}).result.content[0].text | ConvertFrom-Json
        $status.spawnCount | Should -Be 1
    }
}

Describe "sitter_kill and other processes running the same exe" {
    BeforeAll {
        $script:exe = New-FakeMcpCopy 'shared'
        $script:p = Start-Sitter -ChildExe $script:exe
        $script:other = Start-Sitter -ChildExe $script:exe
        Invoke-Handshake $script:p
        Invoke-Handshake $script:other
        $script:nextId = 1100
        $null = Call-Tool $script:other ($script:nextId++) 'fake_echo' @{ text = 'up' }
        $otherStatus = (Call-Tool $script:other ($script:nextId++) 'sitter_status' @{}).result.content[0].text | ConvertFrom-Json
        $script:otherChildPid = $otherStatus.childPid
    }
    AfterAll {
        Stop-Sitter $script:p
        Stop-Sitter $script:other
    }

    It "stops only its own child and lists the other one" {
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_kill' @{}
        $report = $r.result.content[0].text | ConvertFrom-Json
        @($report.killed | Where-Object relation -eq 'same-path') | Should -BeNullOrEmpty
        $report.sameExeStillRunning | Should -Contain $script:otherChildPid
        $r2 = Call-Tool $script:other ($script:nextId++) 'fake_echo' @{ text = 'still here' }
        $r2.result.content[-1].text | Should -Be 'echo: still here'
    }

    It "stops the other one too with others=true" {
        $r = Call-Tool $script:p ($script:nextId++) 'sitter_kill' @{ others = $true }
        $report = $r.result.content[0].text | ConvertFrom-Json
        @($report.killed | Where-Object relation -eq 'same-path').pid | Should -Contain $script:otherChildPid
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
