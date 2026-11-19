@echo off
rem WorldClock restart: stops any running instance (same or old path) and starts this one.
rem ASCII-only on purpose (cmd reads bat files in the system codepage).
cd /d "%~dp0"
set "SCRIPT="
for %%f in (*.pyw) do set "SCRIPT=%%f"
if not defined SCRIPT (
  echo [error] no .pyw script found in this folder
  pause
  exit /b 1
)
where pythonw >nul 2>nul
if %errorlevel%==0 (
  start "" pythonw "%SCRIPT%" --restart
  exit /b 0
)
where pyw >nul 2>nul
if %errorlevel%==0 (
  start "" pyw "%SCRIPT%" --restart
  exit /b 0
)
echo [error] Python not found.
pause
exit /b 1
