# Live 看门狗 — 崩溃自动重启 (云端交易测试)
# 用法: powershell -ExecutionPolicy Bypass -File .\start-live.ps1
param([int]$HeartbeatSec = 10)

$env:DOTNET_ENVIRONMENT = "cloud"   # 加载 appsettings.cloud.json (WarmupDays=120 等云端参数)

$exe = Join-Path $PSScriptRoot "TradingStudio.exe"
$count = 0

if (-not (Test-Path $exe)) {
    Write-Host "[ERROR] 找不到 $exe" -ForegroundColor Red
    exit 1
}

while ($true) {
    if (-not (Get-Process TradingStudio -ErrorAction SilentlyContinue)) {
        $count++
        $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Write-Host "[$ts] #$count 启动 TradingStudio live..." -ForegroundColor Green
        Start-Process -FilePath $exe -ArgumentList "live" -WorkingDirectory $PSScriptRoot
    }
    Start-Sleep $HeartbeatSec
}
