@echo off
REM ================================================================
REM  CTP C++/CLI Wrapper + C# Demo — One-Click Build
REM  VS 2026 (v18) + .NET 10 + x64 required
REM ================================================================
set ROOT=%~dp0

echo ========================================
echo  Step 1: Build C++/CLI Wrapper
echo ========================================
call "%ROOT%src\CTP\Wrapper\build.bat"
if %ERRORLEVEL% NEQ 0 (
    echo [FAIL] C++/CLI build failed
    exit /b %ERRORLEVEL%
)

echo.
echo ========================================
echo  Step 2: Build C# Demo
echo ========================================
dotnet build "%ROOT%test\CtpDemo\CtpDemo.csproj" -c Debug
if %ERRORLEVEL% NEQ 0 (
    echo [FAIL] C# demo build failed
    exit /b %ERRORLEVEL%
)

echo.
echo ========================================
echo  BUILD SUCCESS
echo ========================================
echo Output: %ROOT%test\CtpDemo\bin\Debug\net10.0\
echo.
echo Run: dotnet run --project test\CtpDemo
echo.
