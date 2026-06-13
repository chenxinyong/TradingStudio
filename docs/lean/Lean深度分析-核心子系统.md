# Lean Engine 深度分析 — 核心子系统代码深读

> 继 Day 1 高层架构分析后，Day 2 (2026-06-12) 深入四个核心子系统的代码实现细节。
> 重点关注对 TradingStudio 有直接参考价值的设计模式和实现技巧。

---

## 目录

1. [Indicators 指标库架构](#1-indicators-指标库架构)
2. [DataFeed Enumerator 管道](#2-datafeed-enumerator-管道)
3. [AlgorithmManager 主循环](#3-algorithmmanager-主循环)
4. [Order / OrderTicket 订单状态机](#4-order--orderticket-订单状态机)
5. [对 TradingStudio 的启示（更新）](#5-对-tradingstudio-的启示)

---

## 1. Indicators 指标库架构

> **关键文件：** `Indicators/IndicatorBase.cs` `Indicators/WindowIndicator.cs` `Indicators/CompositeIndicator.cs`
> **相关文件：** `Indicators/SimpleMovingAverage.cs` `Indicators/BollingerBands.cs` `Indicators/MovingAverageConvergenceDivergence.cs`

### 1.1 四层继承链

```
IndicatorBase                    ← 非泛型根基
  ├── Name: 指标名称
  ├── Current: 最新值 (IndicatorDataPoint)
  ├── Previous: 前值
  ├── Window: RollingWindow<IndicatorDataPoint> ← 每个指标自带历史值缓存
  ├── IsReady: abstract, 由子类定义
  ├── Samples: 已处理样本数
  ├── Reset(): abstract
  ├── Updated: event ← 事件驱动的更新通知
  └── Consolidators: ISet<IDataConsolidator> ← 关联的数据聚合器

IndicatorBase<T> (abstract)     ← 泛型层
  ├── T 可以是 IBaseData / TradeBar / QuoteBar / Tick / IndicatorDataPoint
  ├── Update(T input) → ValidateAndComputeNextValue() → ComputeNextValue()
  └── 核心流程: 数据输入 → 验证 → 计算 → 更新 Current + 触发事件

WindowIndicator<T>              ← 滑动窗口指标 (SMA, EMA, ATR...)
  ├── 持有 RollingWindow<T> (原始数据窗口)
  ├── IsReady = _window.IsReady
  ├── WarmUpPeriod = Period (默认)
  └── ComputeNextValue(IReadOnlyWindow<T> window, T input)

BarIndicator                    ← typedef: IndicatorBase<IBaseDataBar>
TradeBarIndicator              ← typedef: IndicatorBase<TradeBar>
```

### 1.2 SMA 具体实现（极简设计）

```csharp
// SimpleMovingAverage.cs — 仅 78 行
public class SimpleMovingAverage : WindowIndicator<IndicatorDataPoint>
{
    // 内部注入 Sum 子指标用于滚动求和
    public IndicatorBase<IndicatorDataPoint> RollingSum { get; }

    public SimpleMovingAverage(string name, int period)
        : base(name, period)
    {
        RollingSum = new Sum(name + "_Sum", period);  // Sum 也是 WindowIndicator
    }

    protected override decimal ComputeNextValue(
        IReadOnlyWindow<IndicatorDataPoint> window,
        IndicatorDataPoint input)
    {
        RollingSum.Update(input.EndTime, input.Value);
        return RollingSum.Current.Value / window.Count;
    }
}
```

> **设计要点：** SMA 不是直接累加求平均，而是**委托给 Sum 指标**。Sum 本身也继承 WindowIndicator，
> 管理自己的 RollingWindow。这种"指标嵌套指标"的模式让每个指标职责单一、可独立测试。

### 1.3 BollingerBands — 指标组合模式

```csharp
// BollingerBands 内部有 7 个 IndicatorBase<IndicatorDataPoint> 子指标
BollingerBands : Indicator        // 注意：继承非泛型的 Indicator
  ├── MiddleBand       ← SMA (IndicatorBase<IndicatorDataPoint>)
  ├── StandardDeviation ← StdDev (IndicatorBase<IndicatorDataPoint>)
  ├── UpperBand        ← MiddleBand + k * StdDev
  ├── LowerBand        ← MiddleBand - k * StdDev
  ├── BandWidth        ← (Upper - Lower) / Middle * 100
  ├── PercentB         ← (Price - Lower) / (Upper - Lower)
  └── Price            ← 最后输入价格
```

> **关键设计：** `UpperBand` / `LowerBand` / `BandWidth` / `PercentB` 作为公开属性暴露，
> 用户可以通过 `.UpperBand.Current.Value` 读取。每个子指标独立管理自己的 `RollingWindow`。

### 1.4 CompositeIndicator — 事件驱动的指标合成

```csharp
// CompositeIndicator.cs
// 左右两个指标各自推送 Updated 事件，组合器监听两者的更新
public class CompositeIndicator : IndicatorBase<IndicatorDataPoint>
{
    public IndicatorBase Left { get; }
    public IndicatorBase Right { get; }
    private readonly IndicatorComposer _composer;
    // IndicatorComposer = Func<IndicatorBase, IndicatorBase, IndicatorResult>

    private void ConfigureEventHandlers()
    {
        // 左指标更新 → 检查右指标是否也有新数据 → 如果是则 Update
        Left.Updated += (sender, updated) => {
            newLeftData = updated;
            if (newRightData != null || rightIsConstant)
            {
                Update(new IndicatorDataPoint { Time = MaxTime(updated) });
                newLeftData = null; newRightData = null;
            }
        };
        // 右指标同样逻辑...
    }
}
```

> **MACD 内部结构：**
> ```
> MACD : Indicator
>   ├── Fast       ← EMA(fastPeriod) on price
>   ├── Slow       ← EMA(slowPeriod) on price
>   ├── Signal     ← EMA(signalPeriod) on (Fast - Slow)
>   └── Histogram  ← MACD - Signal
> ```
> 注意：MACD 不使用 CompositeIndicator，而是直接持有 4 个 IndicatorBase<IndicatorDataPoint>，
> 因为 CompositeIndicator 不支持中间计算的链式依赖。

### 1.5 对 TradingStudio 的指标设计建议

| Lean 做法 | TradingStudio 可采用 |
|-----------|---------------------|
| `IndicatorBase<T>` 泛型根基 | 同样用泛型根基，T = `TickRecord` / `Bar` / `IndicatorDataPoint` |
| `RollingWindow<T>` 内置历史 | 内置 `CircularBuffer<T>` 或直接用 `LinkedList<T>` |
| 子指标嵌套 | SMA 内部用 Sum，BB 内部用 SMA+StdDev |
| `Updated` 事件驱动 | 指标更新 → 触发 `Updated` → 组合指标自动同步 |
| `WarmUpPeriod` 自动管理 | 每个指标声明 `WarmUpPeriod`，回测引擎检查 `IsReady` |
| `CompositeIndicator` 模式 | 策略中用"两指标差/比/交叉"时很方便 |

---

## 2. DataFeed Enumerator 管道

> **关键文件：**
> - `FileSystemDataFeed.cs` — 回测数据源，构造枚举器链
> - `Enumerators/FillForwardEnumerator.cs` — 前向填充
> - `Enumerators/SynchronizingEnumerator.cs` — 合并多个订阅
> - `Enumerators/SubscriptionFilterEnumerator.cs` — 应用过滤器
> - `Enumerators/PriceScaleFactorEnumerator.cs` — 复权调整
> - `Synchronizer.cs` — 管道出口，向算法产出 TimeSlice
> - `SubscriptionSynchronizer.cs` — 核心合并逻辑

### 2.1 管道构造链（FileSystemDataFeed.CreateSubscription）

```
FileSystemDataFeed.CreateSubscription(request)
  │
  ├─ 如果 IsWarmingUp:
  │   ├─ CreateEnumerator(warmupRequest)  ──→ warmupEnumerator
  │   │   └── FilterEnumerator (截断到warmup.EndTime)
  │   ├─ CreateEnumerator(normalRequest)  ──→ normalEnumerator
  │   │   └── FilterEnumerator (只取>=warmup.EndTime的数据)
  │   └─ ConcatEnumerator(warmupEnumerator, normalEnumerator)
  │
  └─ 否则: CreateEnumerator(request)

CreateEnumerator → CreateDataEnumerator:
  ├── _subscriptionFactory.CreateEnumerator()  ← 从磁盘读取原始数据
  └── ConfigureEnumerator():
        ├── [可选] BaseDataCollectionAggregatorEnumerator ← 聚合成集合
        ├── TryAddFillForwardEnumerator:
        │     ├── [如果是Quote] QuoteBarFillForwardEnumerator
        │     └── FillForwardEnumerator ← 核心：填补数据间隙
        └── SubscriptionFilterEnumerator ← 应用交易所/用户过滤器
```

**关键代码（[FileSystemDataFeed.cs:232-251](c:\Works\ClaudeCode\Lean\Engine\DataFeeds\FileSystemDataFeed.cs#L232-L251)）：**

```csharp
protected IEnumerator<BaseData> ConfigureEnumerator(...)
{
    if (aggregate)
        enumerator = new BaseDataCollectionAggregatorEnumerator(enumerator, symbol);

    enumerator = TryAddFillForwardEnumerator(request, enumerator, ...);

    if (request.Configuration.IsFilteredSubscription)
        enumerator = SubscriptionFilterEnumerator.WrapForDataFeed(
            _resultHandler, enumerator, request.Security, ...);

    return enumerator;
}
```

### 2.2 FillForwardEnumerator — 数据前向填充

**核心职责：** 当数据有间隙时（如非交易时间），重复输出上一根 Bar。

**关键设计（[FillForwardEnumerator.cs:36](c:\Works\ClaudeCode\Lean\Engine\DataFeeds\Enumerators\FillForwardEnumerator.cs#L36)）：**

```
原始数据流:  [9:30] [9:31] [9:33] [9:35]
填充后流:    [9:30] [9:31] [9:32] [9:33] [9:34] [9:35]
                               ↑填充      ↑填充
```

- 持有 `_previous` 保存上一根数据
- 当 `_enumerator.MoveNext()` 的时间间隔超过 fillForwardResolution 时
- 克隆 `_previous`，设置新时间，从子枚举器输出
- 支持动态 fillForwardResolution（通过 `IReadOnlyRef<TimeSpan>`）

> **对 TradingStudio 的启示：** 你的 1min Bar 聚合如果遇到某个分钟没有 tick（如期货成交稀疏），
> 可以用同样的前向填充逻辑产生连续 Bar。当前 TradingStudio 的 `BarAggregator` 已经做了部分这个工作，
> 但 FillForward 模式更加分离——它不关心数据从哪来，只管填补间隙。

### 2.3 SynchronizingEnumerator — 多订阅时间对齐

**抽象基类（[SynchronizingEnumerator.cs:29](c:\Works\ClaudeCode\Lean\Engine\DataFeeds\Enumerators\SynchronizingEnumerator.cs#L29)）：**

```csharp
public abstract class SynchronizingEnumerator<T> : IEnumerator<T>
{
    // 持有多个 IEnumerator<T>
    // 核心方法: GetSynchronizedEnumerator(IEnumerator<T>[] enumerators)
    //   按 GetInstanceTime(T instance) 排序，合并多个枚举器为单个时间序流

    protected abstract DateTime GetInstanceTime(T instance);
}
```

**使用方式：**
```csharp
// 如果有 SPY 和 AAPL 两个订阅的枚举器
var syncEnumerator = new SynchronizingBaseDataEnumerator(spyEnumerator, aaplEnumerator);
// 现在按时间顺序交错输出两个品种的数据
```

### 2.4 SubscriptionSynchronizer.Sync() — 核心合并逻辑

**这是数据管道最核心的方法（[SubscriptionSynchronizer.cs:88-199](c:\Works\ClaudeCode\Lean\Engine\DataFeeds\SubscriptionSynchronizer.cs#L88-L199)）：**

```
Sync(subscriptions, cancellationToken):
  while (!cancelled):
    frontierUtc = _timeProvider.GetUtcNow()  ← 当前时间前沿

    foreach subscription in subscriptions:
      // 预读: 确保 Current != null
      if subscription.Current == null:
        if !subscription.MoveNext():
          OnSubscriptionFinished(); continue;

      DataFeedPacket packet = null;
      while subscription.Current.EmitTimeUtc <= frontierUtc:
        packet.Add(subscription.Current.Data);
        subscription.MoveNext();

      if packet?.Count > 0:
        if 是股票池订阅:
          universeData[universe] += packetData
        else:
          data.Add(packet)  ← 普通行情数据

    // 创建 TimeSlice
    yield return _timeSliceFactory.Create(frontierUtc, data, universeData, changes);
    _frontierTimeProvider.Advance();
```

### 2.5 Synchronizer.StreamData() — 管道出口

**顶层同步器（[Synchronizer.cs:86-161](c:\Works\ClaudeCode\Lean\Engine\DataFeeds\Synchronizer.cs#L86-L161)）：**

```csharp
public virtual IEnumerable<TimeSlice> StreamData(CancellationToken cancellationToken)
{
    var enumerator = SubscriptionSynchronizer
        .Sync(subscriptions, cancellationToken)
        .GetEnumerator();

    while (!cancellationToken.IsCancellationRequested)
    {
        if (!enumerator.MoveNext()) break;
        timeSlice = enumerator.Current;

        // 跳过时间脉冲（当算法已在该时间）
        if (timeSlice.IsTimePulse && algorithm.UtcTime == timeSlice.Time)
            continue;

        // 去重：跳过同时间的空切片
        if (timeSlice.Time != previousEmitTime
            || previousWasTimePulse
            || timeSlice.UniverseData.Count != 0)
        {
            previousEmitTime = timeSlice.Time;
            yield return timeSlice;  // ← 算法拿到 TimeSlice
        }
    }
}
```

### 2.6 TimeSlice — 传递给算法的数据结构

**（[TimeSlice.cs:28](c:\Works\ClaudeCode\Lean\Engine\DataFeeds\TimeSlice.cs#L28)）**

```
TimeSlice
  ├── Time: DateTime (UTC)
  ├── Data: List<DataFeedPacket> (原始数据)
  ├── Slice: Slice (算法的 OnData 输入)
  ├── SecuritiesUpdateData: List<UpdateData<ISecurityPrice>>
  ├── ConsolidatorUpdateData: List<UpdateData<SubscriptionDataConfig>>
  ├── CustomData: List<UpdateData<ISecurityPrice>>
  ├── SecurityChanges: SecurityChanges
  ├── UniverseData: Dictionary<Universe, BaseDataCollection>
  └── IsTimePulse: bool (纯时间推进，无数据)
```

> **关键：** `TimeSlice` 除了包含给用户的 `Slice`，还携带了引擎内部需要的数据——
> Security 更新数据、Consolidator 数据、股票池变更等。这种"一份数据多路消费"的设计
> 让消费者各自过滤自己关心的部分。

---

## 3. AlgorithmManager 主循环

> **关键文件：** `Engine/AlgorithmManager.cs` `Engine/Engine.cs`

### 3.1 完整主循环流程

```
AlgorithmManager.Run(job, algorithm, synchronizer, transactions, results, realtime, ...)
  │
  ├── 1. 预处理
  │     ├── 为自定义数据类型创建 MethodInvoker
  │     └── 注册每日 Midnight 采样事件
  │
  └── 2. foreach (timeSlice in Stream(synchronizer)):  ← 主循环
        │
        ├── TimeLimit.StartNewTimeStep()          ← 时间监控
        ├── 检查算法状态 / 取消令牌
        ├── leanManager.Update()                  ← 主机环境
        │
        ├── 检查组合价值 ≤ 0 → 停止
        │
        ├── realtime.ScanPastEvents(time)         ← 回放遗漏的定时事件
        ├── algorithm.SubscriptionManager.ScanPastConsolidators() ← 聚合器扫描
        │
        ├── algorithm.SetDateTime(time)           ← 设置算法当前时间
        ├── [如果是 TimePulse] continue            ← 跳过纯时间推进
        │
        ├── algorithm.SetCurrentSlice(slice)      ← 设置 CurrentSlice
        │
        ├── 处理 SecurityChanges                  ← 品种变更
        ├── Security.Update(update.Data...)       ← 更新所有 Security 价格
        ├── algorithm.TradeBuilder.SetMarketPrice() ← 更新 TradeBuilder
        │
        ├── 每小时: 扫描保证金利息 + 结算模型
        │
        ├── 处理 UniverseData                     ← 股票池数据缓存
        ├── 更新汇率转换                           ← CashBook.Update()
        ├── portfolio.InvalidateTotalPortfolioValue()
        │
        ├── 处理 SymbolChangedEvents (拆股/改名)
        │
        ├── transactions.ProcessSynchronousEvents() ← 订单撮合!
        ├── realtime.SetTime(timeSlice.Time)      ← 触发定时事件
        │
        ├── 处理 Split/Dividend/Delisting
        │
        ├── 检查算法状态 / 运行时错误
        │
        ├── 处理保证金追缴 (每5分钟)
        │
        ├── OnSecuritiesChanged()                 ← 通知用户品种变更
        │
        ├── HandleDividends/Splits()              ← 应用分红/拆股
        │
        ├── 更新 Consolidators                    ← 遍历所有注册的聚合器
        │
        ├── 触发自定义数据事件 (OnData(Quandl) 等)
        │
        ├── OnSplits/OnDividends/OnDelistings()   ← 触发公司事件
        │
        ├── algorithm.OnData(currentSlice)        ← ★ 用户代码入口！
        └── algorithm.OnFrameworkData(slice)      ← Framework 模型

  └── 3. 清理
        ├── results.SendStatusUpdate(Completed)
        └── 所有 Handler 有序退出
```

**关键代码（[AlgorithmManager.cs:558](c:\Works\ClaudeCode\Lean\Engine\AlgorithmManager.cs#L558)）：**

```csharp
if (timeSlice.Slice.HasData)
{
    // EVENT HANDLER v3.0 -- all data in a single event
    algorithm.OnData(algorithm.CurrentSlice);
}
// always turn the crank on this method to ensure universe selection
// models function properly on day changes w/out data
algorithm.OnFrameworkData(timeSlice.Slice);
```

> **关键发现：** `OnFrameworkData` 是**无条件调用**的（即使没有行情数据），
> 因为 Framework 模型（Alpha/Portfolio/Execution/Risk）需要在每个时间步检查状态，
> 尤其是在跨自然日时触发股票池选择。

### 3.2 执行顺序（为什么这个顺序很重要）

主循环中各个步骤的顺序经过精心设计：

1. **先更新 Security 价格** — 用户拿到的是最新价格
2. **再撮合订单** — `ProcessSynchronousEvents()` 在 OnData 之前，确保挂单已成交
3. **再触发定时事件** — `realtime.SetTime()` 在 OnData 之前，确保定时回调已执行
4. **最后调用 OnData** — 用户拿到的是完全更新后的状态

> **对 TradingStudio 的启示：** 你的主循环也需要同样的执行顺序，尤其是在回测中：
> 价格更新 → 订单撮合 → 风控检查 → 策略 OnBar/OnTick

---

## 4. Order / OrderTicket 订单状态机

> **关键文件：** `Common/Orders/Order.cs` `Common/Orders/OrderTicket.cs` `Common/Orders/OrderEvent.cs`

### 4.1 三层模型

```
Order (抽象基类)         ← 订单的静态属性
  ├── Id, Symbol, Price, Quantity, Type, Status, Time
  ├── Direction (Buy/Sell)
  ├── TimeInForce (GTC/DAY/GTD)
  └── 子类: MarketOrder, LimitOrder, StopMarketOrder, ...

OrderTicket (密封类)     ← 线程安全的订单跟踪器
  ├── OrderId, Status, Symbol
  ├── QuantityFilled, AverageFillPrice
  ├── Submit() / Cancel() / Update()
  ├── OrderEvents: List<OrderEvent>
  ├── _order: 内部 Order 引用
  └── _orderStatusClosedEvent: ManualResetEvent ← 等待订单完成

OrderEvent               ← 订单生命周期中的状态变更
  ├── OrderId, Symbol, UtcTime
  ├── Status: Submitted / Accepted / PartiallyFilled / Filled / Canceled / Invalid
  ├── FillPrice, FillQuantity, OrderFee
  └── Direction, Message
```

### 4.2 OrderTicket 线程安全设计

**（[OrderTicket.cs:29](c:\Works\ClaudeCode\Lean\Common\Orders\OrderTicket.cs#L29)）**

```csharp
public sealed class OrderTicket
{
    private readonly object _lock = new object();
    private Order _order;
    private OrderStatus? _orderStatusOverride;

    // 线程安全的 Status 读取
    public OrderStatus Status => _orderStatusOverride
        ?? _order?.Status
        ?? OrderStatus.New;

    // 阻塞等待订单完成
    public bool OrderClosed => _orderStatusClosedEvent.WaitOneAsync(...);

    // 线程安全的订单更新
    public OrderEvent GetMostRecentOrderEvent() { lock(_lock) {...} }

    // 取消订单
    public CancelOrderRequest Cancel(string tag = null) {...}
}
```

> **关键设计：** `OrderTicket` 是算法层看到的东西。`Order` 是引擎内部处理的东西。
> 两者通过 `OrderTicket._order` 引用关联。`OrderTicket` 额外提供了：
> - 线程安全的状态查询
> - 订单事件的完整历史
> - 等待订单完成的同步机制

### 4.3 订单生命周期

```
用户代码: Buy("SPY", 100)
  │
  ├─→ SubmitOrderRequest (生成 OrderId, 创建 Order)
  │     └─→ OrderTicket (包装 Order)
  │
  ├─→ OrderEvent: Submitted
  │     └─→ Order.Status = Submitted
  │
  ├─→ OrderEvent: Accepted (券商确认)
  │     └─→ Order.Status = Submitted (不变，等待成交)
  │
  ├─→ OrderEvent: PartiallyFilled (部分成交, FillQuantity=60)
  │     └─→ Order.Status = PartiallyFilled
  │     └─→ OrderTicket.QuantityFilled = 60
  │
  ├─→ OrderEvent: Filled (完全成交, FillQuantity=40)
  │     └─→ Order.Status = Filled
  │     └─→ OrderTicket.QuantityFilled = 100
  │     └─→ _orderStatusClosedEvent.Set() ← 通知等待线程
  │
  └─→ 或 → OrderEvent: Canceled / Invalid
        └─→ _orderStatusClosedEvent.Set()
```

### 4.4 OrderEvent 的 ProtoBuf 序列化

**（[OrderEvent.cs:29](c:\Works\ClaudeCode\Lean\Common\Orders\OrderEvent.cs#L29)）**

```csharp
[ProtoContract(SkipConstructor = true)]
public class OrderEvent
{
    [ProtoMember(1)] public int OrderId { get; set; }
    [ProtoMember(2)] public int Id { get; set; }
    [ProtoMember(3)] public Symbol Symbol { get; set; }
    [ProtoMember(4)] public DateTime UtcTime { get; set; }
    [ProtoMember(5)] public OrderStatus Status { get; set; }
    // ... 19 个 ProtoMember 字段
}
```

> **使用 ProtoBuf 的原因：** OrderEvent 需要持久化（存盘、跨进程传输），ProtoBuf 比 JSON 更紧凑快速。
> TradingStudio 的订单记录也可以考虑用同样的方式。

---

## 5. 对 TradingStudio 的启示

### 5.1 数据管道映射

| Lean | TradingStudio (当前) | 建议 |
|------|---------------------|------|
| FileSystemDataFeed.CreateEnumerator | `TickCsvWriter` (只写) + `BarAggregator` | 回测时需要对称的读路径 |
| FillForwardEnumerator | ❌ 无 | Bar 聚合的间隙填充可独立为 FillForward 步骤 |
| SynchronizingEnumerator | ❌ 无 | 多品种同时回测时需要合并多个数据流 |
| SubscriptionFilterEnumerator | ❌ 无 | 日夜盘过滤可作为独立 Filter 步骤 |
| TimeSliceFactory | ❌ 无 | 可以定义一个 `MarketSlice` 结构，封装 Tick/Bar/信号 |

### 5.2 当前 TradingStudio 数据管道 vs Lean 管道

```
TradingStudio 当前（简单但够用）:
  CTP Quote → CtpMdAdapter → Channel<TickRecord> → TickCsvWriter + BarAggregator → BarStore

Lean（完整但复杂）:
  文件读取 → FillForward → Synchronizing → Filter → PriceScale → TimeSlice → Algorithm
```

> **建议路径：** 当前 TradingStudio 处于 Phase 1（实盘数据采集），这条简单管道完全够用。
> 到 Phase 2（回测引擎）时，需要增加：
> 1. 对称的**文件读取路径**（从 CSV/SQLite 回放 Tick/Bar）
> 2. **前向填充**（填补非交易时段的 Bar 间隙）
> 3. **时间多路合并**（如果同时回测多个品种）
> 4. 此时才需要参考 Lean 的 Enumerator 管道设计。

### 5.3 指标系统映射

| Lean | TradingStudio 建议 |
|------|-------------------|
| `IndicatorBase` + `IndicatorBase<T>` | 同样两层根基 |
| `RollingWindow<IndicatorDataPoint> Window` | `CircularBuffer<decimal>` 内置 |
| `Updated` 事件 | 事件或 `IObservable<IndicatorDataPoint>` |
| `CompositeIndicator` | 策略中"两个指标的差/比/交叉" |
| `WarmUpPeriod` + `IsReady` | 每个指标声明预热期，回测引擎统一检查 |
| 166 个指标 | 20 个核心指标（按需增加） |

### 5.4 订单系统映射

| Lean | TradingStudio 建议 |
|------|-------------------|
| `Order` (抽象类) | `OrderBase` (record 或 class) |
| `OrderTicket` (线程安全包装) | 风控引擎横切层的天然节点 |
| `OrderEvent` (状态事件) | `OrderEvent` (record struct)，ProtoBuf 序列化 |
| `SubmitOrderRequest` / `CancelOrderRequest` | 命令对象模式 |
| `TransactionHandler` | 回测用 `SimulatedExecution`，实盘用 `CtpExecution` |

---

## 代码阅读清单

按 TradingStudio 当前开发阶段（Phase 1 → Phase 2 过渡），建议阅读顺序：

| # | 文件 | 关注点 | 行数 | 状态 |
|---|------|--------|------|------|
| 1 | `Indicators/IndicatorBase.cs` | 指标根基设计 | ~300 | ✅ |
| 2 | `Indicators/SimpleMovingAverage.cs` | 具体指标实现 | ~78 | ✅ |
| 3 | `Indicators/CompositeIndicator.cs` | 事件驱动指标组合 | ~215 | ✅ |
| 4 | `Engine/DataFeeds/Enumerators/FillForwardEnumerator.cs` | 前向填充 | ~400 | ✅ |
| 5 | `Engine/DataFeeds/FileSystemDataFeed.cs` | 回测数据源 + 管道构造 | ~306 | ✅ |
| 6 | `Engine/DataFeeds/SubscriptionSynchronizer.cs` | 多订阅合并核心 | ~220 | ✅ |
| 7 | `Engine/DataFeeds/Synchronizer.cs` | 顶层同步器 | ~240 | ✅ |
| 8 | `Engine/AlgorithmManager.cs` | 主循环完整流程 | ~650 | ✅ |
| 9 | `Common/Orders/Order.cs` | 订单基类 | ~500 | ✅ |
| 10 | `Common/Orders/OrderTicket.cs` | 线程安全订单包装 | ~500 | ✅ |
| 11 | `Common/Orders/OrderEvent.cs` | 订单状态事件 | ~370 | ✅ |

---

> 📁 **相关文档：**
> - [Lean引擎架构分析.md](Lean引擎架构分析.md) ← Day 1 架构全景
> - [TradingStudio架构设计-精简版.md](TradingStudio架构设计-精简版.md) ← 精简后的设计方案
> - [架构验证报告.md](架构验证报告.md) ← 设计一致性检查
