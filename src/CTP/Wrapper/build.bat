@echo off
REM Build CTPWrapper (C++/CLI, .NET 10, x64)
set DOTNET_ROOT=%USERPROFILE%\.dotnet
set PATH=%DOTNET_ROOT%;%PATH%
set MSBuildSDKsPath=%DOTNET_ROOT%\sdk\10.0.301\Sdks
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "%~dp0"
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" CTPWrapper.vcxproj /p:Configuration=Debug /p:Platform=x64 /p:NetCoreTargetingPackRoot=%DOTNET_ROOT%\packs /p:FrameworkReferencePackRoot=%DOTNET_ROOT%\packs %*
