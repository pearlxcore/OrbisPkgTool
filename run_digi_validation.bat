@echo off
setlocal enabledelayedexpansion
title OrbisPkgTool - Digimon Rebuild Validation
cd /d "C:\Users\User\source\repos\PS4 Fake Pkg Tools"

set "ROOT=C:\Users\User\source\repos\PS4 Fake Pkg Tools"
set "SRC=%TEMP%\digi_full"
set "PASS=00000000000000000000000000000000"
set "ORBIS=%ROOT%\PS4_Fake_PKG_Tools_3.87_V7\orbis-pub-cmd.exe"
set "OUT=%SRC%\digi_FINAL.pkg"
set "REPORT=%USERPROFILE%\Desktop\digi_validation_report.txt"
set "FAIL=0"

echo ============================================================
echo  OrbisPkgTool - Digimon 11.9GB Rebuild + orbis Validation
echo ============================================================
echo.

:: ---------- 0. Fresh build ----------
echo [0/5] Building OrbisPkgTool (Debug)...
call dotnet build "%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli" -c Debug --nologo >nul 2>&1
if errorlevel 1 (
    echo   [FAIL] dotnet build failed
    set FAIL=1
    goto :done
)
set "EXE=%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe"
echo   OK - %EXE%
echo.

if not exist "%SRC%\project.gp4" (
    echo [FAIL] %SRC%\project.gp4 not found
    set FAIL=1
    goto :done
)

:: ---------- 1. Build the PKG (pure C#, ~11 min) ----------
echo [1/5] Building Digimon PKG (pure C#, ~11 min)...
"%EXE%" build "%SRC%\project.gp4" "%SRC%\Image0" --out "%OUT%" --passcode %PASS%
if errorlevel 1 (
    echo   [FAIL] build command failed
    set FAIL=1
    goto :done
)
for %%f in ("%OUT%") do echo   Built: %%~zf bytes
echo.

:: ---------- 2. Our verify ----------
echo [2/5] Our verify...
"%EXE%" verify "%OUT%" | findstr "Integrity"
echo.

:: ---------- 3. orbis img_file_list (THE test) ----------
echo [3/5] orbis img_file_list...
"%ORBIS%" img_file_list --passcode %PASS% "%OUT%" > "%TEMP%\orb_list.txt" 2>&1
set "ORB_ERR="
findstr /c:"not valid" /c:"Error" "%TEMP%\orb_list.txt" >nul 2>&1 && set "ORB_ERR=YES"
if defined ORB_ERR (
    echo   [FAIL] orbis rejected the package:
    type "%TEMP%\orb_list.txt"
    set FAIL=1
) else (
    findstr /c:"Image0/eboot.bin" "%TEMP%\orb_list.txt" >nul 2>&1
    if errorlevel 1 (
        echo   [FAIL] Image0/eboot.bin not listed
        type "%TEMP%\orb_list.txt"
        set FAIL=1
    ) else (
        set /a "LINES=0"
        for /f %%a in ('type "%TEMP%\orb_list.txt" ^| find /c /v ""') do set LINES=%%a
        echo   [PASS] orbis listed !LINES! entries:
        type "%TEMP%\orb_list.txt"
    )
)
echo.

:: ---------- 4. orbis img_verify ----------
echo [4/5] orbis img_verify (tail)...
"%ORBIS%" img_verify --passcode %PASS% "%OUT%" > "%TEMP%\orb_verify.txt" 2>&1
findstr /c:"Result" "%TEMP%\orb_verify.txt"
echo.

:: ---------- 5. Our list ----------
echo [5/5] Our list (count)...
"%EXE%" list "%OUT%" > "%TEMP%\our_list.txt" 2>&1
set /a "LINES=0"
for /f %%a in ('type "%TEMP%\our_list.txt" ^| find /c /v ""') do set LINES=%%a
echo   Our list: !LINES! lines
echo.

:done
echo ============================================================
if "%FAIL%"=="1" (
    echo  RESULT: FAIL - see output above
) else (
    echo  RESULT: PASS - orbis-pub-cmd accepts our rebuilt Digimon PKG
)
echo ============================================================
echo   Report: %REPORT%
(
    echo Digimon validation report - %date% %time%
    echo Build: %OUT%
    if "%FAIL%"=="1" (echo RESULT: FAIL) else (echo RESULT: PASS)
) > "%REPORT%"
pause
endlocal
