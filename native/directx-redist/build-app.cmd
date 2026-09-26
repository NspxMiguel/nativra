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
rem Direct3D 9 on Direct3D 11. Imports only d3d11, d3dcompiler_47 (packaged
rem beside it) and kernel32: nothing from user32, which a natively loaded DLL
rem does not get in the console's app container.
cl /nologo /LD /EHsc /O2 /MT /std:c++17 d3d9\d3d9_main.cpp d3d9\d3d9_device.cpp d3d9\d3d9_draw.cpp ^
   d3d9\d3d9_resources.cpp d3d9\d3d9_ff.cpp d3d9\d3d9_format.cpp d3d9\dxso.cpp ^
   /Fo:obj-app\ /Fe:"%OUT%\d3d9.dll" /link /APPCONTAINER /DEF:d3d9\d3d9.def d3d11.lib d3dcompiler.lib dxguid.lib uuid.lib
if errorlevel 1 exit /b 1
del /q "%OUT%\*.exp" "%OUT%\*.lib" 2>nul
dir /b "%OUT%"
