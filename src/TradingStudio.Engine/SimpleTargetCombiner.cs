using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 简单合并器 v1: 每个品种取优先级最高的策略信号，等权分配仓位。
///
/// 冲突消解规则：
/// 1. 同一品种多策略信号 → 取 Priority 最小的（越小的越优先）
/// 2. 同 Priority → 取 Conviction 更高的
/// 3. Flat 信号优先级最高（强制平仓）
/// 4. 同时有 Long/Short → Flat 优先，否则取 Priority 高的
///
/// 仓位分配：
/// - 等权分配：MaxWeightPerInstrument = 总权益 × 权重 / 当前价格 → 手数
/// - Flat 信号 → TargetQuantity = 0
/// - 多空方向统一（Long→正手数, Short→负手数）
/// </summary>
public class SimpleTargetCombiner : ITargetCombiner
{
    /// <summary>最大持仓品种数</summary>
    public int MaxPositions { get; init; } = 5;

    /// <summary>单品种最大权重</summary>
    public double MaxWeightPerInstrument { get; init; } = 0.20;

    /// <summary>单品种最大手数（硬上限）</summary>
    public int MaxLots { get; init; } = 10;

    /// <summary>信号强度下限：Conviction 低于此值的信号被忽略</summary>
    public double MinConviction { get; init; } = 0.1;

    public List<PortfolioTarget> Combine(
        IReadOnlyList<TradeSignal> signals,
        IPortfolioState portfolio,
        FutureRegistry registry)
    {
        if (signals.Count == 0)
            return new List<PortfolioTarget>();

        // 1. 按品种分组，每个品种选择最佳信号
        var bestByInstrument = new Dictionary<string, TradeSignal>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in signals)
        {
            if (s.Conviction < MinConviction)
                continue;

            if (!bestByInstrument.TryGetValue(s.InstrumentId, out var existing))
            {
                bestByInstrument[s.InstrumentId] = s;
                continue;
            }

            // 冲突消解
            bestByInstrument[s.InstrumentId] = ResolveConflict(existing, s);
        }

        // 2. 按 Priority(升序) + Conviction(降序) 排序
        var ranked = bestByInstrument.Values
            .OrderBy(s => s.Priority)
            .ThenByDescending(s => s.Conviction)
            .Take(MaxPositions)
            .ToList();

        // 3. 等权分配 → 手数计算
        var weightPerInstrument = Math.Min(MaxWeightPerInstrument, 1.0 / Math.Max(1, ranked.Count));
        var targets = new List<PortfolioTarget>();

        foreach (var signal in ranked)
        {
            var future = registry.Resolve(signal.InstrumentId);
            if (future == null)
                continue;

            int lots;
            if (signal.Direction == SignalDirection.Flat)
            {
                lots = 0;
            }
            else
            {
                lots = ComputeLots(signal, future, portfolio.Equity, weightPerInstrument);
            }

            // 多空方向
            var signedLots = signal.Direction switch
            {
                SignalDirection.Short => -lots,
                SignalDirection.Flat => 0,
                SignalDirection.Reduce => lots / 2, // 减仓只做一半
                _ => lots, // Long
            };

            targets.Add(new PortfolioTarget
            {
                InstrumentId = signal.InstrumentId,
                TargetQuantity = signedLots,
                TargetWeight = signal.Direction == SignalDirection.Flat ? 0 : weightPerInstrument,
                SourceStrategyIds = new List<string> { signal.StrategyId },
                CompositeConviction = signal.Conviction,
                StopLoss = signal.SuggestedStop,
                TakeProfit = signal.SuggestedTarget,
            });
        }

        return targets;
    }

    /// <summary>
    /// 手数计算：
    /// v2 精确 — 手数 = 权益 × 权重 / (参考价 × 合约乘数 × 保证金率)，向下取整，控制在 [1, MaxLots]。
    /// v1 回退 — 信号缺参考价（或品种参数缺失）时，用「权重 × 5」粗估。
    /// </summary>
    private int ComputeLots(TradeSignal signal, Future future, decimal equity, double weight)
    {
        if (signal.ReferencePrice is double price && price > 0
            && future.TradingUnit > 0 && future.MarginRate > 0)
        {
            var marginPerLot = (decimal)price * future.TradingUnit * future.MarginRate;
            if (marginPerLot > 0)
            {
                var exact = equity * (decimal)weight / marginPerLot;
                return Math.Clamp((int)Math.Floor(exact), 1, MaxLots);
            }
        }
        return Math.Min(MaxLots, Math.Max(1, (int)(weight * 5)));
    }

    /// <summary>同品种两个信号冲突消解</summary>
    private static TradeSignal ResolveConflict(TradeSignal a, TradeSignal b)
    {
        // Flat 优先级最高
        if (a.Direction == SignalDirection.Flat) return a;
        if (b.Direction == SignalDirection.Flat) return b;

        // 方向冲突 (Long vs Short): 取方向更明确（Conviction 更高）的那个
        if (a.Direction != b.Direction)
            return a.Conviction >= b.Conviction ? a : b;

        // 同方向: Priority 优先，同 Priority 取 Conviction 高
        if (a.Priority != b.Priority)
            return a.Priority < b.Priority ? a : b;

        return a.Conviction >= b.Conviction ? a : b;
    }
}
