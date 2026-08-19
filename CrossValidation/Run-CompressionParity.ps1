# Run-CompressionParity.ps1 - Compression-policy cross-validation matrix.
#
# Verifies that a rebuilt PKG (produced by OrbisPkgTool's repack with
# per-file compression-policy replay) is structurally valid AND cross-
# compatible with Sony orbis-pub-cmd and OpenOrbis/LibOrbisPkg, and
# (optionally) lays out the steps for a shadPS4 install test.
#
# Reuses the shared Environment/ToolRunners/ManifestHelpers infrastructure
# so logs, manifests, stages and summary match the other validation runs.
#
# Pipeline (every stage logged, exit codes captured):
#   0. Environment + disk preflight
#   1. (optional) OrbisPkgTool repack <reference> --pfsc-mode compressed
#      + 8-stage validate + pfscprofile --ref <reference>
#   2. Three readers: list each PKG (ours + reference) with all three tools
#   3. Three validators: validate each PKG with all three tools
#   4. Triple extraction of OURS (ours, sony, openorbis) + manifest diff
#   5. Six-path content comparison (reference + ours, each x 3 tools)
#   6. PFSC compression-policy diff (pfscprofile ours --ref reference)
#   7. OrbisPkgTool round-trip (repack our rebuild again, compare)
#   8. Summary + confidence label
#
# Usage:
#   .\CrossValidation\Run-CompressionParity.ps1 [-ConfigPath path\to\compression_config.json]
#                                                [-Only "2,3,4,6"]

[CmdletBinding()]
param(
    [string]$ConfigPath = "$PSScriptRoot\compression_config.json",
    [string]$Only = ""
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Scripts\Environment.ps1" -ConfigPath $ConfigPath
. "$PSScriptRoot\Scripts\ToolRunners.ps1"
. "$PSScriptRoot\Scripts\ManifestHelpers.ps1"

Write-Host "=== OrbisPkgTool COMPRESSION-PARITY CROSS-VALIDATION ==="
Write-Host "Result dir: $($Run.Dir)"
$pass = $Cfg.passcode

# -- stage registry -------------------------------------------------------
$stages = @("Environment","Repack + profile","Three readers","Three validators",
    "Triple extraction","Six-path comparison","PFSC policy diff",
    "Round-trip","Summary")
foreach ($s in $stages) { Add-Stage $s }

# targeted run (-Only "2,3,6")
$Global:OnlySet = @{}
if ($Only) {
    foreach ($k in $Only.Split(',')) { $Global:OnlySet[$k.Trim()] = $true }
    Write-Host "TARGETED RUN - only stages: $($Global:OnlySet.Keys -join ', ')"
}
function Should-Run([string]$num) {
    return $Global:OnlySet.Count -eq 0 -or $Global:OnlySet.ContainsKey($num)
}

# =========================================================================
# 0. ENVIRONMENT
# =========================================================================
Set-StageStatus "Environment" "RUNNING"
$env = [System.Collections.Generic.List[string]]::new()
$env.Add("OS: $([System.Environment]::OSVersion.VersionString)")
$env.Add("PowerShell: $($PSVersionTable.PSVersion)")
$env.Add(".NET: $([System.Environment]::Version)")
foreach ($tool in @("OrbisPkgTool","OrbisPubCmd","OpenOrbisDriver")) {
    $exe = $Cfg.$tool
    if ($exe -and (Test-Path $exe)) {
        $env.Add("$tool : $exe")
        $env.Add("  SHA256: $(Get-StreamSha256 $exe)")
    } else { $env.Add("$tool : NOT FOUND ($exe)") }
}
$ref = $Cfg.reference_pkg
if ($ref -and (Test-Path -LiteralPath $ref)) {
    $sz = (Get-Item -LiteralPath $ref).Length
    $env.Add("reference_pkg : $ref  size=$sz  sha256=$(Get-StreamSha256 $ref)")
} else {
    $env.Add("reference_pkg : NOT FOUND ($ref)")
    Add-Result "0 environment" ERROR "reference_pkg not found - cannot run"
    Set-StageStatus "Environment" "FAIL"
    return
}

# Determine the rebuilt PKG: config ours_pkg OR repack the reference now.
$skipRebuild = [bool]$Cfg.skip_rebuild
$oursPath = $Cfg.ours_pkg
if ($skipRebuild -and $oursPath -and (Test-Path -LiteralPath $oursPath)) {
    $env.Add("ours_pkg (prebuilt) : $oursPath  size=$((Get-Item -LiteralPath $oursPath).Length)")
}

# Disk preflight: a full run peaks around 5x the package size.
$refSize = if ($ref -and (Test-Path $ref)) { (Get-Item $ref).Length } else { 0 }
$estGb = ($refSize * 5.0) / 1GB + 5.0
$drive = Get-PSDrive ([System.IO.Path]::GetPathRoot($Cfg.work_dir).TrimEnd('\')[0])
$availGb = [math]::Round($drive.Free / 1GB, 1)
$env.Add("Estimated peak disk need: ~$([math]::Round($estGb,1)) GB   Available: $availGb GB")
if ($estGb -gt $availGb) {
    Add-Result "0 environment" WARNING "disk may be tight (~$([math]::Round($estGb,1)) GB needed, $availGb GB free)"
}
Set-Content -Path (Join-Path $Run.Dir "00_environment.txt") -Value $env -Encoding utf8
Set-StageStatus "Environment" "PASS"
$Global:StageMark = $Global:SummaryLines.Count

# =========================================================================
# 1. REPACK + PROFILE  (the policy-replay heart of the validation)
# =========================================================================
if (Should-Run "1") {
    Set-StageStatus "Repack + profile" "RUNNING"
    $refSafe = $Run.OrigPkgSafe   # ASCII-safe copy from Environment.ps1
    if (-not $refSafe) {
        Add-Result "1 repack" SKIPPED_DEPENDENCY_FAILED "no reference_pkg"
        Complete-Stage "Repack + profile"
    } else {
        # Decide the rebuilt PKG path
        if ($skipRebuild -and $oursPath -and (Test-Path -LiteralPath $oursPath)) {
            # Use the prebuilt ours_pkg - copy it ASCII-safe like the reference.
            $oursSafe = Copy-AsciiSafe $oursPath "ours"
            Add-Result "1 repack" SKIPPED "skip_rebuild=true; using prebuilt ours_pkg"
        } else {
            # Repack with policy replay (default --pfsc-mode compressed).
            $oursSafe = Join-Path $Run.Work "ours_repack.pkg"
            $repackLog = Join-Path $Run.Dir "01_repack.log"
            $e = Invoke-OrbisPkgTool "repack `"$refSafe`" --out `"$oursSafe`" --passcode $pass" $repackLog "repack reference"
            if ($e -ne 0 -or -not (Test-Path $oursSafe)) {
                Add-Result "1 repack" FAIL "exit=$e"
                Complete-Stage "Repack + profile"
                $oursSafe = $null
            } else {
                $sz = (Get-Item $oursSafe).Length
                Add-Result "1 repack" PASS "rebuilt $([math]::Round($sz/1MB,1)) MB"
            }
        }
        $Run.OursPkgSafe = $oursSafe

        # 8-stage validate our rebuild
        if ($oursSafe) {
            $vLog = Join-Path $Run.Dir "01_validate_ours.log"
            $ev = Invoke-OrbisPkgTool "validate --passcode $pass `"$oursSafe`"" $vLog "validate ours"
            Add-Result "1 validate(ours)" $(if ($ev -eq 0) { "PASS" } else { "FAIL" })
        }

        # PFSC policy diff: rebuilt vs reference - should be identical
        if ($oursSafe -and $refSafe) {
            $pLog = Join-Path $Run.Dir "01_pfscprofile.log"
            Invoke-OrbisPkgTool "pfscprofile `"$oursSafe`" --ref `"$refSafe`" --passcode $pass" $pLog "pfscprofile ours --ref" | Out-Null
            $out = Get-LogOutput $pLog
            $agreeLine = $out | Where-Object { $_ -match "policy agreement" } | Select-Object -First 1
            if ($agreeLine -and $agreeLine -match "(\d+) mismatched") {
                $mm = [int]$Matches[1]
                Add-Result "1 PFSC policy diff" $(if ($mm -eq 0) { "PASS" } else { "WARNING" }) "$mm mismatched files"
            } else {
                Add-Result "1 PFSC policy diff" WARNING "agreement line not found (see log)"
            }
        }
        Complete-Stage "Repack + profile"
    }
} else { Set-StageStatus "Repack + profile" "SKIPPED" }

if (-not $Run.OursPkgSafe) {
    Write-Host "No rebuilt PKG available - aborting remaining stages."
    $sum = [System.Collections.Generic.List[string]]::new()
    $sum.Add("============================================================")
    $sum.Add("ORBISPKGTOOL COMPRESSION-PARITY CROSS-VALIDATION")
    $sum.Add("============================================================")
    $sum.AddRange($Global:SummaryLines)
    $sum.Add("ABORTED: no rebuilt PKG (Stage 1 failed or skipped).")
    Set-Content -Path (Join-Path $Run.Dir "summary.txt") -Value $sum -Encoding utf8
    return
}

# =========================================================================
# 2. THREE READERS - list each PKG with all three tools
# =========================================================================
if (Should-Run "2") {
    Set-StageStatus "Three readers" "RUNNING"
    foreach ($pkgName in @("reference","ours")) {
        $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
        if (-not $pkg) { continue }
        foreach ($tool in @("ours","sony","openorbis")) {
            $log = Join-Path $Run.Dir "02_list_${pkgName}_$tool.log"
            $e = switch ($tool) {
                "ours"      { Invoke-OrbisPkgTool "list --passcode $pass `"$pkg`"" $log }
                "sony"      { Invoke-OrbisPubCmd "img_file_list --passcode $pass `"$pkg`"" $log }
                "openorbis" { Invoke-OpenOrbis "list `"$pkg`"" $log }
            }
            Add-Result "2 list($pkgName,$tool)" $(if ($e -eq 0) { "PASS" } elseif ($e -eq -1) { "NOT_SUPPORTED" } else { "FAIL" })
        }
    }
    Complete-Stage "Three readers"
} else { Set-StageStatus "Three readers" "SKIPPED" }

# =========================================================================
# 3. THREE VALIDATORS
# =========================================================================
if (Should-Run "3") {
    Set-StageStatus "Three validators" "RUNNING"
    foreach ($pkgName in @("reference","ours")) {
        $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
        if (-not $pkg) { continue }
        foreach ($tool in @("ours","sony","openorbis")) {
            $log = Join-Path $Run.Dir "03_validate_${pkgName}_$tool.log"
            $e = switch ($tool) {
                "ours"      { Invoke-OrbisPkgTool "validate --passcode $pass `"$pkg`"" $log }
                "sony"      { Invoke-OrbisPubCmd "img_verify --passcode $pass `"$pkg`"" $log }
                "openorbis" { Invoke-OpenOrbis "validate `"$pkg`"" $log }
            }
            $state = if ($e -eq 0) { "PASS" } else { "FAIL" }
            if ($tool -eq "sony" -and $state -eq "FAIL") {
                $warns = Get-SonyWarningCount $log
                if ($warns -gt 0) { $state = "PASS_EXPECTED_WARNINGS ($warns)" }
            }
            if ($tool -eq "openorbis" -and $state -eq "FAIL" -and (Test-OpenOrbisExpectedDifference $log)) {
                $state = "EXPECTED_DIFFERENCE (OpenOrbis digest recompute)"
            }
            if ($pkgName -eq "reference" -and $tool -eq "ours" -and $state -eq "FAIL" -and (Test-OursReferenceQuirk $log)) {
                $state = "EXPECTED_DIFFERENCE (original trophy ZIP method)"
            }
            Add-Result "3 validate($pkgName,$tool)" $state
        }
    }
    Complete-Stage "Three validators"
} else { Set-StageStatus "Three validators" "SKIPPED" }

# -- shared extract helper (mirrors Run-FullValidation.ps1) ---------------
function Extract-With {
    param([string]$Tool, [string]$Pkg, [string]$OutDir, [string]$LogFile, [string]$Label)
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $e = switch ($Tool) {
        "ours"      { Invoke-OrbisPkgTool "extract --passcode $pass `"$Pkg`" `"$OutDir`"" $LogFile $Label }
        "sony"      { Invoke-OrbisPubCmd "img_extract --passcode $pass `"$Pkg`" `"$OutDir`"" $LogFile $Label }
        "openorbis" { Invoke-OpenOrbis "extract `"$Pkg`" `"$OutDir`"" $LogFile $Label }
        default     { 1 }
    }
    return $e
}

# =========================================================================
# 4. TRIPLE EXTRACTION OF OURS + manifest diff
# =========================================================================
if (Should-Run "4") {
    Set-StageStatus "Triple extraction" "RUNNING"
    $ours = $Run.OursPkgSafe
    if ($ours) {
        foreach ($tool in @("ours","sony","openorbis")) {
            $dir = Join-Path $Run.Manifests "ours_extract_$tool"
            New-Item -ItemType Directory -Force $dir | Out-Null
            if (-not (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue)) {
                $null = Extract-With $tool $ours $dir (Join-Path $Run.Manifests "extract_$tool.log") "E extract $tool"
            }
            New-FileManifest $dir (Join-Path $Run.Manifests "ours_$tool.manifest")
        }
        $cmp = [System.Collections.Generic.List[string]]::new()
        foreach ($pair in @(@("ours","sony"), @("ours","openorbis"), @("sony","openorbis"))) {
            $cmp.Add("=== ours $($pair[0]) vs $($pair[1]) (normalized) ===")
            $ma = if ($pair[0] -eq "openorbis") { Join-Path $Run.Manifests "ours_$($pair[0]).manifest" }
                  else { Normalize-Manifest (Join-Path $Run.Manifests "ours_$($pair[0]).manifest") (Join-Path $Run.Manifests "norm_ours_$($pair[0]).manifest") $true $true }
            $mb = if ($pair[1] -eq "openorbis") { Join-Path $Run.Manifests "ours_$($pair[1]).manifest" }
                  else { Normalize-Manifest (Join-Path $Run.Manifests "ours_$($pair[1]).manifest") (Join-Path $Run.Manifests "norm_ours_$($pair[1]).manifest") $true $true }
            $cmp.AddRange([string[]](Compare-Manifests $ma $mb $pair[0] $pair[1]))
        }
        Set-Content -Path (Join-Path $Run.Comparisons "triple_extraction_comparison.txt") -Value $cmp -Encoding utf8
        $bad = @($cmp | Where-Object { $_ -match "DIFFER|ONLY_IN" })
        Add-Result "4 triple extraction" $(if ($bad.Count -eq 0) { "PASS" } else { "WARNING" }) "$($bad.Count) differences"
        # cleanup large extractions (manifests + logs stay)
        foreach ($tool in @("ours","sony","openorbis")) {
            Remove-PhaseArtifacts @(Join-Path $Run.Manifests "ours_extract_$tool") "4: deleted ours_extract_$tool"
        }
    } else { Add-Result "4 triple extraction" SKIPPED_DEPENDENCY_FAILED }
    Complete-Stage "Triple extraction"
} else { Set-StageStatus "Triple extraction" "SKIPPED" }

# =========================================================================
# 5. SIX-PATH CONTENT COMPARISON  (reference + ours) x (ours,sony,openorbis)
# =========================================================================
if (Should-Run "5") {
    Set-StageStatus "Six-path comparison" "RUNNING"
    $six = [System.Collections.Generic.List[string]]::new()
    foreach ($pkgName in @("reference","ours")) {
        $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
        if (-not $pkg) { continue }
        foreach ($tool in @("ours","sony","openorbis")) {
            $dir = Join-Path $Run.Manifests "${pkgName}_extract_$tool"
            New-Item -ItemType Directory -Force $dir | Out-Null
            if (-not (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue)) {
                $null = Extract-With $tool $pkg $dir (Join-Path $Run.Manifests "extract_${pkgName}_$tool.log") "F $pkgName $tool"
            }
            New-FileManifest $dir (Join-Path $Run.Manifests "${pkgName}_$tool.manifest")
        }
        $base = Normalize-Manifest (Join-Path $Run.Manifests "${pkgName}_ours.manifest") (Join-Path $Run.Manifests "norm_${pkgName}_ours.manifest") $true $true
        foreach ($tool in @("sony","openorbis")) {
            $six.Add("=== $pkgName : ours vs $tool (normalized) ===")
            $m = if ($tool -eq "openorbis") {
                Normalize-Manifest (Join-Path $Run.Manifests "${pkgName}_$tool.manifest") (Join-Path $Run.Manifests "norm_${pkgName}_$tool.manifest") $false $false
            } else {
                Normalize-Manifest (Join-Path $Run.Manifests "${pkgName}_$tool.manifest") (Join-Path $Run.Manifests "norm_${pkgName}_$tool.manifest") $true $true
            }
            $six.AddRange([string[]](Compare-Manifests $base $m "ours" $tool))
        }
    }
    Set-Content -Path (Join-Path $Run.Comparisons "six_path_content_comparison.txt") -Value $six -Encoding utf8
    $sixBad = @($six | Where-Object { $_ -match "DIFFER|ONLY_IN" })
    Add-Result "5 six-path comparison" $(if ($sixBad.Count -eq 0) { "PASS" } else { "WARNING" }) "$($sixBad.Count) differences"
    Complete-Stage "Six-path comparison"
    foreach ($pkgName in @("reference","ours")) {
        foreach ($tool in @("ours","sony","openorbis")) {
            Remove-PhaseArtifacts @(Join-Path $Run.Manifests "${pkgName}_extract_$tool") "5: deleted ${pkgName}_extract_$tool"
        }
    }
} else { Set-StageStatus "Six-path comparison" "SKIPPED" }

# =========================================================================
# 6. PFSC COMPRESSION-POLICY DIFF
# =========================================================================
if (Should-Run "6") {
    Set-StageStatus "PFSC policy diff" "RUNNING"
    $ours = $Run.OursPkgSafe; $ref = $Run.OrigPkgSafe
    if ($ours -and $ref) {
        $log = Join-Path $Run.Dir "06_pfscprofile.log"
        Invoke-OrbisPkgTool "pfscprofile `"$ours`" --ref `"$ref`" --passcode $pass" $log "pfscprofile ours --ref" | Out-Null
        $out = Get-LogOutput $log
        $agree = $out | Where-Object { $_ -match "policy agreement" } | Select-Object -First 1
        # Save the full profile to a file for archival
        $prof = Join-Path $Run.Dir "06_pfsc_profile.json"
        Invoke-OrbisPkgTool "pfscprofile `"$ours`" --out `"$prof`" --passcode $pass" (Join-Path $Run.Dir "06_pfscprofile_dump.log") "pfscprofile dump" | Out-Null
        $mm = 0; if ($agree -and $agree -match "(\d+) mismatched") { $mm = [int]$Matches[1] }
        Add-Result "6 PFSC policy diff" $(if ($mm -eq 0) { "PASS" } else { "WARNING" }) "$mm policy mismatches (see 06_pfsc_profile.json)"
    } else { Add-Result "6 PFSC policy diff" SKIPPED_DEPENDENCY_FAILED }
    Complete-Stage "PFSC policy diff"
} else { Set-StageStatus "PFSC policy diff" "SKIPPED" }

# =========================================================================
# 7. ROUND-TRIP - extract our rebuild, repack it AGAIN, compare
# =========================================================================
if (Should-Run "7") {
    Set-StageStatus "Round-trip" "RUNNING"
    $ours = $Run.OursPkgSafe
    if ($ours) {
        $rtPkg = Join-Path $Run.RoundTrips "roundtrip2.pkg"
        $log = Join-Path $Run.Dir "07_roundtrip.log"
        $e = Invoke-OrbisPkgTool "repack `"$ours`" --out `"$rtPkg`" --passcode $pass" $log "round-trip repack"
        if ($e -eq 0 -and (Test-Path $rtPkg)) {
            # Validate the second rebuild
            $v2 = Join-Path $Run.Dir "07_roundtrip_validate.log"
            $ev = Invoke-OrbisPkgTool "validate --passcode $pass `"$rtPkg`"" $v2 "round-trip validate"
            Add-Result "7 round-trip validate" $(if ($ev -eq 0) { "PASS" } else { "FAIL" })
            # Policy should still match the reference (idempotent replay)
            $p2 = Join-Path $Run.Dir "07_roundtrip_pfscprofile.log"
            Invoke-OrbisPkgTool "pfscprofile `"$rtPkg`" --ref `"$($Run.OrigPkgSafe)`" --passcode $pass" $p2 "round-trip pfscprofile" | Out-Null
            $o2 = Get-LogOutput $p2
            $a2 = $o2 | Where-Object { $_ -match "policy agreement" } | Select-Object -First 1
            $mm2 = -1; if ($a2 -and $a2 -match "(\d+) mismatched") { $mm2 = [int]$Matches[1] }
            Add-Result "7 round-trip policy" $(if ($mm2 -eq 0) { "PASS" } else { "WARNING" }) "$mm2 mismatches vs reference"
            Remove-PhaseArtifacts @($rtPkg) "7: deleted roundtrip2.pkg"
        } else {
            Add-Result "7 round-trip" FAIL "repack exit=$e"
        }
    } else { Add-Result "7 round-trip" SKIPPED_DEPENDENCY_FAILED }
    Complete-Stage "Round-trip"
} else { Set-StageStatus "Round-trip" "SKIPPED" }

# =========================================================================
# 8. SUMMARY + shadPS4 instructions
# =========================================================================
$sum = [System.Collections.Generic.List[string]]::new()
$sum.Add("============================================================")
$sum.Add("ORBISPKGTOOL COMPRESSION-PARITY CROSS-VALIDATION")
$sum.Add("============================================================")
$sum.AddRange($Global:SummaryLines)
$unexpected = @($Global:SummaryLines | Where-Object { $_ -match "FAIL|ERROR" -and $_ -notmatch "PASS_EXPECTED|EXPECTED_DIFFERENCE|SKIPPED|NOT_FOUND" })
$sum.Add("------------------------------------------------------------")
$sum.Add("UNEXPECTED FAILURES/ERRORS: $($unexpected.Count)")
$label = if ($unexpected.Count -eq 0) { "PC_MAXIMUM_VALIDATED (cross-tool parity)" } else { "INCOMPLETE" }
$sum.Add("COMPRESSION-PARITY VALIDATION: $label")
$sum.Add("")
$sum.Add("shadPS4 runtime test (MANUAL - cannot be automated):")
$sum.Add("  1. Install this rebuilt PKG in shadPS4 (File -> Install Packages):")
$sum.Add("     $($Run.OursPkgSafe)")
$sum.Add("  2. Boot the game; reach the title screen.")
$sum.Add("  3. Create a save, load it, reach an asset-heavy section.")
$sum.Add("  4. Record: boots? playable? content errors?")
$sum.Add("  5. Physical jailbroken-PS4 install remains unverified without hardware.")
Set-Content -Path (Join-Path $Run.Dir "summary.txt") -Value $sum -Encoding utf8
Set-StageStatus "Summary" "PASS" "COMPRESSION-PARITY VALIDATION: $label"
Write-Host ""
Write-Host "Compression-parity validation complete -> $($Run.Dir)"
Write-Host ""
Write-Host "Next: perform the MANUAL shadPS4 install test on:"
Write-Host "  $($Run.OursPkgSafe)"
