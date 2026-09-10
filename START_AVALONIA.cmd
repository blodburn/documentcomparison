@echo off
setlocal EnableExtensions
cd /d "%~dp0"

where dotnet >nul 2>nul || (
  echo [ERROR] .NET 8 SDK or newer is required.
  echo         Run INSTALL_DOTNET8_SDK.cmd, then run this file again.
  pause
  exit /b 1
)

echo [SDK]
dotnet --version
echo.

set "NEED_ENGINE=0"
if not exist "ENGINE_DEV\DocumentCompare.Engine.exe" set "NEED_ENGINE=1"
if "%NEED_ENGINE%"=="0" (
  powershell -NoProfile -Command "$exe=(Get-Item 'ENGINE_DEV\DocumentCompare.Engine.exe').LastWriteTimeUtc; if ((Get-Item 'engine_bridge.py').LastWriteTimeUtc -gt $exe -or (Get-Item 'app.py').LastWriteTimeUtc -gt $exe) { exit 1 } else { exit 0 }"
  if errorlevel 1 set "NEED_ENGINE=1"
)
if "%NEED_ENGINE%"=="1" (
  echo [1/3] Building updated Python comparison engine sidecar...
  call BUILD_ENGINE_SIDECAR.cmd || exit /b 1
) else (
  echo [1/3] Engine sidecar is current.
)

set "DOCUMENT_COMPARE_ENGINE=%CD%\ENGINE_DEV\DocumentCompare.Engine.exe"
echo [2/3] Cleaning Avalonia intermediate output...
if exist "avalonia\DocumentCompare.Avalonia\bin" rmdir /s /q "avalonia\DocumentCompare.Avalonia\bin"
if exist "avalonia\DocumentCompare.Avalonia\obj" rmdir /s /q "avalonia\DocumentCompare.Avalonia\obj"

echo [3/3] Starting Avalonia UI...
dotnet run --project "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" -c Release
if errorlevel 1 (
  echo.
  echo [ERROR] Avalonia build/run failed. Copy this entire window and send it back.
  pause
  exit /b 1
)
exit /b 0
