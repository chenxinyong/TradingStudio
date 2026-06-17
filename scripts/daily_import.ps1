param(
    [string]$Date = (Get-Date -Format "yyyyMMdd"),
    [string]$ApiKey = "157145f8520657fefcc45fe897153118",
    [string]$Product = "fut_tickkz_ctp_aftcls",
    [string]$DataDir = "C:\Works\Datas\Jinshuyuan\Daily"
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$repoRoot = Resolve-Path "$scriptDir\.."

# 确保 UnRAR.exe 在 ToolBox 输出目录
$toolBoxDir = "$repoRoot\src\TradingStudio.ToolBox\bin\x64\Debug\net10.0"
$unrarSrc = "$repoRoot\tools\UnRAR.exe"
$unrarDst = "$toolBoxDir\tools\UnRAR.exe"
if (-not (Test-Path $unrarDst)) {
    New-Item -ItemType Directory -Force "$toolBoxDir\tools" | Out-Null
    Copy-Item $unrarSrc $unrarDst -ErrorAction SilentlyContinue
}

# 1. 获取下载 URL
Write-Host "[1/4] Fetching download URL..." -ForegroundColor Cyan
$apiUrl = "http://api.jinshuyuan.net/get_today_fileurl?apikey=$ApiKey&pdtnm=$Product"
try {
    $downloadUrl = (Invoke-WebRequest -Uri $apiUrl -UseBasicParsing -TimeoutSec 10).Content.Trim()
} catch {
    # fallback: try curl
    $downloadUrl = (curl.exe -s $apiUrl 2>$null).Trim()
}
if (-not $downloadUrl -or -not $downloadUrl.StartsWith("http")) {
    Write-Host "[ERROR] Failed to get download URL: $downloadUrl" -ForegroundColor Red
    exit 1
}
Write-Host "  URL: $downloadUrl"

# 2. 下载 RAR
$dbName = "bars_$Date.db"
$rarFile = "$DataDir\$Date.rar"
$dbFile = "$DataDir\$dbName"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

Write-Host "[2/4] Downloading RAR (~108MB)..." -ForegroundColor Cyan
if (Test-Path $rarFile) {
    Write-Host "  Already downloaded, skip"
} else {
    try {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $rarFile -UseBasicParsing -TimeoutSec 120
    } catch {
        curl.exe -L -o $rarFile $downloadUrl 2>&1
    }
    $size = (Get-Item $rarFile).Length / 1MB
    Write-Host "  Downloaded: $([math]::Round($size, 1)) MB"
}

# 3. 导入
Write-Host "[3/4] Importing → $dbName..." -ForegroundColor Cyan
$toolBox = "$toolBoxDir\TradingStudio.ToolBox.dll"
Push-Location (Split-Path $toolBox)
try {
    dotnet TradingStudio.ToolBox.dll import-url `
        --url $downloadUrl `
        --date $Date `
        --db $dbFile 2>&1 | ForEach-Object { Write-Host "  $_" }
} finally {
    Pop-Location
}

# 4. 验证
Write-Host "[4/4] Verify $dbName..." -ForegroundColor Cyan
dotnet TradingStudio.ToolBox.dll verify --db $dbFile 2>&1 | ForEach-Object { Write-Host "  $_" }

$dbSize = (Get-Item $dbFile).Length / 1MB
Write-Host ""
Write-Host "DONE: $dbFile ($([math]::Round($dbSize, 1)) MB)" -ForegroundColor Green
