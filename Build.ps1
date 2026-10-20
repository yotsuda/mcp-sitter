<#
.SYNOPSIS
    Release-publish mcp-sitter (NativeAOT) and deploy to npm/platforms/win32-x64/bin.
.DESCRIPTION
    Runs `dotnet publish -c Release` (NativeAOT single native exe into ./dist),
    optionally Authenticode-signs the binary, and mirrors the resulting binary
    to ./npm/platforms/win32-x64/bin so the Windows platform sub-package ships
    the fresh build. No process is stopped: a running copy of either exe is
    renamed aside first (Windows allows renaming a running binary), so the
    mcp-sitter instances of every MCP client session keep running.
.PARAMETER ReadmeOnly
    Only regenerate npm/README.md from README.md.
.PARAMETER Sign
    Authenticode-sign the published binary. Requires -PfxPath and will prompt
    for the PFX password at sign time.
.PARAMETER PfxPath
    Path to the PFX file holding the code-signing certificate.
    Default: C:\MyProj\vault\yotsuda.pfx
.PARAMETER TimestampUrl
    RFC 3161 timestamp authority used to timestamp the signature so it remains
    verifiable after the cert expires.
    Default: http://timestamp.digicert.com
.EXAMPLE
    .\Build.ps1
.EXAMPLE
    .\Build.ps1 -Sign
#>
[CmdletBinding()]
param(
    [switch]$Sign,
    [string]$PfxPath = 'C:\MyProj\vault\yotsuda.pfx',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$ReadmeOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'McpSitter.csproj'
$DistDir = Join-Path $ProjectRoot 'dist'
$NpmWin32BinDir = Join-Path $ProjectRoot 'npm\platforms\win32-x64\bin'

# Regenerate npm/README.md from root README.md (npmjs.com does not render
# mermaid fences): the Architecture diagram becomes an ASCII sketch, the
# lazy-respawn sequence is dropped, and any other diagram becomes a link.
function Update-NpmReadme {
    $readmeSrc = Join-Path $ProjectRoot 'README.md'
    $readmeDst = Join-Path $ProjectRoot 'npm\README.md'
    if (-not (Test-Path $readmeSrc)) { return }
    $text = [System.IO.File]::ReadAllText($readmeSrc)
    $architecture = @"
``````
MCP Client <--stdio--> mcp-sitter <--stdio--> your MCP server
  (spawned and owned by the bridge; restarted on a new build, after
   sitter_kill, or after a crash)
``````

See the [GitHub README](https://github.com/yotsuda/mcp-sitter#architecture)
for rendered architecture and sequence diagrams.
"@
    $link = 'See the [GitHub README](https://github.com/yotsuda/mcp-sitter) for the rendered diagram.'
    $stripped = [regex]::Replace($text, '(?s)### Lazy respawn sequence\r?\n\r?\n```mermaid.*?```\r?\n\r?\n', '')
    $stripped = [regex]::Replace($stripped, '(?s)(## Architecture\r?\n\r?\n)```mermaid.*?```', { param($m) $m.Groups[1].Value + $architecture })
    $stripped = [regex]::Replace($stripped, '(?s)```mermaid.*?```', $link)
    [System.IO.File]::WriteAllText($readmeDst, $stripped)
    Write-Host "      Regenerated $readmeDst (mermaid stripped)" -ForegroundColor Green
}

if ($ReadmeOnly) { Update-NpmReadme; return }

# Replaces $Destination even while it runs: the old file is renamed aside
# first, and the running process keeps the renamed file. Stashes left by
# earlier runs are removed once nothing holds them.
function Move-Aside([string]$Destination) {
    $dir = Split-Path $Destination -Parent
    $name = Split-Path $Destination -Leaf
    Get-ChildItem -LiteralPath $dir -Filter "$name.stash-*" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $Destination) {
        $stash = "$Destination.stash-$(Get-Random)"
        Move-Item -LiteralPath $Destination -Destination $stash -Force
        Remove-Item -LiteralPath $stash -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '=== mcp-sitter Release Publish ===' -ForegroundColor Cyan

Write-Host "`n[1/4] Moving running copies aside (nothing is stopped)..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
Move-Aside (Join-Path $DistDir 'mcp-sitter.exe')

Write-Host "`n[2/4] Publishing (Release, NativeAOT)..." -ForegroundColor Yellow
dotnet publish $ProjectFile -c Release -r win-x64 -o $DistDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

$src = Join-Path $DistDir 'mcp-sitter.exe'
if (-not (Test-Path $src)) { throw "Published binary not found: $src" }

if ($Sign) {
    Write-Host "`n[3/4] Authenticode-signing $src ..." -ForegroundColor Yellow
    if (-not (Test-Path $PfxPath)) { throw "PFX not found at $PfxPath" }
    $pfxPassword = Read-Host "      Enter PFX password" -AsSecureString
    $cert = Get-PfxCertificate -FilePath $PfxPath -Password $pfxPassword
    $result = Set-AuthenticodeSignature `
        -FilePath $src `
        -Certificate $cert `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampUrl `
        -IncludeChain NotRoot
    if ($result.Status -ne 'Valid') {
        throw "Sign failed for $src : $($result.StatusMessage)"
    }
    Write-Host "      Signed (status: Valid, thumbprint: $($cert.Thumbprint))" -ForegroundColor Green
} else {
    Write-Host "`n[3/4] Skipping signing (pass -Sign to enable, e.g. for publish builds)." -ForegroundColor Gray
}

Write-Host "`n[4/4] Deploying to npm/platforms/win32-x64/bin..." -ForegroundColor Yellow
$dst = Join-Path $NpmWin32BinDir 'mcp-sitter.exe'
New-Item -ItemType Directory -Force -Path $NpmWin32BinDir | Out-Null
Move-Aside $dst
Copy-Item $src $dst -Force
$size = [Math]::Round((Get-Item $dst).Length / 1MB, 2)
Write-Host "      Copied to $dst ($size MB)" -ForegroundColor Green

Update-NpmReadme

Write-Host "`n=== Done ===" -ForegroundColor Green
