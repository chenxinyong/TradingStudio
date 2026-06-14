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
    private readonly Queue<double> _fastWindow = new();
    private readonly Queue<double> _slowWindow = new();
    private double _fastSum, _slowSum;
    private double _prevFastMa = double.NaN, _prevSlowMa = double.NaN;

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        _instrument = context.SubscribedInstruments[0];
        _ctx.Log($"初始化: {Name} on {_instrument} (快线={FastPeriod}, 慢线={SlowPeriod})");
    }

    public void OnTick(TickRecord tick, string instrumentId) { /* Phase 2b */ }

    public void OnBar(Bar bar)
    {
        if (bar.InstrumentId != _instrument) return;

        var price = bar.CloseDouble;

        // 内联 SMA 计算（Phase 2a 过渡方案）
        _fastWindow.Enqueue(price); _fastSum += price;
        if (_fastWindow.Count > FastPeriod) _fastSum -= _fastWindow.Dequeue();
        _slowWindow.Enqueue(price); _slowSum += price;
        if (_slowWindow.Count > SlowPeriod) _slowSum -= _slowWindow.Dequeue();

        if (_fastWindow.Count < FastPeriod || _slowWindow.Count < SlowPeriod) return;

        var fastMa = _fastSum / FastPeriod;
        var slowMa = _slowSum / SlowPeriod;

        if (double.IsNaN(_prevFastMa)) { _prevFastMa = fastMa; _prevSlowMa = slowMa; return; }

        var pos = _ctx.GetPosition(_instrument);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // 金叉做多
        if (_prevFastMa <= _prevSlowMa && fastMa > slowMa && !hasLong)
        {
            if (hasShort) _ctx.ClosePosition(_instrument);
            _ctx.MarketBuy(_instrument, Quantity, "金叉做多");
        }
        // 死叉做空
        else if (_prevFastMa >= _prevSlowMa && fastMa < slowMa && !hasShort)
        {
            if (hasLong) _ctx.ClosePosition(_instrument);
            _ctx.MarketSell(_instrument, Quantity, "死叉做空");
        }

        _prevFastMa = fastMa;
        _prevSlowMa = slowMa;
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2}");
        else if (evt.Type == OrderEventType.Rejected)
            _ctx.LogError($"拒绝: {evt.Message}");
    }

    public void OnEndOfAlgorithm()
    {
        _ctx.Log($"回测结束. 最终权益: {_ctx.Equity:C}");
    }
}
