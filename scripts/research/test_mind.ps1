# TradingStudio.Mind 测试脚本
# 用法: .\scripts\research\test_mind.ps1

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\..\..

Write-Host "════════════════════════════════" -ForegroundColor Cyan
Write-Host "  TradingStudio.Mind — LLM 分析测试" -ForegroundColor Cyan
Write-Host "════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# 选择一个有数据的报告
$reportPath = "configs\sma_macd_sa000_15min.report.json"

if (-not (Test-Path $reportPath)) {
    Write-Error "报告不存在: $reportPath"
    exit 1
}

Write-Host "报告: $reportPath"
Write-Host "Provider: DeepSeek Anthropic"
Write-Host "Model: deepseek-v4-pro"
Write-Host ""

dotnet run --project src/TradingStudio.ToolBox -- mind -r $reportPath

Write-Host ""
Write-Host "════════════════════════════════" -ForegroundColor Cyan
Write-Host "检查输出文件:"
Get-ChildItem configs\sma_macd_sa000_15min*.analysis.md 2>$null | ForEach-Object {
    Write-Host "  $($_.FullName) ($($_.Length) bytes)" -ForegroundColor Green
}
