@echo off
setlocal enabledelayedexpansion
title OrbisPkgTool - Digimon Rebuild + Cross-Validation Prep
cd /d "C:\Users\User\source\repos\PS4 Fake Pkg Tools"

set "ROOT=C:\Users\User\source\repos\PS4 Fake Pkg Tools"
set "WORK=%ROOT%\digimon_work"
set "PASS=00000000000000000000000000000000"
set "ORBIS=%ROOT%\PS4_Fake_PKG_Tools_3.87_V7\orbis-pub-cmd.exe"
set "PKG=C:\Users\User\source\repos\PS4 Fake Pkg Tools\Digimon World： Next Order [CUSA05392] 00 - Base.pkg"
set "OUT=%WORK%\digi_rebuilt.pkg"

echo ============================================================
echo  OrbisPkgTool - Digimon Rebuild (stable paths in digimon_work)
echo ============================================================
echo.

if not exist "%WORK%\dump\Image0\eboot.bin" (
    echo [1/4] Extracting original PKG (6.9 GB, ~6 min)...
    call dotnet build "%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli" -c Debug --nologo >nul 2>&1
    "%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe" extract --passcode %PASS% "%PKG%" "%WORK%\dump"
    if not exist "%WORK%\dump\Image0\eboot.bin" (
        echo [FAIL] Extract failed
        pause
        exit /b 1
    )
) else (
    echo [1/4] Dump already extracted
)

echo [2/4] Restructuring (Sc0 -^> Image0/sce_sys, clean PlayGo)...
"%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe" restructure "%WORK%\dump" >nul 2>&1

echo [3/4] Generating GP4...
"%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe" gp4gen "%WORK%\dump\Image0" --title "Digimon World" --title-id CUSA05392 --out "%WORK%\project.gp4" >nul 2>&1
if not exist "%WORK%\project.gp4" (
    echo [FAIL] GP4 generation failed
    pause
    exit /b 1
)

echo [4/4] Building PKG (pure C#, ~11 min)...
"%ROOT%\OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe" build "%WORK%\project.gp4" "%WORK%\dump\Image0" --out "%OUT%" --passcode %PASS% --validate
if not exist "%OUT%" (
    echo [FAIL] Build failed
    pause
    exit /b 1
)
for %%f in ("%OUT%") do echo   Built: %%~zf bytes

echo.
echo ============================================================
echo  DONE - now run the cross-validation:
echo    dotnet build .\CrossValidation\OpenOrbisDriver -c Debug
echo    .\CrossValidation\Run-QuickValidation.ps1
echo    .\CrossValidation\Run-FullValidation.ps1
echo ============================================================
pause
endlocal
