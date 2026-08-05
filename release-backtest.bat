@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Release: Backtest Engine
REM  Output: release/backtest/
REM ================================================================
setlocal
set ROOT=%~dp0
set RELEASE=%ROOT%release\backtest

echo.
echo ========================================
echo   Release: Backtest Engine
echo ========================================
echo.

REM --- Step 1-3: Build engine ---
call "%ROOT%_build.bat" "%RELEASE%"
if %ERRORLEVEL% NEQ 0 exit /b 1

REM --- Step 4: Mode-specific data ---
echo [4/5] symbols.json + strategy examples...
python "%ROOT%src\Scripts\gen_symbols_json.py" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\symbols.json" "%RELEASE%\" >nul 2>&1

mkdir "%RELEASE%\strategies\backtest" 2>nul
if exist "%ROOT%src\TradingStudio\strategies\backtest\*.json" (
    copy /Y "%ROOT%src\TradingStudio\strategies\backtest\*.json" "%RELEASE%\strategies\backtest\" >nul 2>&1
)
echo   OK

REM --- Step 5: Start scripts ---
echo [5/5] start scripts + README...

REM start-bar.bat
(
echo @echo off
echo chcp 65001 ^>nul 2^>^&1
echo cd /d "%%~dp0"
echo echo ========================================
echo echo   TradingStudio - Backtest ^(Bar mode^)
echo echo ========================================
echo echo.
echo if "%%1"=="" ^(
echo     echo Usage: start-bar.bat ^<strategy.json^> [--db path] [--start yyyy-MM-dd] [--end yyyy-MM-dd]
echo     echo.
echo     echo Examples:
echo     echo   start-bar.bat strategies\backtest\ma-cross-ag-1h-adx.json
echo     echo   start-bar.bat strategies\backtest\ma-cross-ag-1h-adx.json --db bars.db --start 2025-01-01 --end 2025-06-01
echo     pause
echo     exit /b 1
echo ^)
echo set CONFIG=%%1
echo set ARGS=%%2 %%3 %%4 %%5 %%6 %%7 %%8 %%9
echo echo Config: %%CONFIG%%
echo echo.
echo TradingStudio.exe backtest --config %%CONFIG%% %%ARGS%%
echo echo.
echo pause
) > "%RELEASE%\start-bar.bat"

REM start-tick.bat
(
echo @echo off
echo chcp 65001 ^>nul 2^>^&1
echo cd /d "%%~dp0"
echo echo ========================================
echo echo   TradingStudio - Backtest ^(Tick mode^)
echo echo ========================================
echo echo.
echo if "%%1"=="" ^(
echo     echo Usage: start-tick.bat ^<strategy.json^> --data-dir ^<csv_dir^> [--start yyyy-MM-dd] [--end yyyy-MM-dd]
echo     echo.
echo     echo Example:
echo     echo   start-tick.bat strategies\backtest\ma-cross-ag-1h-adx.json --data-dir TickData --start 2025-01-01 --end 2025-01-31
echo     pause
echo     exit /b 1
echo ^)
echo set CONFIG=%%1
echo set ARGS=%%2 %%3 %%4 %%5 %%6 %%7 %%8 %%9
echo echo Config: %%CONFIG%%
echo echo.
echo TradingStudio.exe backtest --mode tick --config %%CONFIG%% %%ARGS%%
echo echo.
echo pause
) > "%RELEASE%\start-tick.bat"
echo   OK

REM --- README ---
(
echo TradingStudio - Backtest Engine v0.2.0
echo ======================================
echo.
echo Historical backtest engine - bar mode + tick mode.
echo .NET 10 ^(SelfContained^)
echo.
echo --- Quick Start ---
echo   1. Copy bars.db ^(or TickData/^) to this directory
echo   2. Edit strategies\backtest\ma-cross-ag-1h-adx.json ^(or use as-is^)
echo   3. Run:
echo      start-bar.bat strategies\backtest\ma-cross-ag-1h-adx.json --db bars.db --start 2025-01-01 --end 2025-06-01
echo      start-tick.bat strategies\backtest\ma-cross-ag-1h-adx.json --data-dir TickData --start 2025-01-01 --end 2025-01-31
echo.
echo --- CLI Options ---
echo   --config, -c   Strategy JSON file ^(required^)
echo   --mode,  -m    bar ^| tick ^(default: bar^)
echo   --db,    -d    SQLite bars.db path
echo   --data-dir     Tick CSV directory ^(tick mode only^)
echo   --symbols      symbols.json path ^(default: symbols.json^)
echo   --start / --end  Date range ^(yyyy-MM-dd^)
echo.
echo --- Output ---
echo   ^<strategy^>.report.json   Full backtest report
echo   Console output            Summary: Equity, Return, Drawdown, Win Rate
) > "%RELEASE%\README.txt"

echo.
echo ========================================
echo   BUILD COMPLETE - release/backtest/
echo ========================================
echo   Bar:   start-bar.bat strategies\backtest\ma-cross-ag-1h-adx.json --db bars.db
echo   Tick:  start-tick.bat strategies\backtest\ma-cross-ag-1h-adx.json --data-dir TickData
echo.
