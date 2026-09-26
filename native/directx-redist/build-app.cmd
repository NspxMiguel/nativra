@echo off
rem Copies of the redist shims for the Nativra package: static C runtime
rem (/MT), because the console carries only the _app build of the desktop
rem runtime, and /APPCONTAINER, as every DLL in an app package must be.
rem Output goes to uwp\Redist, which Kiosk.csproj packages at the root,
rem where LoadPackagedLibrary finds a DLL by name.
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "DXR_VS=%%i"
call "%DXR_VS%\VC\Auxiliary\Build\vcvarsall.bat" x64
if errorlevel 1 exit /b 1
cd /d "%~dp0"
set "OUT=%~dp0..\..\uwp\Redist"
if not exist "%OUT%" mkdir "%OUT%"
if not exist obj-app mkdir obj-app

cl /nologo /LD /EHsc /O2 /MT /DUNICODE /D_UNICODE xaudio2_7\xaudio2_7.cpp /Fo:obj-app\ /Fe:"%OUT%\xaudio2_7.dll" /link /APPCONTAINER /DEF:xaudio2_7\xaudio2_7.def xaudio2.lib ole32.lib
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /O2 /MT x3daudio1_7\x3daudio1_7.cpp /Fo:obj-app\ /Fe:"%OUT%\x3daudio1_7.dll" /link /APPCONTAINER /DEF:x3daudio1_7\x3daudio1_7.def xaudio2.lib
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /O2 /MT xapofx1_5\xapofx1_5.cpp /Fo:obj-app\ /Fe:"%OUT%\xapofx1_5.dll" /link /APPCONTAINER /DEF:xapofx1_5\xapofx1_5.def xaudio2.lib
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /O2 /MT d3dx9_43\d3dx9_43.cpp /Fo:obj-app\ /Fe:"%OUT%\d3dx9_43.dll" /link /APPCONTAINER
if errorlevel 1 exit /b 1
del /q "%OUT%\*.exp" "%OUT%\*.lib" 2>nul
dir /b "%OUT%"
