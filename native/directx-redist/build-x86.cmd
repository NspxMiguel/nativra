@echo off
rem 32-bit (x86) copies of the redist shims, for 32-bit games running under the
rem x86 layer, which maps them into the guest like any DLL a game carries.
rem Static runtime (/MT): the guest has no C runtime DLL to lean on.
rem
rem Usage: build-x86.cmd [output folder]   (default: bin\x86)
rem
rem x86 __stdcall exports are decorated (_Name@N) unless a .def names them, and
rem games import the plain names, so every export is checked with dumpbin.
setlocal enabledelayedexpansion
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "DXR_VS=%%i"
call "%DXR_VS%\VC\Auxiliary\Build\vcvarsall.bat" x86
if errorlevel 1 exit /b 1
cd /d "%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%~dp0bin\x86"
if not exist "%OUT%" mkdir "%OUT%"
if not exist obj-x86 mkdir obj-x86

cl /nologo /LD /EHsc /O2 /MT /DUNICODE /D_UNICODE xaudio2_7\xaudio2_7.cpp /Fo:obj-x86\ /Fe:"%OUT%\xaudio2_7.dll" /link /APPCONTAINER /DEF:xaudio2_7\xaudio2_7.def xaudio2.lib ole32.lib
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /O2 /MT x3daudio1_7\x3daudio1_7.cpp /Fo:obj-x86\ /Fe:"%OUT%\x3daudio1_7.dll" /link /APPCONTAINER /DEF:x3daudio1_7\x3daudio1_7.def xaudio2.lib
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /O2 /MT xapofx1_5\xapofx1_5.cpp /Fo:obj-x86\ /Fe:"%OUT%\xapofx1_5.dll" /link /APPCONTAINER /DEF:xapofx1_5\xapofx1_5.def xaudio2.lib
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /O2 /MT d3dx9_43\d3dx9_43.cpp /Fo:obj-x86\ /Fe:"%OUT%\d3dx9_43.dll" /link /APPCONTAINER /DEF:d3dx9_43\d3dx9_43.def
if errorlevel 1 exit /b 1
del /q "%OUT%\*.exp" "%OUT%\*.lib" 2>nul

rem Every export must be reachable by its plain name.
call :exports xaudio2_7 "DllGetClassObject DllCanUnloadNow" || exit /b 1
call :exports x3daudio1_7 "X3DAudioInitialize X3DAudioCalculate" || exit /b 1
call :exports xapofx1_5 "CreateFX" || exit /b 1
set "D3DX="
for /f "skip=2 tokens=1" %%n in (d3dx9_43\d3dx9_43.def) do set "D3DX=!D3DX! %%n"
call :exports d3dx9_43 "!D3DX!" || exit /b 1
dir /b "%OUT%"
exit /b 0

:exports
dumpbin /nologo /exports "%OUT%\%~1.dll" > obj-x86\%~1.exports.txt
for %%n in (%~2) do (
    findstr /r /c:" %%n$" obj-x86\%~1.exports.txt >nul
    if errorlevel 1 (
        echo %~1.dll: export %%n missing or decorated
        exit /b 1
    )
)
echo %~1.dll: exports ok
exit /b 0
