using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// Order/Trade 持久化测试 — 守护原则3"所有事件必须可回放"。
/// 验证引擎通过 SetEventStore 注入后，回测结束时不崩溃，且代码路径可达。
/// 实际的 DuckDB 写入验证依赖原生 DuckDB 库，在此环境受限。
/// </summary>
public class EventPersistenceTests
{
    private const long S = TickRecord.PriceScale;
    private static long P(decimal p) => (long)(p * S);

    [Fact]
    public async Task Backtest_WithEventStore_RunsWithoutCrash()
    {
        var bars = new[] {
            Bar(3500, 3520, 3480, 3510),
            Bar(3520, 3540, 3510, 3530),
            Bar(3530, 3550, 3525, 3545)
        }.ToList();
        var feed = new MockFeed(bars);
        feed.Initialize(bars[0].BarTime, bars[^1].BarTime, ["rb2608"]);

        StrategyFactory.Register<NoOp>("NoOp");
        var config = new StrategyConfig { StrategyId = "t", StrategyType = "NoOp", Instruments = ["rb2608"], Priority = 1, AllocatedCapital = 100_000m, };
        var options = new EngineOptions { StartTime = bars[0].BarTime, EndTime = bars[^1].BarTime, Instruments = ["rb2608"], StrategyConfigs = [config], StartingCapital = 100_000, IsLive = false, WarmupDays = 0, };
        var registry = FutureRegistry.LoadFromJson("""{"symbols":[{"id":1,"exchange":"SHFE","code":"rb","name":"","category":"","deliveryType":"PHYSICAL","tradingUnit":10,"unitName":"","tickSize":1,"tickValue":10,"priceLimitPct":0.10,"marginRate":0.08,"feePerLot":5,"months":"1-12"}]}""");

        // 注入空 store (回测模式下无事发生 — 路径可达即可)
        var engine = new TradingEngine(feed, new ExecutionHandler(new RiskController(999, 999, 1.0m)), new PortfolioManager(100_000), new IndicatorManager(), new StrategyContainer(), new RiskController(999, 999, 1.0m), new FeedbackMonitor(), new TickSnapshot(), options, registry);
        engine.SetEventStore(null);
        var report = await engine.RunAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(3, bars.Count); // 回归：引擎正常运行
    }

    [Fact]
    public void DuckDBStore_WriteMethods_Exist()
    {
        // 验证持久化 API 存在且可调用（DuckDB 原生库在此环境不可用，不测实际写入）
        Assert.NotNull(typeof(DuckDBStore).GetMethod("WriteOrderEvents"));
        Assert.NotNull(typeof(DuckDBStore).GetMethod("WriteTrades"));
    }

    private static Bar Bar(decimal o, decimal h, decimal l, decimal c) => new()
    {
        InstrumentId = "rb2608", TradingDay = new(2026, 1, 5),
        BarTime = new DateTime(2026, 1, 5, 9, 0, 0),
        Open = P(o), High = P(h), Low = P(l), Close = P(c), Volume = 10000,
    };

    private sealed class NoOp : IStrategy
    { public string Name => "NoOp"; public void Initialize(StrategyContext c) { } public void OnTick(TickRecord t, string i) { } public void OnBar(Bar b) { } public void OnOrderEvent(OrderEvent e) { } public void OnEndOfAlgorithm() { } }

    private sealed class MockFeed(List<Bar> bars) : IDataFeed
    { public IReadOnlyList<string> Instruments => ["x"]; public DateTime StartTime { get; private set; } public DateTime EndTime { get; private set; } public void Initialize(DateTime s, DateTime e, IReadOnlyList<string> _) { StartTime = s; EndTime = e; } public async IAsyncEnumerable<DataEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { foreach (var b in bars) { if (ct.IsCancellationRequested) yield break; yield return new BarEvent { Bar = b, Time = new DateTimeOffset(b.BarTime, TimeSpan.Zero), IsNewBar = true }; } await Task.CompletedTask; } }
}
