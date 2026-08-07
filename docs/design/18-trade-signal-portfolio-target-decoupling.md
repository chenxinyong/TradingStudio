# TradeSignal / PortfolioTarget 解耦设计

> 状态: Draft | 日期: 2026-08-07 | 优先级: P3

## 1. 问题陈述

### 当前架构的耦合

```
Strategy.OnBar()
  → ctx.MarketBuy(inst, qty, tag)   // 信号 + 仓位计算 + 下单耦合在一个调用里
  → ctx.ClosePosition(inst)         // 无"减仓"语义, 只能全平
```

策略直接通过 `StrategyContext` 发出订单，将三类独立的决策耦合在一起：

| 决策层 | 当前归属 | 问题 |
|--------|----------|------|
| **信号生成** (方向/时机) | 策略内 | 正确 |
| **仓位计算** (买多少手) | 策略内 | **越界了** — 仓位应该是组合层决策 |
| **订单执行** (Market/Limit) | 策略内 | 合理（策略知道执行紧迫性） |

### 具体痛点

1. **多策略协同困难**: 策略A和策略B同时想做多AG2608，各自独立计算手数，组合层无法控制总暴露
2. **冲突无法解决**: 策略A做多、策略B做空同一品种 → 两个订单都执行，内部对冲白交手续费
3. **仓位逻辑重复**: 每个策略都复制一遍 `PositionSizer.FromAtrStop(...)` 调用
4. **风险预算无层级**: 无法做到 "策略A分配30%风险预算，策略B分配20%"
5. **回测信号质量难分离**: 想评估 "纯信号方向准确率" 还是 "信号+仓位综合表现"？分不开
6. **无法表达减仓**: `ClosePosition()` 只能全平，无法表达 "从3手减到1手"

## 2. 目标架构

```
┌─────────────┐     ┌──────────────┐     ┌───────────────┐     ┌──────────┐
│  Strategy   │────▶│ TradeSignal  │────▶│ PortfolioTarget│────▶│  Orders  │
│  (纯信号)    │     │ (抽象意向)    │     │ (具体目标)     │     │ (执行)   │
└─────────────┘     └──────────────┘     └───────────────┘     └──────────┘
                           │                      │
                     TargetCombiner          Rebalancer
                   (合并/消解冲突)          (增量订单生成)
                           │
                     RiskBudget
                   (风险预算分配)
```

### 两层解耦

| 阶段 | 输入 | 输出 | 负责 |
|------|------|------|------|
| **信号层** | Bar数据 + 指标 | `TradeSignal` | 策略 |
| **目标层** | `List<TradeSignal>` | `List<PortfolioTarget>` | `ITargetCombiner` |
| **执行层** | `List<PortfolioTarget>` + 当前持仓 | `List<Order>` | `Rebalancer` + `ExecutionHandler` |

## 3. 核心抽象

### 3.1 TradeSignal — 策略发出的抽象意向

```csharp
namespace TradingStudio.Core.Engine;

/// <summary>
/// 策略发出的交易意向 — 不含手数，仅表达方向+信心。
/// 策略只负责到这里，PositionSizing 由组合层统一处理。
/// </summary>
public class TradeSignal
{
    /// <summary>策略ID</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>品种代码</summary>
    public string InstrumentId { get; init; } = "";

    /// <summary>方向意向</summary>
    public SignalDirection Direction { get; init; }

    /// <summary>信号强度 [0, 1]。0=微弱, 1=极强。用于复合信号加权。</summary>
    public double Conviction { get; init; } = 0.5;

    /// <summary>信号来源说明（人工可读）</summary>
    public string Reason { get; init; } = "";

    /// <summary>信号时间戳</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>可选: 策略建议的止损价位（组合层可覆盖）</summary>
    public double? SuggestedStop { get; init; }

    /// <summary>可选: 策略建议的止盈价位（组合层可覆盖）</summary>
    public double? SuggestedTarget { get; init; }

    /// <summary>可选: 期望的最大持仓权重 (占组合 %)</summary>
    public double? MaxWeight { get; init; }
}

public enum SignalDirection
{
    /// <summary>做多意向</summary>
    Long,
    /// <summary>做空意向</summary>
    Short,
    /// <summary>平仓/减仓意向</summary>
    Flat,
    /// <summary>减仓意向 (保留部分)</summary>
    Reduce,
}
```

### 3.2 PortfolioTarget — 组合层输出的具体目标

```csharp
/// <summary>
/// 组合层计算后的目标持仓 — 精确到手数和权重。
/// 当前持仓 vs 目标持仓的差异 → Rebalancer 生成订单。
/// </summary>
public class PortfolioTarget
{
    /// <summary>品种代码</summary>
    public string InstrumentId { get; init; } = "";

    /// <summary>目标持仓手数: 正=多头, 负=空头, 0=空仓</summary>
    public int TargetQuantity { get; init; }

    /// <summary>目标占组合权重 [0, 1]</summary>
    public double TargetWeight { get; init; }

    /// <summary>生成此目标的信号来源（用于审计/调试）</summary>
    public List<string> SourceStrategyIds { get; init; } = new();

    /// <summary>合成信号强度 (多个策略的加权平均)</summary>
    public double CompositeConviction { get; init; }

    /// <summary>组合层设置的止损价</summary>
    public double? StopLoss { get; init; }

    /// <summary>组合层设置的止盈价</summary>
    public double? TakeProfit { get; init; }
}
```

### 3.3 ITargetCombiner — 信号→目标转换策略

```csharp
/// <summary>
/// 信号合并器: 将多个策略的信号合并为组合层持仓目标。
/// 不同实现对应不同的组合管理哲学。
/// </summary>
public interface ITargetCombiner
{
    /// <summary>
    /// 将一组 TradeSignal 转换为 PortfolioTarget 列表。
    /// </summary>
    /// <param name="signals">所有策略发出的信号</param>
    /// <param name="portfolio">当前组合状态（权益、现金、现有持仓）</param>
    /// <param name="registry">品种注册表（合约乘数、保证金率等）</param>
    /// <returns>目标持仓列表（未提及的品种 = 维持现仓或平仓，取决于实现）</returns>
    List<PortfolioTarget> Combine(
        IReadOnlyList<TradeSignal> signals,
        IPortfolioState portfolio,
        FutureRegistry registry);
}

/// <summary>简单合并器: 每个品种取优先级最高的策略信号, 等权分配。</summary>
public class SimpleTargetCombiner : ITargetCombiner
{
    public int MaxPositions { get; init; } = 5;
    public double MaxWeightPerInstrument { get; init; } = 0.20;
    public int MaxLots { get; init; } = 10;
    // 实现: 每个品种取 priority 最高的信号, TopN 等权分配
}

/// <summary>加权合并器: 按 Conviction 加权平均多个策略的同品种信号。</summary>
public class WeightedTargetCombiner : ITargetCombiner
{
    public double MinConviction { get; init; } = 0.3;
    // 同品种多信号: weightedDir = Σ(conv_i × dir_i) / Σ(conv_i)
    // weightedDir > 0 → Long, < 0 → Short
}

/// <summary>风险平价合并器: 按风险贡献均等分配仓位。</summary>
public class RiskParityTargetCombiner : ITargetCombiner
{
    public int LookbackDays { get; init; } = 60;
    // 1. 计算每个品种的波动率
    // 2. 分配仓位使得 σ_i × weight_i 均等
    // 3. 正交化: 如果品种间高度相关，降权其中一个
}
```

### 3.4 Rebalancer — 目标→订单

```csharp
/// <summary>
/// 再平衡器: 比较目标持仓与当前持仓，生成增量订单。
/// 不需要改动 ExecutionHandler — 输出仍然是 Order 对象。
/// </summary>
public class Rebalancer
{
    /// <summary>
    /// 生成订单以实现从 currentPositions 到 targets 的转换。
    /// 每个品种生成至多 2 个订单（平仓 + 开仓），避免过度交易。
    /// </summary>
    public List<Order> GenerateOrders(
        IReadOnlyList<PortfolioTarget> targets,
        IReadOnlyList<Position> currentPositions,
        string strategyId)
    {
        // Δ = target - current
        // Δ > 0 → 买入 |Δ| 手
        // Δ < 0 → 卖出 |Δ| 手
        // Δ = 0 → 不操作
        // 先平仓（如果方向改变），再开仓到目标量
    }

    /// <summary>平仓阈值: 目标权重偏离 < 此值 → 不调仓（避免过度交易）</summary>
    public double RebalanceThreshold { get; init; } = 0.005;  // 0.5%

    /// <summary>补仓阈值: 只调超过此比例的偏差</summary>
    public double MinTradeWeight { get; init; } = 0.01;  // 1%
}
```

## 4. 向后兼容的迁移路径

### Phase 1: 双轨运行（不改现有策略）

```csharp
// StrategyContext 新增抽象信号接口:
public virtual void EmitSignal(TradeSignal signal) { }  // 新: 策略用这个

// 现有 MarketBuy/MarketSell 内部转为 TradeSignal + SimpleTargetCombiner:
public virtual OrderTicket MarketBuy(string inst, int qty, string? tag = null)
{
    // 兼容包装: 把 MarketBuy(qty) 转为 TradeSignal + 固定手数
    var signal = new TradeSignal
    {
        StrategyId = StrategyId, InstrumentId = inst,
        Direction = SignalDirection.Long, Conviction = 1.0,
        MaxWeight = qty * ...,  // 从手数反推权重
    };
    EmitSignal(signal);
    // → TargetCombiner → Rebalancer → ExecutionHandler
}
```

**关键**: 现有策略的 `MarketBuy/ClosePosition` 调用无需修改，内部路由到新管线。

### Phase 2: 策略主动迁移

策略改用 `EmitSignal()`:
```csharp
// Old:
_ctx.MarketBuy(bar.InstrumentId, Quantity, "金叉做多");

// New:
_ctx.EmitSignal(new TradeSignal
{
    InstrumentId = bar.InstrumentId,
    Direction = SignalDirection.Long,
    Conviction = 0.7,
    Reason = "金叉做多",
    SuggestedStop = bar.CloseDouble - 2 * s.AtrPrice,
});
```

### Phase 3: 多策略组合优化

```json
// Live 模式配置: strategy-combiner.json
{
  "combiner": "RiskParity",
  "maxPositions": 8,
  "rebalanceInterval": "1d",
  "strategies": [
    { "id": "sma-macd-ag", "priority": 1, "riskBudget": 0.30 },
    { "id": "intraday-mom", "priority": 2, "riskBudget": 0.50 },
    { "id": "composite-factor", "priority": 3, "riskBudget": 0.20 }
  ]
}
```

## 5. 对现有代码的影响

| 文件 | 变更 | 破坏性 |
|------|------|--------|
| `StrategyContext.cs` | 新增 `EmitSignal(TradeSignal)` 虚方法 | 无 |
| `EngineStrategyContext.cs` | 实现 `EmitSignal` → 写入 SignalChannel | 无 |
| `ExecutionHandler.cs` | 新增 `SignalChannel` reader + `ITargetCombiner` | 无 |
| **新增** `TradeSignal.cs` | Core/Engine/ 新文件 | 无 |
| **新增** `PortfolioTarget.cs` | Core/Engine/ 新文件 | 无 |
| **新增** `ITargetCombiner.cs` | Engine/ 接口 + 3 个实现 | 无 |
| **新增** `Rebalancer.cs` | Engine/ 新文件 | 无 |
| `TradingEngine.cs` | 构造时注入 `ITargetCombiner` + `Rebalancer` | 无 |
| 各策略 | 可选改用 `EmitSignal()` | 无 (兼容) |

## 6. 实现优先级

| 迭代 | 内容 | 工时估算 |
|------|------|----------|
| **v1** | `TradeSignal` + `SimpleTargetCombiner` + `Rebalancer` + 双轨兼容 | 2-3 h |
| **v2** | `WeightedTargetCombiner` + 回测验证 | 1 h |
| **v3** | `RiskParityTargetCombiner` + 风险预算配置 | 2 h |
| **v4** | 策略迁移（逐个改为 `EmitSignal`） | 按需 |

## 7. 设计原则

1. **信号和仓位是两个独立的关注点** — 策略负责前者，组合层负责后者
2. **向后兼容是第一优先** — 不破坏现有策略和测试
3. **可组合性** — `ITargetCombiner` 可以嵌套: `RiskParity(Weighted(StrategyA, StrategyB), StrategyC)`
4. **可测试性** — 每个环节独立测试: 信号质量、合并逻辑、再平衡精度
5. **审计可追溯** — `PortfolioTarget.SourceStrategyIds` 记录每个持仓目标的信号来源
