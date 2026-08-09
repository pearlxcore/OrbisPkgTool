@echo off
cd /d "C:\Users\User\source\repos\PS4 Fake Pkg Tools"
echo Creating OrbisPkgTool_src.zip on your desktop...
powershell -Command "$src='OrbisPkgTool';$out=[Environment]::GetFolderPath('Desktop')+'\OrbisPkgTool_src.zip';$files=Get-ChildItem $src -Recurse -File|Where-Object{$_.DirectoryName -notmatch '\\\\bin\\\\|\\\\obj\\\\|\\\\LibOrbis' -and $_.Extension -notin '.dll','.exe','.pdb'};$tmp=$env:TEMP+'\orb_src';Remove-Item $tmp -Recurse -Force -EA 0;foreach($f in $files){$r=$f.FullName.Substring($pwd.Path.Length+1);$d=Join-Path $tmp $r;$null=mkdir(Split-Path $d)-Force;Copy-Item $f.FullName $d -Force};Compress-Archive $tmp\* $out -Force;Remove-Item $tmp -Recurse -Force -EA 0;$s=[math]::Round((Get-Item $out).Length/1KB,1);$c=(Get-ChildItem $tmp -Recurse -File -EA 0).Count;Write-Host \"Done: $out ($s KB)\""
pause
