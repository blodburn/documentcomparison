@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "DIST_ROOT=DEPLOY_PACKAGE"
set "APP_DIR=%DIST_ROOT%\DocumentCompare"
set "ZIP_FILE=%DIST_ROOT%\DocumentCompare_Windows_x64_Portable.zip"
set "PUBLISH_STAGE=.build\avalonia_publish"

echo =====================================================
echo   Document Compare V5.20.5 - Native C# One File Build
echo =====================================================
echo.
where dotnet >nul 2>nul || (
  echo [ERROR] .NET 8 SDK or newer is required to build the release.
  pause
  exit /b 1
)

if not exist "avalonia\DocumentCompare.Avalonia\Assets\DocumentCompare.ico" (
  echo [ERROR] Application icon is missing.
  pause
  exit /b 1
)

echo [1/5] Cleaning previous output...
if exist ".build" rmdir /s /q ".build"
if exist "%DIST_ROOT%" rmdir /s /q "%DIST_ROOT%"
mkdir "%PUBLISH_STAGE%" || goto :fail
mkdir "%APP_DIR%" || goto :fail

echo [2/5] Restoring native C# application...
dotnet restore "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" || goto :fail

echo [3/5] Publishing self-contained Windows x64 single EXE...
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

if not exist "%PUBLISH_STAGE%\DocumentCompare.exe" goto :fail
copy /y "%PUBLISH_STAGE%\DocumentCompare.exe" "%APP_DIR%\DocumentCompare.exe" >nul || goto :fail

echo [4/5] Verifying one-file distribution...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$items=@(Get-ChildItem -LiteralPath '%APP_DIR%' -Force); if($items.Count -ne 1 -or $items[0].Name -ne 'DocumentCompare.exe'){ exit 1 }" || goto :fail

echo [5/5] Creating portable ZIP...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%APP_DIR%\DocumentCompare.exe' -DestinationPath '%ZIP_FILE%' -Force" || goto :fail

echo.
echo BUILD COMPLETE
echo   %CD%\%APP_DIR%\DocumentCompare.exe
echo   %CD%\%ZIP_FILE%
echo.
echo Native C# comparison engine is compiled directly into DocumentCompare.exe.
echo No Python runtime or sidecar engine is used.
pause
exit /b 0

:fail
echo.
echo [BUILD FAILED] One-file distribution was not completed.
pause
exit /b 1
