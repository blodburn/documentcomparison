@echo off
setlocal
cd /d "%~dp0"
dotnet run --project tests\DocumentCompare.Regression\DocumentCompare.Regression.csproj -c Release
if errorlevel 1 exit /b %errorlevel%
echo.
echo All DocumentCompare regression tests passed.
