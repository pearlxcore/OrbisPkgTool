# ToolRunners.ps1 — dot-source after Environment.ps1.
# Thin per-tool wrappers: every invocation is logged, exit codes returned.

function Invoke-OrbisPkgTool {
    param([string]$ArgsLine, [string]$LogFile, [string]$Name = "OrbisPkgTool $ArgsLine")
    $exe = $Cfg.OrbisPkgTool
    if (-not (Test-Path $exe)) { Add-Result "tool missing" ERROR "OrbisPkgTool exe not found: $exe"; return -1 }
    return Invoke-Logged -Name $Name -LogFile $LogFile -WorkingDir (Split-Path $exe) -ScriptBlock {
        & $exe $ArgsLine.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    }
}

function Invoke-OrbisPubCmd {
    param([string]$ArgsLine, [string]$LogFile, [string]$Name = "orbis-pub-cmd $ArgsLine")
    $exe = $Cfg.OrbisPubCmd
    if (-not (Test-Path $exe)) { Add-Result "tool missing" ERROR "orbis-pub-cmd not found: $exe"; return -1 }
    return Invoke-Logged -Name $Name -LogFile $LogFile -WorkingDir (Split-Path $exe) -ScriptBlock {
        & $exe $ArgsLine.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    }
}

function Invoke-OpenOrbis {
    param([string]$ArgsLine, [string]$LogFile, [string]$Name = "OpenOrbisDriver $ArgsLine")
    $exe = $Cfg.OpenOrbisDriver
    if (-not (Test-Path $exe)) { Add-Result "tool missing" ERROR "OpenOrbisDriver not found: $exe"; return -1 }
    return Invoke-Logged -Name $Name -LogFile $LogFile -WorkingDir (Split-Path $exe) -ScriptBlock {
        & $exe $ArgsLine.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    }
}
