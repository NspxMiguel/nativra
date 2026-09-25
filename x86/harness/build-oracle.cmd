@echo off
rem Builds the 32-bit hardware oracle with MSVC's x86 toolchain and runs it to
rem produce oracle-vectors.txt: the before/after guest states a real processor
rem (under WoW64 on the runner) reached for every snippet. The C# differential
rem tests replay these through the interpreter and the JIT.
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "ORACLE_VS=%%i"
call "%ORACLE_VS%\VC\Auxiliary\Build\vcvarsall.bat" x86
if errorlevel 1 exit /b 1
cd /d "%~dp0"
cl /nologo /O2 /Zl /MT oracle.c /Fe:oracle.exe /link /nodefaultlib:libcmt.lib libcmt.lib
if errorlevel 1 exit /b 1
oracle.exe snippets.txt > oracle-vectors.txt
if errorlevel 1 exit /b 1
for /f %%c in ('find /c "T " ^< oracle-vectors.txt') do echo generated %%c snippet vectors
exit /b 0
