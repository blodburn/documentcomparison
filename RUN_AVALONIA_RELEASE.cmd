@echo off
setlocal
cd /d "%~dp0"
if not exist "DEPLOY_PACKAGE\DocumentCompare\DocumentCompare.exe" (
  echo Clean distribution not found.
  echo Run BUILD_AVALONIA_RELEASE.cmd first.
  pause
  exit /b 1
)
start "" "DEPLOY_PACKAGE\DocumentCompare\DocumentCompare.exe"
