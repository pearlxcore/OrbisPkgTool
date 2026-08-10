# Environment.ps1 — dot-source this first from any run script.
# Loads the config, creates the timestamped result run, and provides shared state.

param(
    [string]$ConfigPath = "$PSScriptRoot\..\config.json"
)

if (-not (Test-Path $ConfigPath)) {
    throw "Config not found: $ConfigPath  (copy config.example.json to config.json and edit paths)"
}

# Read as UTF-8 explicitly: PS 5.1 Get-Content misreads UTF-8 (no BOM) files
# using the ANSI codepage, corrupting non-ASCII paths (e.g. the full-width
# colon in the Digimon filename).
$Global:Cfg = [System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json

# ── timestamped result run ────────────────────────────────────────────────
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$Global:RunDir = Join-Path $Cfg.results_dir "$stamp`_$($Cfg.label)"
$Global:Run = @{
    Dir          = $RunDir
    OrbisPkgTool = Join-Path $RunDir "OrbisPkgTool"
    Sony         = Join-Path $RunDir "Sony"
    OpenOrbis    = Join-Path $RunDir "OpenOrbis"
    Manifests    = Join-Path $RunDir "Manifests"
    Comparisons  = Join-Path $RunDir "Comparisons"
    GP4          = Join-Path $RunDir "GP4"
    InnerPfs     = Join-Path $RunDir "InnerPfs"
    OuterPfs     = Join-Path $RunDir "OuterPfs"
    RoundTrips   = Join-Path $RunDir "RoundTrips"
    Work         = $Cfg.work_dir
}
foreach ($d in $Run.Values) { if ($d -ne $Run.Work) { New-Item -ItemType Directory -Force $d | Out-Null } }
New-Item -ItemType Directory -Force (Join-Path $Run.Work "run_$stamp") | Out-Null
$Global:Run.Work = Join-Path $Run.Work "run_$stamp"

# ── ASCII-safe copy for Sony (orbis 3.87 fails on U+FF1A etc.) ─────────────
$Global:Run.OrigPkgSafe = $null
$Global:Run.OursPkgSafe = $null
function Copy-AsciiSafe([string]$src, [string]$name) {
    if (-not $src -or -not (Test-Path -LiteralPath $src)) { return $null }
    $dst = Join-Path $Run.Work ($name + [System.IO.Path]::GetExtension($src))
    if (Test-Path $dst) { Remove-Item $dst -Force }
    Copy-Item -LiteralPath $src -Destination $dst
    return $dst
}
$Global:Run.OrigPkgSafe = Copy-AsciiSafe $Cfg.reference_pkg "reference"
$Global:Run.OursPkgSafe = Copy-AsciiSafe $Cfg.ours_pkg "ours"

# ── summary collector ─────────────────────────────────────────────────────
$Global:SummaryLines = [System.Collections.Generic.List[string]]::new()
function Add-Summary([string]$line) { $Global:SummaryLines.Add($line) }
function Add-Result([string]$test, [string]$state, [string]$note = "") {
    $line = "{0,-38} {1,-30} {2}" -f $test, $state, $note
    Add-Summary $line
    Write-Host $line
}

# ── incremental run status (run_status.txt, rewritten after every stage) ──
$Global:RunStages = [ordered]@{}
$Global:StageMark = 0

function Add-Stage([string]$stage) { $Global:RunStages[$stage] = "PENDING" }

function Set-StageStatus([string]$stage, [string]$state, [string]$note = "") {
    $Global:RunStages[$stage] = $state
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($s in $Global:RunStages.Keys) {
        $st = $Global:RunStages[$s]
        $n = if ($note -and $s -eq $stage) { "  $note" } else { "" }
        $out.Add(("[{0}] {1}{2}" -f $st.PadRight(10), $s, $n))
    }
    Set-Content -Path (Join-Path $Run.Dir "run_status.txt") -Value $out -Encoding utf8
}

# Mark the current stage PASS unless a real FAIL was added since it started
# (PASS_EXPECTED_WARNINGS / EXPECTED_DIFFERENCE do not count as failures).
function Complete-Stage([string]$stage, [string]$note = "") {
    $fail = $false
    for ($i = $Global:StageMark; $i -lt $Global:SummaryLines.Count; $i++) {
        if ($Global:SummaryLines[$i] -match "FAIL" -and $Global:SummaryLines[$i] -notmatch "PASS_EXPECTED|EXPECTED_DIFFERENCE|SKIPPED") {
            $fail = $true; break
        }
    }
    Set-StageStatus $stage $(if ($fail) { "FAIL" } else { "PASS" }) $note
    $Global:StageMark = $Global:SummaryLines.Count
}

# The OpenOrbis digest-recomputation trio its validator gets wrong on ANY
# package (Content Digest, Major Param Digest, and the entry digests it hashes
# at the logical DataSize rather than the aligned stored region — it fails the
# ORIGINAL Sony package the same way).
function Test-OpenOrbisExpectedDifference([string]$LogFile) {
    if (-not (Test-Path $LogFile)) { return $false }
    $out = Get-LogOutput $LogFile
    $trio = @($out | Where-Object { $_ -match "Content Digest|Major Param Digest| digest @" })
    $fails = @($out | Where-Object { $_ -match "Fail " })
    return $trio.Count -gt 0 -and $fails.Count -eq $trio.Count
}

# ── logging ───────────────────────────────────────────────────────────────
function Invoke-Logged {
    param(
        [string]$Name,
        [string]$LogFile,
        [string]$WorkingDir,
        [scriptblock]$ScriptBlock
    )
    New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("COMMAND:")
    [void]$sb.AppendLine("  $Name")
    [void]$sb.AppendLine("WORKING DIRECTORY:")
    [void]$sb.AppendLine("  $WorkingDir")
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$sb.AppendLine("START: $(Get-Date -Format o)")
    $output = [System.Text.StringBuilder]::new()
    $exit = 0
    try {
        $prev = Get-Location
        $prevEap = $ErrorActionPreference
        try {
            # PS 5.1 turns native stderr into TERMINATING errors under
            # EAP=Stop — override it for the native invocation only.
            $ErrorActionPreference = "Continue"
            $PSNativeCommandUseErrorActionPreference = $false
            if ($WorkingDir) { Set-Location $WorkingDir }
            # Capture stderr via a temp file (works on PS 5.1 and 7; nothing discarded).
            $errFile = Join-Path $Run.Work ("err_" + [System.IO.Path]::GetRandomFileName() + ".txt")
            $null = & $ScriptBlock 2> $errFile | ForEach-Object { [void]$output.AppendLine($_.ToString()) }
            $exit = $LASTEXITCODE
            if (Test-Path $errFile) {
                Get-Content $errFile | ForEach-Object { [void]$output.AppendLine($_) }
                Remove-Item $errFile -Force -ErrorAction SilentlyContinue
            }
        } finally {
            $ErrorActionPreference = $prevEap
            Set-Location $prev
        }
    } catch {
        [void]$output.AppendLine("EXCEPTION: $_")
        $exit = 1
    }
    $sw.Stop()
    [void]$sb.AppendLine("END: $(Get-Date -Format o)")
    [void]$sb.AppendLine("DURATION: $($sw.Elapsed.TotalSeconds.ToString('F2')) s")
    [void]$sb.AppendLine("EXIT CODE: $exit")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("================ OUTPUT (stdout + stderr interleaved) ================")
    [void]$sb.AppendLine($output.ToString())
    Set-Content -Path $LogFile -Value $sb.ToString() -Encoding utf8
    return $exit
}

function Get-LogOutput([string]$LogFile) {
    if (-not (Test-Path $LogFile)) { return @() }
    $lines = Get-Content $LogFile
    $i = [Array]::IndexOf($lines, "================ OUTPUT (stdout + stderr interleaved) ================")
    if ($i -lt 0) { return @() }
    return $lines[($i + 1)..($lines.Count - 2)]
}

# ── streaming SHA256 (never loads large files into RAM) ──────────────────
function Get-StreamSha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $buf = New-Object byte[] 1048576
        $n = $fs.Read($buf, 0, $buf.Length)
        while ($n -gt 0) {
            $null = $sha.TransformBlock($buf, 0, $n, $null, 0)
            $n = $fs.Read($buf, 0, $buf.Length)
        }
        $null = $sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
        return ([BitConverter]::ToString($sha.Hash)).Replace("-", "")
    } finally { $fs.Dispose(); $sha.Dispose() }
}
