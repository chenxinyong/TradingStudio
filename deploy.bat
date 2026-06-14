@echo off
REM ================================================================
REM  TradingStudio Deployment Script
REM
REM  Output:
REM    dist/Server/  Trading Engine (Windows Service, 7x24)
REM    dist/Desktop/ K-line Chart (WPF Desktop, on-demand)
REM
REM  Requires: Visual Studio 2026+ (for C++/CLI wrapper)
REM ================================================================
set ROOT=%~dp0
set DIST=%ROOT%dist
set LOG=%TEMP%\deploy-publish.log

echo ========================================
echo  1/5  C++/CLI Wrapper
echo ========================================
call "%ROOT%src\CTP\Wrapper\build.bat"
if %ERRORLEVEL% NEQ 0 (echo FAIL & exit /b 1)

echo.
echo ========================================
echo  2/5  TradingStudio (Engine)
echo ========================================
REM use msbuild (not dotnet publish) because TradingStudio
REM references CTPWrapper.vcxproj which requires C++ toolchain
msbuild "%ROOT%src\TradingStudio\TradingStudio.csproj" /t:Publish /p:Configuration=Release /p:PublishDir="%DIST%\Server" /p:SelfContained=false > "%LOG%" 2>&1
if %ERRORLEVEL% NEQ 0 (
    type "%LOG%"
    echo FAIL & exit /b 1
)
findstr /V "info :" "%LOG%"

echo.
echo ========================================
echo  3/5  TradingStudio.UI (Desktop)
echo ========================================
dotnet publish "%ROOT%src\TradingStudio.UI\TradingStudio.UI.csproj" -c Release -o "%DIST%\Desktop" --self-contained false > "%LOG%" 2>&1
if %ERRORLEVEL% NEQ 0 (
    type "%LOG%"
    echo FAIL & exit /b 1
)
findstr /V "info :" "%LOG%"

echo.
echo ========================================
echo  4/5  Copy CTP Dependencies
echo ========================================
copy /Y "%ROOT%src\CTP\Wrapper\bin\Release\*.dll" "%DIST%\Server\" >nul 2>&1
copy /Y "%ROOT%src\CTP\Wrapper\bin\Debug\*.dll" "%DIST%\Server\" >nul 2>&1
copy /Y "%ROOT%src\CTP\SDK\dll\*.dll" "%DIST%\Server\" >nul 2>&1
echo DLLs deployed

echo.
echo ========================================
echo  5/5  Generate symbols.json
echo ========================================
python "%ROOT%src\Scripts\gen_symbols_json.py" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\symbols.json" "%DIST%\Server\" >nul 2>&1
echo symbols.json deployed

echo.
echo ========================================
echo  DEPLOY COMPLETE
echo ========================================
echo.
echo  dist/Server/  (Engine)
if exist "%DIST%\Server\TradingStudio.exe" (
    dir /b "%DIST%\Server" 2>nul | findstr /V ".pdb$"
) else (
    echo   (empty)
)
echo.
echo  dist/Desktop/ (Desktop)
if exist "%DIST%\Desktop\TradingStudio.UI.exe" (
    dir /b "%DIST%\Desktop" 2>nul | findstr /V ".pdb$"
) else (
    echo   (empty)
)

echo.
echo  Run:
echo    Engine:  dist\Server\TradingStudio.exe collect [SHFE] [ag2608]
echo    Desktop: dist\Desktop\TradingStudio.UI.exe
