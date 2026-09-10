@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "PY="
where py >nul 2>nul
if %errorlevel%==0 (
  py -3.13 -c "import sys" >nul 2>nul && set "PY=py -3.13"
  if not defined PY py -3.12 -c "import sys" >nul 2>nul && set "PY=py -3.12"
  if not defined PY py -3.11 -c "import sys" >nul 2>nul && set "PY=py -3.11"
  if not defined PY py -3 -c "import sys" >nul 2>nul && set "PY=py -3"
)
if not defined PY (
  where python >nul 2>nul && set "PY=python"
)
if not defined PY (
  echo [ERROR] Python 3.11+ is required to BUILD the comparison engine.
  echo         Python is not required to RUN the completed Avalonia release.
  pause
  exit /b 1
)

if not defined ENGINE_DIST_DIR set "ENGINE_DIST_DIR=ENGINE_DEV"
if not defined ENGINE_WORK_DIR set "ENGINE_WORK_DIR=.engine_build"

if not exist ".enginevenv\Scripts\python.exe" %PY% -m venv .enginevenv || exit /b 1
call ".enginevenv\Scripts\activate.bat" || exit /b 1
python -m pip install --upgrade pip || exit /b 1
python -m pip install -r requirements.txt "pyinstaller>=6.10" || exit /b 1
if exist "%ENGINE_DIST_DIR%" rmdir /s /q "%ENGINE_DIST_DIR%"
if exist "%ENGINE_WORK_DIR%" rmdir /s /q "%ENGINE_WORK_DIR%"
mkdir "%ENGINE_DIST_DIR%" >nul 2>nul
mkdir "%ENGINE_WORK_DIR%" >nul 2>nul
pyinstaller --specpath "%ENGINE_WORK_DIR%" --noconfirm --clean --onefile --console --exclude-module tkinter --exclude-module tkinterdnd2 --name "DocumentCompare.Engine" --distpath "%ENGINE_DIST_DIR%" --workpath "%ENGINE_WORK_DIR%" engine_bridge.py || exit /b 1
if not exist "%ENGINE_DIST_DIR%\DocumentCompare.Engine.exe" (
  echo [ERROR] Comparison engine executable was not created.
  exit /b 1
)
echo [OK] Engine: %CD%\%ENGINE_DIST_DIR%\DocumentCompare.Engine.exe
exit /b 0
