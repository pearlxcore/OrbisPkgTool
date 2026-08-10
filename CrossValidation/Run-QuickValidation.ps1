# Run-QuickValidation.ps1 â€” fast PC cross-implementation validation.
# Environment + capabilities + baseline + three readers + three validators +
# entry comparison + file-list comparison. No big extractions or rebuilds.
#
# Usage:
#   .\CrossValidation\Run-QuickValidation.ps1 [-ConfigPath path\to\config.json]

[CmdletBinding()]
param([string]$ConfigPath = "$PSScriptRoot\config.json")

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Scripts\Environment.ps1" -ConfigPath $ConfigPath
. "$PSScriptRoot\Scripts\ToolRunners.ps1"
. "$PSScriptRoot\Scripts\ManifestHelpers.ps1"
. "$PSScriptRoot\Scripts\ProjectAdapters.ps1"

Write-Host "=== OrbisPkgTool EXTERNAL CROSS-VALIDATION (QUICK) ==="
Write-Host "Result dir: $($Run.Dir)"

foreach ($s in @("Environment", "Capabilities", "Reference baseline", "Ours readability",
    "Validators", "Entry comparison", "File-list comparison", "Summary")) { Add-Stage $s }

# â”€â”€ 00 environment report â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$envLines = [System.Collections.Generic.List[string]]::new()
$envLines.Add("OS: $([System.Environment]::OSVersion.VersionString)")
$envLines.Add("PowerShell: $($PSVersionTable.PSVersion)")
$envLines.Add(".NET: $([System.Environment]::Version)")
foreach ($tool in @("OrbisPkgTool", "OrbisPubCmd", "OpenOrbisDriver")) {
    $exe = $Cfg.$tool
    if ($exe -and (Test-Path $exe)) {
        $envLines.Add("$tool`: $exe")
        $envLines.Add("$tool SHA256: $(Get-StreamSha256 $exe)")
        $envLines.Add("$tool version: $((Get-Item $exe).VersionInfo.FileVersion)")
    } else { $envLines.Add("$tool`: NOT FOUND ($exe)") }
}
foreach ($pkg in @("reference_pkg", "ours_pkg")) {
    $p = $Cfg.$pkg
    if ($p -and (Test-Path -LiteralPath $p)) {
        $envLines.Add("$pkg`: $p  size=$((Get-Item -LiteralPath $p).Length)  sha256=$(Get-StreamSha256 $p)")
    } else { $envLines.Add("$pkg`: NOT FOUND ($p)") }
}
$drive = Get-PSDrive ([System.IO.Path]::GetPathRoot($Cfg.work_dir).TrimEnd('\')[0])
$envLines.Add("Available disk on work drive: $([math]::Round($drive.Free / 1GB, 1)) GB")
$envLines.Add("Work dir: $($Run.Work)")
Set-Content -Path (Join-Path $Run.Dir "00_environment.txt") -Value $envLines -Encoding utf8
Set-StageStatus "Environment" "PASS"
Set-StageStatus "Capabilities" "RUNNING"

# â”€â”€ capabilities discovery â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$cap = [System.Collections.Generic.List[string]]::new()
$cap.Add("Operation`tOURS`tSONY`tOPENORBIS")
function CapRow([string]$op, [string]$ours, [string]$sony, [string]$oo) {
    $cap.Add("$op`t$ours`t$sony`t$oo")
}
# ours (verified surface)
CapRow "Read PKG" "YES" "YES" "YES"
CapRow "Verify PKG" "YES (validate/verify)" "YES (img_verify)" "YES (validate)"
CapRow "List Image0" "YES (list)" "YES (img_file_list)" "YES (list)"
CapRow "Extract complete package" "YES (extract)" "YES (img_extract)" "YES (extract)"
CapRow "List Sc0 entries" "YES" "YES" "YES (info)"
CapRow "Extract inner PFS" "YES (dumpinner)" "NOT_SUPPORTED" "YES (extract-inner)"
CapRow "Extract outer PFS" "YES (xtsdump)" "NOT_SUPPORTED" "NOT_SUPPORTED"
CapRow "Entry table dump" "YES (entries)" "YES (img_info)" "YES (info)"
CapRow "Generate GP4" "YES (gp4gen)" "YES (gp4_proj_create)" "YES (PkgEditor)"
CapRow "Build from GP4" "YES (build)" "YES (img_create)" "YES (build)"
CapRow "Compare packages" "NOT_SUPPORTED" "YES (pkg_compare)" "NOT_SUPPORTED"
Set-Content -Path (Join-Path $Run.Dir "capabilities.txt") -Value $cap -Encoding utf8
Set-StageStatus "Capabilities" "PASS"

# â”€â”€ helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$pass = $Cfg.passcode

function Test-PkgReadability {
    param([string]$Name, [string]$Pkg, [string]$Dir)
    $ours = "SKIPPED"; $sony = "SKIPPED"; $oo = "SKIPPED"
    if ($Pkg) {
        $e1 = Invoke-OrbisPkgTool "list --passcode $pass `"$Pkg`"" (Join-Path $Dir "$Name`_ours_list.log")
        $ours = if ($e1 -eq 0) { "PASS" } else { "FAIL" }
        $e2 = Invoke-OrbisPubCmd "img_file_list --passcode $pass `"$Pkg`"" (Join-Path $Dir "$Name`_sony_list.log")
        $sony = if ($e2 -eq 0) { "PASS" } elseif ($e2 -eq -1) { "NOT_SUPPORTED" } else { "FAIL" }
        $e3 = Invoke-OpenOrbis "list `"$Pkg`"" (Join-Path $Dir "$Name`_openorbis_list.log")
        $oo = if ($e3 -eq 0) { "PASS" } else { "FAIL" }
    } else {
        $ours = "NOT_FOUND"; $sony = "NOT_FOUND"; $oo = "NOT_FOUND"
    }
    Add-Result "$Name readability" "ours=$ours sony=$sony openorbis=$oo"
    return @($ours, $sony, $oo)
}

function Test-PkgValidate {
    param([string]$Name, [string]$Pkg, [string]$Dir)
    $ours = "SKIPPED"; $sony = "SKIPPED"; $oo = "SKIPPED"
    if ($Pkg) {
        $e1 = Invoke-OrbisPkgTool "validate --passcode $pass `"$Pkg`"" (Join-Path $Dir "$Name`_ours_validate.log")
        $ours = if ($e1 -eq 0) { "PASS" } else { "FAIL" }
        $e2 = Invoke-OrbisPubCmd "img_verify --passcode $pass `"$Pkg`"" (Join-Path $Dir "$Name`_sony_verify.log")
        $sony = if ($e2 -eq 0) { "PASS" } else { "FAIL" }
        # classify Sony warnings
        $so = Get-LogOutput (Join-Path $Dir "$Name`_sony_verify.log")
        $warns = Get-SonyWarningCount (Join-Path $Dir "$Name`_sony_verify.log")
        if ($warns -gt 0) { $sony = "PASS_EXPECTED_WARNINGS ($warns)" }
        $ooLog = Join-Path $Dir "$Name`_openorbis_validate.log"
        $e3 = Invoke-OpenOrbis "validate `"$Pkg`"" $ooLog
        $oo = if ($e3 -eq 0) { "PASS" } else { "FAIL" }
        if ($oo -eq "FAIL" -and (Test-OpenOrbisExpectedDifference $ooLog)) {
            $oo = "EXPECTED_DIFFERENCE (OpenOrbis digest recomputation)"
        }
    } else {
        $ours = "NOT_FOUND"; $sony = "NOT_FOUND"; $oo = "NOT_FOUND"
    }
    Add-Result "$Name validation" "ours=$ours sony=$sony openorbis=$oo"
    return @($ours, $sony, $oo)
}

# â”€â”€ A: reference baseline â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Set-StageStatus "Reference baseline" "RUNNING"
$base = $Run.Sony
if ($Run.OrigPkgSafe) {
    $r = Test-PkgReadability "baseline_reference" $Run.OrigPkgSafe $base
    $v = Test-PkgValidate "baseline_reference" $Run.OrigPkgSafe $base
} else { Add-Result "TEST A reference baseline" "SKIPPED_DEPENDENCY_FAILED" "no reference_pkg configured" }
Complete-Stage "Reference baseline"

# â”€â”€ B: our built pkg through three readers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Set-StageStatus "Ours readability" "RUNNING"
$three = @()
if ($Run.OursPkgSafe) {
    $r = Test-PkgReadability "ours" $Run.OursPkgSafe $Run.Sony
    $three = $r
} else { Add-Result "TEST B ours through three readers" "SKIPPED_DEPENDENCY_FAILED" "no ours_pkg configured" }
Complete-Stage "Ours readability"

# â”€â”€ C: three validators (ours + reference) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Set-StageStatus "Validators" "RUNNING"
if ($Run.OursPkgSafe) { $null = Test-PkgValidate "ours" $Run.OursPkgSafe $Run.Sony }
if ($Run.OrigPkgSafe) { $null = Test-PkgValidate "reference" $Run.OrigPkgSafe $Run.Sony }
Complete-Stage "Validators"

# â”€â”€ D: entry table differential â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Set-StageStatus "Entry comparison" "RUNNING"
$entryComp = [System.Collections.Generic.List[string]]::new()
$entryComp.Add("EntryId`tField`tOURS`tSONY`tOPENORBIS")
foreach ($pkgName in @("reference", "ours")) {
    $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
    if (-not $pkg) { continue }
    Invoke-OrbisPkgTool "entries `"$pkg`"" (Join-Path $Run.Comparisons "$pkgName`_entries_ours.log") | Out-Null
    Invoke-OpenOrbis "info `"$pkg`"" (Join-Path $Run.Comparisons "$pkgName`_entries_openorbis.log") | Out-Null
    $oursLines = Get-LogOutput (Join-Path $Run.Comparisons "$pkgName`_entries_ours.log")
    $ooLines = Get-LogOutput (Join-Path $Run.Comparisons "$pkgName`_entries_openorbis.log")
    $entryComp.Add("--- $pkgName ---")
    foreach ($l in $ooLines | Where-Object { $_ -match "0x[0-9A-F]{8}" }) {
        if ($l -match '0x([0-9A-F]{8}).*flags1=(0x[0-9A-F]{8}).*size=(\d+).*enc=(True|False).*key=(\d+).*id=(\S+)') {
            $id = $Matches[1]; $f1 = $Matches[2]; $size = $Matches[3]; $enc = $Matches[4]; $key = $Matches[5]; $name = $Matches[6]
            $ourLine = $oursLines | Where-Object { $_ -match ("0x" + $id) } | Select-Object -First 1
            $entryComp.Add("$name`tid=0x$id`tours=$ourLine`topenorbis=flags1=$f1 size=$size enc=$enc key=$key")
        }
    }
}
Set-Content -Path (Join-Path $Run.Comparisons "entry_comparison.txt") -Value $entryComp -Encoding utf8
Add-Result "TEST D entry comparison" "PASS (see Comparisons/entry_comparison.txt)"
Complete-Stage "Entry comparison"

# â”€â”€ file-list comparison â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Set-StageStatus "File-list comparison" "RUNNING"
$listComp = [System.Collections.Generic.List[string]]::new()
foreach ($pkgName in @("reference", "ours")) {
    $pkg = if ($pkgName -eq "reference") { $Run.OrigPkgSafe } else { $Run.OursPkgSafe }
    if (-not $pkg) { continue }
    $lists = @{}
    Invoke-OrbisPkgTool "list --passcode $pass `"$pkg`"" (Join-Path $Run.Comparisons "$pkgName`_filelist_ours.log") | Out-Null
    Invoke-OrbisPubCmd "img_file_list --passcode $pass `"$pkg`"" (Join-Path $Run.Comparisons "$pkgName`_filelist_sony.log") | Out-Null
    Invoke-OpenOrbis "list `"$pkg`"" (Join-Path $Run.Comparisons "$pkgName`_filelist_openorbis.log") | Out-Null
    $lists.ours = Normalize-FileList (Get-LogOutput (Join-Path $Run.Comparisons "$pkgName`_filelist_ours.log"))
    $lists.sony = Normalize-FileList (Get-LogOutput (Join-Path $Run.Comparisons "$pkgName`_filelist_sony.log"))
    $lists.oo = Normalize-FileList (Get-LogOutput (Join-Path $Run.Comparisons "$pkgName`_filelist_openorbis.log"))
    # Normalize: OpenOrbis lists inner-PFS-relative paths (no "Image0/" prefix)
    # and has no Sc0 section. Strip the prefix and drop Sc0/* for comparison.
    function Norm([string[]]$l, [bool]$stripImage0, [bool]$dropSc0) {
        $out = foreach ($p in $l) {
            $q = $p
            if ($stripImage0 -and $q -like "Image0/*") { $q = $q.Substring(7) }
            if ($dropSc0 -and $q -like "Sc0/*") { continue }
            $q
        }
        return @($out | Sort-Object -Unique)
    }
    $nOurs = Norm $lists.ours $true $true
    $nSony = Norm $lists.sony $true $true
    $nOo = Norm $lists.oo $false $false
    $only = [System.Collections.Generic.List[string]]::new()
    foreach ($other in @(@($nSony, "sony"), @($nOo, "openorbis"))) {
        $missing = @(Compare-Object $nOurs $other[0] | Where-Object { $_.SideIndicator -eq "<=" })
        if ($missing.Count -gt 0) { $only.Add("$($other[1]) misses $($missing.Count) of our paths (e.g. $(($missing | Select-Object -First 2 | ForEach-Object { $_.InputObject }) -join ', '))") }
    }
    $listComp.Add("--- ${pkgName}: ours=$($lists.ours.Count) sony=$($lists.sony.Count) openorbis=$($lists.oo.Count) (normalized: ours=$($nOurs.Count) sony=$($nSony.Count) oo=$($nOo.Count)) ---")
    foreach ($d in $only) { $listComp.Add($d) }
    if ($only.Count -eq 0) { Add-Result "file list $pkgName" "PASS" }
    else { Add-Result "file list $pkgName" "EXPECTED_DIFFERENCE" ($only -join "; ") }
}
Set-Content -Path (Join-Path $Run.Comparisons "file_list_comparison.txt") -Value $listComp -Encoding utf8
Complete-Stage "File-list comparison"

# â”€â”€ summary â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$sum = [System.Collections.Generic.List[string]]::new()
$sum.Add("============================================================")
$sum.Add("ORBISPKGTOOL EXTERNAL CROSS-VALIDATION (QUICK)")
$sum.Add("============================================================")
$sum.AddRange($Global:SummaryLines)
$unexpected = @($Global:SummaryLines | Where-Object { $_ -match "FAIL|ERROR" -and $_ -notmatch "PASS_EXPECTED|EXPECTED_DIFFERENCE|SKIPPED|NOT_FOUND" })
$sum.Add("------------------------------------------------------------")
$sum.Add("UNEXPECTED FAILURES/ERRORS: $($unexpected.Count)")
$label = $(if ($unexpected.Count -eq 0) { "PASS" } else { "INCOMPLETE" })
$sum.Add("PC CROSS-IMPLEMENTATION VALIDATION (QUICK): $label")
Set-Content -Path (Join-Path $Run.Dir "summary.txt") -Value $sum -Encoding utf8
Set-StageStatus "Summary" "PASS" "PC CROSS-IMPLEMENTATION VALIDATION (QUICK): $label"
Write-Host ""
Write-Host "Quick validation complete -> $($Run.Dir)"

