@echo off
setlocal
where dotnet >nul 2>nul && (
  echo .NET SDK is already installed:
  dotnet --version
  pause
  exit /b 0
)
where winget >nul 2>nul || (
  echo [ERROR] winget was not found.
  echo Install the .NET 8 SDK manually from Microsoft, then run START_AVALONIA.cmd.
  pause
  exit /b 1
)
echo Installing Microsoft .NET 8 SDK with winget...
winget install --id Microsoft.DotNet.SDK.8 --exact --accept-package-agreements --accept-source-agreements
if errorlevel 1 (
  echo [ERROR] .NET SDK installation failed.
  pause
  exit /b 1
)
echo.
echo Installation completed. Close this window, then run START_AVALONIA.cmd.
pause
