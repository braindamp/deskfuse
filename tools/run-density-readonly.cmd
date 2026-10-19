@echo off
setlocal
rem READ ONLY: compiles an inspection executable; it does not install or change displays.
where cl.exe >nul 2>nul
if errorlevel 1 (
  echo Requires an x64 Native Tools Command Prompt for VS 2022 ^(where cl.exe^).
  exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"
cl /nologo /W4 /EHsc /std:c++17 /DUNICODE /D_UNICODE /Fo:"%~dp0bin\df-density-readonly.obj" /Fe:"%~dp0bin\df-density-readonly.exe" "%~dp0df-density-readonly.cpp" user32.lib shcore.lib
if errorlevel 1 exit /b 1
"%~dp0bin\df-density-readonly.exe"
exit /b %errorlevel%
