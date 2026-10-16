@echo off
rem Builds Orion into orion\build: winmm.dll (the stub that goes in the game folder) and orion.dll (the core that goes in
rem the app-data orion\<version> folder). Needs Visual Studio's C++ tools and Python (for the export table of winmm).
rem No third-party code is linked: Orion hooks nothing by itself, and a hook library in a DLL is what antivirus heuristics flag.
setlocal
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSPATH=%%i
if not defined VSPATH (
  echo Visual Studio C++ build tools not found.
  exit /b 1
)
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist build\generated mkdir build\generated
python tools\gen_proxy.py build\generated || exit /b 1

set CFLAGS=/nologo /O2 /MT /EHsc /std:c++17 /utf-8 /W4 /WX /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS /Fobuild\

rem The stub: forwarders plus the loader of the core, nothing else.
ml64 /nologo /c /Fobuild\stub_exports.obj build\generated\stub_exports.asm || exit /b 1
rc /nologo /fo build\stub.res src\stub\stub.rc || exit /b 1
cl %CFLAGS% /LD /Ibuild\generated src\stub\stub.cpp build\stub_exports.obj build\stub.res /Fe:build\winmm.dll /link /DEF:build\generated\stub.def || exit /b 1

rem The core.
rc /nologo /fo build\core.res src\core\core.rc || exit /b 1
cl %CFLAGS% /LD /I..\sdk\include src\core\core.cpp src\core\util.cpp src\core\json.cpp src\core\manifest.cpp src\core\honest.cpp src\core\filehooks.cpp build\core.res /Fe:build\orion.dll /link /EXPORT:OrionCoreStart shell32.lib ole32.lib || exit /b 1
echo Built orion\build\winmm.dll and orion\build\orion.dll
