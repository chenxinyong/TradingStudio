@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Release: Live Engine
REM  Output: release/live/
REM ================================================================
setlocal
set ROOT=%~dp0
set RELEASE=%ROOT%release\live

echo.
echo ========================================
echo   Release: Live Engine
echo ========================================
echo.

REM --- Step 1-3: Build engine ---
call "%ROOT%_build.bat" "%RELEASE%"
if %ERRORLEVEL% NEQ 0 exit /b 1

REM --- Step 4: Mode-specific data ---
echo [4/5] symbols.json + configs...
python "%ROOT%src\Scripts\gen_symbols_json.py" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\symbols.json" "%RELEASE%\" >nul 2>&1

REM Copy live config template (don't overwrite existing)
if not exist "%RELEASE%\appsettings.json" (
    copy /Y "%ROOT%deploy\configs\appsettings.live.json" "%RELEASE%\appsettings.json" >nul 2>&1
)
REM Copy strategy configs
mkdir "%RELEASE%\configs\strategies" 2>nul
if exist "%ROOT%deploy\configs\strategies\*.json" (
    copy /Y "%ROOT%deploy\configs\strategies\*.json" "%RELEASE%\configs\strategies\" >nul 2>&1
)
echo   OK

REM --- Step 5: Start scripts ---
echo [5/5] start scripts + README...
(
echo @echo off
echo chcp 65001 ^>nul 2^>^&1
echo cd /d "%%~dp0"
echo title TradingStudio Live v0.3.0
echo mkdir logs 2^>nul
echo echo ========================================
echo echo   TradingStudio - Live Engine v0.3.0
echo echo ========================================
echo echo   .NET 10 x64 ^| CTP 6.7.13
echo echo   REST: http://localhost:59661/api/health
echo echo ========================================
echo echo.
echo echo [Pre-flight]
echo if not exist "TradingStudio.exe" ^(echo [ERROR] TradingStudio.exe not found ^& pause ^& exit /b 1^)
echo if not exist "appsettings.json" ^(echo [ERROR] appsettings.json not found ^& pause ^& exit /b 1^)
echo if not exist "symbols.json" ^(echo [ERROR] symbols.json not found ^& pause ^& exit /b 1^)
echo echo [OK] All files present
echo echo.
echo echo Starting TradingStudio.exe live ...
echo echo.
echo "%%~dp0TradingStudio.exe" live
echo set EXITCODE=%%ERRORLEVEL%%
echo echo.
echo echo ========================================
echo echo   TradingStudio exited ^(code: %%EXITCODE%%^)
echo echo ========================================
echo echo.
echo pause
echo exit /b %%EXITCODE%%
) > "%RELEASE%\start.bat"
echo   OK

REM --- README ---
(
echo TradingStudio - Live Engine v0.2.0
echo ================================
echo.
echo Real-time trading engine with SignalR hub + REST API.
echo Protocol: CTP 6.7.13  ^|  .NET 10 ^(SelfContained^)
echo.
echo --- Quick Start ---
echo   1. Edit appsettings.json ^(Live section^):
echo      MdFront, TraderFront, UserId, Password, StrategyConfig
echo   2. Edit configs\strategies\live-test.json
echo   3. Run: start.bat
echo   4. Open http://localhost:59661/api/health for health check
echo.
echo --- Ports ---
echo   59661  HTTP REST API + SignalR Hub ^(/hubs/engine^)
echo.
echo --- Install as Windows Service ---
echo   sc create TradingStudio binPath= "%CD%\TradingStudio.exe live" start= auto
echo   sc start TradingStudio
echo.
echo --- Output ---
echo   bars_live.db  1-min + daily K bars
echo   TickData/     42-column Tick CSV
echo   logs/         Serilog rolling log
echo   health.json   Health status ^(every minute^)
) > "%RELEASE%\README.txt"

echo.
echo ========================================
echo   BUILD COMPLETE - release/live/
echo ========================================
echo   1. Edit release\live\appsettings.json
echo   2. Edit release\live\configs\strategies\live-test.json
echo   3. Run  release\live\start.bat
echo.
