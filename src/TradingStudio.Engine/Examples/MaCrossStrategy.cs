using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>双均线突破策略 — Phase 2a 第一个验证策略</summary>
public class MaCrossStrategy : IStrategy
{
    [StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60, Category = "Entry")]
    public int FastPeriod { get; set; } = 5;

    [StrategyParameter(Description = "慢线周期", DefaultValue = 20, Min = 5, Max = 200, Category = "Entry")]
    public int SlowPeriod { get; set; } = 20;

    [StrategyParameter(Description = "每笔交易手数", DefaultValue = 1, Min = 1, Max = 100, Category = "Position")]
    public int Quantity { get; set; } = 1;

    public string Name => "MA双均线突破";

    private StrategyContext _ctx = null!;
    private string _instrument = "";

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        _instrument = context.SubscribedInstruments[0];

        // Phase 2a: 策略内联计算指标（过渡方案）
        // Phase 2b: 改用 _ctx.RegisterIndicator()
        _ctx.Log($"策略初始化: {Name} on {_instrument} (快线={FastPeriod}, 慢线={SlowPeriod})");
    }

    public void OnTick(TickRecord tick, string instrumentId) { /* Phase 2b */ }
    public void OnBar(Bar bar) { /* Phase 2a 实现 */ }
    public void OnOrderEvent(OrderEvent orderEvent) { }
    public void OnEndOfAlgorithm() { _ctx.Log($"回测结束. 最终权益: {_ctx.Equity:C}"); }
}
