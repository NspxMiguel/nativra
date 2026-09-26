@echo off
rem Real MSVC 32-bit programs the x86 layer must run end to end (see
rem guests\crt_test.cpp); GuestProgramTests runs them when
rem NATIVRA_GUEST_PROGRAMS names this folder's guests\bin.
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "GUEST_VS=%%i"
call "%GUEST_VS%\VC\Auxiliary\Build\vcvarsall.bat" x86
if errorlevel 1 exit /b 1
cd /d "%~dp0guests"
if not exist bin mkdir bin
if not exist obj mkdir obj
cl /nologo /O2 /EHsc /MT crt_test.cpp /Fo:obj\ /Fe:bin\crt_static.exe
if errorlevel 1 exit /b 1
cl /nologo /O2 /EHsc /MD crt_test.cpp /Fo:obj\ /Fe:bin\crt_dynamic.exe
if errorlevel 1 exit /b 1
rem Sanity check on real hardware (WoW64) before the layer runs them.
bin\crt_static.exe
if not "%errorlevel%"=="42" exit /b 1
bin\crt_dynamic.exe
if not "%errorlevel%"=="42" exit /b 1
del /q guest-out.txt 2>nul
dir /b bin
exit /b 0
