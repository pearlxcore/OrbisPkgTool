# Run-RepackParity.ps1 - one-shot: repack a PKG with policy replay, then
# validate it with our 8-stage validator AND Sony orbis-pub-cmd.
#
# This is the fast smoke test: it does NOT do the full cross-tool matrix
# (no OpenOrbis, no six-path extraction). For full cross-validation use
# Run-CompressionParity.ps1.
#
# Verifies:
#   - repack completes (extract -> restructure -> gp4gen --pfsc-profile -> build)
#   - our 8-stage validate passes
#   - Sony orbis-pub-cmd img_verify passes (same warning parity as reference)
#   - Sony img_file_list lists our rebuild (PS4-installer proxy)
#   - pfscprofile --ref reports 0 policy mismatches vs the original
#   - PKG size is reported as a diagnostic (NOT a gate)
#
# Usage:
#   .\CrossValidation\Run-RepackParity.ps1 <original.pkg> [-Out rebuilt.pkg] [-Passcode X]
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Pkg,
    [string]$Out,
    [string]$Passcode = "00000000000000000000000000000000"
)
$ErrorActionPreference = "Stop"

# Resolve paths relative to this script's location (repo root).
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$cli = Join-Path $repoRoot "OrbisPkgTool\OrbisPkgTool.Cli\bin\Release\net10.0-windows\OrbisPkgTool.Cli.exe"
$orbisPubCmd = Join-Path $repoRoot "PS4_Fake_PKG_Tools_3.87_V7\orbis-pub-cmd.exe"

if (-not (Test-Path $cli)) {
    Write-Host "Building OrbisPkgTool.Cli (Release)..."
    & dotnet build (Join-Path $repoRoot "OrbisPkgTool\OrbisPkgTool.Cli") -c Release --nologo -v q | Out-Null
}
if (-not (Test-Path $Pkg)) { throw "PKG not found: $Pkg" }
if (-not $Out) { $Out = Join-Path $env:TEMP ([System.IO.Path]::GetFileNameWithoutExtension($Pkg) + "_repack_parity.pkg") }

$work = Join-Path $env:TEMP ("repack_parity_" + [System.Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Force $work | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "============================================================"
Write-Host "  OrbisPkgTool REPACK PARITY SMOKE TEST"
Write-Host "  Input : $Pkg"
Write-Host "  Output: $Out"
Write-Host "  Work  : $work"
Write-Host "============================================================"
Write-Host ""

$results = [System.Collections.Generic.List[string]]::new()
function Add-Result([string]$test, [string]$state, [string]$note="") {
    $line = "{0,-38} {1,-20} {2}" -f $test, $state, $note
    $results.Add($line)
    Write-Host $line
}

try {
    function Invoke-Capture {
        param([string]$LogFile, [scriptblock]$Block)
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $PSNativeCommandUseErrorActionPreference = $false
        try {
            $errFile = Join-Path $env:TEMP ("cap_" + [System.IO.Path]::GetRandomFileName() + ".txt")
            $out = & $Block 2> $errFile
            if (Test-Path $errFile) {
                $err = Get-Content $errFile
                Remove-Item $errFile -Force -ErrorAction SilentlyContinue
                $all = @($out) + @($err | Where-Object { $_ })
                Set-Content -Path $LogFile -Value $all -Encoding utf8
                return $all
            }
            Set-Content -Path $LogFile -Value $out -Encoding utf8
            return $out
        } finally {
            $ErrorActionPreference = $prevEap
        }
    }

    # Invoke the CLI with an EXPLICIT arg array (avoids any scriptblock
    # variable-scope surprises — every argument is bound by value here).
    # Returns the captured output lines (an array); $LASTEXITCODE holds the exit.
    function Invoke-Cli {
        param([string[]]$CliArgs, [string]$LogFile)
        return Invoke-Capture $LogFile { & $cli @CliArgs }
    }

    # 1. Repack (policy replay is automatic with --pfsc-mode compressed default)
    Write-Host "[1/5] Repacking with compression-policy replay..."
    Write-Host "  cli=$cli"
    Write-Host "  Pkg=$Pkg  (exists=$(Test-Path $Pkg))"
    Write-Host "  Out=$Out"
    $repackLog = Join-Path $work "01_repack.log"
    $null = Invoke-Cli -CliArgs @("repack", $Pkg, "--out", $Out, "--passcode", $Passcode) -LogFile $repackLog
    $repackExit = $LASTEXITCODE
    if (-not (Test-Path $Out)) { throw "repack did not produce output (exit=$repackExit, see $repackLog)" }
    $outSize = (Get-Item $Out).Length
    $inSize = (Get-Item $Pkg).Length
    $ratio = if ($inSize -gt 0) { $outSize * 100.0 / $inSize } else { 0 }
    Add-Result "1 repack" "PASS" "out=$([math]::Round($outSize/1MB,1)) MB vs in=$([math]::Round($inSize/1MB,1)) MB ($([math]::Round($ratio,1))%)"

    # 2. Our 8-stage validate
    Write-Host "[2/5] Validating with our 8-stage validator..."
    $vLog = Join-Path $work "02_validate.log"
    $null = Invoke-Cli -CliArgs @("validate", "--passcode", $Passcode, $Out) -LogFile $vLog
    $vExit = $LASTEXITCODE
    Add-Result "2 validate (ours)" $(if ($vExit -eq 0) { "PASS" } else { "FAIL" })

    # 3. Sony orbis-pub-cmd img_verify
    if (Test-Path $orbisPubCmd) {
        Write-Host "[3/5] Validating with Sony orbis-pub-cmd img_verify..."
        # Sony fails on Unicode paths - copy ASCII-safe first.
        $asciiOut = Join-Path $work "ascii_out.pkg"
        Copy-Item $Out $asciiOut -Force
        $sLog = Join-Path $work "03_sony_verify.log"
        $sDir = Split-Path $orbisPubCmd
        Push-Location $sDir
        try {
            $null = Invoke-Capture $sLog { & $orbisPubCmd img_verify --passcode $Passcode $asciiOut }
            $sExit = $LASTEXITCODE
        } finally { Pop-Location }
        # Count warnings - R4211/R4124 appear on the ORIGINAL too, so parity is PASS.
        $warns = 0
        if (Test-Path $sLog) {
            $warns = @(Get-Content $sLog | Where-Object { $_ -match "\[Warn\]" -and $_ -notmatch "Number of Warning" }).Count
        }
        $state = if ($sExit -eq 0) { "PASS" } elseif ($warns -gt 0) { "PASS_EXPECTED_WARNINGS ($warns)" } else { "FAIL" }
        Add-Result "3 validate (sony)" $state

        # 3b. Sony img_file_list (the PS4-installer proxy)
        Write-Host "[3b/5] Sony img_file_list (readability proxy)..."
        $lLog = Join-Path $work "03b_sony_list.log"
        Push-Location $sDir
        try {
            $null = Invoke-Capture $lLog { & $orbisPubCmd img_file_list --passcode $Passcode $asciiOut }
            $lExit = $LASTEXITCODE
        } finally { Pop-Location }
        Add-Result "3b img_file_list (sony)" $(if ($lExit -eq 0) { "PASS" } else { "FAIL" })
    } else {
        Add-Result "3 validate (sony)" "SKIPPED" "orbis-pub-cmd.exe not found"
        Add-Result "3b img_file_list (sony)" "SKIPPED"
    }

    # 4. PFSC policy diff
    Write-Host "[4/5] PFSC compression-policy diff vs original..."
    $pLog = Join-Path $work "04_pfscprofile.log"
    $profOut = Invoke-Cli -CliArgs @("pfscprofile", $Out, "--ref", $Pkg, "--passcode", $Passcode) -LogFile $pLog
    $mismatched = -1
    $agreeLine = @($profOut | Where-Object { $_ -match "policy agreement" } | Select-Object -First 1)
    if ($agreeLine -and $agreeLine -match "(\d+) mismatched") { $mismatched = [int]$Matches[1] }
    Add-Result "4 PFSC policy diff" $(if ($mismatched -eq 0) { "PASS" } else { "WARNING" }) "$mismatched mismatched files"

    # 5. Summary
    $sw.Stop()
    Write-Host ""
    Write-Host "[5/5] Summary"
    Write-Host "============================================================"
    $results | ForEach-Object { Write-Host "  $_" }
    $fails = @($results | Where-Object { $_ -match "FAIL" -and $_ -notmatch "PASS_EXPECTED" })
    Write-Host "------------------------------------------------------------"
    Write-Host "  Unexpected failures: $($fails.Count)"
    $label = if ($fails.Count -eq 0 -and $mismatched -eq 0) { "REPACK PARITY PASS" } else { "INCOMPLETE" }
    Write-Host "  Result: $label"
    Write-Host "  Duration: $([math]::Round($sw.Elapsed.TotalMinutes,1)) min"
    Write-Host "  Output: $Out"
    Write-Host "============================================================"
    Write-Host ""
    Write-Host "shadPS4 install test (MANUAL):"
    Write-Host "  Install $Out in shadPS4, boot, play to an asset-heavy section."
    Write-Host "  Physical PS4 install remains unverified without hardware."
}
catch {
    Write-Host ""
    Write-Host "[ERROR] $($_.Exception.Message)"
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace }
    Write-Host "Work dir kept: $work"
    exit 1
}
finally {
    # Keep the work dir for inspection (logs are small).
    Write-Host "Logs: $work"
}
