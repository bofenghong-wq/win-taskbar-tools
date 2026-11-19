@echo off
rem Builds DockFolder.exe with the C# compiler that ships with Windows
rem (.NET Framework 4.x) - nothing to install.
rem ASCII-only on purpose: cmd reads .bat files in the system codepage.
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo [error] .NET Framework 4 compiler not found.
  pause
  exit /b 1
)

rem stop a running copy so the exe can be replaced
if exist DockFolder.exe (
  start "" /wait DockFolder.exe --exit
  ping -n 2 127.0.0.1 >nul
)

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /utf8output ^
  /out:DockFolder.exe /win32icon:src\app.ico /win32manifest:src\app.manifest ^
  /lib:"%FW%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:UIAutomationClient.dll /r:UIAutomationTypes.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll /r:System.Core.dll ^
  src\*.cs > build.log 2>&1
set ERR=%errorlevel%
type build.log
if not "%ERR%"=="0" (
  echo.
  echo [error] build failed, see build.log
  pause
  exit /b 1
)
echo.
echo Build OK: DockFolder.exe
if /i not "%~1"=="/nostart" start "" DockFolder.exe
exit /b 0
