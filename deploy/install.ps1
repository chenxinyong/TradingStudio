<#
.SYNOPSIS
    TradingStudio 实盘部署

.DESCRIPTION
    将 dist/ 安装到目标目录，创建运行时目录，注册 Windows Service。

.PARAMETER TargetDir
    安装根目录。默认: C:\TradingStudio

.PARAMETER ConfigOnly
    只更新配置和策略文件，跳过二进制发布和编译。

.EXAMPLE
    .\install.ps1                         # 完整安装
    .\install.ps1 -ConfigOnly             # 仅更新配置
    .\install.ps1 -TargetDir D:\Trading   # 自定义目录
#>

param(
    [string]$TargetDir = "C:\TradingStudio",
    [switch]$ConfigOnly
)

$ErrorActionPreference = "Stop"
$ServiceName = "TradingStudio"
$DistRoot = "$PSScriptRoot\..\dist"

Write-Host "=== TradingStudio 实盘部署 ===" -ForegroundColor Cyan
Write-Host "  目标: $TargetDir" -ForegroundColor Gray

# ── Step 1: Publish ──
if (-not $ConfigOnly) {
    Write-Host "`n[1/5] 编译发布..." -ForegroundColor Yellow
    $deployScript = "$PSScriptRoot\..\deploy.bat"
    if (Test-Path $deployScript) {
        cmd /c $deployScript
        if ($LASTEXITCODE -ne 0) { throw "deploy.bat failed" }
    } else {
        Write-Host "  deploy.bat not found, assuming dist/ already built" -ForegroundColor Gray
    }
    Write-Host "  OK" -ForegroundColor Green
}

# ── Step 2: Create directories ──
Write-Host "`n[2/5] 创建目录..." -ForegroundColor Yellow
$dirs = @(
    "$TargetDir\app",
    "$TargetDir\desktop",
    "$TargetDir\data",
    "$TargetDir\data\ticks",
    "$TargetDir\data\reports",
    "$TargetDir\config",
    "$TargetDir\config\strategies",
    "$TargetDir\logs"
)
foreach ($d in $dirs) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}
Write-Host "  OK" -ForegroundColor Green

# ── Step 3: Stop service ──
Write-Host "`n[3/5] 停止服务..." -ForegroundColor Yellow
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Stop-Service $ServiceName -Force
    Write-Host "  已停止 $ServiceName" -ForegroundColor Gray
} else {
    Write-Host "  未运行" -ForegroundColor Gray
}

# ── Step 4: Copy files ──
if (-not $ConfigOnly) {
    Write-Host "`n[4/5] 部署文件..." -ForegroundColor Yellow

    # Server binaries
    $serverSrc = "$DistRoot\Server"
    if (Test-Path $serverSrc) {
        robocopy $serverSrc "$TargetDir\app" /MIR /NJH /NJS /NP /XD logs data 2>&1 | Out-Null
    }

    # Desktop binaries
    $desktopSrc = "$DistRoot\Desktop"
    if (Test-Path $desktopSrc) {
        robocopy $desktopSrc "$TargetDir\desktop" /MIR /NJH /NJS /NP /XD logs data 2>&1 | Out-Null
    }

    Write-Host "  OK" -ForegroundColor Green
}

# ── Step 5: Configs ──
Write-Host "`n[5/5] 配置..." -ForegroundColor Yellow

# App settings (don't overwrite existing)
$configSrc = "$PSScriptRoot\configs\appsettings.live.json"
$configDst = "$TargetDir\config\appsettings.json"
if (Test-Path $configSrc) {
    if (-not (Test-Path $configDst)) {
        Copy-Item $configSrc $configDst
        Write-Host "  创建 $configDst (模板)" -ForegroundColor Gray
    } else {
        Write-Host "  保留已有 $configDst" -ForegroundColor Gray
    }
}

# Strategy configs (don't overwrite existing)
$strategySrcDir = "$PSScriptRoot\configs\strategies"
$strategyDstDir = "$TargetDir\config\strategies"
if (Test-Path $strategySrcDir) {
    Get-ChildItem $strategySrcDir -Filter *.json | ForEach-Object {
        $dst = Join-Path $strategyDstDir $_.Name
        if (-not (Test-Path $dst)) {
            Copy-Item $_.FullName $dst
            Write-Host "  创建 $dst" -ForegroundColor Gray
        }
    }
}

# Default config link in app/ (engine looks for appsettings.json next to exe)
$appConfigLink = "$TargetDir\app\appsettings.json"
if (-not (Test-Path $appConfigLink)) {
    Copy-Item $configDst $appConfigLink -ErrorAction SilentlyContinue
}

Write-Host "  OK" -ForegroundColor Green

# ── Windows Service ──
Write-Host "`n=== Windows Service ===" -ForegroundColor Cyan
$exePath = "$TargetDir\app\TradingStudio.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "  WARNING: $exePath not found, skip service registration" -ForegroundColor Yellow
} else {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existing) {
        sc.exe create $ServiceName binPath= "$exePath" start= auto
        sc.exe description $ServiceName "TradingStudio - 量化交易引擎"
        sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000
        Write-Host "  服务已创建: $ServiceName" -ForegroundColor Green
    } else {
        Write-Host "  服务已存在: $ServiceName" -ForegroundColor Gray
    }
    sc.exe start $ServiceName 2>&1 | Out-Null
    Write-Host "  服务已启动" -ForegroundColor Green
}

# ── Done ──
Write-Host "`n=== 安装完成 ===" -ForegroundColor Green
Write-Host "  引擎:  $TargetDir\app\TradingStudio.exe"
Write-Host "  K线:   $TargetDir\desktop\TradingStudio.UI.exe"
Write-Host "  数据:  $TargetDir\data\"
Write-Host "  日志:  $TargetDir\logs\"
Write-Host "  配置:  $TargetDir\config\appsettings.json"
