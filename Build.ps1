<#
.SYNOPSIS
    Release-publish mcp-sitter (NativeAOT) and deploy to npm/platforms/win32-x64/bin.
.DESCRIPTION
    Stops any running mcp-sitter.exe, runs `dotnet publish -c Release` (NativeAOT
    single native exe into ./dist), optionally Authenticode-signs the binary,
    and mirrors the resulting binary to ./npm/platforms/win32-x64/bin so the
    Windows platform sub-package ships the fresh build.
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
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'McpSitter.csproj'
$DistDir = Join-Path $ProjectRoot 'dist'
$NpmWin32BinDir = Join-Path $ProjectRoot 'npm\platforms\win32-x64\bin'

Write-Host '=== mcp-sitter Release Publish ===' -ForegroundColor Cyan

Write-Host "`n[1/4] Stopping running mcp-sitter.exe processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'mcp-sitter' -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

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
Copy-Item $src $dst -Force
$size = [Math]::Round((Get-Item $dst).Length / 1MB, 2)
Write-Host "      Copied to $dst ($size MB)" -ForegroundColor Green

# Regenerate npm/README.md from root README.md, stripping mermaid blocks
# (npmjs.com does not render mermaid fences).
$NpmDir = Join-Path $ProjectRoot 'npm'
$readmeSrc = Join-Path $ProjectRoot 'README.md'
$readmeDst = Join-Path $NpmDir 'README.md'
if (Test-Path $readmeSrc) {
    $text = [System.IO.File]::ReadAllText($readmeSrc)
    $replacement = @"
``````
MCP Client <--stdio--> mcp-sitter <--stdio--> your MCP server
  (spawned and owned by the bridge; lazily respawned after kill)
``````

See the [GitHub README](https://github.com/yotsuda/mcp-sitter#architecture)
for rendered architecture and sequence diagrams.
"@
    # 1. Strip the "### Lazy respawn sequence" subsection entirely (subheader + its mermaid block)
    $stripped = [regex]::Replace($text, '(?s)### Lazy respawn sequence\r?\n\r?\n```mermaid.*?```\r?\n\r?\n', '')
    # 2. Replace the remaining mermaid block (under "## Architecture") with ASCII + GitHub link
    $stripped = [regex]::Replace($stripped, '(?s)```mermaid.*?```', $replacement)
    [System.IO.File]::WriteAllText($readmeDst, $stripped)
    Write-Host "      Regenerated $readmeDst (mermaid stripped)" -ForegroundColor Green
}

Write-Host "`n=== Done ===" -ForegroundColor Green
