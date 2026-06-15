@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Release Collect Build Script
REM
REM  Output: release/ dir with all runtime files + one-click start
REM
REM  Usage:
REM    release-collect.bat          Full build
REM    release-collect.bat --quick  Copy from dist/Server/ (skip build)
REM
REM  Run after release:
REM    release\start.bat            All symbols
REM    release\start.bat SHFE       SHFE only
REM    release\start.bat SHFE ag2608 Single contract
REM    release\start.ps1            PowerShell version
REM ================================================================
setlocal enabledelayedexpansion
set ROOT=%~dp0
set RELEASE=%ROOT%release
set LOG=%TEMP%\release-collect.log

echo.
echo ========================================
echo   TradingStudio - Release Collect Build
echo ========================================
echo.

if "%1"=="--quick" goto :copy

REM --- Step 1: C++/CLI Wrapper ---
echo [1/5] C++/CLI Wrapper...
call "%ROOT%src\CTP\Wrapper\build.bat"
if %ERRORLEVEL% NEQ 0 (echo FAIL: CTPWrapper build failed & exit /b 1)
echo   OK

REM --- Step 2: TradingStudio Engine ---
echo.
echo [2/5] TradingStudio Engine (Release)...
if not exist "%RELEASE%" mkdir "%RELEASE%"
msbuild "%ROOT%src\TradingStudio\TradingStudio.csproj" /t:Publish /p:Configuration=Release /p:PublishDir="%RELEASE%" /p:SelfContained=true > "%LOG%" 2>&1
if %ERRORLEVEL% NEQ 0 (
    type "%LOG%"
    echo FAIL: msbuild failed
    exit /b 1
)
echo   OK

REM --- Step 3: CTP Dependencies ---
echo.
echo [3/5] CTP native DLLs...
copy /Y "%ROOT%src\CTP\Wrapper\bin\Release\*.dll" "%RELEASE%\" >nul 2>&1
copy /Y "%ROOT%src\CTP\Wrapper\bin\Debug\*.dll" "%RELEASE%\" >nul 2>&1
copy /Y "%ROOT%src\CTP\SDK\dll\*.dll" "%RELEASE%\" >nul 2>&1
echo   OK

REM --- Step 4: symbols.json ---
echo.
echo [4/5] symbols.json...
python "%ROOT%src\Scripts\gen_symbols_json.py" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\symbols.json" "%RELEASE%\" >nul 2>&1
echo   OK

REM --- Step 5: start scripts ---
echo.
echo [5/5] start scripts + README...
copy /Y "%ROOT%src\TradingStudio\start-collect.bat" "%RELEASE%\start.bat" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\start-collect.ps1" "%RELEASE%\start.ps1" >nul 2>&1
echo   OK

goto :done

:copy
REM --- Quick mode: copy from dist/Server/ ---
echo [quick] Copying from dist/Server/...
if not exist "%ROOT%dist\Server\TradingStudio.exe" (
    echo FAIL: dist\Server\TradingStudio.exe not found. Run deploy.bat first.
    exit /b 1
)
if not exist "%RELEASE%" mkdir "%RELEASE%"
robocopy "%ROOT%dist\Server" "%RELEASE%" /MIR /NJH /NJS /NP /XD logs data >nul 2>&1
copy /Y "%ROOT%deploy\configs\appsettings.live.json" "%RELEASE%\appsettings.json" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\start-collect.bat" "%RELEASE%\start.bat" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\start-collect.ps1" "%RELEASE%\start.ps1" >nul 2>&1
echo   OK

:done
REM --- Write README.txt ---
echo TradingStudio Market Data Collector v0.2.0> "%RELEASE%\README.txt"
echo ==========================================>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo One-click real-time market data collection for China's six futures exchanges.>> "%RELEASE%\README.txt"
echo Output: 1-min bars ^(bars.db^) + Tick CSV ^(data/ticks/^)>> "%RELEASE%\README.txt"
echo Protocol: CTP 6.7.13>> "%RELEASE%\README.txt"
echo Runtime: .NET 10.0>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo --- Quick Start --->> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo   1. Edit appsettings.json, verify CTP market data front address and account:>> "%RELEASE%\README.txt"
echo      Collect.MdFront   = tcp://182.254.243.31:30011>> "%RELEASE%\README.txt"
echo      Collect.BrokerId  = 9999>> "%RELEASE%\README.txt"
echo      Collect.UserId    = ^(your CTP account^)>> "%RELEASE%\README.txt"
echo      Collect.Password  = ^(your CTP password^)>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo   2. Double-click start.bat to start full collection>> "%RELEASE%\README.txt"
echo      Or:   start.bat SHFE          SHFE only>> "%RELEASE%\README.txt"
echo      Or:   start.bat SHFE ag2608   Single contract>> "%RELEASE%\README.txt"
echo      Or:   powershell .\start.ps1  PowerShell version>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo   3. Press Ctrl+C to stop>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo --- Exchange Codes --->> "%RELEASE%\README.txt"
echo   SHFE  Shanghai Futures Exchange>> "%RELEASE%\README.txt"
echo   DCE   Dalian Commodity Exchange>> "%RELEASE%\README.txt"
echo   CZCE  Zhengzhou Commodity Exchange>> "%RELEASE%\README.txt"
echo   CFFEX China Financial Futures Exchange>> "%RELEASE%\README.txt"
echo   INE   Shanghai Intl Energy Exchange>> "%RELEASE%\README.txt"
echo   GFEX  Guangzhou Futures Exchange>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo --- Output Files --->> "%RELEASE%\README.txt"
echo   bars.db          SQLite - 1-min K bars + daily bars>> "%RELEASE%\README.txt"
echo   data/ticks/      CSV - 42-column tick data>> "%RELEASE%\README.txt"
echo   logs/log*.txt    Serilog rolling logs>> "%RELEASE%\README.txt"
echo   health.json      Health status ^(refreshed every minute^)>> "%RELEASE%\README.txt"
echo   crash.log        Crash log ^(if any^)>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo --- System Requirements --->> "%RELEASE%\README.txt"
echo   Windows 10+ x64 / Windows Server 2019+>> "%RELEASE%\README.txt"
echo   .NET 10 Runtime  ^(https://dotnet.microsoft.com^)>> "%RELEASE%\README.txt"
echo   VC++ Runtime     ^(included in release package^)>> "%RELEASE%\README.txt"
echo.>> "%RELEASE%\README.txt"
echo --- More Info --->> "%RELEASE%\README.txt"
echo   TradingStudio/CLAUDE.md - Project documentation>> "%RELEASE%\README.txt"
echo   docs/                   - Design documents>> "%RELEASE%\README.txt"

echo.
echo ========================================
echo   BUILD COMPLETE
echo ========================================
echo.
echo   Output: %RELEASE%
echo.
echo --- Next Steps ---
echo   1. Edit release\appsettings.json - set CTP credentials
echo   2. Run  release\start.bat        - start collecting
echo.
echo   All symbols:  release\start.bat
echo   SHFE only:    release\start.bat SHFE
echo   Single:       release\start.bat SHFE ag2608
echo.
echo --- release/ contents ---
dir /b "%RELEASE%" 2>nul | findstr /V ".pdb$"
echo.
echo   ... (more .NET DLLs omitted)
echo.
echo --- Distribution ---
echo   Zip the release\ directory to deploy to other x64 machines
