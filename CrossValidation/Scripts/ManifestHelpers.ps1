# ManifestHelpers.ps1 — dot-source after Environment.ps1.
# Deterministic file manifests (Type<TAB>Size<TAB>SHA256<TAB>RelativePath), comparisons.

# .NET Framework (PS 5.1) lacks Path.GetRelativePath — manual version.
function Get-RelPath([string]$Root, [string]$Full) {
    $r = $Root.TrimEnd('\') + '\'
    if ($Full.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Full.Substring($r.Length)
    }
    return $Full
}

# Build a deterministic manifest of a directory tree. Streaming hashes.
function New-FileManifest {
    param([string]$Root, [string]$OutPath)
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName) {
        $rel = (Get-RelPath $Root $f.FullName).Replace('\', '/')
        $sha = Get-StreamSha256 $f.FullName
        $lines.Add("F`t$($f.Length)`t$sha`t$rel")
    }
    foreach ($d in Get-ChildItem -LiteralPath $Root -Recurse -Directory | Sort-Object FullName) {
        $rel = (Get-RelPath $Root $d.FullName).Replace('\', '/')
        $lines.Add("D`t0`t-`t$rel")
    }
    Set-Content -Path $OutPath -Value $lines -Encoding utf8
    return $lines
}

# Normalize a manifest for cross-tool comparison: ours/Sony extracts write
# Image0/ + Sc0/ top dirs while OpenOrbis writes the inner tree directly.
# Maps "Image0/x" → "x" and drops Sc0/* (OpenOrbis has no Sc0 section).
function Normalize-Manifest {
    param([string]$InPath, [string]$OutPath, [bool]$StripImage0 = $true, [bool]$DropSc0 = $true)
    $lines = foreach ($l in Get-Content $InPath) {
        if (-not $l) { continue }
        $p = $l.Split("`t")
        if ($p.Count -lt 4) { continue }
        $rel = $p[3]
        if ($DropSc0 -and ($rel -eq "Sc0" -or $rel -like "Sc0/*")) { continue }
        if ($StripImage0 -and ($rel -eq "Image0" -or $rel -like "Image0/*")) {
            if ($rel -eq "Image0") { continue }
            $rel = $rel.Substring(7)
        }
        $p[3] = $rel
        ($p -join "`t")
    }
    Set-Content -Path $OutPath -Value $lines -Encoding utf8
    return $OutPath
}

# Compare two manifests (same format). Returns a list of "state`tdetail" lines.
function Compare-Manifests {
    param([string]$A, [string]$B, [string]$LabelA, [string]$LabelB)
    $mapA = @{}; $mapB = @{}
    foreach ($l in Get-Content $A) { if ($l) { $p = $l.Split("`t"); if ($p.Count -ge 4) { $mapA[$p[3]] = $l } } }
    foreach ($l in Get-Content $B) { if ($l) { $p = $l.Split("`t"); if ($p.Count -ge 4) { $mapB[$p[3]] = $l } } }
    $out = [System.Collections.Generic.List[string]]::new()
    $mismatch = 0
    foreach ($k in ($mapA.Keys | Sort-Object)) {
        if (-not $mapB.ContainsKey($k)) { $out.Add("ONLY_IN_$LabelA`t$k"); $mismatch++ }
        elseif ($mapA[$k] -ne $mapB[$k]) { $out.Add("DIFFER`t$k`t$($mapA[$k])`tVS`t$($mapB[$k])"); $mismatch++ }
    }
    foreach ($k in ($mapB.Keys | Sort-Object)) {
        if (-not $mapA.ContainsKey($k)) { $out.Add("ONLY_IN_$LabelB`t$k"); $mismatch++ }
    }
    $out.Add("SUMMARY`tentriesA=$($mapA.Count) entriesB=$($mapB.Count) mismatches=$mismatch")
    return $out
}

# Normalize a file-list text (from any tool) into sorted unique paths.
function Normalize-FileList {
    param([string[]]$Lines)
    $paths = foreach ($l in $Lines) {
        $t = $l.Trim()
        if (-not $t) { continue }
        # driver/tool stderr summary lines are not paths
        if ($t -match '^(files|extracted|inner|validations|built|contentId|entryCount|EXIT|COMMAND|START|END|DURATION|====|===):?') { continue }
        # strip a leading D/F marker and sizes, keep the path part
        if ($t -match '^(D|F)\s+(.*)$') { $t = $Matches[2] }
        # strip a leading "size " for our list output ("F 123 Image0/a")
        if ($t -match '^(\d+)\s+(.+)$') { $t = $Matches[2] }
        $t.Trim()
    }
    return @($paths | Where-Object { $_ -and -not $_.StartsWith('#') } | Sort-Object -Unique)
}
