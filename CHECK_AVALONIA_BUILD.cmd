@echo off
setlocal EnableExtensions
cd /d "%~dp0"
where dotnet >nul 2>nul || (
  echo [ERROR] dotnet not found.
  pause
  exit /b 1
)
echo ==== dotnet --info ====
dotnet --info
echo.
echo ==== clean ====
dotnet clean "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" -c Release
if errorlevel 1 goto :fail
echo.
echo ==== build ====
dotnet build "avalonia\DocumentCompare.Avalonia\DocumentCompare.Avalonia.csproj" -c Release --no-incremental
if errorlevel 1 goto :fail
echo.
echo [OK] Avalonia build passed.
pause
exit /b 0
:fail
echo.
echo [FAILED] Copy this window and send it back.
pause
exit /b 1
