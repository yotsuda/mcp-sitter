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
    # MCP_SITTER_EXE runs the suite against another build, e.g. the NativeAOT one in dist/.
    $script:relayExe = if ($env:MCP_SITTER_EXE) { $env:MCP_SITTER_EXE } else { Join-Path $PSScriptRoot "../bin/Debug/net9.0/mcp-sitter$ext" }
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

Describe "mcp-sitter add" {
    BeforeAll {
        function Invoke-Add {
            param([string[]]$AddArgs)
            $out = & $script:relayExe add --dry-run @AddArgs 2>&1
            [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
        }
        $script:fakeConfig = Join-Path $TestDrive 'claude.json'
        @{ mcpServers = @{ myserver = @{ command = 'prod.exe'; env = @{ PIPE = 'PROD'; SHARED = 'from-prod' } } } } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $script:fakeConfig
        $env:MCP_SITTER_CLAUDE_JSON = $script:fakeConfig
    }
    AfterAll {
        Remove-Item Env:MCP_SITTER_CLAUDE_JSON -ErrorAction SilentlyContinue
    }

    It "appends -dev to the name and registers the child behind this mcp-sitter" {
        $r = Invoke-Add @('myserver', $script:childExe)
        $r.Code | Should -Be 0
        $r.Text | Should -Match '^claude mcp add --scope user myserver-dev -- '
        $r.Text | Should -Match ([regex]::Escape([IO.Path]::GetFullPath($script:childExe)))
    }

    It "keeps a name that already ends in -dev" {
        (Invoke-Add @('myserver-dev', $script:childExe)).Text | Should -Match ' myserver-dev -- '
        (Invoke-Add @('myserver-dev', $script:childExe)).Text | Should -Not -Match 'myserver-dev-dev'
    }

    It "copies env from --from and lets -e override it" {
        $r = Invoke-Add @('myserver', '--from', 'myserver', '-e', 'PIPE=DEV', $script:childExe)
        $r.Code | Should -Be 0
        $r.Text | Should -Match '-e SHARED=from-prod'
        $r.Text | Should -Match '-e PIPE=DEV'
        $r.Text | Should -Not -Match 'PIPE=PROD'
    }

    It "passes mcp-sitter options before the child and child args after it" {
        $r = Invoke-Add @('myserver', '--no-reload-on-change', $script:childExe, '--flag', 'x')
        $r.Text | Should -Match '--no-reload-on-change -- .+FakeMcp\S* --flag x$'
    }

    It "fails clearly for an unknown --from server or a missing child" {
        $r = Invoke-Add @('myserver', '--from', 'nosuch', $script:childExe)
        $r.Code | Should -Not -Be 0
        $r.Text | Should -Match "no server named 'nosuch'"
        $r = Invoke-Add @('myserver', (Join-Path $TestDrive 'missing.exe'))
        $r.Code | Should -Not -Be 0
        $r.Text | Should -Match 'child executable not found'
    }
}

Describe "hub" {
    BeforeAll {
        function Start-Hub {
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = $script:relayExe
            $psi.ArgumentList.Add('--hub')
            $psi.RedirectStandardInput = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
            $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
            $p = [System.Diagnostics.Process]::new(); $p.StartInfo = $psi; $null = $p.Start()
            return $p
        }
        # Every line up to (and including) the response with this id.
        function Recv-Lines-Until-Id {
            param($p, $id, [int]$timeoutSec = 20)
            $lines = @()
            $deadline = (Get-Date).AddSeconds($timeoutSec)
            while ((Get-Date) -lt $deadline) {
                $t = $p.StandardOutput.ReadLineAsync()
                if (-not $t.Wait([int](($deadline - (Get-Date)).TotalMilliseconds))) { break }
                if ($null -eq $t.Result) { break }
                $obj = $t.Result | ConvertFrom-Json
                $lines += $obj
                if ($null -ne $obj.id -and $obj.id -eq $id) { return $lines }
            }
            throw "timeout waiting for id=$id"
        }
        function Get-ToolNames {
            param($p, $id)
            Send-Msg $p @{ jsonrpc='2.0'; id=$id; method='tools/list'; params=@{} }
            @((Recv-Until-Id $p $id).result.tools | ForEach-Object name)
        }

        # The hub remembers attachments per project; keep this run's memory private.
        $env:MCP_SITTER_HUB_STATE = Join-Path $TestDrive 'hub-state'
        $script:exe = New-FakeMcpCopy 'hub'
        $script:h = Start-Hub
        Invoke-Handshake $script:h
        $script:nextId = 1300
    }
    AfterAll {
        Stop-Sitter $script:h
        Remove-Item Env:MCP_SITTER_HUB_STATE -ErrorAction SilentlyContinue
    }

    It "starts with only its own tools" {
        $names = Get-ToolNames $script:h ($script:nextId++)
        $names | Should -Contain 'sitter_attach'
        $names | Should -Contain 'sitter_detach'
        $names | Should -Contain 'sitter_list'
        $names | Where-Object { $_ -like 'fake_*' } | Should -BeNullOrEmpty
    }

    It "attaches a server, announces its tools at once and returns its instructions" {
        $id = $script:nextId++
        Send-Msg $script:h @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name='sitter_attach'; arguments=@{
            name = 'fake'; command = $script:exe; env = @{ FAKEMCP_INSTRUCTIONS = 'Always say please.' } } } }
        $lines = Recv-Lines-Until-Id $script:h $id
        ($lines | Where-Object method -eq 'notifications/tools/list_changed') | Should -Not -BeNullOrEmpty
        $text = $lines[-1].result.content[0].text
        $lines[-1].result.isError | Should -Not -BeTrue
        $text | Should -Match 'fake_echo\s+->\s+fake_echo'
        $text | Should -Match 'Always say please\.'
        $names = Get-ToolNames $script:h ($script:nextId++)
        $names | Should -Contain 'fake_echo'
        $names | Should -Contain 'fake_sitter_status'
    }

    It "routes a prefixed call to the server under its own name" {
        $r = Call-Tool $script:h ($script:nextId++) 'fake_echo' @{ text = 'via hub' }
        $r.result.content[-1].text | Should -Be 'echo: via hub'
    }

    It "passes a crash through as an error, not a hang" {
        $r = Call-Tool $script:h ($script:nextId++) 'fake_crash' @{ text = 'x' } 10
        $r.error.message | Should -Match 'exited \(code 3\)'
        $r = Call-Tool $script:h ($script:nextId++) 'fake_echo' @{ text = 'back' } 15
        $r.result.content[-1].text | Should -Be 'echo: back'
    }

    It "reloads a new build of an attached server and re-announces its changed tools" {
        Set-Content -LiteralPath (Join-Path (Split-Path $script:exe) 'toolset.txt') -Value 'b'
        Publish-NewBuild $script:exe
        $id = $script:nextId++
        Send-Msg $script:h @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name='fake_echo'; arguments=@{ text='new build' } } }
        $lines = Recv-Lines-Until-Id $script:h $id
        $texts = @($lines[-1].result.content | ForEach-Object text)
        $texts | Should -Contain 'echo: new build'
        ($texts -join "`n") | Should -Match 'server restarted \(binary changed'
        Start-Sleep -Milliseconds 500
        $names = Get-ToolNames $script:h ($script:nextId++)
        $names | Should -Contain 'fake_reverse'
        $names | Should -Not -Contain 'fake_log'
    }

    It "keeps two servers with the same tool names apart" {
        $other = New-FakeMcpCopy 'hub2'
        $r = Call-Tool $script:h ($script:nextId++) 'sitter_attach' @{ name = 'other'; command = $other } 20
        $r.result.isError | Should -Not -BeTrue
        (Call-Tool $script:h ($script:nextId++) 'other_fake_upper' @{ text = 'abc' }).result.content[-1].text | Should -Be 'ABC'
        (Call-Tool $script:h ($script:nextId++) 'fake_echo' @{ text = 'still' }).result.content[-1].text | Should -Be 'echo: still'
    }

    It "shortens a tool name past the length limit and keeps it routable" {
        $r = Call-Tool $script:h ($script:nextId++) 'sitter_attach' @{ name = 'a-very-long-server-name-for-the-limit'; command = $script:exe } 20
        $names = @((Get-ToolNames $script:h ($script:nextId++)) | Where-Object { $_ -like 'a-very-long*' })
        foreach ($n in $names) { $n.Length | Should -BeLessOrEqual (64 - 'mcp__sitter-dev__'.Length) }
        # <name>_sitter_child_stderr is 57 characters: cut, with a hash of the full name
        $hashed = @($names | Where-Object { $_ -match '_[0-9a-f]{6}$' })
        $hashed | Should -Not -BeNullOrEmpty
        $map = (Call-Tool $script:h ($script:nextId++) 'sitter_list' @{}).result.content[0].text | ConvertFrom-Json
        $entry = $map.attached | Where-Object name -eq 'a-very-long-server-name-for-the-limit'
        $stderrName = $entry.tools.PSObject.Properties | Where-Object Value -eq 'sitter_child_stderr' | ForEach-Object Name
        $hashed | Should -Contain $stderrName
        $r = Call-Tool $script:h ($script:nextId++) $stderrName @{}
        $r.error | Should -BeNullOrEmpty
        $r.result.content[0].text | Should -Match 'FakeMcp started'
        $null = Call-Tool $script:h ($script:nextId++) 'sitter_detach' @{ name = 'a-very-long-server-name-for-the-limit' }
    }

    It "detaches a server and removes its tools" {
        $r = Call-Tool $script:h ($script:nextId++) 'sitter_detach' @{ name = 'other' }
        $r.result.content[0].text | Should -Match "Detached 'other'"
        $names = Get-ToolNames $script:h ($script:nextId++)
        $names | Where-Object { $_ -like 'other_*' } | Should -BeNullOrEmpty
        $r = Call-Tool $script:h ($script:nextId++) 'other_fake_upper' @{ text = 'x' }
        $r.error.message | Should -Match 'unknown tool'
    }

    It "refuses to attach a command that does not exist" {
        $r = Call-Tool $script:h ($script:nextId++) 'sitter_attach' @{ name = 'broken'; command = (Join-Path $TestDrive 'nope.exe') } 20
        $r.result.isError | Should -BeTrue
        $r.result.content[0].text | Should -Match 'command not found'
    }

    It "runs from a shadow copy, so a build can overwrite the exe in place while it runs" {
        $text = (Call-Tool $script:h ($script:nextId++) 'sitter_list' @{}).result.content[0].text
        $status = (Call-Tool $script:h ($script:nextId++) 'fake_sitter_status' @{}).result.content[-1].text | ConvertFrom-Json
        $status.shadowCopy | Should -Not -BeNullOrEmpty
        $status.shadowCopy | Should -Not -Be ([IO.Path]::GetFullPath($script:exe))
        # in place, no rename: this is what a plain `dotnet build` does, and
        # it fails on Windows when the exe itself is running
        $tmp = "$script:exe.tmp"
        Copy-Item -LiteralPath $script:exe -Destination $tmp
        { Copy-Item -LiteralPath $tmp -Destination $script:exe -Force } | Should -Not -Throw
        (Get-Item -LiteralPath $script:exe).LastWriteTimeUtc = [datetime]::UtcNow
        Remove-Item -LiteralPath $tmp
        $r = Call-Tool $script:h ($script:nextId++) 'fake_echo' @{ text = 'in place' } 20
        $texts = @($r.result.content | ForEach-Object text)
        $texts | Should -Contain 'echo: in place'
        ($texts -join "`n") | Should -Match 'binary changed'
    }

    It "tells the AI how to load the new tools" {
        $other = New-FakeMcpCopy 'hub3'
        $r = Call-Tool $script:h ($script:nextId++) 'sitter_attach' @{ name = 'tmp'; command = $other; remember = $false } 20
        $r.result.content[0].text | Should -Match 'select:mcp__sitter-dev__tmp_fake_echo,'
        $r.result.content[0].text | Should -Match 'Not remembered'
        $null = Call-Tool $script:h ($script:nextId++) 'sitter_detach' @{ name = 'tmp' }
    }

    It "attaches the remembered servers again in the next session of this project" {
        $h2 = Start-Hub
        try {
            Send-Msg $h2 @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; clientInfo=@{ name='pester'; version='0' }; capabilities=@{} } }
            $init = Recv-Until-Id $h2 1
            $init.result.instructions | Should -Match 'Remembered for this project.*fake'
            $init.result.instructions | Should -Not -Match '\bother\b'
            Send-Msg $h2 @{ jsonrpc='2.0'; method='notifications/initialized' }
            $deadline = (Get-Date).AddSeconds(20); $names = @()
            while ((Get-Date) -lt $deadline -and $names -notcontains 'fake_echo') {
                Start-Sleep -Milliseconds 300
                $names = Get-ToolNames $h2 (Get-Random -Minimum 5000 -Maximum 9000)
            }
            $names | Should -Contain 'fake_echo'
            (Call-Tool $h2 9100 'fake_echo' @{ text = 'remembered' }).result.content[-1].text | Should -Be 'echo: remembered'
            # detach forgets it: a third session starts empty
            $null = Call-Tool $h2 9101 'sitter_detach' @{ name = 'fake' }
        }
        finally { Stop-Sitter $h2 }
        $h3 = Start-Hub
        try {
            Send-Msg $h3 @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; clientInfo=@{ name='pester'; version='0' }; capabilities=@{} } }
            (Recv-Until-Id $h3 1).result.instructions | Should -Not -Match 'Remembered for this project'
        }
        finally { Stop-Sitter $h3 }
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
