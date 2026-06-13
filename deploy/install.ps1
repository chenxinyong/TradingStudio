<#
.SYNOPSIS
    Install TradingStudio as a Windows Service.

.DESCRIPTION
    1. dotnet publish TradingStudio as single-file executable
    2. Copy to C:\TradingStudio\ (or $TargetDir)
    3. Register Windows Service via sc.exe
    4. Start the service

.PARAMETER TargetDir
    Deployment directory. Default: C:\TradingStudio

.PARAMETER ConfigOnly
    Skip publish, only update configs and restart service.

.EXAMPLE
    .\install.ps1                         # Full install
    .\install.ps1 -ConfigOnly             # Update configs only
    .\install.ps1 -TargetDir D:\Trading   # Custom directory
#>

param(
    [string]$TargetDir = "C:\TradingStudio",
    [switch]$ConfigOnly
)

$ErrorActionPreference = "Stop"
$ServiceName = "TradingStudio"
$ProjectDir = "$PSScriptRoot\..\src\TradingStudio"

Write-Host "=== TradingStudio 实盘部署 ===" -ForegroundColor Cyan

# ─── Step 1: Publish ───
if (-not $ConfigOnly) {
    Write-Host "`n[1/5] dotnet publish..." -ForegroundColor Yellow
    dotnet publish $ProjectDir `
        --configuration Release `
        --output $TargetDir `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:DebugType=none

    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    Write-Host "  ✓ Published to $TargetDir" -ForegroundColor Green
}

# ─── Step 2: Copy configs ───
Write-Host "`n[2/5] Copy configs..." -ForegroundColor Yellow
$configSource = "$PSScriptRoot\configs"
$configTarget = "$TargetDir\configs"
if (-not (Test-Path $configTarget)) {
    Copy-Item -Recurse $configSource $configTarget
}
# Ensure data + logs dirs exist
@("$TargetDir\data", "$TargetDir\logs") | ForEach-Object {
    if (-not (Test-Path $_)) { New-Item -ItemType Directory -Path $_ -Force | Out-Null }
}
Write-Host "  ✓ Configs + data dirs ready" -ForegroundColor Green

# ─── Step 3: Stop existing service ───
Write-Host "`n[3/5] Stop existing service..." -ForegroundColor Yellow
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq 'Running') {
        Stop-Service $ServiceName -Force
    }
    Write-Host "  ✓ Service stopped" -ForegroundColor Green
} else {
    Write-Host "  · No existing service" -ForegroundColor Gray
}

# ─── Step 4: Register/update Windows Service ───
Write-Host "`n[4/5] Register Windows Service..." -ForegroundColor Yellow
$exePath = "$TargetDir\TradingStudio.exe"

if ($svc) {
    # Update binary path
    & sc.exe config $ServiceName binPath= $exePath start= auto 2>&1 | Out-Null
    Write-Host "  ✓ Service updated: $exePath" -ForegroundColor Green
} else {
    & sc.exe create $ServiceName `
        binPath= $exePath `
        start= auto `
        DisplayName= "TradingStudio — 量化交易引擎" 2>&1 | Out-Null
    Write-Host "  ✓ Service created: $ServiceName" -ForegroundColor Green
}

# Set recovery options: restart on failure
& sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 2>&1 | Out-Null

# ─── Step 5: Start ───
Write-Host "`n[5/5] Start service..." -ForegroundColor Yellow
Start-Service $ServiceName
Start-Sleep -Seconds 3
$svc = Get-Service $ServiceName
Write-Host "  ✓ Service status: $($svc.Status)" -ForegroundColor Green

# ─── Summary ───
Write-Host "`n=== 部署完成 ===" -ForegroundColor Cyan
Write-Host "  Service:  $ServiceName ($($svc.Status))"
Write-Host "  API:      http://localhost:5199/api/health"
Write-Host "  Hub:      http://localhost:5199/hubs/engine"
Write-Host "  Logs:     $TargetDir\logs\"
Write-Host ""
Write-Host "  管理命令:" -ForegroundColor Gray
Write-Host "    查看状态:  Get-Service $ServiceName"
Write-Host "    查看日志:  Get-Content $TargetDir\logs\trading-*.log -Tail 50"
Write-Host "    重启服务:  Restart-Service $ServiceName"
Write-Host "    停止服务:  Stop-Service $ServiceName"
Write-Host "    卸载:     .\uninstall.ps1"
