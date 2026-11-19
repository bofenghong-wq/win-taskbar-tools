@echo off
rem WorldClock launcher. ASCII-only on purpose: cmd reads bat files
rem in the system codepage (GBK on zh-CN), so Chinese text here would
rem be garbled and break the launch. The .pyw name is found via wildcard.
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
  start "" pythonw "%SCRIPT%"
  exit /b 0
)
where pyw >nul 2>nul
if %errorlevel%==0 (
  start "" pyw "%SCRIPT%"
  exit /b 0
)
where python >nul 2>nul
if %errorlevel%==0 (
  start "" python "%SCRIPT%"
  exit /b 0
)
echo [error] Python not found. Please install Python 3.9+ from python.org
pause
exit /b 1
