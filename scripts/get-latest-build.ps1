# Pulls the most recent SUCCESSFUL CI build into dist\Huginn.zip.
#
# dist\ is not produced by the build and nothing updates it automatically, so a
# hand-copied zip goes stale the moment another run finishes — which is how you
# end up sending someone an old build believing it is current. Run this instead
# of copying by hand, and check the commit it prints against what you expect.

[CmdletBinding()]
param(
    # Skip the download when dist already matches the latest build.
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $repoRoot 'dist'
$distZip = Join-Path $distDir 'Huginn.zip'

$ghExe = Get-Command gh -ErrorAction SilentlyContinue
if (-not $ghExe) {
    $fallback = 'C:\Program Files\GitHub CLI\gh.exe'
    if (-not (Test-Path $fallback)) { throw 'GitHub CLI not found. Install it with: winget install --id GitHub.cli' }
    $ghExe = $fallback
} else {
    $ghExe = $ghExe.Source
}

$run = & $ghExe run list --limit 1 --status success `
    --json databaseId,displayTitle,headSha,createdAt | ConvertFrom-Json
if (-not $run) { throw 'No successful runs found.' }

$runId = $run[0].databaseId
Write-Host "Latest successful build" -ForegroundColor Cyan
Write-Host "  run     : $runId"
Write-Host "  commit  : $($run[0].headSha.Substring(0,7)) - $($run[0].displayTitle)"
Write-Host "  built   : $($run[0].createdAt)"

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "huginn_build_$runId"
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

& $ghExe run download $runId -n Huginn-macOS -D $staging | Out-Null
$fresh = Join-Path $staging 'Huginn.zip'
if (-not (Test-Path $fresh)) { throw "Artifact did not contain Huginn.zip (run $runId)." }

$freshHash = (Get-FileHash $fresh -Algorithm SHA256).Hash
$currentHash = if (Test-Path $distZip) { (Get-FileHash $distZip -Algorithm SHA256).Hash } else { $null }

if ($freshHash -eq $currentHash) {
    Write-Host "`ndist\Huginn.zip is already current." -ForegroundColor Green
    Remove-Item -LiteralPath $staging -Recurse -Force
    return
}

if ($CheckOnly) {
    Write-Host "`ndist\Huginn.zip is STALE. Re-run without -CheckOnly to update." -ForegroundColor Yellow
    Remove-Item -LiteralPath $staging -Recurse -Force
    return
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item $fresh $distZip -Force
Remove-Item -LiteralPath $staging -Recurse -Force

$size = [math]::Round((Get-Item $distZip).Length / 1MB, 2)
Write-Host "`nUpdated $distZip ($size MB)" -ForegroundColor Green
Write-Host "  sha256: $freshHash"
