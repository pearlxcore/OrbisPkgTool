# Merge-Results.ps1 - combine two run directories into one coherent report.
# For every phase/test line, the PATCH run's result wins if it ran that phase;
# otherwise the BASE run's result is kept. The merged summary recomputes the
# unexpected-failure count and the final label.
#
# Usage:
#   .\CrossValidation\Merge-Results.ps1 -Base <runDir> -Patch <runDir> [-Out <runDir>]

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][string]$Patch,
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path (Join-Path $Base "summary.txt"))) { throw "Base has no summary.txt: $Base" }
if (-not (Test-Path (Join-Path $Patch "summary.txt"))) { throw "Patch has no summary.txt: $Patch" }

if (-not $Out) {
    $Out = Join-Path (Split-Path $Base) ("{0}_Merged" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
}
New-Item -ItemType Directory -Force $Out | Out-Null

# ---- key of a summary line: the test name (everything before 2+ spaces) ----
function Get-Key([string]$line) {
    $m = [regex]::Match($line, '^\S.*?\s{2,}')
    if ($m.Success) { return $m.Value.Trim() }
    return $line.Trim()
}

$baseLines = Get-Content (Join-Path $Base "summary.txt")
$patchLines = Get-Content (Join-Path $Patch "summary.txt")

$map = [ordered]@{}
$fromPatch = [System.Collections.Generic.HashSet[string]]::new()
# Only result lines are mergeable - skip headers/trailers.
function Is-ResultLine([string]$l) {
    return $l -match "^\S" -and $l -notmatch "^(=+|-+|ORBISPKGTOOL|UNEXPECTED FAILURES|PC CROSS|  base|  patch)"
}
foreach ($l in $baseLines) { if (Is-ResultLine $l) { $map[$(Get-Key $l)] = $l } }
foreach ($l in $patchLines) {
    if (Is-ResultLine $l) {
        $k = Get-Key $l
        $map[$k] = $l
        $null = $fromPatch.Add($k)
    }
}

$merged = [System.Collections.Generic.List[string]]::new()
$merged.Add("============================================================")
$merged.Add("ORBISPKGTOOL EXTERNAL CROSS-VALIDATION (FULL) - MERGED")
$merged.Add("  base : $Base")
$merged.Add("  patch: $Patch")
$merged.Add("============================================================")
foreach ($k in $map.Keys) { $merged.Add($map[$k]) }

$unexpected = @($map.Values | Where-Object { $_ -match "FAIL|ERROR" -and $_ -notmatch "PASS_EXPECTED|EXPECTED_DIFFERENCE|SKIPPED|NOT_FOUND" })
$merged.Add("------------------------------------------------------------")
$merged.Add("UNEXPECTED FAILURES/ERRORS: $($unexpected.Count)")
$label = if ($unexpected.Count -eq 0) { "PC_MAXIMUM_VALIDATED (subject to roundtrip results above)" } else { "INCOMPLETE" }
$merged.Add("PC CROSS-IMPLEMENTATION VALIDATION: $label")
Set-Content -Path (Join-Path $Out "summary.txt") -Value $merged -Encoding utf8

# ---- run_status.txt merge (stage-keyed) ----
if (Test-Path (Join-Path $Base "run_status.txt")) {
    $bStatus = Get-Content (Join-Path $Base "run_status.txt")
    $pStatus = Get-Content (Join-Path $Patch "run_status.txt") -ErrorAction SilentlyContinue
    $sMap = [ordered]@{}
    foreach ($l in $bStatus) {
        if ($l -match '^\[(\w+)\]\s+(.+)$') { $sMap[$Matches[2].Trim()] = $l }
    }
    if ($pStatus) {
        foreach ($l in $pStatus) {
            if ($l -match '^\[(\w+)\]\s+(.+)$') { $sMap[$Matches[2].Trim()] = $l }
        }
    }
    Set-Content -Path (Join-Path $Out "run_status.txt") -Value @($sMap.Values) -Encoding utf8
}

# ---- per-line provenance ----
$prov = [System.Collections.Generic.List[string]]::new()
foreach ($k in $map.Keys) {
    $prov.Add("$k`t$(if ($fromPatch.Contains($k)) { 'patch' } else { 'base' })")
}
Set-Content -Path (Join-Path $Out "merge_provenance.txt") -Value $prov -Encoding utf8

Write-Host "Merged report -> $Out"
Write-Host ""
Get-Content (Join-Path $Out "summary.txt") | Select-Object -Skip 4 | ForEach-Object { Write-Host $_ }
