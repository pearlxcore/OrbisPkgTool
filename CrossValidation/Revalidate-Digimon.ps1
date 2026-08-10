# Revalidate-Digimon.ps1 - re-run the failed phases with the fixed harness and
# merge each result onto the previous run's report.
#
# Flow:
#   1. detects the base run (newest full run) and any newer targeted patch
#   2. merges base + patches into a growing merged report
#   3. runs the failing phases only (default: H,K then M then I,J)
#   4. merges each re-run result onto the report
#   5. prints the final merged summary
#
# Usage:
#   .\CrossValidation\Revalidate-Digimon.ps1 [-Base <runDir>] [-RunPhases "H,K,M,I,J"]
#       [-SkipMerge] [-CleanOldRun]

[CmdletBinding()]
param(
    [string]$Base = "",
    [string]$RunPhases = "H,K,M,I,J",
    [switch]$SkipMerge,
    [switch]$CleanOldRun
)

$ErrorActionPreference = "Stop"
$results = "C:\Users\User\source\repos\PS4 Fake Pkg Tools\CrossValidation\Results"

# ---- locate base run ----
if (-not $Base) {
    $candidates = Get-ChildItem $results -Directory | Where-Object {
        $_.Name -notmatch "MERGED" -and (Test-Path (Join-Path $_.FullName "summary.txt"))
    } | Sort-Object LastWriteTime -Descending
    $Base = $candidates | Select-Object -First 1 -ExpandProperty FullName
}
if (-not (Test-Path (Join-Path $Base "summary.txt"))) { throw "Base run has no summary.txt: $Base" }
Write-Host "BASE RUN: $Base"

# ---- archive the base summary/status to Desktop ----
$archive = Join-Path $env:USERPROFILE "Desktop\digimon_validation_archive"
New-Item -ItemType Directory -Force $archive | Out-Null
Copy-Item (Join-Path $Base "summary.txt") (Join-Path $archive "base_summary.txt") -Force
if (Test-Path (Join-Path $Base "run_status.txt")) {
    Copy-Item (Join-Path $Base "run_status.txt") (Join-Path $archive "base_status.txt") -Force
}
Write-Host "Base report archived -> $archive"

# ---- merge any newer targeted patch onto the base ----
$current = $Base
if (-not $SkipMerge) {
    $patches = Get-ChildItem $results -Directory | Where-Object {
        $_.Name -notmatch "MERGED" -and $_.FullName -ne $Base -and
        (Test-Path (Join-Path $_.FullName "summary.txt")) -and
        $_.LastWriteTime -gt (Get-Item $Base).LastWriteTime
    } | Sort-Object LastWriteTime
    foreach ($patch in $patches) {
        $next = Join-Path $results ("{0}_Merged_{1}" -f (Split-Path $patch -Leaf), (Get-Date -Format "HHmmss"))
        & "$PSScriptRoot\Merge-Results.ps1" -Base $current -Patch $patch.FullName -Out $next | Out-Null
        Write-Host "Merged patch $($patch.Name) -> $next"
        $current = $next
    }
}

# ---- disk preflight for the re-runs ----
$drive = Get-PSDrive ([System.IO.Path]::GetPathRoot($results).TrimEnd('\')[0])
$free = [math]::Round($drive.Free / 1GB, 1)
Write-Host "Disk free: $free GB (H,K,I,J need roughly 60-90 GB)"
if ($free -lt 90) { Write-Host "WARNING: low disk - consider deleting old runs first." }

# ---- run the failing phases, merging after each group ----
$groups = @()
foreach ($ph in $RunPhases.Split(',')) {
    $ph = $ph.Trim().ToUpper()
    if ($groups.Count -eq 0 -or $groups[-1] -notmatch "^(H|K)$" -or $ph -notmatch "^(H|K)$") {
        $groups += $ph
    } else {
        $groups[-1] += ",$ph"
    }
}
foreach ($group in $groups) {
    Write-Host ""
    Write-Host "=== Running phases: $group ==="
    $out = & "$PSScriptRoot\Run-FullValidation.ps1" -Only $group 2>&1
    $out | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
    # find the newest run dir created
    $newest = Get-ChildItem $results -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $newest -or $newest.FullName -eq $current) { Write-Host "WARNING: no new run detected for $group"; continue }
    if (-not $SkipMerge) {
        $next = Join-Path $results ("{0}_Merged_{1}" -f $newest.Name, (Get-Date -Format "HHmmss"))
        & "$PSScriptRoot\Merge-Results.ps1" -Base $current -Patch $newest.FullName -Out $next | Out-Null
        Write-Host "Merged $group result -> $next"
        $current = $next
    }
}

# ---- final report ----
Write-Host ""
Write-Host "============================================================"
Write-Host "FINAL MERGED REPORT: $current"
Write-Host "============================================================"
Get-Content (Join-Path $current "summary.txt") | Select-Object -Skip 4 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Send to analysis:"
Write-Host "  $current\summary.txt"
Write-Host "  $current\run_status.txt"

# ---- optional cleanup of the old heavy run ----
if ($CleanOldRun -and $Base -ne $current) {
    $ans = Read-Host "Delete the old base run artifacts ($Base) to free disk? (y/N)"
    if ($ans -eq "y") {
        Remove-Item $Base -Recurse -Force
        Write-Host "Deleted $Base"
    }
}
