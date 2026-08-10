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

# ---- key of a summary line: the test name ----
# Extraction rules, first match wins:
#   1. text before 2+ spaces (normal Add-Result layout)
#   2. text before the first TAB, then strip a trailing " F"/" D"
#      (roundtrip lines whose padding collapsed to a single space)
#   3. text before " BUILT" (old full-run BUILT_FAILED lines with
#      single-space padding)
# Old full-run summaries also carry a UTF-8 arrow (U+2192) that later
# got double-mojibake'd into the literal sequence U+00E2 U+2020 U+2019
# ("â†'") by an ANSI round-trip. Re-runs use "->" — normalize BOTH
# spellings to "->" so the re-run line replaces its old counterpart.
function Get-Key([string]$line) {
    $key = $line.TrimEnd()
    # 2+ spaces NOT at end-of-line (trailing padding must not count)
    $m2 = [regex]::Match($line, '^(\S.*?)\s{2,}\S')
    if ($m2.Success) {
        $key = $m2.Groups[1].Value.Trim()
    } else {
        $tab = $line.IndexOf("`t")
        if ($tab -ge 0) {
            $key = $line.Substring(0, $tab).TrimEnd()
            if ($key -match ' [FD]$') { $key = $key.Substring(0, $key.Length - 2) }
        } else {
            $b = $line.IndexOf(" BUILT")
            if ($b -ge 0) { $key = $line.Substring(0, $b).TrimEnd() }
            else { $key = $line.Trim() }
        }
    }
    $key = $key -replace [string][char]0x2192, '->'
    $key = $key -replace ([string][char]0xE2 + [string][char]0x2020 + [string][char]0x2019), '->'
    return $key
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
        if ($l -match '^\[(\w+)\s*\]\s+(.+)$') { $sMap[$Matches[2].Trim()] = $l }
    }
    if ($pStatus) {
        foreach ($l in $pStatus) {
            if ($l -match '^\[(\w+)\s*\]\s+(.+)$') { $sMap[$Matches[2].Trim()] = $l }
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
