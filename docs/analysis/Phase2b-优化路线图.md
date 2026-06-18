# Phase 2b：回测引擎优化路线图

> 2026-06-18 | 基于 TradingStudio vs Lean 对比分析 + 实际代码审查
>
> 原则：**每个优化必须直接提升回测结果可信度或策略开发效率。不为了"像 Lean"而改。**

---

## 现状诊断（代码审查发现）

### ExecutionHandler 的实际问题

看了 [ExecutionHandler.cs](../src/TradingStudio.Engine/ExecutionHandler.cs) 后，纠正对比文档中的一处误判：

**Bar 撮合其实已经做了 High/Low 检查。** 这不是"缺失功能"，而是"实现偏差"：

| 订单类型 | 当前行为 | 问题 |
|---------|---------|------|
| 市价单 | 以 Bar.Open 成交 | ✅ 正确（防前进偏差） |
| 限价买单 | Low ≤ limitPrice → 以 **Low** 成交 | ❌ 太乐观，应该以 **limitPrice** 成交 |
| 限价卖单 | High ≥ limitPrice → 以 **High** 成交 | ❌ 太乐观，应该以 **limitPrice** 成交 |
| 止损买单 | High ≥ stopPrice → 以 **High** 成交 | ❌ 可能偏悲观（取决于 High 和 stopPrice 的关系） |
| 止损卖单 | Low ≤ stopPrice → 以 **Low** 成交 | ❌ 同上 |

**核心问题：限价单以最优价成交而非指定价。** 这意味着限价策略的回测结果会系统性地优于实盘——你在 K 线最低价买入，但实盘中只有在"最低价恰好是你的限价"时才能成交。

### PortfolioManager 的实际问题

[PortfolioManager.cs](../src/TradingStudio.Engine/PortfolioManager.cs) 整体扎实，但手续费模型有结构性缺陷：

```csharp
// 当前：统一费率
var fee = Math.Max(1m, contractValue * rate);

// 缺失：国内期货的平今/平昨区分
// 上期所：平今免（ag/al/au/bu/cu/hc/ni/pb/rb/ru/sn/sp/ss/wr/zn 全部平今免）
// 郑商所/大商所：平今加倍（部分品种）
// 中金所：平今正常（部分品种平今高）
```

这意味着**日内策略的回测手续费会被严重错估**——平今免品种多扣了手续费，平今加倍品种少扣了手续费。

### Indicator 的实际问题

5 个指标各自管理 `Queue<double>` + `List<double>` + `_sum`。功能正确，但：

1. **无共享基类** — SMA 的 `_buffer` / `_values` / `_sum` 和 EMA 的完全不同，增加新指标需从头写
2. **BollingerIndicator 每根 Bar 重算 mean/std** — O(n×period) 累计成本，10 万 Bar × 20 期 = 200 万次浮点运算
3. **无前向保护** — 回放时如果数据错序，指标静默产出错误值
4. **无指标组合机制** — 想做 `SMA(RSI(14), 9)` 需要手动写一个新类

### 已经做对、不需要改的东西

- PortfolioManager 的子账户隔离 → 保持
- ExecutionHandler 的锁分离（快照-锁外撮合-锁内写回）→ 保持
- RiskController 的预订单阻断机制 → 保持
- Channel 异步管道 → 保持
- ContractActivityTracker 软过滤 → 保持

---

## 优化任务（按优先级排列）

### P0 — 阻塞策略研究的关键修复

#### 任务 1：限价单/止损单成交价修正

**影响：** 限价策略回测结果失真（高估 5-15%），直接导致策略研发走弯路。

**文件：** `src/TradingStudio.Engine/ExecutionHandler.cs` — `MatchBar` 方法

**当前代码 (line 289-306)：**
```csharp
case OrderType.Limit:
    if (order.Direction == OrderDirection.Buy && (decimal)bar.LowDouble <= order.LimitPrice.Value)
        fillPrice = (decimal)bar.LowDouble;  // ← 应该用 limitPrice
    else if (order.Direction == OrderDirection.Sell && (decimal)bar.HighDouble >= order.LimitPrice.Value)
        fillPrice = (decimal)bar.HighDouble; // ← 应该用 limitPrice
    ...
case OrderType.Stop:
    if (order.Direction == OrderDirection.Buy && (decimal)bar.HighDouble >= order.StopPrice.Value)
        fillPrice = (decimal)bar.HighDouble; // ← 止损触发后应该用 stopPrice（但保守用 High 可接受）
```

**修正逻辑（参考 Lean FillModel）：**

```
限价买单成交价 = min(limitPrice, High)  // 不高于限价，不高于最高价
限价卖单成交价 = max(limitPrice, Low)   // 不低于限价，不低于最低价

止损买单：High >= stopPrice → 触发，成交价 = max(stopPrice, Open)
  // 使用 Open 作为"下一个可用价格"，如果 stopPrice 更高则用 stopPrice
止损卖单：Low <= stopPrice → 触发，成交价 = min(stopPrice, Open)
```

**验证标准：** 拿同一段数据，限价策略的 Sharpe 应该在修正后下降（因为不再以最优价成交）。

**预计工时：** 1-2 小时

---

#### 任务 2：平今/平昨手续费区分

**影响：** 日内策略（当天开仓当天平仓）的手续费错误可达 5-20x。这是实盘亏损的直接来源。

**文件：**
- `src/TradingStudio.Core/Models/Future.cs` — 增加 `FeeType` 字段
- `src/TradingStudio.Engine/PortfolioManager.cs` — `ProcessFillLocked` 方法
- `src/TradingStudio/symbols.json` — 补充所有品种的平今手续费率
- `src/Scripts/gen_symbols_json.py` — 生成逻辑

**设计：**

```csharp
// Future.cs 增加
public enum FeeType { Flat, CloseTodayFree, CloseTodayDouble }
public decimal CloseTodayFeeRate { get; init; } // 平今费率（与 FeeRate 独立）

// PortfolioManager.ProcessFillLocked 中
bool isCloseToday = pos.CreatedTime.Date == fill.Time.Date; // 当天开当天平
decimal feeRate = isCloseToday
    ? (future.CloseTodayFeeRate > 0 ? future.CloseTodayFeeRate : future.FeeRate)
    : future.FeeRate;
```

**品种映射（关键品种）：**
| 品种 | 开仓费率 | 平今费率 | FeeType |
|------|---------|---------|---------|
| ag (白银) | 万0.5 | 0 | CloseTodayFree |
| rb (螺纹) | 万1 | 0 | CloseTodayFree |
| cu (铜) | 万0.5 | 0 | CloseTodayFree |
| IF (沪深300) | 万0.23 | 万3.45 | CloseTodayDouble |
| sa (纯碱) | 万4 | 万4 | Flat |

**验证标准：** 用 simnow 的成交回报对比模拟交易的手续费。

**预计工时：** 2-3 小时

---

### P1 — 回测精度改善

#### 任务 3：Bar 撮合增加 Volume 约束

**影响：** 当前 Bar 撮合假设所有订单都能无限量成交——不存在流动性不足。加入成交量约束后：

- 如果订单量 > Bar.Volume，只能部分成交
- 大单的冲击成本会自然体现

**文件：** `ExecutionHandler.MatchBar`

**改动项：**
```csharp
// 成交量约束
int fillQty = Math.Min(order.Quantity - order.FilledQuantity, (int)bar.Volume);
// 注：bar.Volume 是 int，但可能需要转换为手数
```

**预计工时：** 30 分钟

---

#### 任务 4：市价单增加流动性滑点

**影响：** 当前市价单滑点为 0（以 Open 成交）。实盘中市价单必然有滑点。

**设计（简化模型）：**
```csharp
// 基础滑点 = TickSize × SlippageMultiplier
// SlippageMultiplier 默认 1（保守）
var slippage = future.TickSize * future.SlippageMultiplier; // 从配置读取

if (order.Type == OrderType.Market)
{
    fillPrice = order.Direction == OrderDirection.Buy
        ? (decimal)bar.OpenDouble + slippage  // 买入滑点向上
        : (decimal)bar.OpenDouble - slippage; // 卖出滑点向下
}
```

**注：** 这应该是一个可选参数。回测时默认开启（模拟滑点），但可以关掉做敏感度分析。

**预计工时：** 1 小时

---

### P2 — 策略开发效率

#### 任务 5：新增 StrategyContext.History()

**影响：** 策略当前直接依赖 `IBarStore` 查历史数据，耦合度高。`History()` 封装后：
- 策略不需要知道存储后端是什么
- 支持自动预热
- 接口统一（回测/实盘都用同一个方法）

**文件：**
- `src/TradingStudio.Core/Strategy/StrategyContext.cs` — 增加 `History` 虚方法
- `src/TradingStudio.Engine/EngineStrategyContext.cs` — 实现

**接口设计（参考 Lean）：**
```csharp
public abstract class StrategyContext
{
    // 现有方法: MarketBuy/Sell, Limit, Stop, GetPosition, RegisterIndicator...
    
    /// <summary>
    /// 请求历史 Bar 数据。自动等待预热完成后返回。
    /// </summary>
    /// <param name="instrumentId">合约代码，如 "ag2608"</param>
    /// <param name="count">Bar 数量</param>
    /// <returns>按时间升序排列的 Bar 序列（索引 0 = 最早）</returns>
    public abstract Task<IReadOnlyList<Bar>> History(string instrumentId, int count);
    
    /// <summary>多合约历史数据</summary>
    public abstract Task<IReadOnlyDictionary<string, IReadOnlyList<Bar>>> History(
        string[] instrumentIds, int count);
}
```

**实现：**
```csharp
// EngineStrategyContext
public override async Task<IReadOnlyList<Bar>> History(string instrumentId, int count)
{
    // 从 TradingDay 约束的 Bar 序列中取最近 count 根
    return await _barStore.QueryBarsAsync(instrumentId, BarPeriod.Minute, count);
}
```

**预计工时：** 1-2 小时

---

#### 任务 6：指标基类重构

**影响：** 写新指标更快、更不容易出错。6 个指标以下可以忍手工管理，10+ 个指标后必须统一基类。

**设计（不照搬 Lean，做适合期货的精简版）：**

```csharp
// TradingStudio.Core.Indicators
public abstract class IndicatorBase
{
    public string Name { get; }
    public string Tag { get; }
    public bool IsReady { get; protected set; }
    public double CurrentValue { get; protected set; }
    public int WarmupPeriod { get; init; }
    
    // RollingWindow — 固定大小的环形缓冲，取代各指标自己管理 Queue/List
    protected RollingWindow<double> Window { get; }
    
    // 历史值访问
    public double this[int index] => Window[index]; // [0]=当前, [1]=前一根
    public double Previous => Window[1];
    
    // 前向保护
    private DateTime _lastBarTime;
    
    protected IndicatorBase(string name, int windowSize = 2, int warmupPeriod = 0)
    {
        Name = name;
        WarmupPeriod = warmupPeriod;
        Window = new RollingWindow<double>(windowSize);
    }
    
    public void Update(Bar bar)
    {
        // 前向保护：拒绝乱序 Bar
        if (bar.BarTime <= _lastBarTime)
            return; // 或 throw，取决于设计偏好
        _lastBarTime = bar.BarTime;
        
        var result = ComputeNextValue(bar);
        if (result.IsValid)
        {
            CurrentValue = result.Value;
            Window.Push(result.Value);
            IsReady = Window.Count >= WarmupPeriod;
        }
    }
    
    // 子类只实现这一件事
    protected abstract (double Value, bool IsValid) ComputeNextValue(Bar bar);
    
    public abstract void Reset();
}
```

**SMA 用新基类重写：**
```csharp
public class SmaIndicator : IndicatorBase
{
    private readonly int _period;
    private double _sum;
    
    public SmaIndicator(int period = 20) 
        : base($"SMA({period})", windowSize: period, warmupPeriod: period) { }
    
    protected override (double, bool) ComputeNextValue(Bar bar)
    {
        Window.Push(bar.CloseDouble);
        _sum += bar.CloseDouble;
        if (Window.Count > _period)
            _sum -= Window[_period]; // 被挤出窗口的值
        return Window.Count >= _period 
            ? (_sum / _period, true) 
            : (0, false);
    }
    
    public override void Reset() { _sum = 0; Window.Clear(); }
}
```

**BollingerIndicator 优化（避免每次重算）：**
```csharp
// 改用 Welford 在线算法 — O(1) per bar 而非 O(n)
// 或者：如果 period 不大（<100），O(n) 问题不大
// 当前 period=20，每个 Bar 遍历 20 个元素 → 200万次操作（10万Bar）
// 不紧急，但值得做一个 Issue 记录
```

**不改的东西：**
- 暂不做 `IndicatorBase<T>` 泛型基类（只有 Bar 类型输入时不需要）
- 暂不做指标组合（`.Of()` / `.Plus()` — 策略规模不到需要的时候）
- 暂不做 Consolidator 自动接线（当前手动 `indicator.Update(bar)` 够了）

**预计工时：** 2-3 小时

---

### P3 — 低优先级 / 观望

#### 任务 7：Symbol 不可变 ID

当前用 `string instId` 做 Key。在以下场景会出问题：
- 主力合约换月后，`ag2608` → `ag2609`，两个是不同的合约但属于同一个交易逻辑
- 回测跨合约时，需要知道"我在交易 ag 这个品种"而不只是"ag2608 这个合约"

**触发条件：** 当回测需要跨合约连续回放（如"回测 ag 主力连续 2020-2025"）时才需要做。

**建议：** 暂不做。等到 Phase 3 真的需要跨合约回测时再设计。

#### 任务 8：DataNormalizationMode / Panama Canal 调整

主力换月时的价格跳空调整。**触发条件：** 使用主力连续合约回测时才需要。当前用的是具体合约（ag2608），不需要。

#### 任务 9：Handler 分离架构

**永远不需要。** 除非接入第二个券商。

---

## 实施顺序

```
Week 1:
  Day 1-2:  任务1 (限价/止损成交价修正) ← 最重要
  Day 2-3:  任务2 (平今/平昨手续费)
  Day 3-4:  任务3+4 (Volume约束 + 市价单滑点)

Week 2:
  Day 1-2:  任务6 (指标基类重构)
  Day 3:    任务5 (History() 接口)
  Day 4:    回归测试 — 用已有的 2 个示例策略 (MaCross, MaCrossMultiTf) 
            对比修正前后的回测结果差异
```

---

## 不做的事情（明确拒绝）

1. **不做 150 个指标** — 一个策略需要什么指标就实现什么指标
2. **不做 Consolidator 自动接线** — 手动 `Update(bar)` 在当前规模下更清晰
3. **不做 IndicatorBase\<T\> 泛型基类** — 所有指标都吃 Bar，不需要泛型
4. **不做 8 对 Backtest/Live Handler 分离** — if/else 在当前规模下是合理的选择
5. **不做多币种现金管理** — 国内期货只有人民币
6. **不做 T+N 结算模型** — 期货是每日盯市即时结算
7. **不做 Symbol/SecurityIdentifier 不可变 ID** — 字符串 Key 在当前合约规模下工作正常
8. **不做 Panama Canal 换月调整** — 当前只做具体合约回测，不做连续合约

---

## 验证标准

优化完成后，用以下标准判断是否成功：

1. **限价策略回测 Sharpe 下降 5-15%**（因为不再以最优价成交）
2. **日内策略手续费与 simnow 实盘偏差 < 5%**
3. **新增一个指标类（如 ATR）能在 10 分钟内完成**（而不是现在的 30+ 分钟）
4. **策略可以用 `await History("ag2608", 100)` 获取历史 Bar**（而不是直接依赖 IBarStore）
