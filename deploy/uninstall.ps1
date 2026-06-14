<#
.SYNOPSIS
    TradingStudio 卸载

.DESCRIPTION
    停止并删除 Windows Service，可选清理安装目录。

.PARAMETER TargetDir
    安装目录。默认: C:\TradingStudio

.PARAMETER CleanAll
    删除所有文件（包括 data/ 和 logs/）。不加此参数只删除二进制。

.EXAMPLE
    .\uninstall.ps1                         # 停止服务 + 删除 app/ 和 desktop/
    .\uninstall.ps1 -CleanAll               # 全部清除（包括数据）
#>

param(
    [string]$TargetDir = "C:\TradingStudio",
    [switch]$CleanAll
)

$ErrorActionPreference = "Continue"
$ServiceName = "TradingStudio"

Write-Host "=== TradingStudio 卸载 ===" -ForegroundColor Cyan

# ── Stop and remove service ──
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "停止服务 $ServiceName..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName 2>&1 | Out-Null
    Write-Host "  已删除" -ForegroundColor Green
} else {
    Write-Host "  服务不存在，跳过" -ForegroundColor Gray
}

# ── Remove binaries ──
if (Test-Path "$TargetDir\app") {
    Write-Host "删除 app/..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "$TargetDir\app" -ErrorAction SilentlyContinue
    Write-Host "  已删除" -ForegroundColor Green
}

if (Test-Path "$TargetDir\desktop") {
    Write-Host "删除 desktop/..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "$TargetDir\desktop" -ErrorAction SilentlyContinue
    Write-Host "  已删除" -ForegroundColor Green
}

# ── Clean all (data/logs/config) ──
if ($CleanAll) {
    Write-Host "`n=== 清除全部数据 ===" -ForegroundColor Red
    $dirs = @("$TargetDir\data", "$TargetDir\logs", "$TargetDir\config")
    foreach ($d in $dirs) {
        if (Test-Path $d) {
            Write-Host "删除 $(Split-Path $d -Leaf)..." -ForegroundColor Yellow
            Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path $TargetDir) {
        $remaining = Get-ChildItem $TargetDir -ErrorAction SilentlyContinue
        if (-not $remaining) {
            Remove-Item $TargetDir -Force -ErrorAction SilentlyContinue
            Write-Host "  安装目录已清空" -ForegroundColor Green
        }
    }
} else {
    Write-Host ""
    Write-Host "data/ logs/ config/ 已保留。如需全部清除: .\uninstall.ps1 -CleanAll" -ForegroundColor Gray
}

Write-Host "`n=== 卸载完成 ===" -ForegroundColor Green
