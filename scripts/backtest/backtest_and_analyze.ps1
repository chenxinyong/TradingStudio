# 回测 + LLM 分析一键串联
# 用法: .\scripts\backtest_and_analyze.ps1 -Config <strategy.json> [-Db <path>]
param(
    [Parameter(Mandatory=$true)] [string]$Config,
    [string]$Db = "data/bars_history.duckdb"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\..\..

Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
Write-Host "  TradingStudio — 回测 + AI 分析" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ═══ Phase 1: 回测 ═══
Write-Host "[1/2] 回测中..." -ForegroundColor Yellow
$backtestResult = dotnet run --project src/TradingStudio -- backtest --config $Config --db $Db 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "回测失败:" -ForegroundColor Red
    Write-Host ($backtestResult | Out-String)
    exit $LASTEXITCODE
}
Write-Host ($backtestResult | Select-String "Report saved|Equity|Return|Drawdown" | Out-String)

# ═══ Phase 2: 查找报告 ═══
$reportPath = [System.IO.Path]::ChangeExtension($Config, ".report.json")
if (-not (Test-Path $reportPath)) {
    # 可能在不同目录
    $altPath = Join-Path "configs" ([System.IO.Path]::GetFileName($reportPath))
    if (Test-Path $altPath) { $reportPath = $altPath }
    else {
        Write-Error "找不到回测报告: $reportPath"
        exit 1
    }
}

# ═══ Phase 3: LLM 分析 ═══
Write-Host ""
Write-Host "[2/2] LLM 分析中..." -ForegroundColor Yellow
$mindResult = dotnet run --project src/TradingStudio.ToolBox -- mind -r $reportPath -c $Config 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "LLM 分析失败:" -ForegroundColor Red
    Write-Host ($mindResult | Out-String)
    exit $LASTEXITCODE
}

# ═══ Phase 4: 展示结果 ═══
Write-Host ""
Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
Write-Host "  分析完成" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════" -ForegroundColor Cyan

Get-ChildItem ($reportPath -replace '\.report\.json$', '*.analysis.md') | ForEach-Object {
    Write-Host "  $($_.Name)" -ForegroundColor Green
}
Write-Host ""
Write-Host "查看报告: code $($reportPath -replace '\.report\.json$', '.analysis.md')"
