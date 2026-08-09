@echo off
setlocal enabledelayedexpansion
title OrbisPkgTool - Full Integration Test
cd /d "C:\Users\User\source\repos\PS4 Fake Pkg Tools"

set "ORBIS=PS4_Fake_PKG_Tools_3.87_V7\orbis-pub-cmd.exe"
set "PROJ=OrbisPkgTool\OrbisPkgTool.Cli"
set "PASS=00000000000000000000000000000000"
set "OUTDIR=%TEMP%\orbis_integration_test"
set "REPORT=%USERPROFILE%\Desktop\test_report.txt"

echo ============================================================ > "%REPORT%"
echo  OrbisPkgTool Integration Test - %date% %time% >> "%REPORT%"
echo ============================================================ >> "%REPORT%"
echo.

:: ========== STEP 0: Kill any running tools ==========
echo [0/5] Cleaning up...
taskkill /f /im orbis-pub-cmd.exe /t 2>nul
taskkill /f /im dotnet.exe /t 2>nul
rmdir /s /q "%OUTDIR%" 2>nul
mkdir "%OUTDIR%\original" 2>nul
mkdir "%OUTDIR%\rebuilt" 2>nul
mkdir "%OUTDIR%\extract_our" 2>nul
mkdir "%OUTDIR%\extract_orb" 2>nul

:: ========== STEP 1: Create test files + build reference PKG ==========
echo [1/5] Creating test project...
:: Create source files
mkdir "%OUTDIR%\src\sce_sys" 2>nul
echo Hello PS4 > "%OUTDIR%\src\eboot.bin"
echo Readme file > "%OUTDIR%\src\readme.txt"
echo param_data > "%OUTDIR%\src\sce_sys\icon0.png"

:: Create param.sfo
dotnet run --no-build --project "%PROJ%" -c Debug -- sfo create "%OUTDIR%\src\sce_sys\param.sfo" --title "Integration Test" --title-id CUSA00001 --content-id EP0001-CUSA00001_00-INTEGRATIONTEST 2>&1 >nul

:: Create orbis-compatible GP4
(
echo ^<?xml version="1.0" encoding="utf-8" standalone="yes"?^>
echo ^<psproject fmt="gp4" version="1000"^>
echo   ^<volume^>
echo     ^<volume_type^>pkg_ps4_app^</volume_type^>
echo     ^<volume_ts^>2026-08-07 12:00:00^</volume_ts^>
echo     ^<package content_id="EP0001-CUSA00001_00-INTEGRATIONTEST" passcode="00000000000000000000000000000000" storage_type="digital50" app_type="full" /^>
echo     ^<chunk_info chunk_count="1" scenario_count="1"^>
echo       ^<chunks^>^<chunk id="0" layer_no="0" label="Chunk #0" /^>^</chunks^>
echo       ^<scenarios default_id="0"^>^<scenario id="0" type="sp" initial_chunk_count="1" label="Scenario #0"^>0^</scenario^>^</scenarios^>
echo     ^</chunk_info^>
echo   ^</volume^>
echo   ^<files img_no="0"^>
echo     ^<file targ_path="sce_sys/param.sfo" orig_path="sce_sys/param.sfo" /^>
echo     ^<file targ_path="eboot.bin" orig_path="eboot.bin" /^>
echo     ^<file targ_path="readme.txt" orig_path="readme.txt" /^>
echo   ^</files^>
echo   ^<rootdir /^>
echo ^</psproject^>
) > "%OUTDIR%\test.gp4"
echo   Created test project (eboot.bin + readme.txt)
:: ========== STEP 2: Build PKG ==========
echo [2/5] Building PKG with OrbisPkgTool (pure C#)...
set "PKG=%OUTDIR%\test.pkg"
dotnet run --project "%PROJ%" -c Debug -- build "%OUTDIR%\test.gp4" "%OUTDIR%\src" --out "%PKG%" --passcode %PASS%
if not exist "%PKG%" (
    echo [FAIL] Build failed
    goto :done
)
for %%f in ("%PKG%") do set /a "SZMB=%%~zf/1048576"
echo   Built: !SZMB! MB >> "%REPORT%"

:: ========== STEP 3: Validate PKG ==========
echo [3/5] Validating with both tools...

:: -- orbis list --
echo   orbis img_file_list...
"%ORBIS%" img_file_list --passcode %PASS% "%PKG%" 2>&1 > "%TEMP%\orb_list.txt"
set "ORB_IMG=?"
set "ORB_SC0=?"
findstr "Image0" "%TEMP%\orb_list.txt" >nul 2>&1 && set "ORB_IMG=YES" || set "ORB_IMG=NO"
findstr "Sc0" "%TEMP%\orb_list.txt" >nul 2>&1 && set "ORB_SC0=YES" || set "ORB_SC0=NO"
echo   orbis list       : Image0=!ORB_IMG! Sc0=!ORB_SC0! >> "%REPORT%"
echo     !ORB_IMG! !ORB_SC0!

:: -- orbis verify --
echo   orbis img_verify...
"%ORBIS%" img_verify --passcode %PASS% "%PKG%" 2>&1 > "%TEMP%\orb_verify.txt"
set "ORB_VERIFY=PASS"
findstr /c:"ERROR" "%TEMP%\orb_verify.txt" >nul 2>&1 && set "ORB_VERIFY=FAIL"
echo   orbis verify     : !ORB_VERIFY! >> "%REPORT%"
echo     !ORB_VERIFY!

:: -- Our verify --
echo   our verify...
dotnet run --no-build --project "%PROJ%" -c Debug -- verify "%PKG%" 2>&1 > "%TEMP%\our_verify.txt"
set "OUR_VERIFY=PASS"
findstr "OK" "%TEMP%\our_verify.txt" >nul 2>&1 || set "OUR_VERIFY=FAIL"
echo   our verify       : !OUR_VERIFY! >> "%REPORT%"
echo     !OUR_VERIFY!

:: -- Our list --
echo   our list...
dotnet run --no-build --project "%PROJ%" -c Debug -- list "%PKG%" 2>&1 > "%TEMP%\our_list.txt"
set "OUR_IMG=?"
set "OUR_SC0=?"
findstr "Image0" "%TEMP%\our_list.txt" >nul 2>&1 && set "OUR_IMG=YES" || set "OUR_IMG=NO"
findstr "Sc0" "%TEMP%\our_list.txt" >nul 2>&1 && set "OUR_SC0=YES" || set "OUR_SC0=NO"
echo   our list         : Image0=!OUR_IMG! Sc0=!OUR_SC0! >> "%REPORT%"
echo     !OUR_IMG! !OUR_SC0!

:: ========== STEP 4: Extract + compare ==========
echo [4/5] Extracting with both tools...

:: our extract
echo   our extract...
dotnet run --no-build --project "%PROJ%" -c Debug -- extract --passcode %PASS% "%PKG%" "%OUTDIR%\extract_our" >nul 2>&1
set "OUR_EXT=0"
for /r "%OUTDIR%\extract_our" %%f in (*) do set /a OUR_EXT+=1
echo   our extract      : !OUR_EXT! files >> "%REPORT%"
echo     !OUR_EXT! files

:: orbis extract
echo   orbis extract...
"%ORBIS%" img_extract --passcode %PASS% "%PKG%" "%OUTDIR%\extract_orb" >nul 2>&1
set "ORB_EXT=0"
for /r "%OUTDIR%\extract_orb" %%f in (*) do set /a ORB_EXT+=1
echo   orbis extract    : !ORB_EXT! files >> "%REPORT%"
echo     !ORB_EXT! files

:: ========== STEP 5: Report ==========
echo.
echo ============================================================
echo  TEST RESULTS
echo ============================================================
echo   orbis list   : Image0=!ORB_IMG!  Sc0=!ORB_SC0!
echo   orbis verify : !ORB_VERIFY!
echo   our list     : Image0=!OUR_IMG!  Sc0=!OUR_SC0!
echo   our verify   : !OUR_VERIFY!
echo   our extract  : !OUR_EXT! files
echo   orbis extract: !ORB_EXT! files
echo.
echo   Report: %REPORT%
echo.
pause
exit /b 0

:done
echo.
echo TEST FAILED - check errors above.
pause
exit /b 1
