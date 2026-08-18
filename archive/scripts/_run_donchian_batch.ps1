param($StartDate = "2024-01-01", $EndDate = "2024-06-30")

$instruments = @("rb000", "ag000", "ma000", "sa000", "ta000", "fg000", "v000", "i000", "p000", "ru000")

$projDir = "src/TradingStudio"
$dbPath = "../../data/bars_history.duckdb"
$configDir = "configs/batch"

foreach ($inst in $instruments) {
    $configId = "Donchian-${inst}-15min"
    $config = @{
        StrategyId = $configId
        StrategyType = "DonchianTrend"
        Instruments = @($inst)
        PrimaryBarType = "bars_1min"
        BarPeriodMinutes = 15
        AllocatedCapital = 500000
        MaxDrawdownPct = 0.30
        MaxPositionPerInstrument = 60
        Priority = 1
        SessionFilter = "All"
        SkipAuction = $true
        Parameters = @{
            ChannelPeriod = 20
            ExitPeriod = 10
            TrendMAPeriod = 50
            AtrPeriod = 20
            StopAtrMult = 2.0
            TakeProfitAtrMult = 3.0
            MinVolatility = 0.002
            RiskPerTrade = 0.02
            MaxBarsInTrade = 0
            ReentryCooldown = 3
        }
    } | ConvertTo-Json -Depth 3

    $configPath = "$configDir/donchian_$inst.json"
    New-Item -Force -Path (Split-Path $configPath -Parent) -ItemType Directory | Out-Null
    $config | Out-File -Encoding utf8 $configPath

    Write-Host "=== $configId ==="
    & dotnet run --no-build --project $projDir -- backtest --config $configPath --db $dbPath --start $StartDate --end $EndDate 2>&1 | Select-String "Trades|Net Profit|Win Rate|Max Drawdown|Total Return"
    Write-Host ""
}
