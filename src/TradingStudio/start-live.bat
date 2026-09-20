@echo off
rem Live 看门狗启动器 (双击运行)
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0start-live.ps1"
