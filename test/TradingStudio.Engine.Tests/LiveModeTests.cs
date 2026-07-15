using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// Live 模式集成测试 — 验证 IsLive 标志穿透整个管线（ExecutionHandler → Engine → Report）。
/// Live 与回测的关键差异：市价单跳过本地撮合、发往 CTP（实盘中由 SendToExchange 处理）。
/// </summary>
public class LiveModeTests
{
    [Fact]
    public async Task LiveMode_RunAsync_WithBarFeed_ReturnsReport()
    {
        // 用一个产出1根 Bar 后立即结束的 Feed → RunAsync 正常返回
        var bars = new[] { new Bar { InstrumentId = "rb2608", TradingDay = new(2026,1,5), BarTime = new(2026,1,5,9,0,0), Open = 3500L*10_000_000, High = 3520L*10_000_000, Low = 3480L*10_000_000, Close = 3510L*10_000_000, Volume = 1000 } }.ToList();
        var feed = new FiniteFeed(bars);
        feed.Initialize(DateTime.Today, DateTime.Today.AddDays(1), ["rb2608"]);

        StrategyFactory.Register<LiveNoOp>("LiveNoOp");
        var config = new StrategyConfig { StrategyId = "s", StrategyType = "LiveNoOp", Instruments = ["rb2608"], Priority = 1, AllocatedCapital = 100_000m, };
        var options = new EngineOptions { StartTime = DateTime.Today, EndTime = DateTime.Today.AddDays(1), Instruments = ["rb2608"], StrategyConfigs = [config], StartingCapital = 100_000, IsLive = true, WarmupDays = 0, };
        var registry = FutureRegistry.LoadFromJson("""{"symbols":[{"id":1,"exchange":"SHFE","code":"rb","name":"","category":"","deliveryType":"PHYSICAL","tradingUnit":10,"unitName":"","tickSize":1,"tickValue":10,"priceLimitPct":0.10,"marginRate":0.08,"feePerLot":5,"months":"1-12"}]}""");

        var engine = new TradingEngine(feed, new ExecutionHandler(new RiskController(999,999,1.0m)), new PortfolioManager(100_000), new IndicatorManager(), new StrategyContainer(), new RiskController(999,999,1.0m), new FeedbackMonitor(), new TickSnapshot(), options, registry);
        var report = await engine.RunAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(100_000m, report.FinalPortfolio.StartingCapital);
    }

    [Fact]
    public void IsLive_MarketOrder_SkipsLocalMatching()
    {
        // 回测模式下，市价单被 ProcessTick/ProcessBar 本地撮合
        // Live 模式下，市价单跳过本地撮合，走 SendToExchange
        var exec = new ExecutionHandler(new RiskController(999, 999, 1.0m)) { IsLive = true };

        // 提交市价单 → 不调用 SendToExchange 时，订单保持 Submitted/Active
        var ticket = exec.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy, Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");
        Assert.Equal(OrderStatus.Submitted, ticket.Status);
        Assert.Single(exec.ActiveOrders);

        // Live 模式下 ProcessTick 不撮合市价单
        var future = new Future { Code = "rb", TradingUnit = 10, TickSize = 1, MarginRate = 0.08m, PriceLimitPct = 0.10m };
        var tick = new TickRecord { LastPrice = 3500L * 10_000_000, BidPrice1 = 3499L * 10_000_000, AskPrice1 = 3500L * 10_000_000, Volume = 100, ExchangeTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        var fills = exec.ProcessTick(tick, "rb", future);
        Assert.Empty(fills); // 市价单在 Live 模式不本地撮合
    }

    private sealed class LiveNoOp : IStrategy
    { public string Name => "LiveNoOp"; public void Initialize(StrategyContext c) { } public void OnTick(TickRecord t, string i) { } public void OnBar(Bar b) { } public void OnOrderEvent(OrderEvent e) { } public void OnEndOfAlgorithm() { } }

    private sealed class EmptyFeed : IDataFeed
    {
        public IReadOnlyList<string> Instruments { get; private set; } = [];
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
        public void Initialize(DateTime s, DateTime e, IReadOnlyList<string> i) { StartTime = s; EndTime = e; Instruments = i; }
        public async IAsyncEnumerable<DataEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.Delay(-1, ct); yield break; }
    }

    private sealed class FiniteFeed(List<Bar> bars) : IDataFeed
    {
        public IReadOnlyList<string> Instruments { get; private set; } = [];
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
        public void Initialize(DateTime s, DateTime e, IReadOnlyList<string> i) { StartTime = s; EndTime = e; Instruments = i; }
        public async IAsyncEnumerable<DataEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { foreach (var b in bars) { if (ct.IsCancellationRequested) yield break; yield return new BarEvent { Bar = b, Time = new DateTimeOffset(b.BarTime, TimeSpan.FromHours(8)), IsNewBar = true }; } await Task.CompletedTask; }
    }
}
