@echo off
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "TASKBAR_VS=%%i"
if not defined TASKBAR_VS exit /b 1
call "%TASKBAR_VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
pushd "%~dp0"
cl /nologo /std:c++20 /EHsc /O2 /MT /LD /DUNICODE /D_UNICODE MenuSurface.cpp /link user32.lib dwmapi.lib /OUT:LightSuiteMenu.dll
set "TASKBAR_RESULT=%errorlevel%"
popd
exit /b %TASKBAR_RESULT%
