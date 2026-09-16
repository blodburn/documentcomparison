@echo off
setlocal EnableExtensions
cd /d "%~dp0"

where dotnet >nul 2>nul || (
  echo [ERROR] .NET 8 SDK or newer is required.
  echo         Run INSTALL_DOTNET8_SDK.cmd, then run this file again.
  pause
  exit /b 1
)

echo [1/2] Building native C# comparison engine + Avalonia UI...
dotnet build "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" -c Release || exit /b 1

echo [2/2] Starting DocumentCompare...
dotnet run --project "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" -c Release --no-build
if errorlevel 1 (
  echo.
  echo [ERROR] DocumentCompare failed. Copy this entire window and send it back.
  pause
  exit /b 1
)
exit /b 0
