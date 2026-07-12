using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// 端到端"黄金用例"回测验证 —— Phase 2 封顶测试。
///
/// 构造确定性行情 + 脚本化策略，手算最终权益与单笔盈亏，断言整条链路
/// (DataFeed → Engine → Strategy → 前视撮合 → Portfolio → Report) 跑出同样的数。
/// 组件级单测再多，也不等于整条链路对——这个测试补上"链路正确"的证明。
///
/// 时序（引擎前视：bar N 的 OnBar 下单，bar N+1 撮合于其 Open）：
///   OnBar#2 (bar1) → MarketBuy → 撮合于 bar2.Open(3520)+1跳 = 3521
///   OnBar#4 (bar3) → ClosePosition → 撮合于 bar4.Open(3560)-1跳 = 3559
///   （撮合时 ATR 未满 14 根 → 滑点退回固定 1 跳）
///
/// 手算：
///   毛盈亏 = (3559-3521)×10 = 380；手续费 固定 5元/手 ×2 = 10 → 单笔净盈亏 370
///   保证金 3521×10×0.08=2816.8 开仓扣、平仓还，抵消 → 最终权益 = 100000 + 370 = 100370
/// </summary>
public class GoldenBacktestTests
{
    private const long S = TickRecord.PriceScale;
    private static long P(decimal p) => (long)(p * S);
    private static readonly DateOnly Day = new(2026, 1, 5);

    private static Bar Bar(int min, decimal o, decimal h, decimal l, decimal c) => new()
    {
        InstrumentId = "rb2608", TradingDay = Day,
        BarTime = new DateTime(2026, 1, 5, 9, 0, 0).AddMinutes(min),
        Open = P(o), High = P(h), Low = P(l), Close = P(c), Volume = 10000,
    };

    // 6 根行情：买撮合于 bar2.Open=3520，平撮合于 bar4.Open=3560
    private static List<Bar> Bars() =>
    [
        Bar(0, 3500, 3510, 3490, 3505),
        Bar(1, 3500, 3515, 3490, 3510),   // OnBar#2 → MarketBuy
        Bar(2, 3520, 3540, 3515, 3535),   // 买撮合 @ 3520+1 = 3521
        Bar(3, 3540, 3560, 3535, 3555),   // OnBar#4 → ClosePosition
        Bar(4, 3560, 3575, 3555, 3570),   // 平撮合 @ 3560-1 = 3559
        Bar(5, 3560, 3570, 3550, 3560),   // 结算
    ];

    private sealed class MockBarFeed(List<Bar> bars) : IDataFeed
    {
        public IReadOnlyList<string> Instruments => ["rb2608"];
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
        public void Initialize(DateTime start, DateTime end, IReadOnlyList<string> instruments)
        { StartTime = start; EndTime = end; }

        public async IAsyncEnumerable<DataEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var bar in bars)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new BarEvent { Bar = bar, Time = new DateTimeOffset(bar.BarTime, TimeSpan.FromHours(8)), IsNewBar = true };
            }
            await Task.CompletedTask;
        }
    }

    // 脚本化策略：第 2 根 OnBar 买 1 手，第 4 根 OnBar 平仓
    private sealed class ScriptedStrategy : IStrategy
    {
        public string Name => "GoldenScripted";
        private StrategyContext _ctx = null!;
        private int _bars;
        public void Initialize(StrategyContext context) => _ctx = context;
        public void OnTick(TickRecord tick, string instrumentId) { }
        public void OnBar(Bar bar)
        {
            _bars++;
            if (_bars == 2) _ctx.MarketBuy("rb2608", 1);
            else if (_bars == 4) _ctx.ClosePosition("rb2608");
        }
        public void OnOrderEvent(OrderEvent orderEvent) { }
        public void OnEndOfAlgorithm() { }
    }

    private static FutureRegistry Registry() => FutureRegistry.LoadFromJson("""
        {"symbols":[{"id":1,"exchange":"SHFE","code":"rb","name":"螺纹钢","category":"black",
        "deliveryType":"physical","tradingUnit":10,"unitName":"吨","tickSize":1,"tickValue":10,
        "priceLimitPct":0.10,"marginRate":0.08,"feePerLot":5,"months":"1-12"}]}
        """);

    [Fact]
    public async Task GoldenCase_FullPipeline_MatchesHandComputedPnL()
    {
        StrategyFactory.Register<ScriptedStrategy>("GoldenScripted");

        var bars = Bars();
        var feed = new MockBarFeed(bars);
        feed.Initialize(bars[0].BarTime, bars[^1].BarTime, ["rb2608"]);

        var config = new StrategyConfig
        {
            StrategyId = "golden", StrategyType = "GoldenScripted",
            Instruments = ["rb2608"], Priority = 1, AllocatedCapital = 100_000,
        };
        var options = new EngineOptions
        {
            StartTime = bars[0].BarTime, EndTime = bars[^1].BarTime,
            Instruments = ["rb2608"], StrategyConfigs = [config],
            StartingCapital = 100_000, IsLive = false, WarmupDays = 0,
        };
        var engine = new TradingEngine(
            feed, new ExecutionHandler(new RiskController(maxDrawdown: 1.0m)),
            new PortfolioManager(100_000), new IndicatorManager(),
            new StrategyContainer(), new RiskController(maxDrawdown: 1.0m),
            new FeedbackMonitor(), new TickSnapshot(), options, Registry());

        var report = await engine.RunAsync(CancellationToken.None);

        // ── 账户级：整条链路的最终结果 ──
        Assert.Equal(100_000m, report.FinalPortfolio.StartingCapital);
        Assert.Equal(100_370m, report.FinalPortfolio.TotalEquity);   // 100000 + 370
        Assert.Equal(100_370m, report.FinalPortfolio.Cash);
        Assert.Equal(0m, report.FinalPortfolio.MarginUsed);          // 已平仓，保证金释放

        // ── 逐笔：手算成交价与盈亏 ──
        var sr = Assert.Single(report.StrategyReports);
        Assert.Equal(1, sr.TotalTrades);
        var trade = Assert.Single(sr.Trades);
        Assert.Equal(3521m, trade.EntryPrice);   // bar2.Open + 1 跳
        Assert.Equal(3559m, trade.ExitPrice);    // bar4.Open - 1 跳
        Assert.Equal(370m, trade.PnL);           // 380 毛利 - 10 手续费
        Assert.Equal(10m, trade.Fee);            // 固定 5 元/手 × 2
    }
}
