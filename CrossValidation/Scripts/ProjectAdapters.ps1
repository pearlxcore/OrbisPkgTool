# ProjectAdapters.ps1 — dot-source after Environment.ps1.
# Build Sony-format and our-format GP4 projects from an extracted dump folder.
# Dump layout expected (as produced by OrbisPkgTool extract or restructure):
#   <root>/Image0/...     game files (sce_sys inside)
#   <root>/Sc0/...        optional separate Sc0 (flattened into sce_sys if present)

# Read param.sfo metadata with a minimal SFO parser (magic 0x00PSF, key table,
# data table). Matches keys in the KEY TABLE, reads values from the DATA TABLE.
function Read-ParamSfoMetadata {
    param([string]$SfoPath)
    $meta = @{ content_id = "EP0001-CUSA00001_00-REBUILD0000000001"; title_id = "CUSA00001"; title = "Rebuild" }
    if (-not (Test-Path $SfoPath)) { return $meta }
    $b = [System.IO.File]::ReadAllBytes($SfoPath)
    if ($b.Length -lt 20 -or $b[0] -ne 0 -or $b[1] -ne [byte][char]'P' -or $b[2] -ne [byte][char]'S' -or $b[3] -ne [byte][char]'F') { return $meta }
    $keyTable = [BitConverter]::ToUInt32($b, 8)
    $dataTable = [BitConverter]::ToUInt32($b, 12)
    $count = [BitConverter]::ToUInt32($b, 16)
    # Two index-entry layouts exist:
    #  - Sony standard (32B): name[16] embedded, alignment(2)@16, type(2)@18,
    #    maxLen(4)@20, dataLen(4)@24, dataOffset(4)@28. Index starts at
    #    keyTableOffset.
    #  - OrbisPkgTool writer (16B): keyOffset(2)@0, type(2)@2, len(4)@4,
    #    maxLen(4)@8, dataOffset(4)@12; index starts at 0x14, names packed at
    #    keyTableOffset.
    # Detect by the first index byte at 0x14: 0x00 (our keyOffset 0) vs ASCII.
    $isSonyLayout = $b[0x14] -ne 0
    # Sony: index entries at keyTableOffset. Ours: index entries at 0x14
    # (the keyTableOffset field points at the packed name area).
    $indexBase = if ($isSonyLayout) { $keyTable } else { 0x14 }
    for ($i = 0; $i -lt $count; $i++) {
        $kOff = $indexBase + $i * $(if ($isSonyLayout) { 32 } else { 16 })
        if ($isSonyLayout) {
            $name = ([System.Text.Encoding]::ASCII.GetString($b, $kOff, 16)).TrimEnd([char]0)
            $dataLen = [BitConverter]::ToUInt32($b, $kOff + 24)
            $dataOff = [BitConverter]::ToUInt32($b, $kOff + 28)
        } else {
            $nameOff = [BitConverter]::ToUInt16($b, $kOff)
            $name = ([System.Text.Encoding]::ASCII.GetString($b, $keyTable + $nameOff, 32)).Split([char]0)[0]
            $dataLen = [BitConverter]::ToUInt32($b, $kOff + 4)
            $dataOff = [BitConverter]::ToUInt32($b, $kOff + 12)
        }
        if ($dataLen -gt 0 -and ($dataTable + $dataOff + $dataLen) -le $b.Length) {
            $val = ([System.Text.Encoding]::ASCII.GetString($b, [int]($dataTable + $dataOff), [int]$dataLen)).TrimEnd([char]0)
            switch ($name) {
                "CONTENT_ID" { $meta.content_id = $val }
                "TITLE_ID"   { $meta.title_id = $val }
                "TITLE"      { $meta.title = $val }
            }
        }
    }
    return $meta
}

# Collect the flat file list under $image0 (paths relative to $image0).
function Get-DumpFileList {
    param([string]$Image0)
    $files = @()
    foreach ($f in Get-ChildItem -LiteralPath $Image0 -Recurse -File) {
        $files += (Get-RelPath $Image0 $f.FullName).Replace('\', '/')
    }
    return ($files | Sort-Object)
}

# Sony-format GP4: <file targ_path="..." orig_path="..."/> with package attributes.
function New-SonyGp4 {
    param(
        [string]$Image0,
        [string]$OutPath,
        [string]$ContentId = "",
        [string]$Passcode = "00000000000000000000000000000000",
        [string]$TitleId = "CUSA00001",
        [string]$Title = "Rebuild",
        [switch]$WithRootDirs
    )
    if (-not $ContentId) {
        $sfo = Join-Path $Image0 "sce_sys/param.sfo"
        $m = Read-ParamSfoMetadata $sfo
        $ContentId = $m.content_id; $TitleId = $m.title_id; $Title = $m.title
    }
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8" standalone="yes"?>')
    [void]$sb.AppendLine('<psproject fmt="gp4" version="1000">')
    [void]$sb.AppendLine('  <volume>')
    [void]$sb.AppendLine('    <volume_type>pkg_ps4_app</volume_type>')
    [void]$sb.AppendLine("    <volume_ts>$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')</volume_ts>")
    [void]$sb.AppendLine("    <package content_id=""$ContentId"" passcode=""$Passcode"" storage_type=""digital50"" app_type=""full"" />")
    [void]$sb.AppendLine('    <chunk_info chunk_count="1" scenario_count="1">')
    [void]$sb.AppendLine('      <chunks><chunk id="0" layer_no="0" label="Chunk #0" /></chunks>')
    [void]$sb.AppendLine('      <scenarios default_id="0"><scenario id="0" type="sp" initial_chunk_count="1" label="Scenario #0">0</scenario></scenarios>')
    [void]$sb.AppendLine('    </chunk_info>')
    [void]$sb.AppendLine('  </volume>')
    [void]$sb.AppendLine('  <files>')
    foreach ($rel in Get-DumpFileList $Image0) {
        $relXml = $rel -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;'
        [void]$sb.AppendLine("    <file targ_path=""$relXml"" orig_path=""$relXml"" />")
    }
    [void]$sb.AppendLine('  </files>')
    if ($WithRootDirs) {
        # OpenOrbis's BuildFSTree walks <rootdir> — enumerate every subdirectory.
        $dirs = Get-ChildItem -LiteralPath $Image0 -Recurse -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            (Get-RelPath $Image0 $_.FullName).Replace('\', '/')
        } | Sort-Object -Unique
        $sb.AppendLine('  <rootdir>') | Out-Null
        foreach ($dir in $dirs) {
            $dirXml = $dir -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;'
            [void]$sb.AppendLine("    <dir targ_name=""$dirXml"" />")
        }
        [void]$sb.AppendLine('  </rootdir>')
    } else {
        [void]$sb.AppendLine('  <rootdir />')
    }
    [void]$sb.AppendLine('</psproject>')
    Set-Content -Path $OutPath -Value $sb.ToString() -Encoding utf8
    return $OutPath
}

# Our-format GP4 (child-element form, accepted by our parser).
function New-OrbisPkgToolGp4 {
    param(
        [string]$Image0,
        [string]$OutPath,
        [string]$ContentId = "",
        [string]$TitleId = "CUSA00001",
        [string]$Title = "Rebuild"
    )
    if (-not $ContentId) {
        $sfo = Join-Path $Image0 "sce_sys/param.sfo"
        $m = Read-ParamSfoMetadata $sfo
        $ContentId = $m.content_id; $TitleId = $m.title_id; $Title = $m.title
    }
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<psproject fmt="gp4" version="1.0">')
    [void]$sb.AppendLine('  <volume>')
    [void]$sb.AppendLine('    <volume_type>pkg_ps4_app</volume_type>')
    [void]$sb.AppendLine('    <package>')
    [void]$sb.AppendLine("      <content_id>$ContentId</content_id>")
    [void]$sb.AppendLine('      <passcode></passcode>')
    [void]$sb.AppendLine('      <storage_type>digital25</storage_type>')
    [void]$sb.AppendLine('      <app_type>full</app_type>')
    [void]$sb.AppendLine('      <version>01.00</version>')
    [void]$sb.AppendLine("      <title_id>$TitleId</title_id>")
    [void]$sb.AppendLine("      <title>$Title</title>")
    [void]$sb.AppendLine('      <app_version>01.00</app_version>')
    [void]$sb.AppendLine('    </package>')
    [void]$sb.AppendLine('  </volume>')
    [void]$sb.AppendLine('  <files>')
    foreach ($rel in Get-DumpFileList $Image0) {
        $relXml = $rel -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;'
        [void]$sb.AppendLine("    <file><entry path=""$relXml"" /><orig_path>$relXml</orig_path></file>")
    }
    [void]$sb.AppendLine('  </files>')
    [void]$sb.AppendLine('</psproject>')
    Set-Content -Path $OutPath -Value $sb.ToString() -Encoding utf8
    return $OutPath
}
