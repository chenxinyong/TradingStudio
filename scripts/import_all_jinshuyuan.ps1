<#
.SYNOPSIS
    金数源历史 Tick 数据全量导入 TradingStudio。

.DESCRIPTION
    从 C:\Works\Datas\Jinshuyuan 读取 2020-2025 共 72 个月度 RAR 文件，
    逐月解压 → CSV 解析 → Bar 聚合 → SQLite 写入。

    特性:
    - 断点续传：.import_progress.txt 记录已完成月份
    - 容错：单个 RAR 失败不中断整体流程
    - 预估：首次启动显示数据量和预计时间

.PARAMETER DataDir
    金数源数据根目录，默认 C:\Works\Datas\Jinshuyuan

.PARAMETER DbPath
    输出 SQLite 数据库路径，默认 bars_history.db

.PARAMETER Layer
    导入层：all（全部）/ active（活跃品种）/ main（主力连续），默认 all

.PARAMETER FromYear
    起始年份，默认 2020

.PARAMETER ToYear
    结束年份，默认 2025

.PARAMETER DryRun
    仅列出匹配文件，不执行导入

.PARAMETER Resume
    从上次中断处继续（读取 .import_progress.txt）

.PARAMETER Force
    忽略进度文件，从头开始

.EXAMPLE
    .\import_all_jinshuyuan.ps1
    全量导入 2020-2025 所有品种全部合约

.EXAMPLE
    .\import_all_jinshuyuan.ps1 -Layer active -FromYear 2024
    仅导入 2024-2025 活跃品种合约

.EXAMPLE
    .\import_all_jinshuyuan.ps1 -DryRun
    预览匹配数据量

.EXAMPLE
    .\import_all_jinshuyuan.ps1 -Resume
    从中断处继续
#>

param(
    [string]$DataDir = "C:\Works\Datas\Jinshuyuan",
    [string]$DbPath = "bars_history.db",
    [ValidateSet("all", "active", "main")]
    [string]$Layer = "all",
    [int]$FromYear = 2020,
    [int]$ToYear = 2025,
    [switch]$DryRun,
    [switch]$Resume,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$script:StartTime = Get-Date

# ═══════════════════════════════════════════════════════════════
# 0. 初始化
# ═══════════════════════════════════════════════════════════════

$ProgressFile = Join-Path $PSScriptRoot ".import_progress.txt"
$LogFile = Join-Path $PSScriptRoot "import_$(Get-Date -Format 'yyyyMMdd_HHmmss').log"

# 查找 TradingStudio.ToolBox.exe
$ExePaths = @(
    (Join-Path $PSScriptRoot "..\src\TradingStudio.ToolBox\bin\Release\net10.0\TradingStudio.ToolBox.exe"),
    (Join-Path $PSScriptRoot "..\src\TradingStudio.ToolBox\bin\x64\Release\net10.0\TradingStudio.ToolBox.exe"),
    (Join-Path $PSScriptRoot "..\dist\ToolBox\TradingStudio.ToolBox.exe"),
    (Join-Path $PSScriptRoot "..\src\TradingStudio\bin\x64\Release\net10.0\TradingStudio.exe"),
    (Join-Path $PSScriptRoot "..\dist\Server\TradingStudio.exe")
)
$ToolBoxExe = $null
foreach ($p in $ExePaths) {
    $resolved = Resolve-Path $p -ErrorAction SilentlyContinue
    if ($resolved -and (Test-Path $resolved)) {
        $ToolBoxExe = $resolved.Path
        break
    }
}

if (-not $ToolBoxExe) {
    Write-Host @"

╔══════════════════════════════════════════════════════════╗
║  TradingStudio.exe 未找到。                              ║
║  请在 Visual Studio 中先构建 Release 配置。               ║
║  或在命令行手动指定:                                      ║
║    dotnet run --project src/TradingStudio.ToolBox -- import-jinshuyuan ...  ║
╚══════════════════════════════════════════════════════════╝

"@ -ForegroundColor Red
    exit 1
}

Write-Host "TradingStudio: $ToolBoxExe" -ForegroundColor Gray

# 日志记录
function Write-Log {
    param([string]$Message, [string]$Color = "White")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$timestamp] $Message"
    Add-Content -Path $LogFile -Value $line
    Write-Host $line -ForegroundColor $Color
}

# 进度管理
function Get-CompletedMonths {
    if ($Force) { return @() }
    if (-not (Test-Path $ProgressFile)) { return @() }
    Get-Content $ProgressFile | Where-Object { $_ -match '^\d{6}$' } | ForEach-Object { $_.Trim() }
}

function Set-CompletedMonth {
    param([string]$YearMonth)
    Add-Content -Path $ProgressFile -Value $YearMonth
}

# ═══════════════════════════════════════════════════════════════
# 1. 扫描 RAR 文件
# ═══════════════════════════════════════════════════════════════

Write-Log "═══════════════════════════════════════════"
Write-Log "  TradingStudio 金数源全量导入"
Write-Log "  数据目录: $DataDir"
Write-Log "  输出: $DbPath"
Write-Log "  层级: $Layer"
Write-Log "  年份: $FromYear - $ToYear"
if ($DryRun) { Write-Log "  模式: DRY RUN" }
Write-Log "  日志: $LogFile"
Write-Log "═══════════════════════════════════════════"
Write-Host ""

$allRarFiles = @()
for ($y = $FromYear; $y -le $ToYear; $y++) {
    $yearDir = Join-Path $DataDir "FutAC_TickKZ_CTP_Daily_$y"
    if (-not (Test-Path $yearDir)) {
        Write-Log "  ⚠ 目录不存在: $yearDir" -Color Yellow
        continue
    }
    $rars = Get-ChildItem $yearDir -Filter "*.rar" | Sort-Object Name
    foreach ($rar in $rars) {
        # 从文件名提取 YYYYMM: FutAC_TickKZ_CTP_Daily_202001.rar → 202001
        $stem = [IO.Path]::GetFileNameWithoutExtension($rar.Name)
        $idx = $stem.LastIndexOf('_')
        if ($idx -lt 0) { continue }
        $ym = $stem.Substring($idx + 1)
        if ($ym.Length -ne 6) { continue }
        $allRarFiles += [PSCustomObject]@{
            Path      = $rar.FullName
            Name      = $rar.Name
            Size      = $rar.Length
            YearMonth = $ym
            Year      = [int]($ym.Substring(0, 4))
        }
    }
}

$allRarFiles = @($allRarFiles | Sort-Object YearMonth)

if ($allRarFiles.Count -eq 0) {
    Write-Log "未找到任何 RAR 文件。" -Color Red
    exit 1
}

# 过滤已完成的月份
$completedMonths = @(Get-CompletedMonths)
$pendingFiles = @($allRarFiles | Where-Object { $_.YearMonth -notin $completedMonths })

$totalSizeMB = [math]::Round(($allRarFiles | Measure-Object Size -Sum).Sum / 1MB, 1)
$pendingSizeMB = [math]::Round(($pendingFiles | Measure-Object Size -Sum).Sum / 1MB, 1)

Write-Log "发现 $($allRarFiles.Count) 个 RAR 文件 ($totalSizeMB MB)" -Color Cyan
Write-Log "  年份范围: $FromYear - $ToYear"
Write-Log "  月度范围: $($allRarFiles[0].YearMonth) - $($allRarFiles[-1].YearMonth)"

if ($completedMonths.Count -gt 0) {
    Write-Log "  已完成: $($completedMonths.Count) 个月"
    Write-Log "  待处理: $($pendingFiles.Count) 个月 ($pendingSizeMB MB)" -Color Yellow
}
Write-Host ""

if ($pendingFiles.Count -eq 0) {
    Write-Log "全部已导入完成！如需重新导入，请使用 -Force。" -Color Green
    exit 0
}

if ($DryRun) {
    Write-Log "═══ DRY RUN — 预览匹配的条目数 ═══" -Color Cyan
    Write-Host ""
    foreach ($rar in $pendingFiles) {
        Write-Host "[$($rar.YearMonth)] $($rar.Name) ($([math]::Round($rar.Size/1MB,1)) MB)"
        $fromTo = "--from $($rar.YearMonth) --to $($rar.YearMonth)"
        $args = @("import-jinshuyuan", "--layer", $Layer, "--data-dir", $DataDir, "--dry-run") +
                @($fromTo.Split(' '))
        & $ToolBoxExe $args 2>&1 | ForEach-Object {
            if ($_ -match 'matching|Matching|entries') { Write-Host "  $_" }
        }
        Write-Host ""
    }
    exit 0
}

# ═══════════════════════════════════════════════════════════════
# 2. 逐月批量导入
# ═══════════════════════════════════════════════════════════════

Write-Log "═══ 开始导入 $($pendingFiles.Count) 个月 ═══" -Color Green
Write-Log "预计临时空间需求: ~60 GB/月（自动清理）"
Write-Log "预计总时间: $([math]::Round($pendingFiles.Count * 5 / 60, 1)) - $([math]::Round($pendingFiles.Count * 15 / 60, 1)) 小时"
Write-Log "按 Ctrl+C 可安全中断（已完成的月份不会丢失）"
Write-Host ""

$okCount = 0
$failCount = 0
$totalTicks = 0L
$totalBars = 0L
$monthStart = Get-Date

for ($i = 0; $i -lt $pendingFiles.Count; $i++) {
    $rar = $pendingFiles[$i]
    $pct = [math]::Round(($i + $completedMonths.Count) / $allRarFiles.Count * 100, 1)
    $eta = ""
    if ($i -gt 0) {
        $elapsed = (Get-Date) - $monthStart
        $avgPerMonth = $elapsed.TotalSeconds / $i
        $remaining = [math]::Round(($pendingFiles.Count - $i) * $avgPerMonth / 60, 0)
        $eta = "ETA: ${remaining}min"
    }

    Write-Log "[$($i+1)/$($pendingFiles.Count) ($pct%)] $($rar.YearMonth) $($rar.Name)  $eta" -Color Cyan

    $fromTo = "--from $($rar.YearMonth) --to $($rar.YearMonth)"
    $cliArgs = @(
        "import-jinshuyuan",
        "--layer", $Layer,
        "--data-dir", $DataDir,
        "--db", $DbPath
    ) + $fromTo.Split(' ')

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $output = & $ToolBoxExe $cliArgs 2>&1
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    # 解析输出提取统计
    $ticks = 0L; $bars = 0L
    foreach ($line in $output) {
        if ($line -match 'Done:.*?(\d[\d,]*) ticks.*?(\d[\d,]*) bars') {
            $ticks = [long]($Matches[1] -replace ',', '')
            $bars = [long]($Matches[2] -replace ',', '')
        }
        Write-Host "  $line"
    }

    if ($exitCode -eq 0 -and $ticks -gt 0) {
        $okCount++
        $totalTicks += $ticks
        $totalBars += $bars
        Set-CompletedMonth $rar.YearMonth
        Write-Log "  ✓ $($rar.YearMonth): $($ticks.ToString('N0')) ticks, $($bars.ToString('N0')) bars  [$([math]::Round($sw.Elapsed.TotalSeconds,0))s]" -Color Green
    }
    elseif ($exitCode -eq 0) {
        $okCount++
        Set-CompletedMonth $rar.YearMonth
        Write-Log "  ✓ $($rar.YearMonth): 0 ticks (空月或休市)" -Color Yellow
    }
    else {
        $failCount++
        Write-Log "  ✗ $($rar.YearMonth): exit code $exitCode  [$([math]::Round($sw.Elapsed.TotalSeconds,0))s]" -Color Red
        # 失败也记录日志但不标记完成（下次 -Resume 会重试）
    }

    Write-Host ""
}

# ═══════════════════════════════════════════════════════════════
# 3. 汇总
# ═══════════════════════════════════════════════════════════════

$totalElapsed = (Get-Date) - $script:StartTime

Write-Log "═══════════════════════════════════════════" -Color Cyan
Write-Log "  导入完成" -Color Green
Write-Log "───────────────────────────────────────────"
Write-Log "  成功: $okCount 个月"
if ($failCount -gt 0) { Write-Log "  失败: $failCount 个月（使用 -Resume 重试）" -Color Red }
Write-Log "  总计: $($totalTicks.ToString('N0')) ticks → $($totalBars.ToString('N0')) bars"
Write-Log "  输出: $(Resolve-Path $DbPath -ErrorAction SilentlyContinue)"
Write-Log "  耗时: $([math]::Round($totalElapsed.TotalHours, 1)) 小时"
Write-Log "  日志: $LogFile"
Write-Log "═══════════════════════════════════════════" -Color Cyan

if ($failCount -gt 0) {
    Write-Host ""
    Write-Host "有 $failCount 个月导入失败。修复问题后运行:" -ForegroundColor Yellow
    Write-Host "  .\import_all_jinshuyuan.ps1 -Resume" -ForegroundColor White
}
