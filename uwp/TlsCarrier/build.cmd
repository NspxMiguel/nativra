@echo off
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "TLS_VS=%%i"
call "%TLS_VS%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
cd /d "%~dp0"
if not exist bin mkdir bin
cl /nologo /c /O2 /Oi /GS- /Zl /TC /Fobin\TlsCarrier.obj TlsCarrier.c
if errorlevel 1 exit /b 1
for /l %%i in (0,1,7) do (
  link /nologo /dll /noentry /nodefaultlib /appcontainer /dynamicbase /nxcompat /machine:x64 /include:_tls_used /out:bin\NativraTls%%i.dll bin\TlsCarrier.obj
  if errorlevel 1 exit /b 1
)
