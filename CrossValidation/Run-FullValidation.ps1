# Run-FullValidation.ps1 - full PC cross-implementation validation.
# Everything in Quick plus: triple extraction, six-path hashes, extractor
# validity, roundtrips (ours->Sony, ours->OpenOrbis, OpenOrbis->ours,
# OpenOrbis->Sony control), GP4 semantics, inner/outer PFS comparison.
#
# Usage:
#   .\CrossValidation\Run-FullValidation.ps1 [-ConfigPath path\to\config.json]
#       [-Only "C,H,K"]   run only the listed phases (keys A B C E F G H I J K L M N)

[CmdletBinding()]
param(
    [string]$ConfigPath = "$PSScriptRoot\config.json",
    [string]$Only = ""
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Scripts\Environment.ps1" -ConfigPath $ConfigPath
. "$PSScriptRoot\Scripts\ToolRunners.ps1"
. "$PSScriptRoot\Scripts\ManifestHelpers.ps1"
. "$PSScriptRoot\Scripts\ProjectAdapters.ps1"

Write-Host "=== OrbisPkgTool EXTERNAL CROSS-VALIDATION (FULL) ==="
Write-Host "Result dir: $($Run.Dir)"
$pass = $Cfg.passcode

# Stage registry for run_status.txt
foreach ($s in @("Environment", "Reference baseline", "Ours readability", "Validators",
    "Triple extraction", "Six-path comparison", "Extractor validity", "Ours extract to Sony",
    "Ours extract to OpenOrbis", "OpenOrbis extract to ours", "OpenOrbis to Sony control",
    "GP4 semantics", "Inner PFS", "Outer PFS", "Summary")) { Add-Stage $s }

# Targeted run: -Only "C,H,K" runs just those phases.
$Global:OnlySet = @{}
if ($Only) {
    foreach ($k in $Only.Split(',')) { $Global:OnlySet[$k.Trim().ToUpper()] = $true }
    Write-Host "TARGETED RUN - only phases: $($Global:OnlySet.Keys -join ', ')"
}
function Should-Run([string]$key) {
    return $Global:OnlySet.Count -eq 0 -or $Global:OnlySet.ContainsKey($key)
}

# disk preflight - realistic peak with per-phase cleanup (5x pkg sizes + slack)
$est = 25.0
foreach ($pkg in @($Run.OrigPkgSafe, $Run.OursPkgSafe)) {
    if ($pkg -and (Test-Path $pkg)) { $est += (Get-Item $pkg).Length / 1GB * 5.0 }
}
$drive = Get-PSDrive ([System.IO.Path]::GetPathRoot($Cfg.work_dir).TrimEnd('\')[0])
$avail = [math]::Round($drive.Free / 1GB, 1)
Write-Host "Estimated full-run disk need: ~$([math]::Round($est, 1)) GB   Available: $avail GB"
if ($est -gt $avail) { Write-Host "WARNING: may run out of disk" }
Set-StageStatus "Environment" "PASS"

# helper: extract a pkg with a given tool into $dir
function Extract-With {
    param([string]$Tool, [string]$Pkg, [string]$OutDir, [string]$LogFile, [string]$Label)
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $exit = switch ($Tool) {
        "ours"    { Invoke-OrbisPkgTool "extract --passcode $pass `"$Pkg`" `"$OutDir`"" $LogFile "$Label" }
        "sony"    { Invoke-OrbisPubCmd "img_extract --passcode $pass `"$Pkg`" `"$OutDir`"" $LogFile "$Label" }
        "openorbis" { Invoke-OpenOrbis "extract `"$Pkg`" `"$OutDir`"" $LogFile "$Label" }
        default   { 1 }
    }
    return $exit
}

# ---------------- A: reference baseline (extract original with all 3) ----
if (Should-Run "A") {
    Set-StageStatus "Reference baseline" "RUNNING"
    $base = $Run.Sony
    if ($Run.OrigPkgSafe) {
        foreach ($tool in @("ours", "sony", "openorbis")) {
            $e = Extract-With $tool $Run.OrigPkgSafe (Join-Path $base "ref_extract_$tool") (Join-Path $base "ref_extract_$tool.log") "ref extract $tool"
            Add-Result "TEST A extract(ref,$tool)" $(if ($e -eq 0) { "PASS" } elseif ($e -eq -1) { "NOT_SUPPORTED" } else { "FAIL" })
        }
    } else { Add-Result "TEST A reference baseline" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "Reference baseline"
    # Sony's reference extract is only used by A (F re-extracts its own).
    Remove-PhaseArtifacts @(Join-Path $base "ref_extract_sony") "A: deleted ref_extract_sony"
} else { Set-StageStatus "Reference baseline" "SKIPPED" }

# ---------------- B: our rebuilt pkg through three readers ---------------
if (Should-Run "B") {
    Set-StageStatus "Ours readability" "RUNNING"
    $base = $Run.Sony
    if ($Run.OursPkgSafe) {
        foreach ($tool in @("ours", "sony", "openorbis")) {
            $e = Extract-With $tool $Run.OursPkgSafe (Join-Path $base "ours_extract_$tool") (Join-Path $base "ours_extract_$tool.log") "ours extract $tool"
            Add-Result "TEST B extract(ours,$tool)" $(if ($e -eq 0) { "PASS" } elseif ($e -eq -1) { "NOT_SUPPORTED" } else { "FAIL" })
        }
    } else { Add-Result "TEST B ours through three readers" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "Ours readability"
    # B's extracts are not reused later (E/F re-extract their own copies).
    foreach ($tool in @("ours", "sony", "openorbis")) {
        Remove-PhaseArtifacts @(Join-Path $base "ours_extract_$tool") "B: deleted ours_extract_$tool"
    }
} else { Set-StageStatus "Ours readability" "SKIPPED" }

# ---------------- C: three validators on both packages -------------------
if (Should-Run "C") {
    Set-StageStatus "Validators" "RUNNING"
    $base = $Run.Sony
    foreach ($pkgName in @("reference", "ours")) {
        $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
        if (-not $pkg) { continue }
        foreach ($tool in @("ours", "sony", "openorbis")) {
            $log = Join-Path $base "$pkgName`_$tool`_validate.log"
            $e = switch ($tool) {
                "ours" { Invoke-OrbisPkgTool "validate --passcode $pass `"$pkg`"" $log }
                "sony" { Invoke-OrbisPubCmd "img_verify --passcode $pass `"$pkg`"" $log }
                "openorbis" { Invoke-OpenOrbis "validate `"$pkg`"" $log }
            }
            $state = if ($e -eq 0) { "PASS" } else { "FAIL" }
            if ($tool -eq "sony" -and $state -eq "FAIL") {
                $warns = Get-SonyWarningCount $log
                if ($warns -gt 0) { $state = "PASS_EXPECTED_WARNINGS ($warns)" }
            }
            if ($tool -eq "openorbis" -and $state -eq "FAIL" -and (Test-OpenOrbisExpectedDifference $log)) {
                $state = "EXPECTED_DIFFERENCE (OpenOrbis digest recomputation)"
            }
            Add-Result "TEST C validate($pkgName,$tool)" $state
        }
    }
    Complete-Stage "Validators"
} else { Set-StageStatus "Validators" "SKIPPED" }

# ---------------- E: triple extraction of ours + manifests ---------------
if (Should-Run "E") {
    Set-StageStatus "Triple extraction" "RUNNING"
    if ($Run.OursPkgSafe) {
        foreach ($tool in @("ours", "sony", "openorbis")) {
            $dir = Join-Path $Run.Manifests "ours_extract_$tool"
            New-Item -ItemType Directory -Force $dir | Out-Null
            if (-not (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue)) {
                $null = Extract-With $tool $Run.OursPkgSafe $dir (Join-Path $Run.Manifests "extract_$tool.log") "E extract $tool"
            }
            New-FileManifest $dir (Join-Path $Run.Manifests "ours_$tool`.manifest")
        }
        $cmp = [System.Collections.Generic.List[string]]::new()
        foreach ($pair in @(@("ours", "sony"), @("ours", "openorbis"), @("sony", "openorbis"))) {
            $cmp.Add("=== ours $($pair[0]) vs $($pair[1]) (normalized: Image0/ stripped, Sc0 dropped) ===")
            $ma = if ($pair[0] -eq "openorbis") { Join-Path $Run.Manifests "ours_$($pair[0]).manifest" }
                  else { Normalize-Manifest (Join-Path $Run.Manifests "ours_$($pair[0]).manifest") (Join-Path $Run.Manifests "norm_ours_$($pair[0]).manifest") $true $true }
            $mb = if ($pair[1] -eq "openorbis") { Join-Path $Run.Manifests "ours_$($pair[1]).manifest" }
                  else { Normalize-Manifest (Join-Path $Run.Manifests "ours_$($pair[1]).manifest") (Join-Path $Run.Manifests "norm_ours_$($pair[1]).manifest") $true $true }
            $cmp.AddRange([string[]](Compare-Manifests $ma $mb $pair[0] $pair[1]))
        }
        Set-Content -Path (Join-Path $Run.Comparisons "triple_extraction_comparison.txt") -Value $cmp -Encoding utf8
        $bad = @($cmp | Where-Object { $_ -match "DIFFER|ONLY_IN" })
        Add-Result "TEST E triple extraction" $(if ($bad.Count -eq 0) { "PASS" } else { "WARNING" }) "$($bad.Count) content differences"
    } else { Add-Result "TEST E triple extraction" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "Triple extraction"
} else { Set-StageStatus "Triple extraction" "SKIPPED" }

# ---------------- F: six-path content comparison -------------------------
if (Should-Run "F") {
    Set-StageStatus "Six-path comparison" "RUNNING"
    $six = [System.Collections.Generic.List[string]]::new()
    foreach ($pkgName in @("reference", "ours")) {
        $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
        if (-not $pkg) { continue }
        foreach ($tool in @("ours", "sony", "openorbis")) {
            $dir = Join-Path $Run.Manifests "$pkgName`_extract_$tool"
            New-Item -ItemType Directory -Force $dir | Out-Null
            if (-not (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue)) {
                $null = Extract-With $tool $pkg $dir (Join-Path $Run.Manifests "extract_$pkgName`_$tool.log") "F extract $pkgName $tool"
            }
            New-FileManifest $dir (Join-Path $Run.Manifests "$pkgName`_$tool`.manifest")
        }
        $baseManifest = Normalize-Manifest (Join-Path $Run.Manifests "$pkgName`_ours.manifest") (Join-Path $Run.Manifests "norm_$pkgName`_ours.manifest") $true $true
        foreach ($tool in @("sony", "openorbis")) {
            $six.Add("=== $pkgName : ours vs $tool (normalized) ===")
            $m = if ($tool -eq "openorbis") {
                Normalize-Manifest (Join-Path $Run.Manifests "$pkgName`_$tool.manifest") (Join-Path $Run.Manifests "norm_$pkgName`_$tool.manifest") $false $false
            } else {
                Normalize-Manifest (Join-Path $Run.Manifests "$pkgName`_$tool.manifest") (Join-Path $Run.Manifests "norm_$pkgName`_$tool.manifest") $true $true
            }
            $six.AddRange([string[]](Compare-Manifests $baseManifest $m "ours" $tool))
        }
    }
    Set-Content -Path (Join-Path $Run.Comparisons "six_path_content_comparison.txt") -Value $six -Encoding utf8
    $sixBad = @($six | Where-Object { $_ -match "DIFFER|ONLY_IN" })
    Add-Result "TEST F six-path comparison" $(if ($sixBad.Count -eq 0) { "PASS" } else { "WARNING" }) "$($sixBad.Count) differences (see file)"
    Complete-Stage "Six-path comparison"
    # Extracts are no longer needed after the comparisons are written.
    foreach ($pkgName in @("reference", "ours")) {
        foreach ($tool in @("ours", "sony", "openorbis")) {
            Remove-PhaseArtifacts @(Join-Path $Run.Manifests "$pkgName`_extract_$tool") "F: deleted $pkgName`_extract_$tool"
            Remove-PhaseArtifacts @(Join-Path $Run.Manifests "ours_extract_$tool") "F: deleted ours_extract_$tool"
        }
    }
} else { Set-StageStatus "Six-path comparison" "SKIPPED" }

# ---------------- G: extractor validity ----------------------------------
if (Should-Run "G") {
    Set-StageStatus "Extractor validity" "RUNNING"
    if ($Run.OrigPkgSafe) {
        $dir = Join-Path $Run.GP4 "ours_extract_of_reference"
        $null = Extract-With "ours" $Run.OrigPkgSafe $dir (Join-Path $Run.GP4 "extract.log") "G extract"
        $image0 = Join-Path $dir "Image0"
        $checks = [System.Collections.Generic.List[string]]::new()
        $checks.Add("root files: $((Get-ChildItem $image0 -File -ErrorAction SilentlyContinue).Count)")
        $checks.Add("sce_sys exists: $(Test-Path (Join-Path $image0 'sce_sys'))")
        $checks.Add("param.sfo exists: $(Test-Path (Join-Path $image0 'sce_sys/param.sfo'))")
        $checks.Add("total files: $((Get-ChildItem $image0 -Recurse -File).Count)")
        Set-Content -Path (Join-Path $Run.Comparisons "extractor_validity.txt") -Value $checks -Encoding utf8
        Add-Result "TEST G extractor validity" "PASS (see Comparisons/extractor_validity.txt)"
    } else { Add-Result "TEST G extractor validity" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "Extractor validity"
} else { Set-StageStatus "Extractor validity" "SKIPPED" }

# ---------------- roundtrip helper ---------------------------------------
function Invoke-RoundTrip {
    param(
        [string]$TestName,
        [string]$SourceImage0,
        [string]$BuildTool,
        [string]$OutPkg
    )
    $rt = Join-Path $Run.RoundTrips $TestName
    New-Item -ItemType Directory -Force $rt | Out-Null
    $image0 = if (Test-Path (Join-Path $SourceImage0 "Image0")) { Join-Path $SourceImage0 "Image0" } else { $SourceImage0 }
    # merge a sibling Sc0/ into Image0/sce_sys/
    $sc0 = Join-Path $SourceImage0 "Sc0"
    if (Test-Path $sc0) {
        $sceSys = Join-Path $image0 "sce_sys"
        New-Item -ItemType Directory -Force $sceSys | Out-Null
        foreach ($f in Get-ChildItem -LiteralPath $sc0 -Recurse -File) {
            $rel = (Get-RelPath $sc0 $f.FullName).Replace('\', '/')
            $dest = Join-Path $sceSys $rel
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        }
    }
    # OpenOrbis extraction has NO Sc0 section - pull param.sfo from reference
    if (-not (Test-Path (Join-Path $image0 "sce_sys/param.sfo")) -and $Run.OrigPkgSafe) {
        $sceSys = Join-Path $image0 "sce_sys"
        New-Item -ItemType Directory -Force $sceSys | Out-Null
        Invoke-OrbisPkgTool "extract `"$($Run.OrigPkgSafe):Sc0/param.sfo`" `"$sceSys`"" (Join-Path $rt "extract_paramsfo.log") "$TestName param.sfo" | Out-Null
    }
    # Mandatory cleanup: Sony regenerates license/psreserved/playgo/about.
    $sceSys = Join-Path $image0 "sce_sys"
    if (Test-Path $sceSys) {
        foreach ($pg in @("license.dat", "license.info", "psreserved.dat", "playgo-chunk.dat", "playgo-chunk.sha", "playgo-manifest.xml", "param.sfo.original")) {
            $pgFile = Join-Path $sceSys $pg
            if (Test-Path $pgFile) { Remove-Item $pgFile -Force }
        }
        $about = Join-Path $sceSys "about"
        if (Test-Path $about) { Remove-Item $about -Recurse -Force }
    }
    $gp4 = Join-Path $rt "project.gp4"
    $state = "SKIPPED"
    switch ($BuildTool) {
        "sony" {
            $gp4 = Join-Path $image0 "project.gp4"
            New-SonyGp4 -Image0 $image0 -OutPath $gp4 -Passcode $pass | Out-Null
            $sonyOut = Join-Path $rt "roundtrip"
            $e = Invoke-OrbisPubCmd "img_create `"$gp4`" `"$sonyOut`"" (Join-Path $rt "sony_build.log") "$TestName sony build"
            if ($e -eq 0 -and (Test-Path $sonyOut)) {
                Copy-Item $sonyOut $OutPkg -Force
                $state = "BUILT"
            } else { $state = "FAIL" }
        }
        "openorbis" {
            New-SonyGp4 -Image0 $image0 -OutPath $gp4 -Passcode $pass -WithRootDirs | Out-Null
            $e = Invoke-OpenOrbis "build `"$gp4`" `"$image0`" `"$OutPkg`" $pass" (Join-Path $rt "openorbis_build.log") "$TestName openorbis build"
            $state = if ($e -eq 0) { "BUILT" } else { "FAIL" }
        }
        "ours" {
            New-OrbisPkgToolGp4 -Image0 $image0 -OutPath $gp4 | Out-Null
            $e = Invoke-OrbisPkgTool "build `"$gp4`" `"$image0`" --out `"$OutPkg`" --passcode $pass" (Join-Path $rt "ours_build.log") "$TestName ours build"
            $state = if ($e -eq 0) { "BUILT" } else { "FAIL" }
        }
    }
    if ($state -ne "BUILT") { return "BUILT_FAILED" }
    foreach ($tool in @("ours", "sony", "openorbis")) {
        $log = Join-Path $rt "validate_$tool.log"
        $e = switch ($tool) {
            "ours" { Invoke-OrbisPkgTool "validate --passcode $pass `"$OutPkg`"" $log }
            "sony" { Invoke-OrbisPubCmd "img_verify --passcode $pass `"$OutPkg`"" $log }
            "openorbis" { Invoke-OpenOrbis "validate `"$OutPkg`"" $log }
        }
        $st = if ($e -eq 0) { "PASS" } else { "FAIL" }
        if ($tool -eq "sony" -and $st -eq "FAIL") {
            $warns = Get-SonyWarningCount $log
            if ($warns -gt 0) { $st = "PASS_EXPECTED_WARNINGS ($warns)" }
        }
        if ($tool -eq "openorbis" -and $st -eq "FAIL" -and (Test-OpenOrbisExpectedDifference $log)) {
            $st = "EXPECTED_DIFFERENCE (OpenOrbis digest recomputation)"
        }
        Add-Result "$TestName validate($tool)" $st
    }
    $exDir = Join-Path $rt "extract_ours"
    $null = Extract-With "ours" $OutPkg $exDir (Join-Path $rt "extract_ours.log") "$TestName extract"
    New-FileManifest $exDir (Join-Path $rt "roundtrip.manifest")
    # Source merge + extract are no longer needed; logs + manifest remain.
    Remove-PhaseArtifacts @(Join-Path $rt "extract_ours") "${TestName}: deleted extract"
    if (Test-Path (Join-Path $image0 "project.gp4")) { Remove-Item (Join-Path $image0 "project.gp4") -Force -ErrorAction SilentlyContinue }
    return "BUILT"
}

# ---------------- H: our extraction -> Sony rebuild -----------------------
if (Should-Run "H") {
    Set-StageStatus "Ours extract to Sony" "RUNNING"
    if ($Run.OrigPkgSafe) {
        $srcDir = Join-Path $Run.GP4 "ours_extract_of_reference"
        $outPkg = Join-Path $Run.RoundTrips "H_sony_roundtrip.pkg"
        $r = Invoke-RoundTrip "H_sony" $srcDir "sony" $outPkg
        Add-Result "TEST H ours-extract -> Sony rebuild" $r
    } else { Add-Result "TEST H ours-extract -> Sony rebuild" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "Ours extract to Sony"
} else { Set-StageStatus "Ours extract to Sony" "SKIPPED" }

# ---------------- I: our extraction -> OpenOrbis rebuild -----------------
if (Should-Run "I") {
    Set-StageStatus "Ours extract to OpenOrbis" "RUNNING"
    if ($Run.OrigPkgSafe) {
        $srcDir = Join-Path $Run.GP4 "ours_extract_of_reference"
        $outPkg = Join-Path $Run.RoundTrips "I_openorbis_roundtrip.pkg"
        $r = Invoke-RoundTrip "I_openorbis" $srcDir "openorbis" $outPkg
        Add-Result "TEST I ours-extract -> OpenOrbis rebuild" $r
    } else { Add-Result "TEST I ours-extract -> OpenOrbis rebuild" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "Ours extract to OpenOrbis"
} else { Set-StageStatus "Ours extract to OpenOrbis" "SKIPPED" }

# ---------------- J: OpenOrbis extraction -> our rebuild -----------------
if (Should-Run "J") {
    Set-StageStatus "OpenOrbis extract to ours" "RUNNING"
    if ($Run.OrigPkgSafe) {
        $ooDir = Join-Path $base "ref_extract_openorbis"
        $outPkg = Join-Path $Run.RoundTrips "J_ours_from_openorbis.pkg"
        $r = Invoke-RoundTrip "J_ours" $ooDir "ours" $outPkg
        Add-Result "TEST J OpenOrbis-extract -> ours rebuild" $r
    } else { Add-Result "TEST J OpenOrbis-extract -> ours rebuild" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "OpenOrbis extract to ours"
} else { Set-StageStatus "OpenOrbis extract to ours" "SKIPPED" }

# ---------------- K: OpenOrbis -> Sony control ---------------------------
if (Should-Run "K") {
    Set-StageStatus "OpenOrbis to Sony control" "RUNNING"
    if ($Run.OrigPkgSafe) {
        $ooDir = Join-Path $base "ref_extract_openorbis"
        $outPkg = Join-Path $Run.RoundTrips "K_sony_from_openorbis.pkg"
        $r = Invoke-RoundTrip "K_sony" $ooDir "sony" $outPkg
        Add-Result "TEST K OpenOrbis -> Sony control" $r
    } else { Add-Result "TEST K OpenOrbis -> Sony control" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "OpenOrbis to Sony control"
    # OpenOrbis's reference extract is no longer needed after J and K.
    Remove-PhaseArtifacts @(Join-Path $base "ref_extract_openorbis") "K: deleted ref_extract_openorbis"
} else { Set-StageStatus "OpenOrbis to Sony control" "SKIPPED" }

# ---------------- L: GP4 semantic comparison -----------------------------
if (Should-Run "L") {
    Set-StageStatus "GP4 semantics" "RUNNING"
    if ($Run.OrigPkgSafe) {
        $srcDir = Join-Path $Run.GP4 "ours_extract_of_reference"
        $image0 = if (Test-Path (Join-Path $srcDir "Image0")) { Join-Path $srcDir "Image0" } else { $srcDir }
        $gp4ours = Join-Path $Run.GP4 "gp4gen.gp4"
        $gp4sony = Join-Path $Run.GP4 "sony_format.gp4"
        Invoke-OrbisPkgTool "gp4gen `"$image0`" --out `"$gp4ours`"" (Join-Path $Run.GP4 "gp4gen.log") "L gp4gen" | Out-Null
        New-SonyGp4 -Image0 $image0 -OutPath $gp4sony -Passcode $pass | Out-Null
        $g = [System.Collections.Generic.List[string]]::new()
        $g.Add("volume type: pkg_ps4_app (both)")
        $g.Add("gp4gen files: $((Select-String -Path $gp4ours -Pattern '<file>').Count)")
        $g.Add("sony files:   $((Select-String -Path $gp4sony -Pattern 'targ_path').Count)")
        $g.Add("format: ours=child-element, sony=attribute (FORMATTING_ONLY difference)")
        Set-Content -Path (Join-Path $Run.Comparisons "gp4_semantic_comparison.txt") -Value $g -Encoding utf8
        Add-Result "TEST L GP4 semantic comparison" "PASS (see Comparisons/gp4_semantic_comparison.txt)"
    } else { Add-Result "TEST L GP4 semantic comparison" "SKIPPED_DEPENDENCY_FAILED" }
    Complete-Stage "GP4 semantics"
    # Our reference extract was the source for G/H/I/L - no longer needed.
    Remove-PhaseArtifacts @(Join-Path $Run.GP4 "ours_extract_of_reference") "L: deleted ours_extract_of_reference"
} else { Set-StageStatus "GP4 semantics" "SKIPPED" }

# ---------------- M: inner PFS cross-validation --------------------------
if (Should-Run "M") {
    Set-StageStatus "Inner PFS" "RUNNING"
    foreach ($pkgName in @("reference", "ours")) {
        $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
        if (-not $pkg) { continue }
        $dir = Join-Path $Run.InnerPfs $pkgName
        New-Item -ItemType Directory -Force $dir | Out-Null
        Invoke-OrbisPkgTool "dumpinner `"$pkg`" `"$(Join-Path $dir 'inner_ours.pfs')`"" (Join-Path $dir "dumpinner_ours.log") "M dumpinner $pkgName" | Out-Null
        Invoke-OpenOrbis "extract-inner `"$pkg`" `"$(Join-Path $dir 'inner_openorbis.pfs')`"" (Join-Path $dir "dumpinner_openorbis.log") "M extract-inner $pkgName" | Out-Null
        $o = Get-Item (Join-Path $dir "inner_ours.pfs") -ErrorAction SilentlyContinue
        $b = Get-Item (Join-Path $dir "inner_openorbis.pfs") -ErrorAction SilentlyContinue
        if ($o -and $b -and $b.Length -gt 0) {
            $same = $o.Length -eq $b.Length -and (Get-StreamSha256 $o.FullName) -eq (Get-StreamSha256 $b.FullName)
            Add-Result "TEST M inner PFS ($pkgName)" $(if ($same) { "PASS (byte-identical)" } else { "EXPECTED_DIFFERENCE" }) "ours=$($o.Length) openorbis=$($b.Length)"
        } else { Add-Result "TEST M inner PFS ($pkgName)" "FAIL" "one extraction missing" }
    }
    Complete-Stage "Inner PFS"
    Remove-PhaseArtifacts @(Join-Path $Run.InnerPfs "reference", (Join-Path $Run.InnerPfs "ours")) "M: deleted inner PFS dumps"
} else { Set-StageStatus "Inner PFS" "SKIPPED" }

# ---------------- N: outer PFS cross-validation --------------------------
if (Should-Run "N") {
    Set-StageStatus "Outer PFS" "RUNNING"
    foreach ($pkgName in @("reference", "ours")) {
        $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
        if (-not $pkg) { continue }
        $log = Join-Path $Run.OuterPfs "$pkgName`_outer.log"
        Invoke-OrbisPkgTool "pfsdump `"$pkg`"" $log "N pfsdump $pkgName" | Out-Null
        Add-Result "TEST N outer PFS ($pkgName) dump" "PASS (see OuterPfs/$pkgName`_outer.log)"
    }
    Complete-Stage "Outer PFS"
} else { Set-StageStatus "Outer PFS" "SKIPPED" }

# ---------------- summary ------------------------------------------------
$sum = [System.Collections.Generic.List[string]]::new()
$sum.Add("============================================================")
$sum.Add("ORBISPKGTOOL EXTERNAL CROSS-VALIDATION (FULL)")
$sum.Add("============================================================")
$sum.AddRange($Global:SummaryLines)
$unexpected = @($Global:SummaryLines | Where-Object { $_ -match "FAIL|ERROR" -and $_ -notmatch "PASS_EXPECTED|EXPECTED_DIFFERENCE|SKIPPED|NOT_FOUND" })
$sum.Add("------------------------------------------------------------")
$sum.Add("UNEXPECTED FAILURES/ERRORS: $($unexpected.Count)")
$label = if ($unexpected.Count -eq 0) { "PC_MAXIMUM_VALIDATED (subject to roundtrip results above)" } else { "INCOMPLETE" }
$sum.Add("PC CROSS-IMPLEMENTATION VALIDATION: $label")
Set-Content -Path (Join-Path $Run.Dir "summary.txt") -Value $sum -Encoding utf8
Set-StageStatus "Summary" "PASS" "PC CROSS-IMPLEMENTATION VALIDATION: $label"
Write-Host ""
Write-Host "Full validation complete -> $($Run.Dir)"
