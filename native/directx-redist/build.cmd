@echo off
rem Builds the DirectX-redistributable replacement DLLs and runs their tests.
rem These stand in for the DirectX June 2010 pieces the console lacks, forwarding
rem to the platform's own XAudio 2.9 / X3DAudio / XAPOFX. x64 to match the
rem loader; the runner has no audio device, so the tests use a mock 2.9 engine.
setlocal enabledelayedexpansion
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "DXR_VS=%%i"
call "%DXR_VS%\VC\Auxiliary\Build\vcvarsall.bat" x64
if errorlevel 1 exit /b 1
cd /d "%~dp0"
if not exist bin mkdir bin

echo === xaudio2_7.dll ===
cl /nologo /LD /EHsc /O2 /MD /DUNICODE /D_UNICODE ^
   xaudio2_7\xaudio2_7.cpp ^
   /Fo:bin\ /Fe:bin\xaudio2_7.dll ^
   /link /DEF:xaudio2_7\xaudio2_7.def xaudio2.lib ole32.lib
if errorlevel 1 exit /b 1

echo === x3daudio1_7.dll ===
cl /nologo /LD /EHsc /O2 /MD ^
   x3daudio1_7\x3daudio1_7.cpp ^
   /Fo:bin\ /Fe:bin\x3daudio1_7.dll ^
   /link /DEF:x3daudio1_7\x3daudio1_7.def xaudio2.lib
if errorlevel 1 exit /b 1

echo === xapofx1_5.dll ===
cl /nologo /LD /EHsc /O2 /MD ^
   xapofx1_5\xapofx1_5.cpp ^
   /Fo:bin\ /Fe:bin\xapofx1_5.dll ^
   /link /DEF:xapofx1_5\xapofx1_5.def xaudio2.lib
if errorlevel 1 exit /b 1

echo === d3dcompiler_43.dll (pure forwarder to d3dcompiler_47) ===
link /nologo /DLL /NOENTRY /DEF:d3dcompiler_43\d3dcompiler_43.def /OUT:bin\d3dcompiler_43.dll /MACHINE:X64
if errorlevel 1 exit /b 1

echo === d3dx9_43.dll ===
cl /nologo /LD /EHsc /O2 /MD ^
   d3dx9_43\d3dx9_43.cpp ^
   /Fo:bin\ /Fe:bin\d3dx9_43.dll
if errorlevel 1 exit /b 1

echo === tests: xaudio2_7 ===
cl /nologo /EHsc /O2 /MD /DUNICODE /D_UNICODE ^
   tests\test_xaudio2_7.cpp xaudio2_7\xaudio2_7.cpp ^
   /Fo:bin\ /Fe:bin\test_xaudio2_7.exe ^
   /link xaudio2.lib ole32.lib
if errorlevel 1 exit /b 1
bin\test_xaudio2_7.exe
if errorlevel 1 exit /b 1

echo === tests: d3dcompiler ===
cl /nologo /EHsc /O2 /MD ^
   tests\test_d3dcompiler.cpp ^
   /Fo:bin\ /Fe:bin\test_d3dcompiler.exe ^
   /link d3dcompiler.lib dxguid.lib
if errorlevel 1 exit /b 1
rem The forwarder must be found next to the test exe.
copy /y bin\d3dcompiler_43.dll . >nul
bin\test_d3dcompiler.exe
set TEST_RC=%errorlevel%
del d3dcompiler_43.dll >nul 2>&1
if not "%TEST_RC%"=="0" exit /b %TEST_RC%

echo === tests: d3dx9 maths ===
cl /nologo /EHsc /O2 /MD ^
   tests\test_d3dx9.cpp d3dx9_43\d3dx9_43.cpp ^
   /Fo:bin\ /Fe:bin\test_d3dx9.exe
if errorlevel 1 exit /b 1
bin\test_d3dx9.exe
if errorlevel 1 exit /b 1

rem D3D9 shader bytecode -> SM5: real D3D9 bytecode from Microsoft's compiler,
rem translated, must render what a direct SM5 compile renders (under WARP).
echo === tests: d3d9 shader translation (dxso) ===
cl /nologo /EHsc /O2 /MD /std:c++17 ^
   tests\test_dxso.cpp d3d9\dxso.cpp ^
   /Fo:bin\ /Fe:bin\test_dxso.exe ^
   /link d3dcompiler.lib d3d11.lib dxguid.lib
if errorlevel 1 exit /b 1
bin\test_dxso.exe --render
if errorlevel 1 exit /b 1

rem Direct3D 9 on Direct3D 11, driven through the public D3D9 API under WARP.
echo === d3d9.dll ===
cl /nologo /LD /EHsc /O2 /MD /std:c++17 ^
   d3d9\d3d9_main.cpp d3d9\d3d9_device.cpp d3d9\d3d9_draw.cpp d3d9\d3d9_resources.cpp ^
   d3d9\d3d9_format.cpp d3d9\dxso.cpp ^
   /Fo:bin\ /Fe:bin\d3d9.dll ^
   /link /DEF:d3d9\d3d9.def d3d11.lib d3dcompiler.lib dxguid.lib uuid.lib
if errorlevel 1 exit /b 1

echo === tests: d3d9 device ===
cl /nologo /EHsc /O2 /MD /std:c++17 ^
   tests\test_d3d9.cpp ^
   /Fo:bin\ /Fe:bin\test_d3d9.exe ^
   /link d3dcompiler.lib
if errorlevel 1 exit /b 1
bin\test_d3d9.exe
exit /b %errorlevel%
