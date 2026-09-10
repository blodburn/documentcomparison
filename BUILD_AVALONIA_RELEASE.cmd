@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "DIST_ROOT=DEPLOY_PACKAGE"
set "APP_DIR=%DIST_ROOT%\DocumentCompare"
set "ZIP_FILE=%DIST_ROOT%\DocumentCompare_Windows_x64_Portable.zip"
set "PUBLISH_STAGE=.build\avalonia_publish"
set "ENGINE_DIST_DIR=.build\engine_dist"
set "ENGINE_WORK_DIR=.build\engine_work"

echo =====================================================
echo   Document Compare V5.19.4 - One File Distribution
echo =====================================================
echo.
where dotnet >nul 2>nul || (
  echo [ERROR] .NET 8 SDK or newer is required to build the release.
  pause
  exit /b 1
)

if not exist "avalonia\DocumentCompare.Avalonia\Assets\DocumentCompare.ico" (
  echo [ERROR] Application icon is missing: avalonia\DocumentCompare.Avalonia\Assets\DocumentCompare.ico
  pause
  exit /b 1
)

echo [1/7] Cleaning previous temporary/distribution folders...
if exist ".build" rmdir /s /q ".build"
if exist "%DIST_ROOT%" rmdir /s /q "%DIST_ROOT%"
mkdir ".build" || goto :fail
mkdir "%PUBLISH_STAGE%" || goto :fail
mkdir "%APP_DIR%" || goto :fail

echo [2/7] Building private comparison engine payload...
call BUILD_ENGINE_SIDECAR.cmd || goto :fail
if not exist "%ENGINE_DIST_DIR%\DocumentCompare.Engine.exe" (
  echo [ERROR] Embedded engine payload is missing.
  goto :fail
)

echo [3/7] Publishing Avalonia + embedded engine as one self-contained EXE...
dotnet restore "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" || goto :fail
dotnet publish "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishTrimmed=false ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "%PUBLISH_STAGE%" || goto :fail

if not exist "%PUBLISH_STAGE%\DocumentCompare.exe" (
  echo [ERROR] Avalonia single EXE was not created.
  goto :fail
)

echo [4/7] Creating runtime-only distribution folder...
copy /y "%PUBLISH_STAGE%\DocumentCompare.exe" "%APP_DIR%\DocumentCompare.exe" >nul || goto :fail

if not exist "%APP_DIR%\DocumentCompare.exe" (
  echo [ERROR] Final DocumentCompare.exe is missing.
  goto :fail
)

echo [5/7] Verifying that the distribution contains exactly one file...
for /d %%D in ("%APP_DIR%\*") do (
  if exist "%%~fD" (
    echo [ERROR] Unexpected directory in one-file distribution: %%~fD
    goto :fail
  )
)
for %%F in ("%APP_DIR%\*") do (
  if /I not "%%~nxF"=="DocumentCompare.exe" (
    echo [ERROR] Unexpected file in one-file distribution: %%~fF
    goto :fail
  )
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$items=@(Get-ChildItem -LiteralPath '%APP_DIR%' -Force); if($items.Count -ne 1 -or $items[0].Name -ne 'DocumentCompare.exe'){ exit 1 }" || (
  echo [ERROR] Distribution verification failed: expected DocumentCompare.exe only.
  goto :fail
)

echo [6/7] Verifying embedded-engine contract in source/project...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=Get-Content -Raw 'avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj'; $c=Get-Content -Raw 'avalonia\DocumentCompare.Avalonia\Engine\PythonBridgeComparisonEngine.cs'; if($p -notmatch 'DocumentCompare\.Embedded\.DocumentCompare\.Engine\.exe' -or $c -notmatch 'GetManifestResourceStream'){ exit 1 }" || (
  echo [ERROR] Embedded engine integration is missing.
  goto :fail
)

echo [7/7] Creating portable ZIP containing the single EXE...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%APP_DIR%\DocumentCompare.exe' -DestinationPath '%ZIP_FILE%' -Force" || goto :fail

echo.
echo =====================================================
echo BUILD COMPLETE
echo =====================================================
echo.
echo Final distribution contains exactly one visible runtime file:
echo   %CD%\%APP_DIR%\DocumentCompare.exe
echo.
echo Portable ZIP:
echo   %CD%\%ZIP_FILE%
echo.
echo NOTE: Avalonia/.NET runtime + Python comparison engine are embedded in DocumentCompare.exe.
echo       On first run, the private Python engine is extracted automatically into:
echo       %%LOCALAPPDATA%%\DocumentCompare\runtime\
echo       Nothing else needs to sit beside DocumentCompare.exe.
echo       The neon C icon is embedded in the EXE and window/taskbar.
echo.
choice /C YN /N /M "Open the distribution folder? [Y/N] "
if errorlevel 2 goto :done
explorer "%CD%\%DIST_ROOT%"
:done
pause
exit /b 0

:fail
echo.
echo [BUILD FAILED] One-file distribution was not completed.
pause
exit /b 1
