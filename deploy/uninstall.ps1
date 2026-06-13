<#
.SYNOPSIS
    Uninstall TradingStudio Windows Service.
#>

param(
    [string]$ServiceName = "TradingStudio",
    [switch]$KeepData
)

Write-Host "=== 卸载 TradingStudio ===" -ForegroundColor Cyan

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' not found." -ForegroundColor Gray
    exit 0
}

Write-Host "[1/2] Stop service..." -ForegroundColor Yellow
if ($svc.Status -eq 'Running') {
    Stop-Service $ServiceName -Force
}
Write-Host "  ✓ Stopped" -ForegroundColor Green

Write-Host "[2/2] Remove service..." -ForegroundColor Yellow
& sc.exe delete $ServiceName 2>&1 | Out-Null
Write-Host "  ✓ Removed" -ForegroundColor Green

if (-not $KeepData) {
    Write-Host "Data + configs kept at C:\TradingStudio\ (use -KeepData to preserve)"
}

Write-Host "`n=== 卸载完成 ===" -ForegroundColor Cyan
