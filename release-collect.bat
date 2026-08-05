@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Release: Market Data Collect
REM  Output: release/collect/
REM  云端行情采集专用 — 不含交易、不含策略，纯数据管线。
REM ================================================================
setlocal
set ROOT=%~dp0
set RELEASE=%ROOT%release\collect

echo.
echo ========================================
echo   Release: Market Data Collect
echo ========================================
echo.

REM --- Step 1-2: Build engine ---
call "%ROOT%_build.bat" "%RELEASE%"
if %ERRORLEVEL% NEQ 0 exit /b 1

REM --- Step 3: Mode-specific data ---
echo [3/4] symbols.json + configs...
python "%ROOT%src\Scripts\gen_symbols_json.py" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\symbols.json" "%RELEASE%\" >nul 2>&1

REM Copy collect config template (don't overwrite if user already configured)
if not exist "%RELEASE%\appsettings.json" (
    copy /Y "%ROOT%deploy\configs\appsettings.cloud.collect.json" "%RELEASE%\appsettings.json" >nul 2>&1
    echo   [INFO] appsettings.json created from template — edit with your CTP credentials
)
echo   OK

REM --- Step 4: Start scripts ---
echo [4/4] start scripts + README...
copy /Y "%ROOT%src\TradingStudio\start-collect.bat" "%RELEASE%\start.bat" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\start-collect.ps1" "%RELEASE%\start.ps1" >nul 2>&1

REM --- README ---
(
echo TradingStudio - Market Data Collect v0.5.0
echo ==========================================
echo.
echo Real-time market data collection for China's six futures exchanges.
echo Output: 1min/5min/15min/Day/Week bars (DuckDB) + Tick CSV (GBK, 44-column)
echo Protocol: CTP via FtdcNet.CTP P/Invoke ^| .NET 10 SelfContained
echo.
echo --- Quick Start ---
echo   1. Edit appsettings.json: Collect.UserId, Collect.Password, Collect.MdFront
echo   2. Double-click start.bat
echo      Or: start.bat SHFE  for single exchange
echo   3. Press Ctrl+C to stop
echo.
echo --- Exchange Codes ---
echo   SHFE / DCE / CZCE / CFFEX / INE / GFEX
echo.
echo --- Output ---
echo   data\bars.duckdb    1min/5min/15min/day/week bars
echo   data\TickData\      44-column Tick CSV (GBK)
echo   logs\               Serilog rolling log
echo   health.json         Health status (every minute)
) > "%RELEASE%\README.txt"

echo.
echo ========================================
echo   BUILD COMPLETE - release/collect/
echo ========================================
echo   1. Edit release\collect\appsettings.json with your CTP credentials
echo   2. Upload release\collect\ to your cloud server
echo   3. Run start.bat
echo.
