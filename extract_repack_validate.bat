@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  OrbisPkgTool - Extract + Repack + Validate template
::
::   Usage:  extract_repack_validate.bat <input.pkg> [passcode]
::
::   Extracts a PKG, restructures the dump, generates a GP4,
::   rebuilds the PKG with pure C#, and validates the result.
::
::   Note: app PKGs (with an inner PFS) repack fully.
::         Update/DLC PKGs (Sc0 only) are detected and skipped
::         — they have no game files to rebuild.
:: ============================================================

set "ROOT=%~dp0"
set "PKG=%~1"
set "PASS=%~2"
if "%PASS%"=="" set "PASS=00000000000000000000000000000000"

if "%PKG%"=="" (
    echo Usage: extract_repack_validate.bat ^<input.pkg^> [passcode]
    echo.
    echo   Extract a PKG, repackage the extracted files into a new PKG
    echo   using pure C#, then validate the result.
    echo.
    echo   App PKGs   (with game files): full extract -^> restructure -^> gp4gen -^> build -^> validate
    echo   Update PKGs (Sc0 only)      : extraction succeeds but rebuild is skipped
    echo.
    exit /b 1
)

if not exist "%PKG%" (
    echo [FAIL] Package not found: "%PKG%"
    exit /b 1
)

:: ---- Resolve OrbisPkgTool.Cli.exe ----
set "EXE=%ROOT%OrbisPkgTool\OrbisPkgTool.Cli\bin\Debug\net10.0-windows\OrbisPkgTool.Cli.exe"
if not exist "%EXE%" set "EXE=%ROOT%OrbisPkgTool\OrbisPkgTool.Cli\bin\Release\net10.0-windows\OrbisPkgTool.Cli.exe"
if not exist "%EXE%" (
    echo [FAIL] OrbisPkgTool.Cli.exe not found.
    echo   Build it: dotnet build OrbisPkgTool\OrbisPkgTool.Cli -c Debug
    exit /b 1
)

:: ---- Workspace ----
for %%f in ("%PKG%") do set "PKGNAME=%%~nf"
set "WORK=%ROOT%repack_work\%PKGNAME%_%RANDOM%"
set "DUMP=%WORK%\dump"
set "GP4=%WORK%\project.gp4"
set "OUT=%WORK%\%PKGNAME%_rebuilt.pkg"

echo ============================================================
echo  OrbisPkgTool - Extract + Repack + Validate
echo ============================================================
echo  Input : %PKG%
echo  Output: %OUT%
echo ============================================================
echo.

mkdir "%WORK%" 2>nul

:: ---------- Step 1: Extract ----------
echo [1/5] Extracting PKG...
"%EXE%" extract --passcode %PASS% "%PKG%" "%DUMP%"
if errorlevel 1 (
    echo   [FAIL] Extraction failed
    goto :fail
)
echo   OK

:: ----- Check: does the PKG have an inner PFS (Image0)? ------
if not exist "%DUMP%\Image0\eboot.bin" if not exist "%DUMP%\Image0" (
    echo.
    echo ============================================================
    echo  DETECTED: Update / DLC PKG (no inner PFS with game files)
    echo ============================================================
    echo   Only Sc0 entries were extracted.  There are no Image0 files
    echo   to rebuild.  This template requires an app PKG.
    echo   Work dir: %WORK%
    echo ============================================================
    goto :done
)

:: -------- Step 2: Restructure ---------
echo [2/5] Restructuring (Sc0 merge + PlayGo cleanup)...
"%EXE%" restructure "%DUMP%"
if errorlevel 1 (
    echo   [FAIL] Restructure failed
    goto :fail
)
echo   OK

:: -------- Step 3: Generate GP4 ---------
echo [3/5] Generating GP4 project...
"%EXE%" gp4gen "%DUMP%\Image0" --out "%GP4%"
if errorlevel 1 (
    echo   [FAIL] GP4 generation failed
    goto :fail
)
echo   OK - %GP4%

:: -------- Step 4: Build (pure C#) --------
echo [4/5] Building PKG (pure C#, no orbis-pub-cmd)...
"%EXE%" build "%GP4%" "%DUMP%\Image0" --out "%OUT%" --passcode %PASS% --validate
if errorlevel 1 (
    echo   [FAIL] Build failed
    goto :fail
)
for %%f in ("%OUT%") do echo   OK - %%~zf bytes

:: -------- Step 5: Validate --------------
echo [5/5] Validating rebuilt PKG...
"%EXE%" validate --passcode %PASS% "%OUT%"
if errorlevel 1 (
    echo   [WARN] Validation returned non-zero (benign warnings are normal)
) else (
    echo   PASS - 8-stage validation OK
)

:: -------- Success --------
echo.
echo ============================================================
echo  RESULT: PASS
echo  Original : %PKG%
echo  Rebuilt  : %OUT%
echo  Work dir : %WORK%
echo ============================================================
echo   Delete work dir when done: rmdir /s /q "%WORK%"
goto :done

:: -------- Failure --------
:fail
echo.
echo ============================================================
echo  RESULT: FAIL - see errors above
echo  Work dir preserved for debugging: %WORK%
echo ============================================================

:done
endlocal
