@echo off

mkdir ..\data ..\data\ticks ..\logs 2>nul

TradingStudio.exe collect %*
pause
