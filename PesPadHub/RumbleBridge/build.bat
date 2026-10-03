@echo off
rem Builds the rumble bridge dinput8.dll (x64, static CRT so the game needs no VC runtime installer).
rem Output: RumbleBridge\bin\dinput8.dll. Keep this file ASCII-only: cmd misparses UTF-8 batch files.
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
    echo Visual Studio with C++ tools was not found
    exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

cd /d "%~dp0"
if not exist bin mkdir bin
if not exist obj mkdir obj
cl /nologo /std:c++17 /O2 /MT /EHsc /W3 /utf-8 /LD dinput8.cpp /Fo:obj\ /Fe:bin\dinput8.dll /link /DEF:dinput8.def /IMPLIB:obj\dinput8.lib /PDB:obj\dinput8.pdb || exit /b 1
echo OK: %~dp0bin\dinput8.dll
