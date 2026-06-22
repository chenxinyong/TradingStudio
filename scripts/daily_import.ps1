param(
    [string]$Date = (Get-Date -Format "yyyyMMdd"),
    [string]$ApiKey = "157145f8520657fefcc45fe897153118",
    [string]$Product = "fut_tickkz_ctp_aftcls",
    [string]$DataDir = "C:\Works\Datas\Jinshuyuan\Daily",
    [string]$TickDataDir = "",               # 自己的落盘 TickData 目录（留空则跳过本地导入）
    [string]$HistoryDb = "",                 # 历史 DuckDB（默认: <repo>\data\bars_history.duckdb）
    [string]$DbPath = "",                    # 当日临时 DB（默认: $DataDir\bars_$Date.duckdb）
    [switch]$SkipJinshuyuan,                 # 跳过金数源下载
    [switch]$SkipLocal,                      # 跳过本地 TickData 导入
    [switch]$SkipAppend,                     # 跳过追加到历史库
    [switch]$SkipPeriods,                    # 跳过多周期聚合
    [switch]$SkipVerify                      # 跳过验证
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$repoRoot = Resolve-Path "$scriptDir\.."

# 路径初始化
$toolBoxDir = "$repoRoot\src\TradingStudio.ToolBox\bin\Debug\net10.0"
$unrarSrc = "$repoRoot\tools\UnRAR.exe"
$unrarDst = "$toolBoxDir\tools\UnRAR.exe"
if (-not (Test-Path $unrarDst)) {
    New-Item -ItemType Directory -Force "$toolBoxDir\tools" | Out-Null
    Copy-Item $unrarSrc $unrarDst -ErrorAction SilentlyContinue
}

if (-not $DbPath)     { $DbPath = "$DataDir\bars_$Date.duckdb" }
if (-not $HistoryDb)  { $HistoryDb = "$repoRoot\data\bars_history.duckdb" }

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

$startTime = Get-Date
$steps = @()

# ═══════════════════════════════════════════
# Step 1: 金数源 → 当日 DuckDB
# ═══════════════════════════════════════════
if (-not $SkipJinshuyuan) {
    Write-Host ""
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  STEP 1/5  金数源 RAR → 当日 DB" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan

    # 1a. 获取下载 URL
    Write-Host "[1.1] Fetching download URL..." -ForegroundColor Cyan
    $apiUrl = "http://api.jinshuyuan.net/get_today_fileurl?apikey=$ApiKey&pdtnm=$Product"
    try {
        $downloadUrl = (Invoke-WebRequest -Uri $apiUrl -UseBasicParsing -TimeoutSec 10).Content.Trim()
    } catch {
        $downloadUrl = (curl.exe -s $apiUrl 2>$null).Trim()
    }
    if (-not $downloadUrl -or -not $downloadUrl.StartsWith("http")) {
        Write-Host "[WARN] Failed to get download URL — skipping Jinshuyuan" -ForegroundColor Yellow
    } else {
        Write-Host "  URL: $downloadUrl"

        # 1b. 下载 RAR
        $rarFile = "$DataDir\$Date.rar"
        Write-Host "[1.2] Downloading RAR..." -ForegroundColor Cyan
        if (Test-Path $rarFile) {
            Write-Host "  Already downloaded, skip"
        } else {
            try { Invoke-WebRequest -Uri $downloadUrl -OutFile $rarFile -UseBasicParsing -TimeoutSec 120 }
            catch { curl.exe -L -o $rarFile $downloadUrl 2>&1 }
            $size = (Get-Item $rarFile).Length / 1MB
            Write-Host "  Downloaded: $([math]::Round($size, 1)) MB"
        }

        # 1c. 导入 RAR → 当日 DuckDB
        Write-Host "[1.3] Importing RAR → $([System.IO.Path]::GetFileName($DbPath))..." -ForegroundColor Cyan
        Push-Location (Split-Path $toolBoxDir)
        try {
            dotnet TradingStudio.ToolBox.dll import-url --url $downloadUrl --date $Date --db $DbPath 2>&1 | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { throw "import-url failed with exit code $LASTEXITCODE" }
        } finally { Pop-Location }
        $steps += "Jinshuyuan import"
    }
}

# ═══════════════════════════════════════════
# Step 2: 自己落盘 TickData → 当日 DuckDB
# ═══════════════════════════════════════════
if (-not $SkipLocal -and $TickDataDir) {
    if (-not (Test-Path $TickDataDir)) {
        Write-Host "[WARN] TickData directory not found: $TickDataDir" -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
        Write-Host "  STEP 2/5  本地 TickData → 当日 DB" -ForegroundColor Cyan
        Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
        Write-Host "  Source: $TickDataDir"

        Push-Location (Split-Path $toolBoxDir)
        try {
            dotnet TradingStudio.ToolBox.dll import --input $TickDataDir --db $DbPath 2>&1 | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { Write-Host "[WARN] Local import exit code $LASTEXITCODE" -ForegroundColor Yellow }
        } finally { Pop-Location }
        $steps += "Local TickData"
    }
}

# ═══════════════════════════════════════════
# Step 3: 追加到历史库
# ═══════════════════════════════════════════
if (-not $SkipAppend) {
    Write-Host ""
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  STEP 3/5  追加到历史库" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  Source: $([System.IO.Path]::GetFileName($DbPath))"
    Write-Host "  Target: $([System.IO.Path]::GetFileName($HistoryDb))"

    if (-not (Test-Path $DbPath)) {
        Write-Host "[WARN] Daily DB not found — nothing to append" -ForegroundColor Yellow
    } elseif (-not (Test-Path $HistoryDb)) {
        Write-Host "[WARN] History DB not found: $HistoryDb" -ForegroundColor Yellow
    } else {
        Push-Location (Split-Path $toolBoxDir)
        try {
            dotnet TradingStudio.ToolBox.dll append --source $DbPath --target $HistoryDb 2>&1 | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { throw "append failed with exit code $LASTEXITCODE" }
        } finally { Pop-Location }
        $steps += "Append to history"
    }
}

# ═══════════════════════════════════════════
# Step 4: 构建多周期表
# ═══════════════════════════════════════════
if (-not $SkipPeriods) {
    Write-Host ""
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  STEP 4/5  构建多周期表（5min/15min/day/week 连续合约）" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan

    if (-not (Test-Path $HistoryDb)) {
        Write-Host "[WARN] History DB not found — skip" -ForegroundColor Yellow
    } else {
        Push-Location (Split-Path $toolBoxDir)
        try {
            dotnet TradingStudio.ToolBox.dll build-periods --db $HistoryDb 2>&1 | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { Write-Host "[WARN] build-periods exit code $LASTEXITCODE" -ForegroundColor Yellow }
        } finally { Pop-Location }
        $steps += "Build multi-period"
    }
}

# ═══════════════════════════════════════════
# Step 5: 验证历史库
# ═══════════════════════════════════════════
if (-not $SkipVerify) {
    Write-Host ""
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  STEP 5/5  验证历史库" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan

    if (-not (Test-Path $HistoryDb)) {
        Write-Host "[WARN] History DB not found — skip verify" -ForegroundColor Yellow
    } else {
        Push-Location (Split-Path $toolBoxDir)
        try {
            dotnet TradingStudio.ToolBox.dll verify --db $HistoryDb --sample 5 2>&1 | ForEach-Object { Write-Host "  $_" }
        } finally { Pop-Location }
        $steps += "Verify"
    }
}

# ════════════════════════════════════════
# Summary
# ════════════════════════════════════════
$elapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "═══════════════════════════════════════" -ForegroundColor Green
Write-Host "  DAILY IMPORT — COMPLETE" -ForegroundColor Green
Write-Host "═══════════════════════════════════════" -ForegroundColor Green
Write-Host "  Date:    $Date"
Write-Host "  Steps:   $($steps -join ' → ')"
Write-Host "  Time:    $([math]::Round($elapsed.TotalMinutes, 1)) min"
Write-Host "  History: $HistoryDb"
if (Test-Path $HistoryDb) {
    $size = (Get-Item $HistoryDb).Length / 1GB
    Write-Host "  Size:    $([math]::Round($size, 2)) GB"
}
Write-Host "═══════════════════════════════════════" -ForegroundColor Green
Write-Host ""

# 用法
Write-Host "── 用法 ──" -ForegroundColor DarkGray
Write-Host "  完整管线:     .\daily_import.ps1 -TickDataDir ..\src\TradingStudio\TickData"
Write-Host "  仅金数源:     .\daily_import.ps1 -SkipLocal"
Write-Host "  仅本地落盘:   .\daily_import.ps1 -SkipJinshuyuan -TickDataDir ..\src\TradingStudio\TickData"
Write-Host "  跳过验证:     .\daily_import.ps1 -SkipVerify"
Write-Host "  全量重建周期: .\daily_import.ps1 -SkipJinshuyuan -SkipLocal -SkipAppend -SkipPeriods"
Write-Host "                → dotnet ToolBox.dll build-periods --db <history> --full"
