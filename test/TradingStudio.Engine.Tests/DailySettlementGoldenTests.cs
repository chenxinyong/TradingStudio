using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// 每日无负债结算（盯市）"黄金用例" —— 手算跨日持仓的日盈亏落袋与成本基重置。
///
/// 国内期货每日无负债结算：收盘按结算价盯市，浮盈浮亏当日计入现金，
/// 次日从结算价重新起算。TradingStudio 回测用"上一交易日最后一根 Bar 收盘价"
/// 作结算价代理。本测试构造一笔跨越 Day1→Day2 的持仓，断言：
///   ① 日切换时按 Day1 结算价 3550 结算，日盈亏 290 落袋进现金；
///   ② 持仓成本基被重置为结算价 3550（→ 平仓 Trade.EntryPrice = 3550，铁证结算发生）；
///   ③ 跨日总盈亏守恒：结算段 290 + 平仓段 80(90毛-10费) = 全程 (3559-3521)×10 - 10费；
///   ④ 最终权益 100370，与"不结算"口径一致（结算只改变现金/浮盈的构成，不改权益）。
///
/// 时序（引擎前视 + 日切换结算）：
///   OnBar#2 (bar1,Day1) → MarketBuy → 撮合于 bar2.Open(3520)+1 = 3521
///   bar3 (Day1 末根)   → Day1 最后盯市价 = 收盘 3550
///   bar4 (Day2 首根)   → SettleDaily @ 3550：日盈亏 (3550-3521)×10 = 290 入现金，成本基→3550
///   OnBar#5 (bar4,Day2) → ClosePosition → 撮合于 bar5.Open(3560)-1 = 3559
/// </summary>
public class DailySettlementGoldenTests
{
    private const long S = TickRecord.PriceScale;
    private static long P(decimal p) => (long)(p * S);
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly Day2 = new(2026, 1, 6);

    private static Bar Bar(DateOnly day, int min, decimal o, decimal h, decimal l, decimal c) => new()
    {
        InstrumentId = "rb2608", TradingDay = day,
        BarTime = day.ToDateTime(new TimeOnly(9, 0)).AddMinutes(min),
        Open = P(o), High = P(h), Low = P(l), Close = P(c), Volume = 10000,
    };

    private static List<Bar> Bars() =>
    [
        Bar(Day1, 0, 3500, 3510, 3490, 3505),
        Bar(Day1, 1, 3500, 3515, 3490, 3510),   // OnBar#2 → MarketBuy
        Bar(Day1, 2, 3520, 3540, 3515, 3535),   // 买撮合 @ 3520+1 = 3521
        Bar(Day1, 3, 3540, 3560, 3535, 3550),   // Day1 末根：结算价 3550
        Bar(Day2, 0, 3550, 3560, 3540, 3545),   // Day2 首根 → 结算 @ 3550；OnBar#5 → ClosePosition
        Bar(Day2, 1, 3560, 3570, 3555, 3565),   // 平撮合 @ 3560-1 = 3559
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

    // 脚本化策略：第 2 根 OnBar 买 1 手，第 5 根 OnBar（Day2 首根）平仓
    private sealed class ScriptedStrategy : IStrategy
    {
        public string Name => "SettlementScripted";
        private StrategyContext _ctx = null!;
        private int _bars;
        public void Initialize(StrategyContext context) => _ctx = context;
        public void OnTick(TickRecord tick, string instrumentId) { }
        public void OnBar(Bar bar)
        {
            _bars++;
            if (_bars == 2) _ctx.MarketBuy("rb2608", 1);
            else if (_bars == 5) _ctx.ClosePosition("rb2608");
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
    public async Task DailySettlement_CrossDayHold_ResetsCostBasisAndBooksDailyPnL()
    {
        StrategyFactory.Register<ScriptedStrategy>("SettlementScripted");

        var bars = Bars();
        var feed = new MockBarFeed(bars);
        feed.Initialize(bars[0].BarTime, bars[^1].BarTime, ["rb2608"]);

        var config = new StrategyConfig
        {
            StrategyId = "settle", StrategyType = "SettlementScripted",
            Instruments = ["rb2608"], Priority = 1, AllocatedCapital = 100_000,
        };
        var options = new EngineOptions
        {
            StartTime = bars[0].BarTime, EndTime = bars[^1].BarTime,
            Instruments = ["rb2608"], StrategyConfigs = [config],
            StartingCapital = 100_000, IsLive = false, WarmupDays = 0,
        };
        var registry = Registry();
        var engine = new TradingEngine(
            feed, new ExecutionHandler(new RiskController(maxDrawdown: 1.0m), registry),
            new PortfolioManager(100_000), new IndicatorManager(),
            new StrategyContainer(), new RiskController(maxDrawdown: 1.0m),
            new FeedbackMonitor(), new TickSnapshot(), options, registry);

        var report = await engine.RunAsync(CancellationToken.None);

        // ── 账户级：结算不改变总权益，只把浮盈"落袋" ──
        Assert.Equal(100_370m, report.FinalPortfolio.TotalEquity);   // 100000 + 290(结算) + 80(平仓段)
        Assert.Equal(100_370m, report.FinalPortfolio.Cash);
        Assert.Equal(0m, report.FinalPortfolio.MarginUsed);

        // ── 逐笔：成本基已被结算重置为 Day1 结算价 3550（铁证每日盯市发生）──
        var sr = Assert.Single(report.StrategyReports);
        var trade = Assert.Single(sr.Trades);
        Assert.Equal(3550m, trade.EntryPrice);   // 原始开仓 3521 → 结算后重置为 3550
        Assert.Equal(3559m, trade.ExitPrice);    // bar5.Open - 1 跳
        Assert.Equal(80m, trade.PnL);            // 平仓段 (3559-3550)×10=90 - 双边费 10；结算段 290 已入现金
    }
}
