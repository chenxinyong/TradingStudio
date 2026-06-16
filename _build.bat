@echo off
chcp 65001 >nul 2>&1
REM ================================================================
REM  TradingStudio - Shared Engine Build
REM  Usage: _build.bat <OUTDIR>
REM ================================================================
setlocal
set ROOT=%~dp0
set OUTDIR=%~1
set LOG=%TEMP%\_build.log

if "%OUTDIR%"=="" (echo Usage: _build.bat ^<output_dir^> & exit /b 1)

echo.
echo ========================================
echo   TradingStudio - Engine Build
echo   Output: %OUTDIR%
echo ========================================
echo.

REM --- Step 1: C++/CLI Wrapper ---
echo [1/3] C++/CLI Wrapper...
call "%ROOT%src\CTP\Wrapper\build.bat"
if %ERRORLEVEL% NEQ 0 (echo FAIL: CTPWrapper build failed & exit /b 1)
echo   OK

REM --- Step 2: TradingStudio Engine ---
echo.
echo [2/3] TradingStudio Engine (Release, SelfContained)...
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
dotnet restore "%ROOT%src\TradingStudio\TradingStudio.csproj" -r win-x64 >nul 2>&1
msbuild "%ROOT%src\TradingStudio\TradingStudio.csproj" /t:Publish /p:Configuration=Release /p:PublishDir="%OUTDIR%" /p:SelfContained=true /p:RuntimeIdentifier=win-x64 > "%LOG%" 2>&1
if %ERRORLEVEL% NEQ 0 (
    type "%LOG%"
    echo FAIL: msbuild failed
    exit /b 1
)
echo   OK

REM --- Step 3: CTP native DLLs ---
echo.
echo [3/3] CTP native DLLs...
if exist "%ROOT%src\CTP\Wrapper\bin\Release\*.dll" (
    copy /Y "%ROOT%src\CTP\Wrapper\bin\Release\*.dll" "%OUTDIR%\" >nul 2>&1
) else (
    copy /Y "%ROOT%src\CTP\Wrapper\bin\Debug\*.dll" "%OUTDIR%\" >nul 2>&1
)
copy /Y "%ROOT%src\CTP\SDK\dll\*.dll" "%OUTDIR%\" >nul 2>&1
echo   OK

echo.
echo   Engine build complete.
echo.
