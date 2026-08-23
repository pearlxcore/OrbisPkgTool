[CmdletBinding()]
param(
    [string]$ToolPath,
    [string]$WorkDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) ('OrbisPkgTool-cli-smoke-' + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ToolPath)) {
    $packageTool = Join-Path $PSScriptRoot 'OrbisPkgTool.exe'
    $sourceTool = Join-Path $PSScriptRoot '..\OrbisPkgTool\bin\Release\net10.0-windows\OrbisPkgTool.exe'
    $ToolPath = if (Test-Path -LiteralPath $packageTool -PathType Leaf) { $packageTool } else { $sourceTool }
}

if (-not (Test-Path -LiteralPath $ToolPath -PathType Leaf)) {
    throw "OrbisPkgTool.exe was not found: $ToolPath"
}

New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null

function Invoke-Tool {
    param([string[]]$Arguments)

    $output = & $ToolPath @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: OrbisPkgTool.exe $($Arguments -join ' ')`n$output"
    }
    return $output
}

try {
    $help = Invoke-Tool @('--help')
    if ($help -notmatch 'OrbisPkgTool') { throw 'The root help text was not returned.' }

    foreach ($command in 'info', 'list', 'extract', 'validate', 'build', 'repack', 'merge', 'sfo', 'trp') {
        $commandHelp = Invoke-Tool @($command, '--help')
        if ([string]::IsNullOrWhiteSpace($commandHelp)) {
            throw "No help text was returned for '$command'."
        }
    }

    $sfoPath = Join-Path $WorkDirectory 'param.sfo'
    Invoke-Tool @('sfo', 'create', $sfoPath, '--title', 'CLI Smoke Test', '--title-id', 'CUSA00001', '--content-id', 'UP0000-CUSA00001_00-CLISMOKETEST0000') | Out-Null
    if (-not (Test-Path -LiteralPath $sfoPath -PathType Leaf)) { throw 'SFO create did not produce its output file.' }

    $check = Invoke-Tool @('sfo', 'check', $sfoPath)
    if ($check -notmatch 'OK') { throw "SFO check did not report success.`n$check" }

    $read = Invoke-Tool @('sfo', 'read', $sfoPath)
    if ($read -notmatch 'CLI Smoke Test') { throw "SFO read did not return the created title.`n$read" }

    Invoke-Tool @('sfo', 'set', $sfoPath, 'TITLE', 'CLI Smoke Test Updated') | Out-Null
    $readUpdated = Invoke-Tool @('sfo', 'read', $sfoPath)
    if ($readUpdated -notmatch 'CLI Smoke Test Updated') { throw 'SFO set did not persist the new title.' }

    Invoke-Tool @('selftest') | Out-Null
    Write-Host 'CLI smoke test passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $WorkDirectory) {
        Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
    }
}
