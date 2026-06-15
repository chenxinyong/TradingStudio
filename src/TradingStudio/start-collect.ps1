<#
.SYNOPSIS
    TradingStudio 行情采集 — 一键启动 (PowerShell)

.DESCRIPTION
    创建运行时目录，验证配置，启动行情采集进程。

.PARAMETER Exchange
    交易所过滤（可选）: SHFE DCE CZCE CFFEX INE GFEX

.PARAMETER Symbol
    合约/品种过滤（可选）: ag2608 / ag

.EXAMPLE
    .\start.ps1                       全品种采集
    .\start.ps1 SHFE                  SHFE 所有品种
    .\start.ps1 SHFE ag2608          白银2608
    .\start.ps1 DCE m                大商所豆粕所有合约
#>
param(
    [string]$Exchange,
    [string]$Symbol
)

$ErrorActionPreference = "Stop"

# ── Banner ──
Write-Host ""
Write-Host "╔══════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  TradingStudio — 行情采集 v0.2.0             ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  .NET 10 x64  |  CTP 6.7.13" -ForegroundColor Gray

# ── Create runtime directories ──
$dirs = @("data", "data\ticks", "logs")
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
    }
}
Write-Host "  数据目录: $((Resolve-Path 'data').Path)" -ForegroundColor Gray
Write-Host "  日志目录: $((Resolve-Path 'logs').Path)" -ForegroundColor Gray
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Gray
Write-Host ""

# ── Pre-flight checks ──
if (-not (Test-Path "TradingStudio.exe")) {
    Write-Host "[ERROR] TradingStudio.exe 不存在！" -ForegroundColor Red
    Write-Host "请在 release/ 目录下运行此脚本" -ForegroundColor Red
    pause
    exit 1
}

if (-not (Test-Path "appsettings.json")) {
    Write-Host "[ERROR] appsettings.json 不存在！" -ForegroundColor Red
    pause
    exit 1
}

# ── Build arguments ──
$args = @("collect")
if ($Exchange) {
    Write-Host "[过滤] 交易所: $Exchange" -ForegroundColor Yellow
    $args += $Exchange
}
if ($Symbol) {
    Write-Host "[过滤] 合约: $Symbol" -ForegroundColor Yellow
    $args += $Symbol
}
Write-Host ""

# ── Run ──
$proc = Start-Process -FilePath ".\TradingStudio.exe" -ArgumentList $args -NoNewWindow -PassThru
$proc.WaitForExit()

$exitCode = $proc.ExitCode
if ($exitCode -ne 0) {
    Write-Host ""
    Write-Host "[ERROR] 进程退出码: $exitCode" -ForegroundColor Red
    if (Test-Path "crash.log") {
        Write-Host "--- crash.log (最后10行) ---" -ForegroundColor Red
        Get-Content "crash.log" -Tail 10 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    }
    Write-Host "查看 logs\ 获取完整日志" -ForegroundColor Red
}

pause
