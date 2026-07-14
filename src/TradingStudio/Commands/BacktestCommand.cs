using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
        var dbPath = ResolveHistoryDb();  // 默认：data/bars_history.duckdb
        var dataDir = "";
        var symbolsPath = "symbols.json";
        var startStr = "";
        var endStr = "";
        var continuousDir = "";
        var enableAi = false;

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
            else if (args[i] is "--ai") enableAi = true;
        }

        if (string.IsNullOrEmpty(configPath))
        {
            Console.Error.WriteLine("Usage: TradingStudio backtest --config <strategy.json> [--mode bar|tick] [--db <path>] [--data-dir <csv_dir>]");
            Console.Error.WriteLine("  Default DB: data/bars_history.duckdb (auto-detected)");
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

        var risk = new RiskController(
            maxPosition: strategyConfig.MaxPositionPerInstrument > 0 ? strategyConfig.MaxPositionPerInstrument : 5,
            maxDrawdown: strategyConfig.MaxDrawdownPct > 0 ? strategyConfig.MaxDrawdownPct : 0.25m,
            maxStrategyDrawdown: strategyConfig.MaxDrawdownPct > 0 ? strategyConfig.MaxDrawdownPct : 0.25m);
        var feedback = new FeedbackMonitor();
        var tickSnapshot = new TickSnapshot();
        var execution = new ExecutionHandler(risk, registry);
        var indicators = new IndicatorManager();
        var strategies = new StrategyContainer();

        var startCapital = strategyConfig.AllocatedCapital > 0 ? strategyConfig.AllocatedCapital : 100_000m;
        var portfolio = new PortfolioManager(startCapital);

        var startTime = string.IsNullOrEmpty(startStr) ? DateTime.Parse("2020-01-01") : DateTime.Parse(startStr);
        var endTime = string.IsNullOrEmpty(endStr) ? DateTime.Parse("2030-01-01") : DateTime.Parse(endStr);

        // 应用 StrategyConfig 中的 IS/OOS 日期约束（覆盖 CLI --start/--end）
        if (strategyConfig.DataStartDate.HasValue)
            startTime = strategyConfig.DataStartDate.Value;
        if (strategyConfig.DataEndDate.HasValue)
            endTime = strategyConfig.DataEndDate.Value;

        switch (strategyConfig.BacktestMode)
        {
            case BacktestMode.Optimization:
                // 仅样本内：endTime 截断到 OptimizationEndDate
                if (strategyConfig.OptimizationEndDate.HasValue)
                    endTime = strategyConfig.OptimizationEndDate.Value;
                Console.WriteLine($"  BacktestMode: Optimization (IS) — {startTime:yyyy-MM-dd} → {endTime:yyyy-MM-dd}");
                break;
            case BacktestMode.Validation:
                // 仅样本外：startTime 从 OptimizationEndDate 开始
                if (strategyConfig.OptimizationEndDate.HasValue)
                    startTime = strategyConfig.OptimizationEndDate.Value;
                Console.WriteLine($"  BacktestMode: Validation (OOS) — {startTime:yyyy-MM-dd} → {endTime:yyyy-MM-dd}");
                Console.WriteLine("  ⚠️  OOS 验证模式：参数必须来自 Optimization 结果，禁止调参！");
                break;
            case BacktestMode.Full:
            default:
                Console.WriteLine($"  BacktestMode: Full — {startTime:yyyy-MM-dd} → {endTime:yyyy-MM-dd}");
                if (strategyConfig.OptimizationEndDate.HasValue)
                    Console.WriteLine($"  💡 IS/OOS 分界点已配置 ({strategyConfig.OptimizationEndDate:yyyy-MM-dd}) 但当前为 Full 模式，未启用分离。");
                break;
        }

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
            SkipAuction = strategyConfig.SkipAuction,
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
                store = new DuckDBStore(dbPath, readOnly: true);
            else
                store = new SqliteBarStore(dbPath);

            var period = strategyConfig.BarPeriodMinutes > 0 ? strategyConfig.BarPeriodMinutes : 1;
            var barTable = !string.IsNullOrEmpty(strategyConfig.PrimaryBarType) ? strategyConfig.PrimaryBarType : "bars_1min";
            dataFeed = new HistoricalBarFeed(store, period, barTable);
        }

        // 5. 创建引擎并运行
        var engine = new TradingEngine(
            dataFeed, execution, portfolio, indicators, strategies,
            risk, feedback, tickSnapshot, options, registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TradingStudio.Engine.TradingEngine>.Instance);

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
                Converters = { new EquityCurveJsonConverter() },
            });
            File.WriteAllText(reportPath, reportJson);
            Console.WriteLine($"  Report saved: {reportPath}");

            // 8. AI 分析（可选）
            if (enableAi)
            {
                Console.WriteLine();
                Console.WriteLine("── AI Analysis ──");
                await RunAiAnalysisAsync(report, strategyConfig, reportPath);
            }

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
    /// 解析默认历史数据库路径。
    /// 优先级：../../data/bars_history.duckdb > data/bars_history.duckdb > bars_history.duckdb
    /// </summary>
    private static string ResolveHistoryDb()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data", "bars_history.duckdb"),
            Path.Combine(AppContext.BaseDirectory, "data", "bars_history.duckdb"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "bars_history.duckdb"),
            "bars_history.duckdb",
        ];
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        // Fallback: return the first candidate so error message is useful
        return Path.GetFullPath(candidates[0]);
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

    /// <summary>
    /// 使用 TradingStudio.Mind 对回测结果进行 AI 分析。
    /// 从 appsettings.local.json 或环境变量加载 API Key。
    /// </summary>
    private static async Task RunAiAnalysisAsync(
        EngineReport report, StrategyConfig config, string reportPath)
    {
        try
        {
            // 加载 Mind 配置（复用 MindTool 的配置加载逻辑）
            var mindConfig = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.local.json", optional: true)
                .AddJsonFile("src/TradingStudio.ToolBox/appsettings.local.json", optional: true)
                .Build();

            var mindOpts = new TradingStudio.Mind.MindOptions();
            mindConfig.GetSection("Mind").Bind(mindOpts);

            // 环境变量覆盖
            var envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey)) mindOpts.AnthropicApiKey = envKey;
            var envUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
            if (!string.IsNullOrWhiteSpace(envUrl)) mindOpts.AnthropicBaseUrl = envUrl;

            mindOpts.Validate();

            var client = TradingStudio.Mind.Clients.MindClientFactory.Create(mindOpts);
            var assembler = new TradingStudio.Mind.Prompts.BacktestContextAssembler();
            var analyst = new TradingStudio.Mind.Analysts.BacktestAnalyst(client, assembler);

            foreach (var perfReport in report.StrategyReports)
            {
                var result = await analyst.AnalyzeAsync(perfReport, config);
                var mdPath = Path.ChangeExtension(reportPath,
                    $".{SanitizeFileName(perfReport.StrategyId)}.analysis.md");
                await File.WriteAllTextAsync(mdPath, result.MarkdownReport, Encoding.UTF8);
                Console.WriteLine($"  AI Report: {mdPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  AI analysis skipped: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray());
    }
}
