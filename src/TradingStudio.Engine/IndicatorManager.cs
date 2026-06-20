using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;

namespace TradingStudio.Engine;

/// <summary>
/// 指标管理器 — 引擎层的数据变换服务。策略注册指标 → 引擎 Feed Bar → 策略只读查询。
///
/// 调用链:
///   Initialize:    Strategy.Initialize → ctx.RegisterIndicator → IndicatorManager.Register
///                  (策略声明需求，同名指标去重复用)
///   Main Loop:     DataFeed → Bar → IndicatorManager.Feed(bar)
///                  (遍历 bar.InstrumentId 的所有注册指标，逐一 Update)
///   策略 Bar 回调:  TradingEngine → Strategy.OnBar → ctx.GetIndicatorValue → IndicatorManager.GetValue
///                  (策略在 OnBar 内查询指标当前值做决策)
///
/// 共享规则: 同一品种+同一指标名+同一 tag → 全局唯一实例，所有策略共享计算结果。
/// 线程模型: 单线程主循环调用 — Feed 和 GetValue 都在引擎线程，无锁。
/// </summary>
public class IndicatorManager
{
    // key = "rb2605:SMA:fast" → 指标实例（全局唯一）
    private readonly SortedDictionary<string, IIndicator> _indicators = new();

    // 按品种快速查找（Feed 时避免遍历全部）
    private readonly Dictionary<string, List<IIndicator>> _byInstrument = new();

    /// <summary>策略初始化时注册。若已存在同名指标则复用。</summary>
    public T Register<T>(string instrumentId, T indicator, string strategyId, string tag = "")
        where T : IIndicator
    {
        var key = $"{instrumentId}:{indicator.Name}:{tag}";
        if (_indicators.TryGetValue(key, out var existing))
            return (T)existing;

        _indicators[key] = indicator;
        if (!_byInstrument.ContainsKey(instrumentId))
            _byInstrument[instrumentId] = new();
        _byInstrument[instrumentId].Add(indicator);
        return indicator;
    }

    /// <summary>每根 Bar 闭合时调用。只更新对应品种的指标。</summary>
    public void Feed(Bar bar)
    {
        if (!_byInstrument.TryGetValue(bar.InstrumentId, out var indicators))
            return;
        foreach (var indicator in indicators)
            indicator.Update(bar);
    }

    /// <summary>批量预热（Bar 回放模式）。</summary>
    public void Warmup(IReadOnlyList<Bar> history, string instrumentId)
    {
        foreach (var bar in history)
            Feed(bar);
    }

    /// <summary>查询指标当前值。</summary>
    public double GetValue(string instrumentId, string indicatorName, string tag = "")
    {
        var key = $"{instrumentId}:{indicatorName}:{tag}";
        return _indicators.TryGetValue(key, out var ind) && ind.IsReady
            ? ind.CurrentValue
            : double.NaN;
    }

    /// <summary>获取指标完整实例（按类型匹配）。</summary>
    public T? Get<T>(string instrumentId, string tag = "") where T : class, IIndicator
    {
        return _indicators.Values
            .FirstOrDefault(i => i is T && i.Tag == tag
                && _byInstrument.ContainsKey(instrumentId)
                && _byInstrument[instrumentId].Contains(i)) as T;
    }

    /// <summary>重置所有指标。</summary>
    public void Reset()
    {
        foreach (var indicator in _indicators.Values)
            indicator.Reset();
    }
}
