@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Shared Engine Build
REM  Usage: _build.bat <OUTDIR>
REM  自 FtdcNet.CTP P/Invoke 迁移后，不再需要 C++/CLI 编译步骤。
REM ================================================================
setlocal
set ROOT=%~dp0
set OUTDIR=%~1

if "%OUTDIR%"=="" (echo Usage: _build.bat ^<output_dir^> & exit /b 1)

echo.
echo ========================================
echo   TradingStudio - Engine Build
echo   Output: %OUTDIR%
echo ========================================
echo.

echo [1/2] dotnet publish TradingStudio (Release, SelfContained, win-x64)...
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
dotnet publish "%ROOT%src\TradingStudio\TradingStudio.csproj" ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishDir="%OUTDIR%" -p:DebugType=none -p:DebugSymbols=false ^
    --nologo -v q
if %ERRORLEVEL% NEQ 0 (
    echo FAIL: dotnet publish failed
    exit /b 1
)
echo   OK

echo [2/2] Strip debug symbols...
del /q "%OUTDIR%\*.pdb" 2>nul
echo   OK

echo.
echo   Engine build complete.
echo.
