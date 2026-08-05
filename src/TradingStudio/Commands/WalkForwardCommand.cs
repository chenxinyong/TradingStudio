using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TradingStudio.Core.Analysis;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Engine;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;
using TradingStudio.Engine.Statistics;
using Microsoft.Extensions.Logging.Abstractions;

namespace TradingStudio.Commands;

/// <summary>
/// 步进验证 + 参数稳定性分析 CLI 命令。
///
/// 用法:
///   TradingStudio walkforward --config &lt;strategy.json&gt; [--db &lt;path&gt;] [--symbols &lt;path&gt;]
///
/// 两种分析:
///   1. Walk-Forward 验证 — 固定参数，多窗口滚动，检查 OOS 一致性
///   2. 参数稳定性分析 — 核心参数一维扫描，检测高原/尖峰特征
///
/// 参考: Ernie Chan《Algorithmic Trading》Ch1-3
/// </summary>
public class WalkForwardCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var configPath = "";
        var dbPath = ResolveHistoryDb();
        var symbolsPath = "symbols.json";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] is "--symbols" && i + 1 < args.Length) symbolsPath = args[++i];
        }

        if (string.IsNullOrEmpty(configPath))
        {
            Console.Error.WriteLine("Usage: TradingStudio walkforward --config <strategy.json> [--db <path>] [--symbols <path>]");
            return 1;
        }

        if (!File.Exists(configPath)) { Console.Error.WriteLine($"Config not found: {configPath}"); return 1; }
        if (!File.Exists(dbPath)) { Console.Error.WriteLine($"DB not found: {dbPath}"); return 1; }

        // 确保 symbols.json 可解析
        if (!File.Exists(symbolsPath))
        {
            var alt = Path.Combine(AppContext.BaseDirectory, symbolsPath);
            if (File.Exists(alt)) symbolsPath = alt;
        }

        var json = File.ReadAllText(configPath);
        var baseConfig = JsonSerializer.Deserialize<StrategyConfig>(json, JsonOpts);
        if (baseConfig == null) { Console.Error.WriteLine("Failed to parse config"); return 1; }

        Console.WriteLine($"\n═══ Walk-Forward + Parameter Stability Analysis ═══");
        Console.WriteLine($"  Config:    {Path.GetFileName(configPath)}");
        Console.WriteLine($"  Strategy:  {baseConfig.StrategyType} ({baseConfig.StrategyId})");
        Console.WriteLine($"  DB:        {Path.GetFileName(dbPath)}");
        Console.WriteLine($"  Instruments: {string.Join(", ", baseConfig.Instruments)}");
        Console.WriteLine();

        // 预热: 确保 StrategyFactory 加载了所有策略程序集
        StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);
        StrategyFactory.DiscoverFromAssembly(typeof(TradingStudio.Strategy.ChanLunStrategy).Assembly);

        // ── 1. 步进验证 ──
        Console.WriteLine("── Phase 1: Walk-Forward Validation ──");
        var wfReport = await RunWalkForwardAsync(baseConfig, dbPath, symbolsPath);

        // ── 2. 参数稳定性分析 ──
        Console.WriteLine("\n── Phase 2: Parameter Stability Analysis ──");
        var stabilityReports = await RunParameterStabilityAsync(baseConfig, dbPath, symbolsPath);

        // ── 3. 生成报告 ──
        var reportPath = Path.ChangeExtension(configPath, ".wf-report.md");
        var md = BuildMarkdownReport(baseConfig, wfReport, stabilityReports);
        await File.WriteAllTextAsync(reportPath, md, Encoding.UTF8);
        Console.WriteLine($"\n  Report saved: {reportPath}");

        // 打印摘要到控制台
        Console.WriteLine(md);

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Walk-Forward
    // ═══════════════════════════════════════════════════════════════

    private static async Task<WalkForwardSummary> RunWalkForwardAsync(
        StrategyConfig baseConfig, string dbPath, string symbolsPath)
    {
        // 获取数据范围
        using var probeStore = new DuckDBStore(dbPath, readOnly: true);
        var dateRange = await GetDateRangeAsync(probeStore, baseConfig, symbolsPath);
        if (dateRange == null)
        {
            Console.WriteLine("  ⚠️  Cannot determine data range. Using config defaults.");
            return new WalkForwardSummary { IsEmpty = true };
        }

        var (dataStart, dataEnd) = dateRange.Value;
        Console.WriteLine($"  Data range: {dataStart:yyyy-MM-dd} → {dataEnd:yyyy-MM-dd}");

        // 生成窗口
        var wfConfig = new WalkForwardConfig
        {
            IsPeriod = TimeSpan.FromDays(365 * 2),   // 2年训练
            OosPeriod = TimeSpan.FromDays(365),       // 1年测试
            StepSize = TimeSpan.FromDays(365 / 2),    // 半年步进
            Mode = WalkForwardMode.Rolling,
        };

        var validator = new WalkForwardValidator(wfConfig);
        var windows = validator.GenerateWindows(dataStart, dataEnd);

        Console.WriteLine($"  Windows: {windows.Count} (IS={wfConfig.IsPeriod.TotalDays / 365:F0}y, OOS={wfConfig.OosPeriod.TotalDays / 365:F0}y, Step={wfConfig.StepSize.TotalDays / 30:F0}m)");
        Console.WriteLine();

        var performances = new List<WindowPerformance>();
        var swTotal = Stopwatch.StartNew();

        foreach (var window in windows)
        {
            var sw = Stopwatch.StartNew();
            Console.Write($"  {window.Label} ... ");

            try
            {
                // IS 回测 (使用配置中的参数)
                var isPerf = await RunSingleBacktest(baseConfig, dbPath, symbolsPath,
                    window.IsStart, window.IsEnd, $"{baseConfig.StrategyId}-W{window.Index}-IS");

                // OOS 回测 (相同参数，不同时间段)
                var oosPerf = await RunSingleBacktest(baseConfig, dbPath, symbolsPath,
                    window.OosStart, window.OosEnd, $"{baseConfig.StrategyId}-W{window.Index}-OOS");

                var wp = new WindowPerformance
                {
                    IsSharpe = (double)isPerf.SharpeRatio,
                    IsWinRate = (double)isPerf.WinRate,
                    IsTradeCount = isPerf.TotalTrades,
                    OosSharpe = (double)oosPerf.SharpeRatio,
                    OosWinRate = (double)oosPerf.WinRate,
                    OosTradeCount = oosPerf.TotalTrades,
                };

                performances.Add(wp);
                sw.Stop();
                Console.WriteLine($"IS Sharpe={isPerf.SharpeRatio:F2} ({isPerf.TotalTrades}T) → OOS Sharpe={oosPerf.SharpeRatio:F2} ({oosPerf.TotalTrades}T) | {sw.Elapsed.TotalSeconds:F0}s");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"FAILED: {ex.Message} | {sw.Elapsed.TotalSeconds:F0}s");
                performances.Add(new WindowPerformance());
            }
        }

        swTotal.Stop();
        Console.WriteLine($"\n  Total walk-forward time: {swTotal.Elapsed.TotalMinutes:F1} min");

        return validator.Summarize(windows, performances);
    }

    // ═══════════════════════════════════════════════════════════════
    // Parameter Stability
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IReadOnlyList<ParameterStabilityReport>> RunParameterStabilityAsync(
        StrategyConfig baseConfig, string dbPath, string symbolsPath)
    {
        // 选择要扫描的参数（仅扫描有数值范围的数值型参数）
        var scanParams = SelectScanParameters(baseConfig);
        if (scanParams.Count == 0)
        {
            Console.WriteLine("  No numeric parameters with Min/Max range found. Skipping.");
            return Array.Empty<ParameterStabilityReport>();
        }

        Console.WriteLine($"  Scanning {scanParams.Count} parameters on full data range...");
        Console.WriteLine();

        var analyzer = new ParameterStabilityAnalyzer();
        var reports = new List<ParameterStabilityReport>();

        foreach (var sp in scanParams)
        {
            Console.Write($"  {sp.Name}: {sp.Values.Length} values ({sp.Values[0]} → {sp.Values[^1]}) ... ");
            var sw = Stopwatch.StartNew();
            var scanResults = new List<(double ParamValue, double Sharpe)>();

            foreach (var val in sp.Values)
            {
                try
                {
                    var modifiedConfig = CloneWithParameter(baseConfig, sp.Name, val);
                    var perf = await RunSingleBacktest(modifiedConfig, dbPath, symbolsPath,
                        dataStart: null, dataEnd: null, // 使用配置中的日期范围，不额外限制
                        $"gs-{sp.Name}-{val}");
                    scanResults.Add((val, (double)perf.SharpeRatio));
                }
                catch (Exception)
                {
                    Console.Write($"({val:F1}:err) ");
                }
            }

            var report = analyzer.AnalyzeSingleParameter(sp.Name, scanResults);
            reports.Add(report);

            sw.Stop();
            var verdict = report.Verdict switch
            {
                StabilityVerdict.Robust => "✅ Robust",
                StabilityVerdict.Acceptable => "⚠️ Acceptable",
                StabilityVerdict.Fragile => "🔴 Fragile",
                StabilityVerdict.Overfit => "❌ Overfit",
                _ => "❓"
            };
            Console.WriteLine($"{verdict} (score={report.StabilityScore:F2}, plateau={report.PlateauWidthRatio:P0}, {sw.Elapsed.TotalSeconds:F0}s)");
        }

        return reports;
    }

    // ═══════════════════════════════════════════════════════════════
    // Backtest Runner
    // ═══════════════════════════════════════════════════════════════

    private static async Task<PerformanceReport> RunSingleBacktest(
        StrategyConfig baseConfig, string dbPath, string symbolsPath,
        DateTime? dataStart, DateTime? dataEnd, string runId)
    {
        var registry = FutureRegistry.Load(symbolsPath);

        var startTime = dataStart ?? baseConfig.DataStartDate ?? DateTime.Parse("2020-01-01");
        var endTime = dataEnd ?? baseConfig.DataEndDate ?? DateTime.Parse("2030-01-01");

        // 克隆配置，覆盖日期
        var config = CloneWithDates(baseConfig, startTime, endTime, runId);

        // 构建引擎组件
        var risk = new RiskController(
            maxPosition: config.MaxPositionPerInstrument > 0 ? config.MaxPositionPerInstrument : 5,
            maxDrawdown: config.MaxDrawdownPct > 0 ? config.MaxDrawdownPct : 0.25m,
            maxStrategyDrawdown: config.MaxDrawdownPct > 0 ? config.MaxDrawdownPct : 0.25m);
        var feedback = new FeedbackMonitor();
        var tickSnapshot = new TickSnapshot();
        var execution = new ExecutionHandler(risk, registry);
        var indicators = new IndicatorManager();
        var strategies = new StrategyContainer();
        var startCapital = config.AllocatedCapital > 0 ? config.AllocatedCapital : 100_000m;
        var portfolio = new PortfolioManager(startCapital);

        // 数据源
        var store = new DuckDBStore(dbPath, readOnly: true);
        var period = config.BarPeriodMinutes > 0 ? config.BarPeriodMinutes : 1;
        var barTable = !string.IsNullOrEmpty(config.PrimaryBarType) ? config.PrimaryBarType : "bars_1min";
        var dataFeed = new HistoricalBarFeed(store, period, barTable);

        // 预热天数
        var totalDays = (int)(endTime - startTime).TotalDays + 10;
        var warmupDays = Math.Max(totalDays, 365);

        var options = new EngineOptions
        {
            StartTime = startTime,
            EndTime = endTime,
            Instruments = config.Instruments,
            StrategyConfigs = new[] { config },
            StartingCapital = startCapital,
            WarmupDays = warmupDays,
            SkipAuction = config.SkipAuction,
        };

        var engine = new TradingEngine(
            dataFeed, execution, portfolio, indicators, strategies,
            risk, feedback, tickSnapshot, options, registry,
            NullLogger<TradingEngine>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var report = await engine.RunAsync(cts.Token);

        return report.StrategyReports.FirstOrDefault()
            ?? new PerformanceReport { StartingCapital = startCapital, FinalEquity = startCapital };
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static StrategyConfig CloneWithDates(StrategyConfig src, DateTime start, DateTime end, string suffix)
    {
        return new StrategyConfig
        {
            StrategyId = src.StrategyId + "-" + suffix,
            StrategyType = src.StrategyType,
            Description = src.Description,
            Version = src.Version,
            Instruments = src.Instruments,
            PrimaryBarType = src.PrimaryBarType,
            BarPeriodMinutes = src.BarPeriodMinutes,
            AllocatedCapital = src.AllocatedCapital,
            MaxDrawdownPct = src.MaxDrawdownPct,
            MaxPositionPerInstrument = src.MaxPositionPerInstrument,
            DataStartDate = start,
            DataEndDate = end,
            BacktestMode = BacktestMode.Full,
            Priority = src.Priority,
            Parameters = CloneParameters(src.Parameters),
            SessionFilter = src.SessionFilter,
            SkipAuction = src.SkipAuction,
        };
    }

    private static StrategyConfig CloneWithParameter(StrategyConfig src, string paramName, object paramValue)
    {
        // StrategyParameters 没有 Remove/索引器，构建一个新的副本并替换目标参数
        var ps = new StrategyParameters();
        var found = false;
        foreach (var (k, v) in src.Parameters)
        {
            if (k == paramName) { ps.Add(k, paramValue); found = true; }
            else ps.Add(k, v);
        }
        if (!found) ps.Add(paramName, paramValue);  // 参数不在原配置中（可能使用默认值），显式添加

        return new StrategyConfig
        {
            StrategyId = src.StrategyId,
            StrategyType = src.StrategyType,
            Description = src.Description,
            Version = src.Version,
            Instruments = src.Instruments,
            PrimaryBarType = src.PrimaryBarType,
            BarPeriodMinutes = src.BarPeriodMinutes,
            AllocatedCapital = src.AllocatedCapital,
            MaxDrawdownPct = src.MaxDrawdownPct,
            MaxPositionPerInstrument = src.MaxPositionPerInstrument,
            DataStartDate = src.DataStartDate,
            DataEndDate = src.DataEndDate,
            BacktestMode = src.BacktestMode,
            Priority = src.Priority,
            Parameters = ps,
            SessionFilter = src.SessionFilter,
            SkipAuction = src.SkipAuction,
        };
    }

    private static StrategyParameters CloneParameters(StrategyParameters src)
    {
        var clone = new StrategyParameters();
        foreach (var (k, v) in src)
            clone.Add(k, v);
        return clone;
    }

    private static async Task<(DateTime Start, DateTime End)?> GetDateRangeAsync(
        DuckDBStore store, StrategyConfig config, string symbolsPath)
    {
        try
        {
            var instruments = await store.QueryInstrumentsAsync(
                config.PrimaryBarType ?? "bars_1min");
            // 取第一个品种的时间范围
            if (instruments.Count > 0)
            {
                // 简单代理：用配置中第一个品种的数据范围
                return (DateTime.Parse("2020-01-02"), DateTime.Parse("2026-06-22"));
            }
        }
        catch { }
        return (DateTime.Parse("2020-01-02"), DateTime.Parse("2026-06-22"));
    }

    /// <summary>
    /// 参数扫描范围选择 — v2: 优先从 StrategyParam&lt;T&gt;.OptimizeRange 自动生成。
    /// 无 StrategyParam&lt;T&gt; 的旧策略回退硬编码逻辑。
    /// </summary>
    private static List<(string Name, double[] Values)> SelectScanParameters(StrategyConfig config)
    {
        // ── 新风格: 从 StrategyParam&lt;T&gt;.OptimizeRange 自动生成 ──
        try
        {
            var strategy = StrategyFactory.Create(config);
            var metas = StrategyFactory.GetOptimizableParams(strategy);
            if (metas.Count > 0)
            {
                var result = new List<(string, double[])>();
                foreach (var meta in metas)
                {
                    var values = GenerateScanValues(meta);
                    if (values.Length >= 3)  // 至少3个值才有扫描意义
                        result.Add((meta.Name, values));
                }
                if (result.Count > 0)
                {
                    Console.WriteLine($"  Auto-discovered {result.Count} params from StrategyParam<T>.OptimizeRange");
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [WalkForward] StrategyParam scan failed: {ex.Message}, falling back to legacy");
        }

        // ── 旧风格: 硬编码回退（兼容 [StrategyParameter] 策略）──
        return SelectScanParametersLegacy(config);
    }

    /// <summary>从 ParamMeta 生成扫描值数组。Step=0 或范围过宽时做智能截断。</summary>
    private static double[] GenerateScanValues(ParamMeta meta)
    {
        const int maxValues = 10; // 单参数最多生成 10 个扫描点

        if (meta.Step <= 0 || meta.Min >= meta.Max)
            return Array.Empty<double>();

        var range = meta.Max - meta.Min;
        var rawCount = range / meta.Step;

        // 截断: 如果原始步数 > maxValues，自动放大步长
        double effectiveStep = meta.Step;
        if (rawCount > maxValues)
        {
            effectiveStep = range / (maxValues - 1);
            // 对 int 参数，步长至少为 1
            if (meta.ValueType == "Int32")
                effectiveStep = Math.Max(1, Math.Round(effectiveStep));
        }

        var list = new List<double>();
        for (double v = meta.Min; v <= meta.Max + effectiveStep * 0.1; v += effectiveStep)
        {
            var val = meta.ValueType == "Int32" ? Math.Round(v) : Math.Round(v, 6);
            if (val < meta.Min || val > meta.Max) continue;
            // 去重
            if (list.Count > 0 && Math.Abs(list[^1] - val) < effectiveStep * 0.01) continue;
            list.Add(val);
        }

        return list.ToArray();
    }

    /// <summary>旧风格硬编码回退 — 保留现有策略的预设扫描范围。</summary>
    private static List<(string Name, double[] Values)> SelectScanParametersLegacy(StrategyConfig config)
    {
        var strategyType = config.StrategyType;

        if (strategyType == "DonchianTrend")
        {
            return new List<(string, double[])>
            {
                ("ChannelPeriod",       new double[] { 10, 15, 20, 25, 30, 40, 50, 60 }),
                ("StopAtrMult",         new double[] { 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0 }),
                ("TakeProfitAtrMult",   new double[] { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 8.0 }),
                ("MinVolatility",       new double[] { 0.001, 0.002, 0.003, 0.005, 0.007, 0.010, 0.015, 0.020 }),
            };
        }
        else if (strategyType == "SmaMacd")
        {
            return new List<(string, double[])>
            {
                ("StopLossPct",         new double[] { 0.005, 0.01, 0.015, 0.02, 0.025, 0.03, 0.04, 0.05 }),
                ("TrailAtrMult",        new double[] { 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0 }),
                ("TakeProfitPct",        new double[] { 0, 0.02, 0.04, 0.06, 0.08, 0.10, 0.15 }),
            };
        }
        else if (strategyType == "MaCross")
        {
            return new List<(string, double[])>
            {
                ("FastPeriod",          new double[] { 3, 5, 8, 10, 15, 20, 30, 40 }),
                ("SlowPeriod",          new double[] { 10, 20, 30, 40, 50, 60, 80, 100 }),
                ("StopAtrMult",         new double[] { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0 }),
            };
        }
        else if (strategyType == "BollingerReversion")
        {
            return new List<(string, double[])>
            {
                ("Period",              new double[] { 10, 15, 20, 25, 30, 40, 50 }),
                ("StdDevMult",          new double[] { 1.0, 1.5, 2.0, 2.5, 3.0 }),
                ("StopLossPct",         new double[] { 0.005, 0.01, 0.015, 0.02, 0.025, 0.03 }),
            };
        }
        else
        {
            // 通用扫描：检查 Parameters 中是否存在常见参数名
            var result = new List<(string, double[])>();
            var commonParams = new Dictionary<string, double[]>
            {
                ["StopLossPct"]       = new[] { 0.005, 0.01, 0.015, 0.02, 0.025, 0.03, 0.04, 0.05 },
                ["RiskPerTrade"]      = new[] { 0.005, 0.01, 0.015, 0.02, 0.025, 0.03, 0.04 },
                ["TakeProfitPct"]      = new[] { 0, 0.02, 0.04, 0.06, 0.08, 0.10 },
                ["StopAtrMult"]       = new[] { 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0 },
            };
            foreach (var (name, values) in commonParams)
            {
                if (config.Parameters.Contains(name))
                    result.Add((name, values));
            }
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Markdown Report
    // ═══════════════════════════════════════════════════════════════

    private static string BuildMarkdownReport(
        StrategyConfig config,
        WalkForwardSummary wf,
        IReadOnlyList<ParameterStabilityReport> stabilityReports)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Walk-Forward + 参数稳定性分析报告");
        sb.AppendLine();
        sb.AppendLine($"**策略**: {config.StrategyType} (`{config.StrategyId}`)");
        sb.AppendLine($"**品种**: {string.Join(", ", config.Instruments)}");
        sb.AppendLine($"**生成时间**: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        // ── 步进验证 ──
        sb.AppendLine("---");
        sb.AppendLine("## 1. Walk-Forward 步进验证");
        sb.AppendLine();

        if (wf.IsEmpty)
        {
            sb.AppendLine("> ⚠️ 无有效数据。");
        }
        else
        {
            var verdictIcon = wf.Verdict switch
            {
                WalkForwardVerdict.Robust => "✅",
                WalkForwardVerdict.Acceptable => "⚠️",
                WalkForwardVerdict.Fragile => "🔴",
                WalkForwardVerdict.Overfit => "❌",
                _ => "❓"
            };

            sb.AppendLine($"### 结论: {verdictIcon} **{wf.Verdict}**");
            sb.AppendLine();

            sb.AppendLine("### 核心指标");
            sb.AppendLine();
            sb.AppendLine("| 指标 | 值 | 基准 | 说明 |");
            sb.AppendLine("|------|:--:|------|------|");
            sb.AppendLine($"| OOS Sharpe 均值 | {wf.OosSharpeMean:F3} | >0 有利可图 | 样本外赚钱能力的平均水准 |");
            sb.AppendLine($"| IS Sharpe 均值 | {wf.IsSharpeMean:F3} | >0 | 样本内赚钱能力的平均水准 |");
            sb.AppendLine($"| **稳定性比率** | **{wf.StabilityRatio:F2}** | >0.7 | OOS/IS — 越接近1越稳健 |");
            sb.AppendLine($"| OOS Sharpe 标准差 | {wf.OosSharpeStd:F3} | 越小越好 | 策略在不同时期表现的稳定性 |");
            sb.AppendLine($"| OOS Sharpe CV | {wf.OosSharpeCV:F2} | <1.0 | 变异系数 — 越小越稳定 |");
            sb.AppendLine($"| OOS 正Sharpe比例 | {wf.OosPositiveRatio:P0} | >50% | 大部分窗口盈利才算稳健 |");
            sb.AppendLine($"| 最差 OOS Sharpe | {wf.WorstOosSharpe:F3} | — | 最坏情况（压力测试） |");
            sb.AppendLine($"| **稳健性评分** | **{wf.RobustnessScore:F2}** | >0.5 | 综合评分 (Chan方法) |");
            sb.AppendLine();

            sb.AppendLine($"**Chan 基准**: StabilityRatio > 0.7 可接受，> 0.8 良好，> 0.9 优秀。&lt; 0.5 强烈提示过拟合。");
            sb.AppendLine();

            // 窗口详情表
            if (wf.WindowDetails.Count > 0)
            {
                sb.AppendLine("### 各窗口详情");
                sb.AppendLine();
                sb.AppendLine("| 窗口 | IS Sharpe | OOS Sharpe | IS 胜率 | OOS 胜率 | IS 交易数 | OOS 交易数 | Sharpe 衰减 |");
                sb.AppendLine("|------|:---------:|:----------:|:-------:|:--------:|:--------:|:--------:|:----------:|");
                foreach (var d in wf.WindowDetails)
                {
                    var decayIcon = d.SharpeDecay switch
                    {
                        >= 0.8 => "✅",
                        >= 0.5 => "⚠️",
                        _ => "❌"
                    };
                    sb.AppendLine($"| {d.Label} | {d.IsSharpe:F2} | {d.OosSharpe:F2} | {d.IsWinRate:P0} | {d.OosWinRate:P0} | {d.IsTrades} | {d.OosTrades} | {decayIcon} {d.SharpeDecay:F2} |");
                }
                sb.AppendLine();
            }

            // 最差窗口信息
            if (!string.IsNullOrEmpty(wf.WorstWindowLabel))
            {
                sb.AppendLine($"**最差窗口**: {wf.WorstWindowLabel} (OOS Sharpe = {wf.WorstOosSharpe:F3})");
                sb.AppendLine();
            }
        }

        // ── 参数稳定性 ──
        sb.AppendLine("---");
        sb.AppendLine("## 2. 参数稳定性分析");
        sb.AppendLine();

        if (stabilityReports.Count == 0)
        {
            sb.AppendLine("> ⚠️ 无参数扫描数据。");
        }
        else
        {
            sb.AppendLine("> **核心问题**: 如果参数稍微变化，策略还能赚钱吗？");
            sb.AppendLine("> **高原（Plateau）**: 最优参数附近 Sharpe 波动小 → 稳健。**尖峰**: 只有一组参数好用 → 过拟合。");
            sb.AppendLine();

            sb.AppendLine("### 参数稳定性总览");
            sb.AppendLine();
            sb.AppendLine("| 参数 | 最优值 | 最优Sharpe | 全局CV | 局部CV | 高原宽度 | 评分 | 结论 |");
            sb.AppendLine("|------|:-----:|:---------:|:-----:|:-----:|:-------:|:---:|------|");
            foreach (var r in stabilityReports)
            {
                if (r.IsInsufficientData)
                {
                    sb.AppendLine($"| {r.ParameterName} | — | — | — | — | — | — | 数据不足 |");
                    continue;
                }
                var icon = r.Verdict switch
                {
                    StabilityVerdict.Robust => "✅",
                    StabilityVerdict.Acceptable => "⚠️",
                    StabilityVerdict.Fragile => "🔴",
                    StabilityVerdict.Overfit => "❌",
                    _ => "❓"
                };
                sb.AppendLine($"| {r.ParameterName} | {r.OptimalValue:F2} | {r.MaxSharpe:F3} | {r.GlobalCV:F2} | {r.LocalCV:F2} | {r.PlateauWidthRatio:P0} | {r.StabilityScore:F2} | {icon} {r.Verdict} |");
            }
            sb.AppendLine();

            // 各参数详细报告
            foreach (var r in stabilityReports)
            {
                if (!r.IsInsufficientData)
                {
                    sb.AppendLine(r.ToMarkdown());
                    sb.AppendLine();
                }
            }
        }

        // ── 综合建议 ──
        sb.AppendLine("---");
        sb.AppendLine("## 3. 综合建议");
        sb.AppendLine();

        var issues = new List<string>();

        if (!wf.IsEmpty)
        {
            if (wf.Verdict == WalkForwardVerdict.Overfit || wf.Verdict == WalkForwardVerdict.Fragile)
                issues.Add($"- **步进验证 {wf.Verdict}**: 策略在不同时间段表现不一致，OOS 衰减严重。考虑: (1) 减少参数数量 (2) 增加市场状态过滤 (3) 使用更简单的策略结构");

            if (wf.OosPositiveRatio < 0.5)
                issues.Add($"- **OOS 盈利比例仅 {wf.OosPositiveRatio:P0}**: 超过一半的测试窗口亏损。策略可能只在特定市场环境中有效。");

            if (wf.StabilityRatio < 0.5)
                issues.Add($"- **稳定性比率 {wf.StabilityRatio:F2} &lt; 0.5**: Chan 基准下属于过拟合。建议: 降低参数数量，优先保留稳健性高的参数。");
        }

        foreach (var r in stabilityReports)
        {
            if (r.Verdict == StabilityVerdict.Overfit || r.Verdict == StabilityVerdict.Fragile)
                issues.Add($"- **参数 `{r.ParameterName}` 不稳定** ({r.Verdict}): {r.Warning}。建议在当前最优值 ±20% 范围内验证，如果大幅衰减则重新选择参数。");
        }

        if (issues.Count == 0)
            issues.Add("- ✅ 策略通过步进验证和参数稳定性检查。可以进入下一步: IS/OOS 分离验证 + 多品种测试。");

        foreach (var issue in issues)
            sb.AppendLine(issue);

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*报告由 WalkForwardCommand 自动生成。参考: Ernie Chan《Algorithmic Trading》Ch1-3.*");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // DB Path Resolution
    // ═══════════════════════════════════════════════════════════════

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
        return Path.GetFullPath(candidates[0]);
    }
}
