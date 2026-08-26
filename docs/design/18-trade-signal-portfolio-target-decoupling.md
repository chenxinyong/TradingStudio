# TradeSignal / PortfolioTarget 解耦设计

> 状态: **v1 已实现（类型 + SimpleTargetCombiner + Rebalancer）· 手数计算已升级 v2 精确 · 管线未接线 · 策略未迁移**
> 日期: 2026-08-07（设计）→ 2026-08-19（状态更新）→ 2026-08-26（手数精确化）
> 优先级: P2（架构补强）

---

## 0. 当前实现状态（与代码对齐，2026-08-19）

| 组件 | 状态 | 位置 |
|------|------|------|
| `TradeSignal` (record, 含 `Priority`) | ✅ 已实现 | `src/TradingStudio.Core/Engine/TradeSignal.cs` |
| `SignalDirection` (Long/Short/Flat/Reduce) | ✅ 已实现 | 同上 |
| `PortfolioTarget` (class) | ✅ 已实现 | `src/TradingStudio.Core/Engine/PortfolioTarget.cs` |
| `ITargetCombiner` 接口 | ✅ 已实现 | `src/TradingStudio.Engine/ITargetCombiner.cs` |
| `SimpleTargetCombiner` (v1) | ✅ 已实现 | `src/TradingStudio.Engine/SimpleTargetCombiner.cs` |
| `Rebalancer` | ✅ 已实现 | `src/TradingStudio.Engine/Rebalancer.cs` |
| `EngineStrategyContext.EmitSignal` | ✅ 已实现 | `src/TradingStudio.Engine/EngineStrategyContext.cs:177` |
| `ExecutionHandler.ProcessSignals` | ✅ 已实现 | `src/TradingStudio.Engine/ExecutionHandler.cs:86` |
| `SignalChannel` 读写 | ✅ 已实现 | `ExecutionHandler.SignalChannel` + `TradingEngine` 注入 |
| `WeightedTargetCombiner` | ❌ 未实现 | — |
| `RiskParityTargetCombiner` | ❌ 未实现 | — |
| 双轨兼容（MarketBuy → EmitSignal 包装） | ❌ 未实现 | 策略仍直连 `_execution.Submit` |
| 策略迁移至 `EmitSignal` | ❌ 未迁移 | 4 策略仍用 MarketBuy/MarketSell/ClosePosition |

### ⚠️ 关键差距：管线已写但**未接线**

`ProcessSignals` 的入口守卫是：

```csharp
if (TargetCombiner == null || Rebalancer == null)
    return;
```

但 `ExecutionHandler.TargetCombiner` / `.Rebalancer` 这两个属性**从未被赋值**（grep 全仓无 `new SimpleTargetCombiner` / `new Rebalancer` 的注入点）。因此当前信号管线是**死代码**：策略发出的 `TradeSignal` 写入 `SignalChannel`，但主循环的 `ProcessSignals` 每次都提前返回，信号被丢弃。

**结论**：架构骨架已就位，但「接线 + 迁移」是后续工作（v2/v3/v4）。现有策略走的是旧的 `MarketBuy/MarketSell/ClosePosition` 直连 `ExecutionHandler.Submit` 路径，行为不变。

---

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

## 3. 核心抽象（已实现）

### 3.1 TradeSignal — 策略发出的抽象意向

> ✅ 已实现为 `record`（不是 `class`），多一个 `Priority` 字段。

```csharp
namespace TradingStudio.Core.Engine;

/// <summary>
/// 策略发出的交易意向 — 不含手数，仅表达方向+信心。
/// </summary>
public record TradeSignal
{
    public string StrategyId { get; init; } = "";
    public string InstrumentId { get; init; } = "";
    public SignalDirection Direction { get; init; }
    public double Conviction { get; init; } = 0.5;   // [0,1] 信号强度
    public string Reason { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double? SuggestedStop { get; init; }
    public double? SuggestedTarget { get; init; }
    public double? MaxWeight { get; init; }
    public double? ReferencePrice { get; init; }     // 参考价, 组合层据此精确计算手数
    public int Priority { get; init; }               // 策略优先级, 越小越优先
}

public enum SignalDirection { Long, Short, Flat, Reduce }
```

**关键差异 vs 早期草稿**：`Timestamp` 默认 `UtcNow`（非空）；`Priority` 是新增字段，供 `SimpleTargetCombiner` 冲突消解与排序使用。`record` 语义让 `EmitSignal` 用 `with` 表达式回填 `StrategyId`/`Timestamp`。

### 3.2 PortfolioTarget — 组合层输出的具体目标

> ✅ 已实现，与草稿一致（class）。

```csharp
public class PortfolioTarget
{
    public string InstrumentId { get; init; } = "";
    public int TargetQuantity { get; init; }        // 正=多头, 负=空头, 0=空仓
    public double TargetWeight { get; init; }        // [0,1]
    public List<string> SourceStrategyIds { get; init; } = new();
    public double CompositeConviction { get; init; }
    public double? StopLoss { get; init; }
    public double? TakeProfit { get; init; }
}
```

### 3.3 ITargetCombiner — 信号→目标转换策略

> ✅ 接口 + `SimpleTargetCombiner` 已实现。⚠️ `Weighted`/`RiskParity` 未实现。

```csharp
public interface ITargetCombiner
{
    List<PortfolioTarget> Combine(
        IReadOnlyList<TradeSignal> signals,
        IPortfolioState portfolio,
        FutureRegistry registry);
}
```

#### SimpleTargetCombiner（已实现 v1）

实际冲突消解规则（`SimpleTargetCombiner.cs`）：

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `MaxPositions` | 5 | 最大持仓品种数 |
| `MaxWeightPerInstrument` | 0.20 | 单品种最大权重 |
| `MaxLots` | 10 | 单品种最大手数硬上限 |
| `MinConviction` | 0.1 | 低于此值信号被忽略 |

同品种多信号消解顺序：
1. **Flat 优先级最高**（强制平仓）
2. 方向冲突（Long vs Short）→ 取 `Conviction` 更高者
3. 同方向 → `Priority` 小者优先；同 Priority → `Conviction` 高者

手数计算（`SimpleTargetCombiner.ComputeLots`，v2 已升级）：
```csharp
// v2 精确: 手数 = 权益 × 权重 / (参考价 × 合约乘数 × 保证金率), 向下取整, clamp [1, MaxLots]
// 信号缺 ReferencePrice（或品种参数缺失）→ 回退 v1「权重 × 5」粗估
lots = ComputeLots(signal, future, portfolio.Equity, weightPerInstrument);
```
`TradeSignal.ReferencePrice`（新增可选字段）提供价格来源：信号带参考价时精确计算；缺省时回退到粗估，保证向后兼容。

#### WeightedTargetCombiner / RiskParityTargetCombiner（未实现）

```csharp
// ⚠️ 以下两个类尚未实现，仅在设计阶段。需要时再建。
public class WeightedTargetCombiner : ITargetCombiner { /* 按 Conviction 加权平均 */ }
public class RiskParityTargetCombiner : ITargetCombiner { /* 风险平价分配 */ }
```

### 3.4 Rebalancer — 目标→订单（已实现）

> ✅ 已实现，比草稿多一个 `MinTradeLots`。

```csharp
public class Rebalancer
{
    public double RebalanceThreshold { get; init; } = 0.005;  // 0.5%
    public double MinTradeWeight { get; init; } = 0.01;        // 1%
    public int MinTradeLots { get; init; } = 1;                // 最小交易手数

    public List<Order> GenerateOrders(
        IReadOnlyList<PortfolioTarget> targets,
        IReadOnlyList<Position> currentPositions,
        string strategyId);
}
```

核心逻辑 `Δ = target - current`：
- `|Δ| < MinTradeLots` → 不交易
- 方向翻转（多→空 / 空→多）→ 先平仓（`CreateCloseOrder`）
- `Δ > 0` → 加仓 Buy；`Δ < 0` → 减仓 Sell（`IsCloseOrder = true`）

## 4. 迁移路径（当前进度：Phase 1 双轨未做，Phase 2 未开始）

### Phase 1: 双轨运行（❌ 未实现）

设计意图：`MarketBuy/MarketSell` 内部转为 `TradeSignal` + `SimpleTargetCombiner`，让现有策略无感切换。

**实际状态**：未实现。`EngineStrategyContext.MarketBuy/MarketSell/ClosePosition` 仍直接调用 `_execution.Submit`（`EngineStrategyContext.cs:75-133`），**没有**路由到信号管线。策略走的是旧路径。

### Phase 2: 策略主动迁移（❌ 未开始，`EmitSignal` 已就绪）

策略改用 `EmitSignal()`：

```csharp
// Old: 直连下单（手数由策略自己算）
_ctx.MarketBuy(bar.InstrumentId, Quantity, "金叉做多");

// New: 只发信号，仓位由组合层统一处理
_ctx.EmitSignal(new TradeSignal
{
    InstrumentId = bar.InstrumentId,
    Direction = SignalDirection.Long,
    Conviction = 0.7,
    Reason = "金叉做多",
    SuggestedStop = bar.CloseDouble - 2 * s.AtrPrice,
});
```

**迁移前置条件**（缺一不可）：
1. 在组合根注入 `SimpleTargetCombiner` + `Rebalancer`（当前 `ExecutionHandler.TargetCombiner/.Rebalancer` 为 null）
2. ~~确认 v1 手数计算的「权重 × 5」粗估是否需要升级为权益×乘数×保证金率精确计算~~ ✅ 已升级（`ComputeLots` + `ReferencePrice`，2026-08-26）
3. 逐个策略迁移，回测对比迁移前后绩效（信号质量 vs 信号+仓位综合表现分离）

**迁移注意事项**（`MarketBuy` → `EmitSignal` 语义差异）：

| 维度 | `MarketBuy/MarketSell`（旧） | `EmitSignal`（新） |
|------|------------------------------|--------------------|
| 返回值 | `OrderTicket`（可查 `Status`/`OrderId`） | `void`（fire-and-forget） |
| 手数 | 策略显式传入 `qty` | 策略不传，由 combiner 定 |
| 反馈 | 同步拿到 Reject/Fill 结果 | 异步（走 channel），无即时反馈 |
| 减仓语义 | `ClosePosition` 只能全平 | `Flat`（全平）/ `Reduce`（减半） |
| 风控位置 | `Submit` 内 `CheckPreOrder` | 同样走 `Submit`，风控不变 |

**含义**：迁移后策略失去「下单即时反馈」和「精确手数控制」。适合「方向明确、仓位由组合层统一」的策略（如横截面/因子策略）；不适合「手数精确、需立即处理拒单」的策略（如单品种缠论）。迁移应逐策略评估，而非一刀切。

### Phase 3: 多策略组合优化（未开始）

```json
{
  "combiner": "RiskParity",
  "maxPositions": 8,
  "strategies": [
    { "id": "sma-macd-ag", "priority": 1, "riskBudget": 0.30 }
  ]
}
```

## 5. 对现有代码的影响（实际 vs 草稿）

| 文件 | 草稿计划 | 实际状态 |
|------|----------|----------|
| `StrategyContext.cs` | 新增 `EmitSignal` 虚方法 | ✅ 已加 |
| `EngineStrategyContext.cs` | 实现 `EmitSignal` → 写 SignalChannel | ✅ 已实现 (`:177`) |
| `ExecutionHandler.cs` | SignalChannel + TargetCombiner + Rebalancer | ✅ 字段就位，⚠️ 未赋值 |
| `TradeSignal.cs` | Core/Engine/ 新文件 | ✅ 已建（record + Priority） |
| `PortfolioTarget.cs` | Core/Engine/ 新文件 | ✅ 已建 |
| `ITargetCombiner.cs` | 接口 + 3 实现 | ⚠️ 接口 + 1 实现（Simple） |
| `Rebalancer.cs` | 新文件 | ✅ 已建 |
| `TradingEngine.cs` | 注入 combiner + rebalancer | ⚠️ 传 SignalChannel.Writer + 调 ProcessSignals，但未注入 combiner |
| 各策略 | 可选改用 EmitSignal | ❌ 0 个迁移（4 策略仍用 Market*） |

## 6. 实现优先级（更新）

| 迭代 | 内容 | 状态 |
|------|------|------|
| **v1** | `TradeSignal` + `SimpleTargetCombiner` + `Rebalancer` | ✅ 已完成（类型层） |
| **v1.5** | 组合根接线 + 双轨兼容 + 回测验证信号管线 | ❌ 未做（管线当前是死代码） |
| **v2** | `WeightedTargetCombiner` + 回测验证 | ❌ 未做 |
| **v3** | `RiskParityTargetCombiner` + 风险预算配置 | ❌ 未做 |
| **v4** | 策略迁移（逐个改为 `EmitSignal`） | ❌ 未做 |

## 7. 设计原则

1. **信号和仓位是两个独立的关注点** — 策略负责前者，组合层负责后者
2. **向后兼容是第一优先** — 不破坏现有策略和测试
3. **可组合性** — `ITargetCombiner` 可以嵌套: `RiskParity(Weighted(StrategyA, StrategyB), StrategyC)`
4. **可测试性** — 每个环节独立测试: 信号质量、合并逻辑、再平衡精度
5. **审计可追溯** — `PortfolioTarget.SourceStrategyIds` 记录每个持仓目标的信号来源
