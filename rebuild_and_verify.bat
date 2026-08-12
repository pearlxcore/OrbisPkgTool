@echo off
setlocal enabledelayedexpansion
:: ============================================================
::  OrbisPkgTool repack + verify template
::  Usage: rebuild_and_verify.bat <input.pkg> [passcode]
::  Default passcode: 00000000000000000000000000000000
:: ============================================================

set "ROOT=%~dp0"
set "EXE=%ROOT%OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe"
set "ORBIS=%ROOT%PS4_Fake_PKG_Tools_3.87_V7\orbis-pub-cmd.exe"

if not exist "%EXE%" (
    echo [FAIL] OrbisPkgTool.Cli.exe not found. Build it: dotnet build OrbisPkgTool\OrbisPkgTool.Cli -c Debug
    exit /b 1
)

set "PKG=%~1"
if "%PKG%"=="" (
    echo Usage: rebuild_and_verify.bat ^<input.pkg^> [passcode]
    exit /b 1
)
if not exist "%PKG%" (
    echo [FAIL] Package not found: %PKG%
    exit /b 1
)

set "PASS=%~2"
if "%PASS%"=="" set "PASS=00000000000000000000000000000000"

for %%f in ("%PKG%") do set "PKGNAME=%%~nf"
set "WORK=%TEMP%\pkg_rebuild_%PKGNAME%_%RANDOM%"
set "OUT=%PKG%_rebuilt.pkg"

echo ============================================================
echo  OrbisPkgTool - REBUILD + VERIFY
echo  Input : %PKG%
echo  Output: %OUT%
echo  Work  : %WORK%
echo ============================================================
echo.

:: ---- Build the CLI ----
echo [BUILD] Building CLI...
dotnet build "%ROOT%OrbisPkgTool\OrbisPkgTool.Cli" -c Debug --nologo >nul 2>&1
if errorlevel 1 (
    echo [FAIL] Build failed
    exit /b 1
)

:: ---- Step 1: Repack ----
echo [1/2] Repacking PKG (no validation)...
"%EXE%" repack "%PKG%" --out "%OUT%" --passcode %PASS% --pfsc-mode compressed
if errorlevel 1 (
    echo [FAIL] Repack failed
    exit /b 1
)
echo   Built: %OUT%

:: ---- Step 2: Verify with orbis ----
echo.
echo [2/2] Verifying with orbis-pub-cmd...
if not exist "%ORBIS%" (
    echo   [SKIP] orbis-pub-cmd not found at %ORBIS%
    goto :done
)

echo   === orbis entry counts ===
for %%f in ("%PKG%") do echo   Original: %%f
"%ORBIS%" img_file_list --passcode %PASS% "%PKG%" 2>&1 | find /c "Image0"
echo   entries

for %%f in ("%OUT%") do echo   Rebuilt: %%f
"%ORBIS%" img_file_list --passcode %PASS% "%OUT%" 2>&1 | find /c "Image0"
echo   entries

echo.
echo   === orbis verify comparison ===
echo   Original:
"%ORBIS%" img_verify --passcode %PASS% "%PKG%" 2>&1 | findstr /C:"Error" /C:"Number of Error(s)"
echo.
echo   Rebuilt:
"%ORBIS%" img_verify --passcode %PASS% "%OUT%" 2>&1 | findstr /C:"Error" /C:"Number of Error(s)"

:done
echo.
echo ============================================================
echo  DONE
echo  Rebuilt: %OUT%
echo  Work:    %WORK%
echo ============================================================
endlocal
