param(
    [string]$Start = "2021-06-01",
    [string]$End = "2025-12-31",
    [string]$Repo = "c:\Works\ClaudeCode\TradingStudio"
)

$ErrorActionPreference = "Continue"
$dbPath = "$Repo\data\bars_history.duckdb"
$symbolsPath = "$Repo\src\TradingStudio\symbols.json"
$outCsv = "$Repo\data\charts\batch_results.csv"
$projPath = "$Repo\src\TradingStudio\TradingStudio.csproj"

$json = Get-Content $symbolsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$products = $json.symbols | Where-Object { $_.isTop30 -eq $true } | ForEach-Object { $_.code.ToLower() }

Write-Host "=== Top30 Batch Backtest ==="
Write-Host "$($products.Count) products | $Start -> $End"
Write-Host ""

$results = @()
$total = $products.Count; $done = 0; $t0 = Get-Date

foreach ($prod in $products) {
    $done++
    $t1 = Get-Date
    $pct = [int]($done / $total * 100)
    Write-Host ("[{0,2}/{1} {2,3}%] {3,-5} " -f $done, $total, $pct, $prod) -NoNewline

    $config = @{
        StrategyId = "MaCross-$prod"
        StrategyType = "MaCross"
        Instruments = @($prod)
        PrimaryBarType = "bars_1min"
        BarPeriodMinutes = 15
        AllocatedCapital = 1000000
        MaxDrawdownPct = 0.40
        MaxPositionPerInstrument = 2
        Priority = 1
        SessionFilter = "All"
        SkipAuction = $true
        Parameters = @{
            FastPeriod = 10; SlowPeriod = 30
            AtrPeriod = 20; StopAtrMult = 2.0
            RiskPerTrade = 0.02; MaxMarginRatio = 0.25
        }
    }
    $configPath = "$Repo\configs\_batch.json"
    $config | ConvertTo-Json -Depth 4 | Set-Content $configPath -Encoding UTF8 -Force

    try {
        $raw = dotnet run --project $projPath `
            -- backtest --config $configPath --db $dbPath --symbols $symbolsPath `
            --start $Start --end $End 2>&1 | Out-String

        # Parse from report JSON (more reliable than stdout regex)
        $reportPath = [System.IO.Path]::ChangeExtension($configPath, ".report.json")
        $profit = 0; $fe = 0; $wr = 0; $dd = 0; $slip = 0; $fees = 0; $t = 0
        if (Test-Path $reportPath) {
            try {
                $r = Get-Content $reportPath -Raw | ConvertFrom-Json
                $sr = $r.strategyReports[0]
                $t = $sr.totalTrades
                $profit = $sr.totalNetProfit
                $fe = $sr.finalEquity
                $wr = [double]$sr.winRate * 100
                $dd = [double]$sr.maxDrawdown * 100
                $slip = $sr.totalSlippage
                $fees = $sr.totalFees
            } catch {}
            Remove-Item $reportPath -Force -ErrorAction SilentlyContinue
        }
        $bars = if ($raw -match '(\d+)\s+bars\)') { [int]$Matches[1] } else { 0 }
        $warm = if ($raw -match '历史不足') { "NO_DATA" } else { "OK" }

        $elap = [int]((Get-Date) - $t1).TotalSeconds
        if ($t -gt 0) {
            Write-Host ("{0,4}t {1,5}%win {2,14} {3,6}%dd {4,3}s" -f $t, $wr, ("¥{0:N0}" -f $profit), $dd, $elap)
        } else {
            Write-Host ("  NO TRADES ({0}s)" -f $elap)
        }
        $results += [PSCustomObject]@{ Product=$prod; Trades=$t; WinRate=$wr; NetProfit=$profit; FinalEquity=$fe; MaxDD=$dd; Slippage=$slip; Fees=$fees; Bars=$bars; Data=$warm }
    }
    catch {
        Write-Host " ERROR"
        $results += [PSCustomObject]@{ Product=$prod; Trades=0; WinRate=0; NetProfit=0; FinalEquity=0; MaxDD=0; Slippage=0; Fees=0; Bars=0; Data="ERROR" }
    }
    Remove-Item $configPath -Force -ErrorAction SilentlyContinue
}

$elapsed = [int]((Get-Date) - $t0).TotalMinutes
Write-Host ""
Write-Host "=== BATCH COMPLETE ($elapsed min) ==="
Write-Host ""

# Sort by profit desc
$results = $results | Sort-Object NetProfit -Descending

# Table
$hdr = "{0,-5} {1,5} {2,6} {3,14} {4,14} {5,7} {6,9} {7,9} {8,8}"
$hdr -f "Prod","Trd","Win%","NetProfit","FinalEq","MaxDD%","Slippage","Fees","Data"
$hdr -f "----","---","----","---------","--------","------","--------","----","----"
foreach ($r in $results) {
    $np = if ($r.NetProfit -gt 0) { "+¥{0:N0}" -f $r.NetProfit } elseif ($r.NetProfit -lt 0) { "-¥{0:N0}" -f [Math]::Abs($r.NetProfit) } else { "¥0" }
    $fe = "¥{0:N0}" -f $r.FinalEquity
    $hdr -f $r.Product, $r.Trades, "$($r.WinRate)%", $np, $fe, "$($r.MaxDD)%", "¥$($r.Slippage)", "¥$($r.Fees)", $r.Data
}

$results | Export-Csv -Path $outCsv -NoTypeInformation -Encoding UTF8
Write-Host ""
Write-Host "CSV: $outCsv"
$prof = ($results | Where-Object NetProfit -gt 0).Count
Write-Host "Profitable: $prof/$total"
