using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;

namespace TradingStudio.Core.Strategy;

/// <summary>
/// 策略接口 — 回测和实盘共用。
/// 每个 Tick 调用 OnTick，每根 Bar 闭合时调用 OnBar。
/// 策略可以选择只重写其中一个。
/// </summary>
public interface IStrategy
{
    string Name { get; }

    /// <summary>初始化。设置参数、注册指标、订阅品种。</summary>
    void Initialize(StrategyContext context);

    /// <summary>每个 Tick 调用一次。高频策略的主入口。</summary>
    void OnTick(TickRecord tick, string instrumentId);

    /// <summary>每根 Bar 闭合时调用。此 Bar 已 feed 给所有注册指标。</summary>
    void OnBar(Bar bar);

    /// <summary>订单状态变更回调（成交/拒绝/取消）。</summary>
    void OnOrderEvent(OrderEvent orderEvent);

    /// <summary>反馈监控告警回调（可选实现）。</summary>
    void OnAlert(MonitorAlert alert) { }

    /// <summary>引擎结束回调。</summary>
    void OnEndOfAlgorithm();
}
