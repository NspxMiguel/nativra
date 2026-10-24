@echo off
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b %errorlevel%
if not exist "%~dp0bin" mkdir "%~dp0bin"
cl /nologo /W4 /WX /std:c++17 /EHsc /LD /Od "%~dp0caller.cpp" /Fo"%~dp0bin\caller.obj" /link /OUT:"%~dp0bin\steam-abi.dll"
exit /b %errorlevel%
