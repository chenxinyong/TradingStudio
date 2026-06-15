@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio Market Data Collect - One-Click Start
REM
REM  Usage:
REM    start.bat              All symbols
REM    start.bat SHFE         SHFE only
REM    start.bat SHFE ag2608  Silver 2608 only
REM    start.bat DCE m2608    DCE soybean meal 2608
REM
REM  Exchange codes: SHFE DCE CZCE CFFEX INE GFEX
REM ================================================================
setlocal

REM --- Create runtime directories ---
mkdir data 2>nul
mkdir data\ticks 2>nul
mkdir logs 2>nul

echo.
echo ========================================
echo   TradingStudio - Market Data Collect
echo   v0.2.0 | .NET 10 x64 | CTP 6.7.13
echo ========================================
echo   Data dir: data\
echo   Log dir:  logs\
echo ========================================
echo.

REM --- Pre-flight checks ---
if not exist "appsettings.json" (
    echo [ERROR] appsettings.json not found!
    echo Please edit appsettings.json with your CTP credentials first.
    pause
    exit /b 1
)

REM --- Filter args ---
set EXTRA=
if not "%1"=="" (
    echo [Filter] Exchange: %1
    set EXTRA=%1
)
if not "%2"=="" (
    echo [Filter] Symbol: %2
    set EXTRA=%EXTRA% %2
)
echo.

REM --- Run ---
TradingStudio.exe collect %EXTRA%
set EXITCODE=%ERRORLEVEL%

if %EXITCODE% NEQ 0 (
    echo.
    echo [ERROR] Exit code: %EXITCODE%
    echo Check crash.log and logs\ for details.
)

pause
