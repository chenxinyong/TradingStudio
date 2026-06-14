using System.Text.Json;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Data.Engine;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;
using TradingStudio.Engine.Examples;

namespace TradingStudio.Commands;

/// <summary>
/// 回测 CLI 命令。
/// 用法: TradingStudio backtest --config <strategy.json> [--mode bar|tick]
/// </summary>
public class BacktestCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configPath = "";
        var mode = "bar";
        var dbPath = "bars_history.db";
        var symbolsPath = "symbols.json";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] is "--mode" or "-m" && i + 1 < args.Length) mode = args[++i];
            else if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] is "--symbols" && i + 1 < args.Length) symbolsPath = args[++i];
        }

        if (string.IsNullOrEmpty(configPath))
        {
            Console.Error.WriteLine("Usage: TradingStudio backtest --config <strategy.json> [--mode bar|tick] [--db bars_history.db]");
            return 1;
        }

        // 1. 加载配置
        if (!File.Exists(configPath)) { Console.Error.WriteLine($"Config not found: {configPath}"); return 1; }
        var json = File.ReadAllText(configPath);
        var strategyConfig = JsonSerializer.Deserialize<StrategyConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (strategyConfig == null) { Console.Error.WriteLine("Failed to parse config"); return 1; }

        // 2. 加载品种注册表
        if (!File.Exists(symbolsPath))
        {
            // Try alternate paths
            var alt = Path.Combine(AppContext.BaseDirectory, symbolsPath);
            if (File.Exists(alt)) symbolsPath = alt;
            else { Console.Error.WriteLine($"symbols.json not found at {symbolsPath}"); return 1; }
        }
        var registry = FutureRegistry.Load(symbolsPath);

        // 3. 构建引擎组件
        StrategyFactory.Register<MaCrossStrategy>("MaCross");

        var risk = new RiskController();
        var execution = new ExecutionHandler(risk);
        var indicators = new IndicatorManager();
        var strategies = new StrategyContainer();

        var startCapital = strategyConfig.AllocatedCapital > 0 ? strategyConfig.AllocatedCapital : 100_000m;
        var portfolio = new PortfolioManager(startCapital);

        var options = new EngineOptions
        {
            StartTime = DateTime.Parse("2020-01-01"),
            EndTime = DateTime.Parse("2020-03-01"),
            Instruments = strategyConfig.Instruments,
            StrategyConfigs = [strategyConfig],
            StartingCapital = startCapital,
        };

        // 4. 构建数据源
        var store = new BarStore(dbPath);
        var table = strategyConfig.PrimaryBarType ?? "bars_1min";
        var dataFeed = new HistoricalBarFeed(store, table);

        // 5. 创建引擎并运行
        var engine = new TradingEngine(
            dataFeed, execution, portfolio, indicators, strategies,
            risk, options, registry);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var report = await engine.RunAsync(cts.Token);

            // 6. 打印报告
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════");
            Console.WriteLine("  Backtest Report");
            Console.WriteLine("═══════════════════════════════════");
            Console.WriteLine($"  Final Equity:    {report.FinalPortfolio.TotalEquity,12:C}");
            Console.WriteLine($"  Total Return:    {report.TotalReturn,12:P2}");
            Console.WriteLine($"  Max Drawdown:    {report.MaxDrawdown,12:P2}");

            foreach (var sr in report.StrategyReports)
            {
                Console.WriteLine($"  ── {sr.StrategyId} ──");
                Console.WriteLine($"    Starting:      {sr.StartingCapital,12:C}");
                Console.WriteLine($"    Final Equity:  {sr.FinalEquity,12:C}");
                Console.WriteLine($"    Net Profit:    {sr.TotalNetProfit,12:C}");
                Console.WriteLine($"    Trades:        {sr.TotalTrades,12}");
                Console.WriteLine($"    Win Rate:      {sr.WinRate,12:P1}");
                Console.WriteLine($"    Avg Win:       {sr.AverageWin,12:C}");
                Console.WriteLine($"    Avg Loss:      {sr.AverageLoss,12:C}");
                Console.WriteLine($"    Total Fees:    {sr.TotalFees,12:C}");
                Console.WriteLine($"    Max Drawdown:  {sr.MaxDrawdown,12:P2}");
            }
            Console.WriteLine("═══════════════════════════════════");

            // 7. 写出 JSON 报告
            var reportPath = Path.ChangeExtension(configPath, ".report.json");
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(reportPath, reportJson);
            Console.WriteLine($"  Report saved: {reportPath}");

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nBacktest cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Backtest failed: {ex}");
            return 1;
        }
    }
}
