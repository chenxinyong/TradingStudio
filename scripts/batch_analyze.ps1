# 批量 LLM 分析所有回测报告
# 用法: .\scripts\batch_analyze.ps1 [-Top N] [-Parallel N] [-Force]
param(
    [int]$Top = 0,           # 只分析前 N 个（0 = 全部）
    [int]$Parallel = 1,      # 并发数（API 有限流，建议 1-3）
    [switch]$Force           # 强制覆盖已有分析
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\..

$reports = Get-ChildItem -Path configs -Recurse -Filter "*.report.json" |
    Where-Object { $_.Name -notmatch "\.analysis\." } |
    Sort-Object Length -Descending

if ($Top -gt 0) { $reports = $reports | Select-Object -First $Top }

# 跳过已有分析的（除非 --Force）
$todo = @()
foreach ($r in $reports) {
    $md = $r.FullName -replace '\.report\.json$', '.analysis.md'
    $json = $r.FullName -replace '\.report\.json$', '.analysis.json'
    if (-not $Force -and (Test-Path $md)) {
        continue  # 跳过已完成
    }
    # 跳过空报告（< 1KB 通常无效）
    if ($r.Length -lt 1024) { continue }
    $todo += $r
}

Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
Write-Host "  TradingStudio.Mind — 批量分析" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
Write-Host "  总报告: $($reports.Count) | 待分析: $($todo.Count) | 并发: $Parallel"
Write-Host ""

if ($todo.Count -eq 0) {
    Write-Host "所有报告已分析完毕。" -ForegroundColor Green
    exit 0
}

$total = $todo.Count
$done = 0
$failed = 0
$lock = [object]::new()

$scriptBlock = {
    param($r, $total, $doneRef, $failedRef, $lock)
    $name = $r.Name
    try {
        $output = dotnet run --project src/TradingStudio.ToolBox -- mind -r $r.FullName 2>&1
        $success = $LASTEXITCODE -eq 0
        [void]$lock.GetType().GetMethod("Enter").Invoke($lock, @())
        if ($success) {
            $script:globalDone++
            $strategyLine = ($output | Select-String "→" | Out-String).Trim()
            Write-Host "[$globalDone/$total] $name `t→ $strategyLine" -ForegroundColor Green
        } else {
            $script:globalFailed++
            $err = ($output | Select-String "失败|错误|Error" | Select-Object -First 1 | Out-String).Trim()
            Write-Host "[$globalDone/$total] $name `t✗ $err" -ForegroundColor Red
        }
        [void]$lock.GetType().GetMethod("Exit").Invoke($lock, @())
    } catch {
        [void]$lock.GetType().GetMethod("Enter").Invoke($lock, @())
        $script:globalFailed++
        Write-Host "[$globalDone/$total] $name `t✗ $_" -ForegroundColor Red
        [void]$lock.GetType().GetMethod("Exit").Invoke($lock, @())
    }
}

# PowerShell parallel foreach is simpler
$globalDone = 0
$globalFailed = 0

foreach ($r in $todo) {
    $name = $r.Name
    Write-Host "[$($globalDone + $globalFailed + 1)/$total] $name ..." -NoNewline

    try {
        $output = dotnet run --project src/TradingStudio.ToolBox -- mind -r $r.FullName 2>&1
        if ($LASTEXITCODE -eq 0) {
            $globalDone++
            $strategyLine = ($output | Select-String "→" | Out-String).Trim()
            Write-Host "`r[$globalDone/$total] $name `t→ OK" -ForegroundColor Green
        } else {
            $globalFailed++
            $err = ($output | Select-String "失败|错误|Error" | Select-Object -First 1 | Out-String).Trim()
            Write-Host "`r[$globalDone/$total] $name `t✗ FAIL: $err" -ForegroundColor Red
        }
    } catch {
        $globalFailed++
        Write-Host "`r[$globalDone/$total] $name `t✗ $_" -ForegroundColor Red
    }

    # API 限流保护
    Start-Sleep -Milliseconds 500
}

Write-Host ""
Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
Write-Host "  完成: $globalDone 成功 / $globalFailed 失败 / $total 总计" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
