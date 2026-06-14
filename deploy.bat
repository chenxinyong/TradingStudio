@echo off
REM ================================================================
REM  TradingStudio 部署脚本 — 构建并打包到 dist/
REM ================================================================
set ROOT=%~dp0
set DIST=%ROOT%dist

echo ========================================
echo  1/5  C++/CLI Wrapper
echo ========================================
call "%ROOT%src\CTP\Wrapper\build.bat"
if %ERRORLEVEL% NEQ 0 (echo FAIL & exit /b 1)

echo.
echo ========================================
echo  2/5  TradingStudio (控制台主机)
echo ========================================
dotnet publish "%ROOT%src\TradingStudio\TradingStudio.csproj" ^
    -c Release -o "%DIST%" --self-contained false 2>&1 | findstr /V "info :"
if %ERRORLEVEL% NEQ 0 (echo FAIL & exit /b 1)

echo.
echo ========================================
echo  3/5  TradingStudio.UI (WPF 桌面)
echo ========================================
dotnet publish "%ROOT%src\TradingStudio.UI\TradingStudio.UI.csproj" ^
    -c Release -o "%DIST%\UI" --self-contained false 2>&1 | findstr /V "info :"
if %ERRORLEVEL% NEQ 0 (echo FAIL & exit /b 1)

echo.
echo ========================================
echo  4/5  复制 CTP 依赖
echo ========================================
copy /Y "%ROOT%src\CTP\Wrapper\bin\Release\*.dll" "%DIST%\" >nul 2>&1
copy /Y "%ROOT%src\CTP\Wrapper\bin\Debug\*.dll" "%DIST%\" >nul 2>&1
copy /Y "%ROOT%src\CTP\SDK\dll\*.dll" "%DIST%\" >nul 2>&1
echo DLLs deployed

echo.
echo ========================================
echo  5/5  生成 symbols.json
echo ========================================
python "%ROOT%src\Scripts\gen_symbols_json.py" >nul 2>&1
copy /Y "%ROOT%src\TradingStudio\symbols.json" "%DIST%\" >nul 2>&1
echo symbols.json deployed

echo.
echo ========================================
echo  DEPLOY COMPLETE
echo ========================================
echo.
echo  dist/
dir /b "%DIST%" 2>nul | findstr /V ".pdb$"
echo.
echo  dist/UI/
dir /b "%DIST%\UI" 2>nul | findstr /V ".pdb$"

echo.
echo  启动:
echo    控制台: dist\TradingStudio.exe collect [SHFE] [ag2608]
echo    K线图表: dist\UI\TradingStudio.UI.exe
