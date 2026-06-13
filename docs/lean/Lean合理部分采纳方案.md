# Lean 架构合理部分采纳方案

> 经过三轮分析（架构全景 → 执行过程 → 第一性原理验证）后的综合判断：
> 什么该拿、什么该改、什么该扔。

---

## 评判标准

对 Lean 的每个子系统，问三个问题：
1. **这个设计解决的是本质问题还是 QuantConnect 特有的问题？**
2. **TradingStudio 有这个问题吗？**
3. **如果有，能用更简单的方式解决吗？**

---

## 一、总判：Lean 170 万行，TradingStudio 实际需要约 40%

```
Lean 复杂度来源             TradingStudio 是否需要
─────────────────────────────────────────────────────
多资产大类 (10 Security 子类)     ✗ 只有期货
全球多时区                        ✗ 只有北京时间
动态股票池 (Universe)             ✗ 品种集合固定
云端调度 (Job Queue/Messaging)     ✗ 单机自托管
MEF 插件体系                       ✗ 直接 DI
Python 互操作                      ✗ C# only
40+ 券商适配                       ✗ 只有 CTP
股票公司事件 (分红/拆股)            ✗ 期货没有
期权 (Greeks/行权)                 ✗ Phase 3+ 再说
─────────────────────────────────────────────────────
ProtoBuf 序列化                    ✗ 已有 TickRecord 80B
多币种资金管理                     ✗ 只做人民币
```

**核心结论：Lean 是为 QuantConnect 云端平台设计的多资产、多市场、多语言通用引擎。TradingStudio 是一个人的中国期货专用系统。70% 的代码解决的是你不存在的问题。**

但剩下的 30%——那些与资产类别无关的**架构模式**——质量极高且经过 10 年验证。

---

## 二、逐项判决

### 2.1 Handler 策略模式 —— ⭐ 采纳，从 5 个精简为 2 个

**Lean 的做法：** 5 个可替换 Handler，回测和实盘各有一套实现。

**TradingStudio 只需要 2 个：**

```
IDataSource             ← 行情数据源
  ├── BarReplaySource   ← 回测：从 SQLite 回放 Bar
  ├── TickReplaySource  ← 回测：从 CSV 回放 Tick (Phase 2.5)
  └── CtpLiveSource     ← 实盘：已有的 CtpMdAdapter（已有！）

IExecutionHandler      ← 订单执行
  ├── SimulatedExecution ← 回测：模拟撮合
  └── CtpExecution      ← 实盘：CTP TraderApi
```

**为什么不像 Lean 那样拆成 5 个？**
- `SetupHandler` → 不需要，TradingStudio 品种在 JSON 中配置，不需要运行时发现
- `ResultHandler` → 不需要独立 Handler，Analytics 直接在循环中采样
- `RealTimeHandler` → 回测不需要（时间由数据驱动），实盘已有 `SessionScheduler`

### 2.2 Enumerator 管道 —— ⭐ 采纳思想，精简实现

**Lean 的 5 层管道：**
```
FileReader → FillForward → Synchronizing → Filter → PriceScale → TimeSlice
```

**TradingStudio 只需要 2-3 层：**

```
BarReader → [FillForward?] → [Filter?] → MarketSlice
                      ↑              ↑
                  对成交稀疏的     日夜盘过滤
                  品种填充空白     (已有tradingHours)
                  Bar
```

**为什么不需要其余两层？**
- `Synchronizing`（多订阅时间对齐）→ 可以用更简单的方式：SQLite 按 `bar_time` 排序查询，天然对齐
- `PriceScale`（复权）→ 期货没有复权需求，主力合约切换是另一个独立问题

**实施方式：** 不用 C# `IEnumerator` 嵌套，用 Channel + `IAsyncEnumerable<MarketSlice>`：
```csharp
public interface IDataSource
{
    IAsyncEnumerable<MarketSlice> StreamAsync(CancellationToken ct);
}
```

### 2.3 TimeSlice → MarketSlice —— ⭐ 采纳，精简字段

**Lean TimeSlice 有 10 个字段**（包括 Universe、SecurityChanges、ConsolidatorUpdates 等）。

**TradingStudio 只需要：**

```csharp
public record MarketSlice
{
    public DateTime Time { get; init; }                    // 当前时间 (北京时间)
    public DateOnly TradingDay { get; init; }              // CTP 交易日
    public Dictionary<string, Bar> Bars { get; init; }     // InstrumentID → Bar
    public Dictionary<string, TickRecord[]> Ticks { get; init; } // Phase 2.5
    public bool IsTimePulse { get; init; }                 // 纯时间推进（无行情）
}
```

五个字段，覆盖全部需求。

### 2.4 AlgorithmManager 主循环 —— ⭐ 采纳执行顺序，大幅精简

**Lean 的 35 步循环是为美股多资产设计的**，包含拆股、分红、退市、期权、汇率、多币种等处理。

**TradingStudio 的循环（8 步）：**

```csharp
public async Task RunAsync(IDataSource source, IExecutionHandler execution, 
                            IStrategy strategy, RiskEngine risk, Analytics analytics)
{
    await foreach (var slice in source.StreamAsync(_cts.Token))
    {
        // 1. 更新所有合约价格
        foreach (var (symbol, bar) in slice.Bars)
            _securities[symbol].Update(bar);

        // 2. 撮合上一轮的挂单（用本轮价格）
        execution.ProcessPendingOrders(slice);

        // 3. 风控检查（横切层，在任何策略代码之前）
        risk.CheckLimits(_portfolio, slice);

        // 4. 执行策略 OnBar
        strategy.OnBar(slice);

        // 5. 撮合策略刚下的市价单
        execution.ProcessMarketOrders(slice);

        // 6. 更新组合估值
        _portfolio.MarkToMarket(slice);

        // 7. 采样（权益曲线、持仓快照）
        analytics.Sample(_portfolio, slice);

        // 8. 检查是否爆仓
        if (_portfolio.TotalValue <= 0) break;
    }
    strategy.OnEndOfAlgorithm();
    analytics.Report();
}
```

**保留了 Lean 的核心顺序：价格 → 撮合 → 风控 → 策略 → 撮合 → 采样。** 这是第一性原理验证过的正确顺序。

扔掉的是：拆股/分红/退市处理、期权行权、汇率转换、保证金利息、股票池变更通知、多币种资金重算。

### 2.5 Order 状态机 —— ⭐ 采纳，精简为期货专用

**Lean 的订单体系：** 13 种订单类型 + OrderTicket（线程安全） + OrderEvent + ProtoBuf。

**TradingStudio 需要：**

```csharp
public enum OrderType { Market, Limit, StopMarket, StopLimit, FAK, FOK }

public enum OrderStatus { New, Submitted, PartiallyFilled, Filled, Canceled, Invalid }

public record Order
{
    public long Id { get; init; }
    public string InstrumentId { get; init; }
    public OrderType Type { get; init; }
    public Direction Direction { get; init; }   // Buy / Sell
    public decimal Price { get; init; }          // 限价（市价单为 0）
    public decimal StopPrice { get; init; }      // 止损价（非止损单为 0）
    public int Quantity { get; init; }
    public int FilledQuantity { get; set; }
    public decimal AverageFillPrice { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public List<OrderEvent> Events { get; init; } = new();
}

public record OrderEvent
{
    public OrderStatus Status { get; init; }
    public int FillQuantity { get; init; }
    public decimal FillPrice { get; init; }
    public decimal Commission { get; init; }
    public string Message { get; init; }
    public DateTime Timestamp { get; init; }
}
```

**扔掉 OrderTicket：** 你的回测引擎是单线程的，不需要线程安全的订单包装器。Order 本身就是"票据"。

**增加 FAK/FOK：** 国内期货特有的指令（Fill-And-Kill / Fill-Or-Kill），Lean 没有。

### 2.6 撮合引擎 —— ⭐ 采纳核心逻辑，复用 Lean 的关键约束

**Lean 的关键约束（[BacktestingBrokerage.cs:266](C:\Works\ClaudeCode\Lean\Brokerages\Backtesting\BacktestingBrokerage.cs#L266)）：**

```csharp
// 市价单在当前 Bar 成交，其他订单在下一个 Bar
if (order.Time == Algorithm.UtcTime 
    && order.Type != OrderType.Market)
{
    stillNeedsScan = true;
    continue;  // 延迟
}
```

**TradingStudio 直接复用这个逻辑：**

```csharp
public class SimulatedExecution : IExecutionHandler
{
    private readonly List<Order> _pending = new();

    public void ProcessPendingOrders(MarketSlice slice)
    {
        foreach (var order in _pending.ToList())
        {
            if (order.CreatedAt == slice.Time && order.Type != OrderType.Market)
                continue;  // ★ 同 Bar 限价单不检查

            var bar = slice.Bars[order.InstrumentId];
            if (TryFill(order, bar))
            {
                order.Events.Add(new OrderEvent { Status = OrderStatus.Filled, ... });
                _pending.Remove(order);
            }
        }
    }
}
```

**期货特有处理：**
- 涨跌停板检查：如果 `bar.IsUpperLimit` 且买单 → 不成交（涨停板无法买入）
- FAK 订单：部分成交后立即取消剩余
- FOK 订单：不能全部成交则立即取消

### 2.7 指标架构 —— ⭐ 采纳，指标放 Core，精简到 15 个

**Lean 的根基（完全保留）：**

```csharp
// 两层继承 + 内置 RollingWindow
public abstract class IndicatorBase<T>
{
    public string Name { get; }
    public T Current { get; protected set; }
    public T Previous { get; protected set; }
    public RollingWindow<T> History { get; }    // 内置历史值
    public abstract bool IsReady { get; }
    public int WarmUpPeriod { get; init; }
    public int Samples { get; protected set; }
    public event Action<T> Updated;
}

public abstract class WindowIndicator<T> : IndicatorBase<T>
{
    protected int Period { get; }
    // IsReady = Samples >= Period
}
```

**第一阶段实现 10 个核心指标：** SMA, EMA, MACD, BollingerBands, RSI, ATR, ADX, KDJ, CCI, OBV

**扔掉：** Lean 的 170 个指标、CompositeIndicator 的复杂事件组合、多分辨率自动注册。

**指标放哪个命名空间？** `TradingStudio.Core.Indicators/`——指标在实盘和回测中都需要。

### 2.8 品种模型 —— ⭐ 你已有的 Future 比 Lean 更精简，保留

**你现在有的（[Future.cs](src/TradingStudio.Core/Models/Future.cs)）：**

```csharp
public sealed record Future
{
    // 标识、分类、交易规则（TradingUnit, TickSize, TickValue, 
    // PriceLimitPct, MarginRate, Months, TradingHours）
}
```

**这已经比 Lean 的 Security + SecurityExchange + SymbolProperties + MarketHours 四件套更简洁。**

**可能需要增加：** 手续费配置（已有 symbols.json 中有这部分数据）
```csharp
public record CommissionConfig
{
    public decimal OpenRatio { get; init; }      // 开仓费率
    public decimal CloseRatio { get; init; }     // 平仓费率
    public decimal CloseTodayRatio { get; init; } // 平今费率（0 = 不特殊处理）
    public bool IsFixedAmount { get; init; }     // 按金额还是按手数
}
```

---

## 三、Phase 2 推荐架构

```
TradingStudio.Backtest/              ← 新项目
├── BacktestEngine.cs                ← 主循环（StrategyRunner，~80行）
├── IDataSource.cs                   ← 数据源接口
├── BarReplaySource.cs               ← SQLite → MarketSlice 流
├── MarketSlice.cs                   ← 时间切片数据结构
│
├── Execution/
│   ├── IExecutionHandler.cs
│   ├── SimulatedExecution.cs         ← 模拟撮合（~200行）
│   └── FillLogic.cs                  ← 成交判断（市价/限价/止损 + 涨跌停检查）
│
├── Orders/
│   ├── Order.cs
│   ├── OrderEvent.cs
│   └── OrderTypes.cs                 ← OrderType, OrderStatus, Direction 枚举
│
├── Strategy/
│   ├── IStrategy.cs                  ← OnBar(MarketSlice) / OnEndOfAlgorithm()
│   └── StrategyContext.cs            ← Portfolio/Orders 的安全访问封装
│
├── Risk/
│   ├── RiskEngine.cs                 ← 横切层：遍历规则列表
│   └── IRiskRule.cs                  ← 单条规则接口

├── Analytics/
│   ├── EquityCurve.cs
│   ├── TradeLog.cs
│   └── PerformanceMetrics.cs         ← Sharpe, MaxDD, WinRate, ProfitFactor

TradingStudio.Core/                   ← 扩展
├── Indicators/                       ← 新增
│   ├── IndicatorBase.cs
│   ├── WindowIndicator.cs
│   ├── SMA.cs, EMA.cs, MACD.cs, ...（10个核心指标）
│   └── RollingWindow.cs              ← 或直接用 CircularBuffer
└── Models/
    └── CommissionConfig.cs           ← 新增：手续费配置
```

**命名空间对应：**

| Lean | TradingStudio | 复杂度比 |
|------|---------------|---------|
| `Engine/` (563行 Engine.cs + 1045行 AlgorithmManager.cs) | `BacktestEngine.cs` (~80行) | 1:20 |
| `Engine/TransactionHandlers/` | `Execution/` (~250行) | 1:8 |
| `Common/Orders/` | `Orders/` (~120行) | 1:10 |
| `Engine/DataFeeds/` (15+ 文件) | `BarReplaySource.cs` (~100行) | 1:30 |
| `Indicators/` (170个) | `Core/Indicators/` (10个) | 1:17 |
| `Securities/` (10个子目录) | `Future.cs` (已有, 35行) | 1:50 |

**总体：TradingStudio Phase 2 约 1500 行 C#，覆盖 Lean 核心功能的 80%，代码量是 Lean 的 2%。**

---

## 四、核心接口定义

```csharp
// ─── 数据源 ───
public interface IDataSource
{
    IAsyncEnumerable<MarketSlice> StreamAsync(CancellationToken ct);
}

// ─── 执行 ───
public interface IExecutionHandler
{
    Order SubmitOrder(OrderRequest request);
    void CancelOrder(long orderId);
    void ProcessPendingOrders(MarketSlice slice);   // 撮合上一轮的挂单
    void ProcessMarketOrders(MarketSlice slice);    // 撮合本轮市价单
    IReadOnlyList<Order> PendingOrders { get; }
}

// ─── 策略 ───
public interface IStrategy
{
    void Initialize(StrategyContext context);
    void OnBar(MarketSlice slice);
    void OnEndOfAlgorithm();
}

// ─── 风控规则 ───
public interface IRiskRule
{
    string Name { get; }
    bool Validate(OrderRequest order, Portfolio portfolio, MarketSlice slice);
    string ViolationMessage { get; }
}

// ─── 策略上下文 ───
public class StrategyContext
{
    public Portfolio Portfolio { get; }
    public IExecutionHandler Execution { get; }
    public IReadOnlyDictionary<string, Future> Futures { get; }
    public IReadOnlyDictionary<string, Bar> CurrentBars { get; }
    
    public Order MarketOrder(string instrumentId, int quantity);
    public Order LimitOrder(string instrumentId, int quantity, decimal price);
    public Order StopOrder(string instrumentId, int quantity, decimal stopPrice);
    public IndicatorBase<T> RegisterIndicator<T>(string name, IndicatorBase<T> indicator);
}
```

---

## 五、实施路线

| 步骤 | 内容 | 估时 |
|------|------|------|
| 1 | `MarketSlice` + `IDataSource` + `BarReplaySource` | 半天 |
| 2 | `Order` + `OrderEvent` + 枚举类型 | 半天 |
| 3 | `IExecutionHandler` + `SimulatedExecution` | 1天 |
| 4 | `IStrategy` + `StrategyContext` + `BacktestEngine` | 1天 |
| 5 | `IRiskRule` + `RiskEngine` | 半天 |
| 6 | `EquityCurve` + `TradeLog` + `PerformanceMetrics` | 1天 |
| 7 | 10 个核心指标 (`IndicatorBase<T>` + 具体实现) | 2天 |
| 8 | 端到端集成测试（单品种 SMA 交叉策略） | 1天 |

**总计：约 7 个工作日**，产出 1500 行 C#，覆盖回测引擎核心功能。

---

## 六、一个具体的端到端示例

```csharp
// 策略：金叉做多，死叉平仓
public class SmaCrossStrategy : IStrategy
{
    private StrategyContext _ctx;

    public void Initialize(StrategyContext ctx)
    {
        _ctx = ctx;
        foreach (var symbol in ctx.Futures.Keys)
        {
            ctx.RegisterIndicator($"{symbol}_SMA5", new SMA(5));
            ctx.RegisterIndicator($"{symbol}_SMA20", new SMA(20));
        }
    }

    public void OnBar(MarketSlice slice)
    {
        var sma5 = _ctx.GetIndicator<SMA>("rb2608_SMA5");
        var sma20 = _ctx.GetIndicator<SMA>("rb2608_SMA20");

        if (!sma5.IsReady || !sma20.IsReady) return;

        if (sma5.Previous < sma20.Previous && sma5.Current > sma20.Current)
            _ctx.MarketOrder("rb2608", 1);  // 金叉做多
        else if (sma5.Previous > sma20.Previous && sma5.Current < sma20.Current)
            _ctx.MarketOrder("rb2608", -1); // 死叉平仓
    }
}

// 启动回测
var source = new BarReplaySource("bars.db", startDate, endDate, symbols);
var execution = new SimulatedExecution(futureRegistry);
var strategy = new SmaCrossStrategy();
var risk = new RiskEngine(new IRiskRule[] { new MaxPositionRule(5) });
var analytics = new Analytics();

var engine = new BacktestEngine(source, execution, strategy, risk, analytics);
await engine.RunAsync();
```

---

> **核心原则：拿 Lean 的架构思想，写 TradingStudio 自己的代码。不要引入一行 Lean 代码，但要理解每一个 Lean 设计决策背后的原因。**
