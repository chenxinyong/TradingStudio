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

REM --- Ensure we're in the directory containing this script ---
cd /d "%~dp0"

REM --- Check .NET runtime ---
dotnet --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] .NET Runtime not found!
    echo Please install .NET 10 SDK/Runtime from https://dotnet.microsoft.com
    pause
    exit /b 1
)

REM --- Check required files ---
if not exist "TradingStudio.exe" (
    echo [ERROR] TradingStudio.exe not found in %~dp0
    pause
    exit /b 1
)
if not exist "appsettings.json" (
    echo [ERROR] appsettings.json not found!
    echo Please edit appsettings.json with your CTP credentials first.
    pause
    exit /b 1
)
if not exist "symbols.json" (
    echo [ERROR] symbols.json not found!
    pause
    exit /b 1
)

REM --- Create runtime directories ---
mkdir data 2>nul
mkdir data\ticks 2>nul
mkdir logs 2>nul

echo.
echo ========================================
echo   TradingStudio - Market Data Collect
echo   v0.2.0 ^| .NET 10 x64 ^| CTP 6.7.13
echo ========================================
echo   CWD:     %CD%
echo   Data:    data\
echo   Logs:    logs\
echo   Config:  Collect.MdFront from appsettings.json
echo ========================================
echo.

REM --- Build filter args ---
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
echo Starting TradingStudio.exe collect %EXTRA% ...
echo Press Ctrl+C to stop.
echo.
TradingStudio.exe collect %EXTRA%
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE% EQU 0 (
    echo [OK] Process exited normally.
) else (
    echo [ERROR] Exit code: %EXITCODE%
    if exist "crash.log" (
        echo --- crash.log ---
        type crash.log
    )
    echo Check logs\ for details.
)

pause
