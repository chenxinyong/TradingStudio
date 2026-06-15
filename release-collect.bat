@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Release: Market Data Collect
REM  Output: release/collect/
REM ================================================================
setlocal
set ROOT=%~dp0
set RELEASE=%ROOT%release\collect

echo.
echo ========================================
echo   Release: Market Data Collect
echo ========================================
echo.

REM --- Step 1-3: Build engine ---
call "%ROOT%_build.bat" "%RELEASE%"
if %ERRORLEVEL% NEQ 0 exit /b 1

REM --- Step 4: Mode-specific data ---
echo [4/5] symbols.json + config...
python "%ROOT%src\Scripts\gen_symbols_json.py" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\symbols.json" "%RELEASE%\" >nul 2>&1
echo   OK

REM --- Step 5: Start scripts ---
echo [5/5] start scripts + README...
copy /Y "%ROOT%src\TradingStudio\start-collect.bat" "%RELEASE%\start.bat" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\start-collect.ps1" "%RELEASE%\start.ps1" >nul 2>&1
echo   OK

REM --- README ---
(
echo TradingStudio - Market Data Collect v0.2.0
echo ==========================================
echo.
echo One-click real-time market data collection for China's six futures exchanges.
echo Output: 1-min bars ^(bars.db^) + Tick CSV ^(data/ticks/^)
echo Protocol: CTP 6.7.13  ^|  .NET 10 ^(SelfContained^)
echo.
echo --- Quick Start ---
echo   1. Edit appsettings.json ^(Collect section^): MdFront, UserId, Password
echo   2. Double-click start.bat
echo      Or: start.bat SHFE ag2608  for single contract
echo   3. Press Ctrl+C to stop
echo.
echo --- Exchange Codes ---
echo   SHFE / DCE / CZCE / CFFEX / INE / GFEX
echo.
echo --- Output ---
echo   bars.db    1-min + daily K bars
echo   logs/      Serilog rolling log
echo   data/ticks/  42-column Tick CSV
echo   health.json   Health status ^(every minute^)
) > "%RELEASE%\README.txt"

echo.
echo ========================================
echo   BUILD COMPLETE - release/collect/
echo ========================================
echo   start.bat              All symbols
echo   start.bat SHFE ag2608  Single contract
echo.
