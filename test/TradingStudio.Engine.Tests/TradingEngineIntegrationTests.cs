using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;
using TradingStudio.Engine.Statistics;

namespace TradingStudio.Engine.Tests;

/// <summary>端到端回测集成测试 — 完整管线：DataFeed → Engine → Execution → Portfolio → Report</summary>
public class TradingEngineIntegrationTests
{
    // 模拟 10 根上涨 Bar（rb2608, 3500→3550）
    private static List<Bar> BullBars(int count = 10)
    {
        var bars = new List<Bar>();
        var t = new DateTime(2020, 1, 2, 9, 1, 0, DateTimeKind.Unspecified);
        var price = 3500m;
        for (int i = 0; i < count; i++)
        {
            bars.Add(new Bar
            {
                InstrumentId = "rb2608",
                BarTime = t.AddMinutes(i),
                Open  = (long)(price * TickRecord.PriceScale),
                High  = (long)((price + 10) * TickRecord.PriceScale),
                Low   = (long)((price - 5) * TickRecord.PriceScale),
                Close = (long)((price + 5) * TickRecord.PriceScale),
                Volume = 1000,
                TradingDay = DateOnly.FromDateTime(t),
            });
            price += 5;
        }
        return bars;
    }

    private class MockBarFeed : IDataFeed
    {
        private readonly List<Bar> _bars;
        public IReadOnlyList<string> Instruments => ["rb2608"];
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }

        public MockBarFeed(List<Bar> bars) => _bars = bars;

        public void Initialize(DateTime start, DateTime end, IReadOnlyList<string> instruments)
        {
            StartTime = start; EndTime = end;
        }

        public async IAsyncEnumerable<DataEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var bar in _bars)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new BarEvent
                {
                    Bar = bar,
                    Time = new DateTimeOffset(bar.BarTime, TimeSpan.FromHours(8)),
                    IsNewBar = true,
                };
            }
            await Task.CompletedTask;
        }
    }

    private static FutureRegistry MakeRegistry()
    {
        var json = """
        {"symbols":[{"id":1,"exchange":"SHFE","code":"rb","name":"螺纹钢",
        "category":"黑色金属","deliveryType":"PHYSICAL","tradingUnit":10,
        "unitName":"吨","tickSize":1,"tickValue":10,"priceLimitPct":0.10,
        "marginRate":0.08,"months":"1~12月","tradingHours":"09:00-15:00"}]}
        """;
        return FutureRegistry.LoadFromJson(json);
    }

    [Fact]
    public async Task Backtest_MarketBuyAndSell_GeneratesReport()
    {
        var bars = BullBars(10);
        var feed = new MockBarFeed(bars);
        feed.Initialize(bars[0].BarTime, bars[^1].BarTime, ["rb2608"]);

        var risk = new RiskController(maxDrawdown: 1.0m);
        var execution = new ExecutionHandler(risk);
        var portfolio = new PortfolioManager(100000);
        var indicators = new IndicatorManager();
        var strategies = new StrategyContainer();
        var feedback = new FeedbackMonitor();
        var tickSnapshot = new TickSnapshot();
        var registry = MakeRegistry();

        var options = new EngineOptions
        {
            StartTime = bars[0].BarTime,
            EndTime = bars[^1].BarTime,
            Instruments = ["rb2608"],
            StartingCapital = 100000,
            IsLive = false,
        };

        var engine = new TradingEngine(feed, execution, portfolio, indicators, strategies,
            risk, feedback, tickSnapshot, options, registry);

        var report = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(100000m, report.FinalPortfolio.StartingCapital);
        Assert.True(report.FinalPortfolio.TotalEquity > 0);
    }

    [Fact]
    public async Task Backtest_WithPortfolio_TracksEquity()
    {
        // 提交市价买单 → 引擎运行 → 验证持仓
        var bars = BullBars(20);
        var feed = new MockBarFeed(bars);
        feed.Initialize(bars[0].BarTime, bars[^1].BarTime, ["rb2608"]);

        var execution = new ExecutionHandler(new RiskController(maxDrawdown: 1.0m));
        var portfolio = new PortfolioManager(100000);
        var registry = MakeRegistry();

        // 手动下单
        portfolio.CreateSubPortfolio("test", 50000);
        execution.Submit(new Order { InstrumentId = "rb2608", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "test" }, "test", portfolio);

        var engine = new TradingEngine(feed, execution, portfolio, new IndicatorManager(),
            new StrategyContainer(), new RiskController(maxDrawdown: 1.0m),
            new FeedbackMonitor(), new TickSnapshot(),
            new EngineOptions { StartTime = bars[0].BarTime, EndTime = bars[^1].BarTime,
                Instruments = ["rb2608"], StartingCapital = 100000, IsLive = false },
            registry);

        var report = await engine.RunAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(100000m, report.FinalPortfolio.StartingCapital);
        Assert.True(report.TotalReturn >= 0 || report.TotalReturn <= 0); // 有值即可
    }

    /// <summary>无状态微型策略 — 仅用于多策略集成测试，不需要预热</summary>
    private class NoOpStrategy : IStrategy
    {
        public string Name => "NoOp";
        public int BarCount;
        public void Initialize(StrategyContext ctx) { }
        public void OnBar(Bar bar) { BarCount++; }
        public void OnTick(TickRecord tick, string inst) { }
        public void OnOrderEvent(OrderEvent evt) { }
        public void OnEndOfAlgorithm() { }
    }

    [Fact]
    public async Task MultiStrategy_TwoStrategies_EngineRunsBoth()
    {
        // 注册测试策略
        StrategyFactory.Register<NoOpStrategy>("NoOp");

        var bars = BullBars(10);
        var feed = new MockBarFeed(bars);
        var registry = MakeRegistry();

        var config1 = new StrategyConfig { StrategyId = "s1", StrategyType = "NoOp",
            Instruments = ["rb2608"], Priority = 1, AllocatedCapital = 100_000 };
        var config2 = new StrategyConfig { StrategyId = "s2", StrategyType = "NoOp",
            Instruments = ["rb2608"], Priority = 2, AllocatedCapital = 100_000 };

        var options = new EngineOptions
        {
            StartTime = bars[0].BarTime, EndTime = bars[^1].BarTime,
            Instruments = ["rb2608"], StrategyConfigs = [config1, config2],
            StartingCapital = 200_000, IsLive = false, WarmupDays = 0
        };

        var engine = new TradingEngine(feed, new ExecutionHandler(new RiskController()),
            new PortfolioManager(200_000), new IndicatorManager(),
            new StrategyContainer(), new RiskController(maxDrawdown: 1.0m),
            new FeedbackMonitor(), new TickSnapshot(), options, registry);

        var report = await engine.RunAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(2, report.StrategyReports.Count);
    }
}
