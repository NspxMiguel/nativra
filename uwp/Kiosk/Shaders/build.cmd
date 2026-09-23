@echo off
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "FSR_VS=%%i"
call "%FSR_VS%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
cd /d "%~dp0"
if not exist bin mkdir bin
fxc /nologo /T cs_5_0 /E Main /O3 /D PASS_EASU=1 /Fo bin\FsrEasu.cso Fsr1\Upscale.hlsl
if errorlevel 1 exit /b 1
fxc /nologo /T cs_5_0 /E Main /O3 /D PASS_EASU=0 /Fo bin\FsrRcas.cso Fsr1\Upscale.hlsl
exit /b %errorlevel%
