@echo off
setlocal enabledelayedexpansion
:: ============================================================
::  OrbisPkgTool - Rebuild Bloodborne 60FPS-1080-DEBUG patch
::  Run this in a regular cmd window (not PowerShell).
:: ============================================================

set "ROOT=C:\Users\User\source\repos\PS4 Fake Pkg Tools"
set "EXE=%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe"
set "PKG=C:\Users\User\Downloads\Compressed\BLBN000109-60FPS-1080-DEBUG\UP9000-CUSA00900_00-BLOODBORNE000000-A0109-V0100-60FPS-1080-DEBUG.pkg"
set "OUT=%PKG%_rebuilt.pkg"
set "PASS=00000000000000000000000000000000"

echo.
echo ============================================================
echo  Bloodborne 60FPS-1080-DEBUG patch rebuild
echo  Input : %PKG%
echo  Output: %OUT%
echo ============================================================
echo.

:: ---- Build the CLI ----
echo [BUILD] Building CLI...
dotnet build "%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli" -c Debug --nologo >nul 2>&1
if errorlevel 1 (
    echo [FAIL] Build failed
    pause
    exit /b 1
)
echo   OK

:: ---- Rebuild ----
echo.
echo [REPACK] Rebuilding (extract -> restructure -> gp4gen -> build)...
"%EXE%" repack "%PKG%" --out "%OUT%" --passcode %PASS% --pfsc-mode compressed
if errorlevel 1 (
    echo [FAIL] Repack failed - see errors above
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  DONE
echo  Rebuilt: %OUT%
echo ============================================================
echo.
pause
endlocal
