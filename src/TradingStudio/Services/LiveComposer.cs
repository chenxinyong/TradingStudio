using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;
using TradingStudio.Live;

namespace TradingStudio.Services;

/// <summary>
/// Live 模式 DI 组合根 — 从 Program.cs 提取，保持入口文件简洁。
/// </summary>
public static class LiveComposer
{
    private static readonly List<string> _pendingStrategyInstruments = new();

    public static void Configure(WebApplicationBuilder builder, IConfiguration config)
    {
        var services = builder.Services;

        // ── 基础设施 ──
        services.AddSingleton<SessionScheduler>();
        services.AddSingleton<HealthMonitor>();

        // ── 品种注册表 ──
        var symbolsPath = config["Live:SymbolsPath"] ?? "symbols.json";
        var registry = FutureRegistry.Load(symbolsPath);
        services.AddSingleton(registry);

        // ── CTP 行情 ──
        var mdOpts = new CtpMdOptions
        {
            MdFront = config["Live:MdFront"]!,
            BrokerId = config["Live:BrokerId"] ?? "9999",
            UserId = config["Live:UserId"]!,
            Password = config["Live:Password"]!,
        };
        var activityTracker = new ContractActivityTracker(observationSeconds: 60);
        services.AddSingleton(activityTracker);
        services.AddSingleton<IDataFeed>(sp =>
        {
            var feed = new CtpLiveFeed(mdOpts, sp.GetRequiredService<Serilog.ILogger>());
            feed.ActivityTracker = activityTracker;
            foreach (var inst in _pendingStrategyInstruments) feed.StrategyInstruments.Add(inst);
            return feed;
        });
        services.AddSingleton(sp => (CtpLiveFeed)sp.GetRequiredService<IDataFeed>());

        // ── 风控 ──
        var dailyTracker = new TradingStudio.Core.Risk.DailyRiskTracker(new TradingStudio.Core.Risk.RiskTrackerConfig
        {
            DailyLossLimit = config.GetValue("Risk:DailyLossLimit", 0.05),
            MonthlyLossLimit = config.GetValue("Risk:MonthlyLossLimit", 0.15),
            MaxConsecutiveLossDays = config.GetValue("Risk:MaxConsecutiveLossDays", 5),
        });
        var risk = new RiskController(
            maxPosition: config.GetValue("Risk:MaxPositionPerInstrument", 5),
            maxOrderQty: config.GetValue("Risk:MaxOrderQuantity", 100),
            maxDrawdown: config.GetValue<decimal>("Risk:MaxDrawdownPct", 0.25m),
            dailyTracker: dailyTracker);
        services.AddSingleton(risk);
        var execution = new ExecutionHandler(risk, registry);
        services.AddSingleton<IExecutionHandler>(execution);
        services.AddSingleton(execution);

        // ── 反馈 + 行情快照 ──
        services.AddSingleton(new FeedbackMonitor());
        services.AddSingleton(new TickSnapshot());

        // ── 指标 + 策略 ──
        services.AddSingleton(new IndicatorManager());
        services.AddSingleton(new StrategyContainer());

        // ── 回测执行器 ──
        services.AddSingleton<BacktestRunner>();

        // ── 数据持久化 ──
        var dataPath = config["Live:DataPath"] ?? "data";
        var dbPath = Path.Combine(dataPath, config["Live:Database"] ?? "bars_live.db");
        var forceDuckDB = config["Live:UseDuckDB"]?.ToLowerInvariant() == "true";
        var isDuckDB = forceDuckDB || dbPath.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase);
        IBarStore barStore = isDuckDB
            ? new DuckDBStore(dbPath, enableTickPurge: true)
            : new SqliteBarStore(dbPath);
        services.AddSingleton(barStore);
        services.AddSingleton<BuildPeriodsService>();
        services.AddSingleton(new TickCsvWriter(Path.Combine(dataPath, "TickData")));

        // ── 资金管理 ──
        var startCapital = config.GetValue<decimal>("Live:StartingCapital", 100_000m);
        var portfolio = new PortfolioManager(startCapital);
        services.AddSingleton(portfolio);

        // ── CTP 交易桥接 ──
        // 策略ID映射在下面加载，但bridge回调异步触发（登录后），故用闭包捕获
        var instrumentStrategyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(config["Live:TraderFront"]))
        {
            var traderOpts = new CtpTraderOptions
            {
                TraderFront = config["Live:TraderFront"]!,
                BrokerId = config["Live:BrokerId"] ?? "9999",
                UserId = config["Live:UserId"] ?? "",
                Password = config["Live:Password"] ?? "",
                AuthCode = config["Live:AuthCode"] ?? "0000000000000000",
                AppId = config["Live:AppId"] ?? "simnow_client_test",
            };
            var bridgeLogger = builder.Services.BuildServiceProvider().GetRequiredService<Serilog.ILogger>();
            var tickSnapshot = builder.Services.BuildServiceProvider().GetRequiredService<TickSnapshot>();
            var bridge = new CtpTraderBridge(execution.FillChannel, traderOpts, bridgeLogger, registry, tickSnapshot);

            // 订阅 CTP 持仓查询结果 → 恢复到 PortfolioManager（异步，登录完成后触发）
            // CTP 对同一品种返回两条记录（PositionDate='1'今仓 + '2'昨仓），Position 分别是各自日期的净持仓。
            // 正确做法：累加两条记录的 NetPosition 得到总净持仓，然后一次性恢复。
            var ctpPosBuffer = new Dictionary<string, CtpPositionInfo>(StringComparer.OrdinalIgnoreCase);
            var ctpPosFlushCts = new CancellationTokenSource();
            bridge.OnPositionReceived += info =>
            {
                try
                {
                    // 仅跟踪策略品种
                    if (_pendingStrategyInstruments.Count > 0 &&
                        !_pendingStrategyInstruments.Contains(info.InstrumentId, StringComparer.OrdinalIgnoreCase))
                        return;

                    // 累加同品种多条记录（今仓+昨仓的 Position 各自独立）
                    if (ctpPosBuffer.TryGetValue(info.InstrumentId, out var prev))
                    {
                        var sumPos = prev.NetPosition + info.NetPosition;
                        // 均价加权平均
                        var totalAbs = Math.Abs(prev.NetPosition) + Math.Abs(info.NetPosition);
                        var weightedCost = totalAbs > 0
                            ? (Math.Abs(prev.NetPosition) * prev.OpenCost + Math.Abs(info.NetPosition) * info.OpenCost) / totalAbs
                            : info.OpenCost;
                        // PositionDate: 取持仓量更大的日期（今仓多→用今仓日期，昨仓多→用昨仓日期）
                        var dominantDate = Math.Abs(info.NetPosition) >= Math.Abs(prev.NetPosition)
                            ? info.PositionDate : prev.PositionDate;
                        ctpPosBuffer[info.InstrumentId] = new CtpPositionInfo
                        {
                            InstrumentId = info.InstrumentId,
                            NetPosition = sumPos,
                            OpenCost = weightedCost,
                            UseMargin = prev.UseMargin + info.UseMargin,
                            PositionDate = dominantDate,
                        };
                    }
                    else ctpPosBuffer[info.InstrumentId] = info;

                    // 延迟刷入：CTP 查询回调同步快速返回，2秒后所有记录到齐再一次性恢复
                    ctpPosFlushCts.Cancel();
                    ctpPosFlushCts = new CancellationTokenSource();
                    var flushToken = ctpPosFlushCts.Token;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(2000, flushToken);
                            FlushCtpPositions(ctpPosBuffer, portfolio, instrumentStrategyMap);
                        }
                        catch (OperationCanceledException) { }
                    }, flushToken);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LiveComposer] CTP持仓恢复失败: {info.InstrumentId}: {ex.Message}");
                }
            };

            // 订阅 CTP 资金账户查询结果 → 恢复真实权益（替代重设 StartingCapital）
            // 登录后回调一次（Balance=动态权益, PreBalance=昨结算权益），据此重构 PortfolioManager 权益。
            bridge.OnAccountReceived += acc =>
            {
                try
                {
                    portfolio.ReconcileEquity((decimal)acc.Balance, (decimal)acc.PositionProfit, (decimal)acc.PreBalance);
                    Console.Error.WriteLine($"[LiveComposer] CTP账户权益已恢复: Balance={acc.Balance:F2} PosProfit={acc.PositionProfit:F2} PreBalance={acc.PreBalance:F2} → Equity={portfolio.Equity:F2}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LiveComposer] CTP账户权益恢复失败: {ex.Message}");
                }
            };

            services.AddSingleton(bridge);
            try
            {
                bridge.Connect();
                Console.Error.WriteLine("[LiveComposer] CtpTraderBridge.Connect() called — FtdcNet.CTP P/Invoke");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LiveComposer] CtpTraderBridge.Connect() FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            execution.SendToExchange = bridge.SendOrder;
        }
        execution.IsLive = true;

        // ── 引擎选项 + 策略加载 ──
        var allInstruments = ContractCodeGenerator.BatchSubscribe(registry.All.Values, 50)
            .SelectMany(b => b).ToList();
        var engineOptions = new EngineOptions
        {
            StartTime = DateTime.Today, EndTime = DateTime.Today.AddDays(1),
            Instruments = allInstruments, StartingCapital = startCapital, IsLive = true,
        };

        // ── 策略配置加载（支持单文件 或 目录批量加载）──
        var strategyConfigPath = config["Live:StrategyConfig"];
        if (!string.IsNullOrEmpty(strategyConfigPath))
        {
            if (!Path.IsPathRooted(strategyConfigPath))
                strategyConfigPath = Path.Combine(AppContext.BaseDirectory, strategyConfigPath);
        }

        var strategyConfigs = new List<TradingStudio.Core.Strategy.StrategyConfig>();
        if (!string.IsNullOrEmpty(strategyConfigPath))
        {
            if (Directory.Exists(strategyConfigPath))
            {
                // 目录模式：加载所有 .json 文件，每个品种独立配置
                foreach (var file in Directory.GetFiles(strategyConfigPath, "*.json").OrderBy(f => f))
                {
                    var sc = TryLoadConfig(file, registry);
                    if (sc != null) strategyConfigs.Add(sc);
                }
                Console.Error.WriteLine($"[LiveComposer] Directory mode: {strategyConfigs.Count} configs from {strategyConfigPath}");
            }
            else if (File.Exists(strategyConfigPath))
            {
                // 单文件模式（向后兼容）
                var sc = TryLoadConfig(strategyConfigPath, registry);
                if (sc != null) strategyConfigs.Add(sc);
            }
        }

        if (strategyConfigs.Count > 0)
        {
            // 构建品种→策略ID映射（供CTP持仓恢复用）
            foreach (var sc in strategyConfigs)
            foreach (var inst in sc.Instruments)
                instrumentStrategyMap[inst] = sc.StrategyId;

            var totalCapital = strategyConfigs.Sum(c => c.AllocatedCapital);
            var engineStartingCapital = totalCapital > 0 ? totalCapital : startCapital;

            var warmupDays = config.GetValue("Live:WarmupDays", 5);
            IBarStore? warmupStore = null;
            if (warmupDays > 0)
            {
                var warmupDb = Path.Combine(dataPath, config["Live:WarmupDatabase"] ?? "bars_history.duckdb");
                if (File.Exists(warmupDb)) warmupStore = new DuckDBStore(warmupDb, readOnly: true);
            }

            engineOptions = new EngineOptions
            {
                StartTime = DateTime.Today, EndTime = DateTime.Today.AddDays(1),
                Instruments = allInstruments, StrategyConfigs = strategyConfigs,
                StartingCapital = engineStartingCapital,
                IsLive = true, WarmupDays = warmupDays, WarmupStore = warmupStore,
            };
            StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);

            foreach (var sc in strategyConfigs)
            foreach (var inst in sc.Instruments)
                _pendingStrategyInstruments.Add(inst);

            var allInst = strategyConfigs.SelectMany(c => c.Instruments);
            Console.Error.WriteLine($"[LiveComposer] {strategyConfigs.Count} strategies, {allInst.Count()} instruments: {string.Join(", ", allInst)}");
        }
        services.AddSingleton(engineOptions);

        // ── 引擎 + 后台服务 ──
        services.AddSingleton(sp => new TradingEngine(
            sp.GetRequiredService<IDataFeed>(), execution, portfolio,
            sp.GetRequiredService<IndicatorManager>(), sp.GetRequiredService<StrategyContainer>(),
            risk, sp.GetRequiredService<FeedbackMonitor>(), sp.GetRequiredService<TickSnapshot>(),
            engineOptions, registry,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TradingStudio.Engine.TradingEngine>>()));

        // ── OrderEventPump（v3: 单消费者泵，消除 OrderOutbox 双消费者竞态）──
        // EngineHubPushService + OrderPersistenceService 作为 IOrderEventSink 注入
        // 借鉴 StockSharp CtpMessageAdapter 回调泵模式
        services.AddSingleton<EngineHubPushService>();
        services.AddSingleton<IOrderEventSink>(sp => sp.GetRequiredService<EngineHubPushService>());
        services.AddHostedService(sp => sp.GetRequiredService<EngineHubPushService>());

        services.AddSingleton<OrderPersistenceService>();
        services.AddSingleton<IOrderEventSink>(sp => sp.GetRequiredService<OrderPersistenceService>());
        services.AddHostedService(sp => sp.GetRequiredService<OrderPersistenceService>());

        services.AddSingleton(sp => new OrderEventPump(
            sp.GetRequiredService<ExecutionHandler>().OrderOutbox.Reader,
            sp.GetServices<IOrderEventSink>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<OrderEventPump>>()));
        services.AddHostedService(sp => sp.GetRequiredService<OrderEventPump>());

        services.AddHostedService<EngineHost>();
        services.AddHostedService<LiveDataCollector>();
        services.AddHostedService<PeriodMaintainer>();
    }

    private static void FlushCtpPositions(Dictionary<string, CtpPositionInfo> buffer,
        PortfolioManager portfolio, Dictionary<string, string> instrumentStrategyMap)
    {
        foreach (var (instId, info) in buffer)
        {
            try
            {
                if (info.NetPosition == 0) continue;
                var sid = instrumentStrategyMap.TryGetValue(instId, out var m) ? m : "live-test";
                // createdDate 仅用于显示/审计（平今/平昨判断走 Position.CTPPositionDate）
                var createdDate = DateTime.Today;
                var restored = portfolio.RestorePosition(instId, sid,
                    info.NetPosition, (decimal)info.OpenCost, (decimal)info.UseMargin,
                    createdDate, info.PositionDate);
                if (restored)
                    Console.Error.WriteLine($"[LiveComposer] CTP持仓已恢复: {instId} x{info.NetPosition} @{info.OpenCost:F4} Margin={info.UseMargin:F2} PosDate={info.PositionDate} ({(info.PositionDate == '1' ? "今仓" : "昨仓")})");
                else
                    Console.Error.WriteLine($"[LiveComposer] CTP持仓更新PosDate: {instId} PosDate={info.PositionDate} (已存在)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LiveComposer] CTP持仓恢复失败: {instId}: {ex.Message}");
            }
        }
        buffer.Clear();
    }

    private static TradingStudio.Core.Strategy.StrategyConfig? TryLoadConfig(string path, FutureRegistry registry)
    {
        try
        {
            var json = File.ReadAllText(path);
            var sc = System.Text.Json.JsonSerializer.Deserialize<TradingStudio.Core.Strategy.StrategyConfig>(
                json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (sc == null) return null;

            // ── 郑商所合约代码归一化：长码(TA2609) → 短码(TA609) ──
            var instruments = sc.Instruments.ToList();
            var normalized = false;
            for (int i = 0; i < instruments.Count; i++)
            {
                try
                {
                    var (symbol, year, month) = ContractCodeGenerator.ParseCode(instruments[i]);
                    var future = registry.Resolve(instruments[i]);
                    if (future?.Exchange == ExchangeCode.CZCE)
                    {
                        var shortCode = ContractCodeGenerator.MakeCode(symbol, ExchangeCode.CZCE, year, month);
                        if (shortCode != instruments[i])
                        {
                            Console.Error.WriteLine($"[LiveComposer] CZCE归一化: {instruments[i]} → {shortCode}");
                            instruments[i] = shortCode;
                            normalized = true;
                        }
                    }
                }
                catch { /* 解析失败则保持原值 */ }
            }

            if (normalized)
            {
                sc = new TradingStudio.Core.Strategy.StrategyConfig
                {
                    StrategyId = sc.StrategyId,
                    StrategyType = sc.StrategyType,
                    Description = sc.Description,
                    Version = sc.Version,
                    Instruments = instruments.AsReadOnly(),
                    PrimaryBarType = sc.PrimaryBarType,
                    BarPeriodMinutes = sc.BarPeriodMinutes,
                    AllocatedCapital = sc.AllocatedCapital,
                    MaxDrawdownPct = sc.MaxDrawdownPct,
                    MaxPositionPerInstrument = sc.MaxPositionPerInstrument,
                    DataStartDate = sc.DataStartDate,
                    DataEndDate = sc.DataEndDate,
                    OptimizationEndDate = sc.OptimizationEndDate,
                    BacktestMode = sc.BacktestMode,
                    Priority = sc.Priority,
                    Parameters = sc.Parameters,
                    RiskRules = sc.RiskRules,
                    SessionFilter = sc.SessionFilter,
                    SkipAuction = sc.SkipAuction,
                };
            }

            Console.Error.WriteLine($"[LiveComposer] Loaded: {Path.GetFileName(path)} → {sc.StrategyId} [{string.Join(",", sc.Instruments)}] Capital={sc.AllocatedCapital}");
            return sc;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LiveComposer] Config parse error: {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}
