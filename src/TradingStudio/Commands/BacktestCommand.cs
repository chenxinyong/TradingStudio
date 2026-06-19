using System.Text.Json;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Engine;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;

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
        var dbPath = "bars.db";
        var dataDir = "";
        var symbolsPath = "symbols.json";
        var startStr = "";
        var endStr = "";
        var continuousDir = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] is "--mode" or "-m" && i + 1 < args.Length) mode = args[++i];
            else if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] is "--continuous-dir" && i + 1 < args.Length) continuousDir = args[++i];
            else if (args[i] is "--data-dir" && i + 1 < args.Length) dataDir = args[++i];
            else if (args[i] is "--symbols" && i + 1 < args.Length) symbolsPath = args[++i];
            else if (args[i] is "--start" && i + 1 < args.Length) startStr = args[++i];
            else if (args[i] is "--end" && i + 1 < args.Length) endStr = args[++i];
        }

        if (string.IsNullOrEmpty(configPath))
        {
            Console.Error.WriteLine("Usage: TradingStudio backtest --config <strategy.json> [--mode bar|tick] [--db <path>] [--data-dir <csv_dir>]");
            return 1;
        }

        if (mode == "tick" && string.IsNullOrEmpty(dataDir))
        {
            Console.Error.WriteLine("Tick mode requires --data-dir <csv_directory>");
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
        StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);
        StrategyFactory.DiscoverFromAssembly(typeof(TradingStudio.Strategy.ChanLunStrategy).Assembly);

        var risk = new RiskController();
        var feedback = new FeedbackMonitor();
        var tickSnapshot = new TickSnapshot();
        var execution = new ExecutionHandler(risk);
        var indicators = new IndicatorManager();
        var strategies = new StrategyContainer();

        var startCapital = strategyConfig.AllocatedCapital > 0 ? strategyConfig.AllocatedCapital : 100_000m;
        var portfolio = new PortfolioManager(startCapital);

        var startTime = string.IsNullOrEmpty(startStr) ? DateTime.Parse("2020-01-01") : DateTime.Parse(startStr);
        var endTime = string.IsNullOrEmpty(endStr) ? DateTime.Parse("2030-01-01") : DateTime.Parse(endStr);

        // 3.5 展开产品代码 → 具体合约代码
        // 注：纯字母代码（如 "SA"）作为产品级查询，不展开
        var expandedInstruments = ExpandInstruments(strategyConfig.Instruments, registry, startTime, endTime);

        // 预热天数：策略需要历史数据来初始化缠论分析（至少覆盖全回测期 + 额外缓冲）
        var totalDays = (int)(endTime - startTime).TotalDays + 10;
        var warmupDays = Math.Max(totalDays, 365);  // 最少1年，确保缠论笔/中枢有足够数据

        var options = new EngineOptions
        {
            StartTime = startTime,
            EndTime = endTime,
            Instruments = expandedInstruments,
            StrategyConfigs = [strategyConfig],
            StartingCapital = startCapital,
            WarmupDays = warmupDays,
        };

        // 4. 构建数据源
        IDataFeed dataFeed;
        if (mode == "tick")
        {
            dataFeed = new HistoricalTickFeed(dataDir);
        }
        else
        {
            IBarStore store;
            if (!string.IsNullOrEmpty(continuousDir))
                store = new ContinuousBarStore(continuousDir);
            else if (dbPath.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase))
                store = new DuckDBStore(dbPath);
            else
                store = new SqliteBarStore(dbPath);

            var period = strategyConfig.BarPeriodMinutes > 0 ? strategyConfig.BarPeriodMinutes : 1;
            var barTable = !string.IsNullOrEmpty(strategyConfig.PrimaryBarType) ? strategyConfig.PrimaryBarType : "bars_1min";
            dataFeed = new HistoricalBarFeed(store, period, barTable);
        }

        // 5. 创建引擎并运行
        var engine = new TradingEngine(
            dataFeed, execution, portfolio, indicators, strategies,
            risk, feedback, tickSnapshot, options, registry);

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
            Console.WriteLine($"  ── Monitor ──");
            Console.WriteLine($"    Total Slippage:{report.MonitorSummary.TotalSlippage,10:F2}");
            Console.WriteLine($"    Alerts:        {report.MonitorSummary.AlertCount,11}");
            Console.WriteLine($"    Max ConsecLoss:{report.MonitorSummary.MaxConsecutiveLosses,8}");
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

    /// <summary>
    /// 将产品代码（如 "ag"）展开为回测期间内所有可能的合约代码。
    /// 已是完整合约代码的（含数字）直接保留。
    /// </summary>
    private static IReadOnlyList<string> ExpandInstruments(
        IReadOnlyList<string> instruments, FutureRegistry registry,
        DateTime start, DateTime end)
    {
        var expanded = new List<string>();

        foreach (var inst in instruments)
        {
            // 如果包含数字 → 已是完整合约代码，直接使用
            if (inst.Any(char.IsDigit))
            {
                expanded.Add(inst);
                continue;
            }

            // 产品代码 → 检查是否在品种注册表中
            var future = registry.All.Values.FirstOrDefault(f =>
                f.Code.Equals(inst, StringComparison.OrdinalIgnoreCase));
            if (future == null)
            {
                // 未知代码: 可能是产品级别名（如 "SA" → LIKE 查询），直接透传
                Console.WriteLine($"  Product-level: '{inst}' (not in registry, passing through)");
                expanded.Add(inst);
                continue;
            }

            // 产品代码在注册表中 → 作为产品级查询透传（由 BarStore 用 LIKE 匹配所有合约）
            Console.WriteLine($"  Product-level: '{inst}' → will match all {inst}% contracts");
            expanded.Add(inst);
        }

        return expanded;
    }
}
