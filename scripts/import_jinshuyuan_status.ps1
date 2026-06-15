<#
.SYNOPSIS
    查看并行导入进度 — 读取各年份的 progress.json

.DESCRIPTION
    当 import_jinshuyuan_parallel.ps1 在运行时，此脚本可随时查看各年份导入进度。
    也支持 watch 模式持续刷新。

.PARAMETER OutputDir
    输出 SQLite 数据库目录（需与导入脚本一致）

.PARAMETER Years
    要查看的年份

.PARAMETER Watch
    持续刷新模式（每 10 秒）

.EXAMPLE
    .\import_jinshuyuan_status.ps1

.EXAMPLE
    .\import_jinshuyuan_status.ps1 -Watch
#>

param(
    [Parameter()]
    [string]$OutputDir = "C:\Works\ClaudeCode\TradingStudio\data",

    [Parameter()]
    [int[]]$Years = @(2021, 2022, 2023, 2024, 2025),

    [Parameter()]
    [switch]$Watch
)

function Show-Status {
    Clear-Host
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  Jinshuyuan Import Status" -ForegroundColor Cyan
    Write-Host "  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
    Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""

    $totalBars = 0
    $totalTicks = 0
    $overallProgress = 0
    $yearCount = 0

    foreach ($year in $Years) {
        $progressFile = Join-Path $OutputDir "bars_$year.progress.json"
        $dbPath = Join-Path $OutputDir "bars_$year.db"

        if (Test-Path $progressFile) {
            try {
                $pj = Get-Content $progressFile -Raw | ConvertFrom-Json
                $pct = $pj.percent
                $totalTicks += $pj.totalTicks
                $totalBars += $pj.totalBars

                # 进度条
                $barLen = 30
                $filled = [Math]::Floor($pct / 100 * $barLen)
                $empty = $barLen - $filled
                $bar = "[" + ("#" * $filled) + ("-" * $empty) + "]"

                $color = if ($pct -ge 100) { "Green" } else { "Cyan" }
                Write-Host "  $year  $bar  $($pct.ToString('F1'))%  |  $($pj.progress)  |  $($pj.totalTicks.ToString('N0')) ticks → $($pj.totalBars.ToString('N0')) bars  |  $($pj.elapsed) / ETA $($pj.eta)" -ForegroundColor $color
                $overallProgress += $pct
                $yearCount++
            }
            catch {}
        } elseif (Test-Path $dbPath) {
            $sizeGB = "{0:N1} GB" -f ((Get-Item $dbPath).Length / 1GB)
            Write-Host "  $year  [COMPLETE]  DB: $sizeGB" -ForegroundColor Green
            $overallProgress += 100
            $yearCount++
        } else {
            Write-Host "  $year  [NOT STARTED]" -ForegroundColor DarkGray
        }
    }

    if ($yearCount -gt 0) {
        $avg = $overallProgress / $yearCount
        Write-Host ""
        Write-Host "  Overall: $($avg.ToString('F1'))%  |  $($totalTicks.ToString('N0')) ticks → $($totalBars.ToString('N0')) bars" -ForegroundColor Yellow
    }
    Write-Host ""
}

do {
    Show-Status
    if ($Watch) { Start-Sleep -Seconds 10 }
} while ($Watch)
