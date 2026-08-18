# MaCross + ADX 趋势过滤 — 参数扫描脚本
# 用法: .\scripts\run_macross_adx_sweep.ps1 [-BaselineOnly] [-SweepOnly] [-DbPath <path>]
param(
    [switch]$BaselineOnly,
    [switch]$SweepOnly,
    [string]$DbPath = "C:\Works\Datas\bars_history.duckdb",
    [string]$ConfigDir = "configs\macross_adx_sweep",
    [string]$ProjectPath = "src\TradingStudio\TradingStudio.csproj"
)

$ErrorActionPreference = "Stop"
$script:StartTime = Get-Date

# ═══ 品种配置 ═══
$Symbols = @("rb000", "MA000", "ag000", "TA000", "FG000")
$SymbolNames = @{
    "rb000" = "螺纹钢"
    "MA000" = "甲醇"
    "ag000" = "白银"
    "TA000" = "PTA"
    "FG000" = "玻璃"
}

# ═══ 固定参数 ═══
$BaseParams = @{
    PrimaryBarType    = "bars_1min"
    BarPeriodMinutes  = 15
    AllocatedCapital  = 500000
    MaxPositionPerInstrument = 3
    AtrPeriod         = 20
    StopAtrMult       = 2.0
    TakeProfitAtrMult = 2.5
    MaxMarginRatio    = 0.35
    AdxPeriod         = 14
    MinAdx            = 20
    DailyTrendFilter  = $true
    DailyTrendPeriod  = 50
    DataStartDate     = "2024-01-01"
    DataEndDate       = "2026-06-30"
    SkipAuction       = $true
}

# ═══ 参数扫描维度 ═══
$FastPeriods  = @(5, 10, 20)
$SlowPeriods  = @(20, 34, 50)
$RiskPerTrade = @(0.01, 0.02, 0.03)

# ═══ 通用配置模板 ═══
function New-StrategyConfig {
    param(
        [string]$StrategyId,
        [string]$Symbol,
        [hashtable]$Overrides
    )
    $p = $BaseParams.Clone()
    foreach ($k in $Overrides.Keys) { $p[$k] = $Overrides[$k] }

    $config = @{
        StrategyId              = $StrategyId
        StrategyType            = "MaCross"
        Description             = "$($SymbolNames[$Symbol]) 双均线+ADX+日线趋势过滤"
        Version                 = 1
        Instruments             = @($Symbol)
        PrimaryBarType          = $p.PrimaryBarType
        BarPeriodMinutes        = $p.BarPeriodMinutes
        AllocatedCapital        = $p.AllocatedCapital
        MaxPositionPerInstrument = $p.MaxPositionPerInstrument
        SkipAuction             = $p.SkipAuction
        Parameters              = @{
            FastPeriod          = $p.FastPeriod
            SlowPeriod          = $p.SlowPeriod
            AtrPeriod           = $p.AtrPeriod
            StopAtrMult         = $p.StopAtrMult
            TakeProfitAtrMult   = $p.TakeProfitAtrMult
            RiskPerTrade        = $p.RiskPerTrade
            MaxMarginRatio      = $p.MaxMarginRatio
            AdxPeriod           = $p.AdxPeriod
            MinAdx              = $p.MinAdx
            DailyTrendFilter    = $p.DailyTrendFilter
            DailyTrendPeriod    = $p.DailyTrendPeriod
        }
        DataStartDate           = $p.DataStartDate
        DataEndDate             = $p.DataEndDate
    }
    return $config
}

# ═══ 生成配置 JSON ═══
function Write-ConfigFile {
    param([string]$Path, [hashtable]$Config)
    $json = $Config | ConvertTo-Json -Depth 5
    # 确保 Boolean 小写 (PowerShell ConvertTo-Json 输出 True/False)
    $json = $json -replace ': True', ': true' -replace ': False', ': false'
    $json | Out-File -FilePath $Path -Encoding utf8
}

# ═══ 运行单个回测 ═══
function Invoke-SingleBacktest {
    param([string]$ConfigPath, [string]$DbPath, [string]$ProjectPath)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $result = & dotnet run --project $ProjectPath --no-build -c Release -- backtest --config $ConfigPath --db $DbPath 2>&1
    $sw.Stop()

    $reportPath = [System.IO.Path]::ChangeExtension($ConfigPath, ".report.json")
    $success = ($LASTEXITCODE -eq 0) -and (Test-Path $reportPath)

    return @{
        ConfigPath  = $ConfigPath
        ReportPath  = $reportPath
        Success     = $success
        DurationSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Output      = $result -join "`n"
    }
}

# ═══ 解析报告摘要 ═══
function Get-ReportSummary {
    param([string]$ReportPath)
    if (-not (Test-Path $ReportPath)) { return $null }
    try {
        $r = Get-Content $ReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $sr = $r.strategyReports[0]
        return @{
            StrategyId   = $sr.strategyId
            NetProfit    = [double]$sr.totalNetProfit
            TotalReturn  = [double]$sr.totalReturn
            Trades       = [int]$sr.totalTrades
            WinRate      = [double]$sr.winRate
            MaxDrawdown  = [double]$sr.maxDrawdown
            AvgWin       = [double]$sr.averageWin
            AvgLoss      = [double]$sr.averageLoss
            TotalFees    = [double]$sr.totalFees
            SharpeRatio  = if ($sr.PSObject.Properties.Name -contains 'sharpeRatio') { [double]$sr.sharpeRatio } else { 0 }
        }
    } catch { return $null }
}

# ══════════════════════════════════════
# 第一阶段：Baseline 回测 (5个品种)
# ══════════════════════════════════════
function Run-Baseline {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  第一阶段: Baseline ADX 回测 (MinAdx=20, DailyTrendFilter=true)" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

    $baselineResults = @()

    foreach ($sym in $Symbols) {
        $id = "macross-adx-$($sym.ToLower())-15min"
        $configPath = Join-Path $ConfigDir "$id.json"

        $config = New-StrategyConfig -StrategyId $id -Symbol $sym -Overrides @{
            FastPeriod   = 10
            SlowPeriod   = 30
            RiskPerTrade = 0.02
        }
        Write-ConfigFile -Path $configPath -Config $config

        Write-Host "  [$($SymbolNames[$sym])] 运行中..." -NoNewline -ForegroundColor Yellow
        $result = Invoke-SingleBacktest -ConfigPath $configPath -DbPath $DbPath -ProjectPath $ProjectPath

        if ($result.Success) {
            $summary = Get-ReportSummary $result.ReportPath
            if ($summary) {
                Write-Host " ✓ NetProfit=$($summary.NetProfit.ToString('N0'))  Return=$($summary.TotalReturn.ToString('P1'))  Trades=$($summary.Trades)  WinRate=$($summary.WinRate.ToString('P1'))  DD=$($summary.MaxDrawdown.ToString('P1'))  [${($result.DurationSec)}s]" -ForegroundColor Green
                $summary | Add-Member -NotePropertyName Symbol -NotePropertyValue $sym
                $baselineResults += $summary
            } else {
                Write-Host " ⚠️ 报告解析失败 [${($result.DurationSec)}s]" -ForegroundColor Yellow
            }
        } else {
            Write-Host " ❌ 失败 [${($result.DurationSec)}s]" -ForegroundColor Red
        }
    }

    return $baselineResults
}

# ══════════════════════════════════════
# 第二阶段：参数扫描 (Fast × Slow × Risk)
# ══════════════════════════════════════
function Run-Sweep {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  第二阶段: 参数扫描 快线×慢线×风险 ($($FastPeriods.Count)×$($SlowPeriods.Count)×$($RiskPerTrade.Count)=$($FastPeriods.Count * $SlowPeriods.Count * $RiskPerTrade.Count)组合/品种)" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

    $sweepResults = @()
    $total = $Symbols.Count * $FastPeriods.Count * $SlowPeriods.Count * $RiskPerTrade.Count
    $done = 0

    foreach ($sym in $Symbols) {
        foreach ($fast in $FastPeriods) {
            foreach ($slow in $SlowPeriods) {
                # 快线必须 < 慢线
                if ($fast -ge $slow) { continue }

                foreach ($risk in $RiskPerTrade) {
                    $done++
                    $id = "macross-sweep-$($sym.ToLower())-f${fast}s${slow}r$($risk.ToString('F2').Replace('.',''))"
                    $configPath = Join-Path $ConfigDir "$id.json"

                    $overrides = @{
                        FastPeriod   = $fast
                        SlowPeriod   = $slow
                        RiskPerTrade = $risk
                    }
                    $config = New-StrategyConfig -StrategyId $id -Symbol $sym -Overrides $overrides
                    Write-ConfigFile -Path $configPath -Config $config

                    $pct = [math]::Round($done / $total * 100, 1)
                    Write-Host "  [$done/$total ${pct}%] $sym f=$fast s=$slow r=$risk ..." -NoNewline -ForegroundColor Yellow
                    $result = Invoke-SingleBacktest -ConfigPath $configPath -DbPath $DbPath -ProjectPath $ProjectPath

                    if ($result.Success) {
                        $summary = Get-ReportSummary $result.ReportPath
                        if ($summary) {
                            $summary | Add-Member -NotePropertyName Symbol -NotePropertyValue $sym
                            $summary | Add-Member -NotePropertyName FastPeriod -NotePropertyValue $fast
                            $summary | Add-Member -NotePropertyName SlowPeriod -NotePropertyValue $slow
                            $summary | Add-Member -NotePropertyName RiskPerTrade -NotePropertyValue $risk
                            $sweepResults += $summary
                            Write-Host " ✓ NP=$($summary.NetProfit.ToString('N0')) R=$($summary.TotalReturn.ToString('P1')) T=$($summary.Trades) WR=$($summary.WinRate.ToString('P1')) DD=$($summary.MaxDrawdown.ToString('P1'))" -ForegroundColor Green
                        } else {
                            Write-Host " ⚠️ 报告解析失败" -ForegroundColor Yellow
                        }
                    } else {
                        Write-Host " ❌ 失败" -ForegroundColor Red
                    }
                }
            }
        }
    }

    return $sweepResults
}

# ══════════════════════════════════════
# 汇总报告
# ══════════════════════════════════════
function Write-SummaryReport {
    param($BaselineResults, $SweepResults)

    Write-Host ""
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  汇总报告" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

    $elapsed = [math]::Round(((Get-Date) - $script:StartTime).TotalMinutes, 1)

    # ── Baseline 对比表 ──
    if ($BaselineResults -and $BaselineResults.Count -gt 0) {
        Write-Host ""
        Write-Host "── Baseline 结果 (Fast=10, Slow=30, Risk=2%, MinAdx=20, DailyTrend=true) ──" -ForegroundColor White
        Write-Host ("{0,-8} {1,>12} {2,>9} {3,>6} {4,>7} {5,>9} {6,>10}" -f "品种","NetProfit","Return","Trades","WinRate","MaxDD","Sharpe")
        Write-Host ("{0,-8} {1,>12} {2,>9} {3,>6} {4,>7} {5,>9} {6,>10}" -f "----","--------","------","------","-------","------","------")

        $sorted = $BaselineResults | Sort-Object TotalReturn -Descending
        foreach ($r in $sorted) {
            Write-Host ("{0,-8} {1,12:N0} {2,9:P1} {3,6} {4,7:P1} {5,9:P1} {6,10:F2}" -f `
                $r.Symbol, $r.NetProfit, $r.TotalReturn, $r.Trades, $r.WinRate, $r.MaxDrawdown, $r.SharpeRatio)
        }
    }

    # ── 参数扫描 Top 10 ──
    if ($SweepResults -and $SweepResults.Count -gt 0) {
        Write-Host ""
        Write-Host "── 参数扫描 Top 10 (按 TotalReturn 排序) ──" -ForegroundColor White
        Write-Host ("{0,-8} {1,>4} {2,>4} {3,>5} {4,>12} {5,>9} {6,>6} {7,>7} {8,>9}" -f `
            "Symbol","Fast","Slow","Risk","NetProfit","Return","Trades","WinRate","MaxDD")
        Write-Host ("{0,-8} {1,>4} {2,>4} {3,>5} {4,>12} {5,>9} {6,>6} {7,>7} {8,>9}" -f `
            "------","----","----","-----","--------","------","------","-------","------")

        $top10 = $SweepResults | Sort-Object TotalReturn -Descending | Select-Object -First 10
        foreach ($r in $top10) {
            Write-Host ("{0,-8} {1,4} {2,4} {3,5:P0} {4,12:N0} {5,9:P1} {6,6} {7,7:P1} {8,9:P1}" -f `
                $r.Symbol, $r.FastPeriod, $r.SlowPeriod, $r.RiskPerTrade,
                $r.NetProfit, $r.TotalReturn, $r.Trades, $r.WinRate, $r.MaxDrawdown)
        }

        # ── 每个品种的最佳参数 ──
        Write-Host ""
        Write-Host "── 每个品种的最佳参数 (按 TotalReturn) ──" -ForegroundColor White
        Write-Host ("{0,-8} {1,>4} {2,>4} {3,>5} {4,>12} {5,>9} {6,>6} {7,>7} {8,>9}" -f `
            "Symbol","Fast","Slow","Risk","NetProfit","Return","Trades","WinRate","MaxDD")
        Write-Host ("{0,-8} {1,>4} {2,>4} {3,>5} {4,>12} {5,>9} {6,>6} {7,>7} {8,>9}" -f `
            "------","----","----","-----","--------","------","------","-------","------")

        foreach ($sym in $Symbols) {
            $best = $SweepResults | Where-Object { $_.Symbol -eq $sym } | Sort-Object TotalReturn -Descending | Select-Object -First 1
            if ($best) {
                Write-Host ("{0,-8} {1,4} {2,4} {3,5:P0} {4,12:N0} {5,9:P1} {6,6} {7,7:P1} {8,9:P1}" -f `
                    $best.Symbol, $best.FastPeriod, $best.SlowPeriod, $best.RiskPerTrade,
                    $best.NetProfit, $best.TotalReturn, $best.Trades, $best.WinRate, $best.MaxDrawdown)
            }
        }
    }

    # ── CSV 导出 ──
    if ($SweepResults -and $SweepResults.Count -gt 0) {
        $csvPath = Join-Path $ConfigDir "sweep_summary.csv"
        $SweepResults | Select-Object Symbol,FastPeriod,SlowPeriod,RiskPerTrade,NetProfit,TotalReturn,Trades,WinRate,MaxDrawdown,AvgWin,AvgLoss,TotalFees,SharpeRatio `
            | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Write-Host ""
        Write-Host "  CSV 已导出: $csvPath" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "  总耗时: ${elapsed} min" -ForegroundColor Gray
}

# ═══ Main ═══
Write-Host "MaCross + ADX 趋势过滤 — 参数扫描" -ForegroundColor Magenta
Write-Host "数据库: $DbPath" -ForegroundColor Gray
Write-Host "配置目录: $ConfigDir" -ForegroundColor Gray
Write-Host "品种: $($Symbols -join ', ')" -ForegroundColor Gray
Write-Host "参数网格: Fast=$($FastPeriods -join ',') × Slow=$($SlowPeriods -join ',') × Risk=$($RiskPerTrade -join ',')" -ForegroundColor Gray
Write-Host ""

$baseline = @()
$sweep = @()

if (-not $SweepOnly) {
    $baseline = Run-Baseline
}

if (-not $BaselineOnly) {
    $sweep = Run-Sweep
}

Write-SummaryReport -BaselineResults $baseline -SweepResults $sweep

Write-Host ""
Write-Host "完成!" -ForegroundColor Green
