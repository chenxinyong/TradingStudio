@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Release ALL modes
REM  Output: release/collect/ + release/live/ + release/backtest/
REM ================================================================

echo.
echo ========================================
echo   TradingStudio - Release ALL Modes
echo ========================================
echo.

set ROOT=%~dp0
set FAILED=0

echo --- [1/3] collect ---
call "%ROOT%release-collect.bat"
if %ERRORLEVEL% NEQ 0 (set FAILED=1 & echo FAIL: collect)

echo.
echo --- [2/3] live ---
call "%ROOT%release-live.bat"
if %ERRORLEVEL% NEQ 0 (set FAILED=1 & echo FAIL: live)

echo.
echo --- [3/3] backtest ---
call "%ROOT%release-backtest.bat"
if %ERRORLEVEL% NEQ 0 (set FAILED=1 & echo FAIL: backtest)

echo.
if %FAILED% EQU 0 (
    echo ========================================
    echo   ALL releases ready
    echo ========================================
    echo.
    echo   release\collect\   Market Data Collect
    echo   release\live\      Live Trading Engine
    echo   release\backtest\  Backtest Engine
    echo.
) else (
    echo [FAIL] One or more releases failed. Check logs.
)

echo.
