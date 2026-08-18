<#
.SYNOPSIS
    Jinshuyuan daily data update
.DESCRIPTION
    1. Call API to get file URL
    2. Download to temp dir
    3. Import via ToolBox
    4. Cleanup
#>

param(
    [string]$DbPath = "$PSScriptRoot\..\..\data\bars_history.db",
    [string]$ApiKey = "157145f8520657fefcc45fe897153118",
    [string]$DataDir = "$env:TEMP\jinshuyuan_daily"
)

$ErrorActionPreference = "Stop"
$ToolBox = "$PSScriptRoot\..\..\src\TradingStudio.ToolBox\bin\Release\net10.0\TradingStudio.ToolBox.exe"

Write-Host "=== Jinshuyuan Daily Update ===" -ForegroundColor Cyan
Write-Host "  Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

# 1. API
Write-Host "`n[1/4] Calling API..." -ForegroundColor Yellow
$apiUrl = "http://api.jinshuyuan.net/get_today_fileurl?apikey=$ApiKey&pdtnm=fut_tickkz_ctp_aftcls"
try {
    $response = Invoke-RestMethod -Uri $apiUrl -Method Get -TimeoutSec 30
    Write-Host "  Response: $response"
} catch {
    Write-Host "  API failed: $_" -ForegroundColor Red
    exit 1
}

$fileUrl = if ($response -is [string]) { $response } else { $response.url }
if (-not $fileUrl -or $fileUrl -eq "no permission" -or $fileUrl -notlike "http*") {
    Write-Host "  API returned: $fileUrl" -ForegroundColor Yellow
    Write-Host "  No valid subscription or no data available yet." -ForegroundColor Yellow
    exit 0
}
Write-Host "  File URL: $fileUrl"

# 2. Download
Write-Host "`n[2/4] Downloading..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$fileName = Split-Path $fileUrl -Leaf
if (-not $fileName) { $fileName = "today.rar" }
$localFile = Join-Path $DataDir $fileName

try {
    Invoke-WebRequest -Uri $fileUrl -OutFile $localFile -TimeoutSec 300
    $fileSize = (Get-Item $localFile).Length
    Write-Host "  Downloaded: $fileName ($('{0:N0}' -f $fileSize) bytes)"
} catch {
    Write-Host "  Download failed: $_" -ForegroundColor Red
    exit 1
}

# 3. Import
Write-Host "`n[3/4] Importing..." -ForegroundColor Yellow
$dbFullPath = Resolve-Path $DbPath -ErrorAction SilentlyContinue
if (-not $dbFullPath) {
    $dbFullPath = (Get-Location).Path + "\$DbPath"
}
Write-Host "  DB: $dbFullPath"

if (Test-Path $ToolBox) {
    $importArgs = @(
        "import-jinshuyuan",
        "--layer", "active",
        "--db", $dbFullPath,
        "--data-dir", $DataDir
    )

    $proc = Start-Process -FilePath $ToolBox -ArgumentList $importArgs -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Write-Host "  Import failed (exit: $($proc.ExitCode))" -ForegroundColor Red
        exit 1
    }
    Write-Host "  Import OK" -ForegroundColor Green
} else {
    Write-Host "  ToolBox not found: $ToolBox" -ForegroundColor Yellow
    Write-Host "  File kept: $localFile"
    exit 1
}

# 4. Cleanup
Write-Host "`n[4/4] Cleanup..." -ForegroundColor Yellow
Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
Write-Host "  Done"

Write-Host "`n=== Complete ===" -ForegroundColor Green
