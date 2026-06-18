# TradingStudio vs Lean 框架 — 架构对比分析

> 2026-06-18 | 陈新勇
>
> 基于两个代码仓库的完整遍历，从架构师视角做一次诚实的对比。
> Lean 是 QuantConnect 团队 10+ 年打磨的企业级量化框架；TradingStudio 是一个人的 Phase 2 回测引擎。
> 对比不是为了分出高下，而是看清差距、确认方向、找到可以借鉴的设计智慧。

---

## 一、体量与成熟度

| 维度 | Lean | TradingStudio |
|------|------|---------------|
| 项目数 | 23 个 .csproj | 10 个 .csproj (含 C++/CLI) |
| 代码规模 | ~50 万行 C# | ~3 万行 C# |
| 支持品种 | 股票/期权/期货/外汇/加密货币/CFD/指数 | **仅国内期货** |
| 指标体系 | 150+ 内置指标 | 5 个 (SMA/EMA/MACD/RSI/Bollinger) |
| 券商接入 | 10+ 家实盘券商 | 仅 CTP (自封装 C++/CLI) |
| 语言支持 | C# + Python (PythonNet) | 仅 C# |
| 回测/实盘 | 完整双模 | 回测引擎刚完成，实盘路径存在但未验证 |
| 测试覆盖 | NUnit 全项目覆盖 | 无自动化测试 |
| 社区/文档 | 完整 API 文档 + 社区 | CLAUDE.md 自文档 |

**结论：体量差距约 15-20 倍，这是合理的——一个人的工作室 vs 一个商业化产品。**

---

## 二、架构哲学差异

### Lean："一切皆可插拔"

Lean 的核心理念是 **Strategy Pattern 无处不在**。每个 Security 对象携带 8+ 个可替换模型：

```
Security
├── FillModel          (IFillModel)           — 订单如何成交
├── FeeModel           (IFeeModel)            — 手续费计算
├── SlippageModel      (ISlippageModel)       — 滑点模拟
├── BuyingPowerModel   (IBuyingPowerModel)    — 保证金/购买力
├── SettlementModel    (ISettlementModel)     — 资金结算(T+2/即时)
├── PortfolioModel     (ISecurityPortfolioModel) — 成交簿记
├── VolatilityModel    (IVolatilityModel)     — 波动率估计
└── MarginInterestRateModel — 融资利息
```

通过 `SetBrokerageModel(BrokerageName.InteractiveBrokers)` 一键替换全部。

### TradingStudio："够用就好，精准聚焦"

TradingStudio 的架构更务实——只抽象真正需要变化的部分：

```
IStrategy          → 策略可插拔
IDataFeed          → 回测/实盘数据源可切换
IExecutionHandler  → 订单执行（回测撮合 vs CTP实盘）
IRiskRule          → 风控规则链
IBarStore          → 存储后端可切换（SQLite→DuckDB→ClickHouse）
IIndicator          → 指标可扩展
```

**关键差异：** TradingStudio 不做过度抽象。比如手续费直接写在 `Future` 品种配置里（`FeeRate`/`FeeType`），而不是一个 `IFeeModel` 接口——因为国内期货手续费规则相对固定，不需要 10 种实现。

**评价：这是正确的取舍。** 一个人维护 8 个可插拔模型接口 = 维护噩梦；5 个核心接口恰恰覆盖了真正需要变化的部分。

---

## 三、数据流架构对比

### Lean：多层枚举器栈

```
FileSystemDataFeed
  └─ SubscriptionCollection
       └─ Subscription (per config)
            └─ Enumerator Stack (从内到外):
                 SubscriptionDataReader (读文件)
                 → FillForwardEnumerator (前向填充)
                 → PriceScaleFactorEnumerator (价格调整)
                 → SubscriptionFilterEnumerator (市场时间过滤)
            → EnqueueableEnumerator (生产者-消费者队列)
       → SubscriptionSynchronizer (多订阅时间对齐)
            → TimeSliceFactory
                 → TimeSlice → Slice
                      → AlgorithmManager.Run()
                           → algorithm.OnData(Slice)
```

特点：
- **枚举器栈模式** — 每层是一个 `IEnumerator<BaseData>`，像洋葱一样包裹
- **后台 Worker 线程** — 文件读取在后台线程，通过 `EnqueueableEnumerator` 解耦
- **时间前沿对齐** — `SubscriptionFrontierTimeProvider` 确保所有订阅在同一时间点对齐
- **类型路由** — `TimeSliceFactory` 将数据按类型分到 `Bars`/`QuoteBars`/`Ticks` 等字典

### TradingStudio：通道驱动的生产者-消费者

```
CTP MdApi (native C++)
  └─ OnQuote 回调 (native thread)
       └─ Channel<Quote> (System.Threading.Channels)
            ├─ PersistChannel → LiveDataCollector (BackgroundService)
            │    ├─ TickCsvWriter (42列CSV)
            │    ├─ BarAggregator.Feed() → 1min Bar
            │    └─ DailyBarAggregator.Feed() → Day Bar
            │
            └─ Merged Channel → CtpLiveFeed.StreamAsync()
                 └─ IAsyncEnumerable<DataEvent>
                      ├─ TickEvent → TradingEngine
                      └─ BarEvent  → TradingEngine
```

特点：
- **直接使用 `System.Threading.Channels`** — 比 Lean 的枚举器栈更简洁，利用 .NET 原生高性能通道
- **无后台 Worker 线程** — Channel 本身就是生产者-消费者，不需要手动调度
- **无时间前沿对齐** — 因为只有一个数据源（CTP），不需要多源同步
- **Bar 在写入 Channel 前就聚合好了** — 比 Lean 的 consolidator 模式更直接

**评价：TradingStudio 的方案更适合单源、单品种类别的场景。** Lean 的多源同步是为股票+期权+期货混合回测设计的，TradingStudio 不需要这份复杂度。

---

## 四、策略/算法模型对比

### Lean：QCAlgorithm — 全功能基类

```csharp
public class MyAlgorithm : QCAlgorithm
{
    public override void Initialize()
    {
        SetStartDate(2020, 1, 1);
        SetCash(100000);
        AddEquity("SPY", Resolution.Minute);
    }

    public override void OnData(Slice slice)
    {
        if (slice.Bars.ContainsKey("SPY"))
        {
            var bar = slice.Bars["SPY"];
            if (!Portfolio.Invested)
                SetHoldings("SPY", 1.0);
        }
    }
}
```

核心设计：
- **胖基类** — `QCAlgorithm` 是一个 3800+ 行的 partial class，策略通过 `Portfolio`、`Securities`、`Transactions` 等强类型属性访问一切
- **时间驱动** — 引擎按时间步进，每个时间点调用 `OnData(Slice)`
- **框架管线** — `Alpha → Portfolio → Risk → Execution` 四个模型链，默认都是 Null（opt-in）
- **安全性锁** — `SetLocked()` 防止初始化后修改配置

### TradingStudio：IStrategy — 精简接口

```csharp
public class MaCrossStrategy : IStrategy
{
    public void Initialize(StrategyContext context) { ... }
    public void OnBar(BarEvent bar) { ... }
    public void OnTick(TickEvent tick) { ... }
    public void OnOrderEvent(OrderEvent orderEvent) { ... }
    public void OnEndOfAlgorithm() { ... }
}
```

核心设计：
- **瘦接口** — 6 个方法，无基类依赖
- **Facade 隔离** — 策略只看到 `StrategyContext`，不能直接访问引擎内部
- **Tick + Bar 双驱动** — 策略可以选择响应 Tick 或 Bar，比 Lean 只有 Slice 更灵活（对期货高频友好）
- **JSON 驱动配置** — `StrategyConfig` + `StrategyParameterAttribute`，参数声明式管理

### 关键差异

| 维度 | Lean | TradingStudio |
|------|------|---------------|
| 策略基类 | 胖基类 (3800行) | 接口 + Facade |
| 数据接入 | 仅 OnData(Slice) | OnTick + OnBar 分离 |
| 下单方式 | `SetHoldings` / `MarketOrder` | `StrategyContext.MarketBuy/Sell` |
| 持仓查询 | `Portfolio["SPY"].Quantity` | `StrategyContext.GetPosition(instrument)` |
| 参数管理 | `GetParameter("name")` | `[StrategyParameter]` 特性 + JSON |
| 多策略隔离 | 无原生支持 | `StrategyContainer` + 子账户 |
| 学习曲线 | 陡峭 (需理解框架) | 平缓 (接口即文档) |

**评价：TradingStudio 的策略接口更简洁，Facade 隔离更好。但缺少 Lean 的框架管线（Alpha→Portfolio→Risk→Execution 的自动编排），需要策略自己管理信号→下单的完整链路。**

---

## 五、投资组合与风控对比

### Lean：分层模型体系

Lean 的投资组合体系非常厚重：

```
SecurityPortfolioManager (总账本)
├── CashBook (已结算现金, 多币种)
├── UnsettledCashBook (未结算现金)
├── SecurityPositionGroupModel (跨品种保证金组)
├── MarginCallModel (追加保证金)
└── Per-Security Models:
     ├── SecurityHolding (持仓数量/均价/盈亏)
     ├── BuyingPowerModel (保证金计算)
     ├── SettlementModel (T+2/即时/期货每日盯市)
     └── FutureHolding(已结算/未结算利润分离)
```

风险管理是框架管线的一环：
```
AlphaModel → Insights
  → PortfolioConstructionModel → Targets
    → RiskManagementModel → Adjusted Targets
      → ExecutionModel → Orders
```

### TradingStudio：精简但完整

TradingStudio 的投资组合和风控更直接：

```
PortfolioManager (单实例, 策略子账户隔离)
├── Cash (decimal, 单一币种)
├── Positions: Dictionary<string, Position>
├── Trades: List<Trade>
├── EquityCurve: List<EquityPoint>
└── 多策略子账户: Dictionary<string, SubPortfolio>

RiskController (独立横切层)
├── IRiskRule[] (链式检查)
├── CheckPreOrder() → 阻断
├── CheckPostFill() → 警告
└── 3 内置规则: MaxPosition, MaxOrderQty, MaxDrawdown
```

### 关键差异

| 维度 | Lean | TradingStudio |
|------|------|---------------|
| 多币种 | CashBook 多币种 + 实时汇率 | 仅人民币 |
| 结算模型 | 4种 (即时/T+N/期货每日/自定义) | 即时 (不需要T+N) |
| 保证金计算 | 品种特定模型 (股票/期货/期权各不相同) | Future.MarginRate (配置驱动) |
| 融资利息 | IMarginInterestRateModel | 无 (期货无融资利息) |
| 风控位置 | 框架管线内 (RiskModel) | 独立横切层 (任何订单必过) |
| 子账户隔离 | 无 | 多策略独立子账户 |

**评价：**
- TradingStudio 的 **风控做在一个正确的抽象层**——独立横切层，这是好的。Lean 的风控在框架管线里，用户如果不用框架管线就等于没风控。
- TradingStudio 的 **投资组合模型刚好够用**——国内期货是单一币种、无融资利息、每日盯市结算。不需要 Lean 那一套多币种+T+N+融资利息的复杂体系。
- **多策略子账户是 TradingStudio 独有的创新**——Lean 没有原生的多策略隔离。

---

## 六、订单执行对比

### Lean：BacktestingBrokerage + FillModel 分离

```
algorithm.MarketOrder("SPY", 100)
  → Transactions.AddOrder()
    → BrokerageTransactionHandler.AddOrder()
      → BacktestingBrokerage.PlaceOrder(order)
        → _pending[order.Id] = order
      → 下个时间步 Scan():
        → security.FillModel.Fill(parameters)
          → MarketFill() / LimitFill() / StopMarketFill()
        → security.FeeModel.GetOrderFee()
        → OnOrderEvents(fills) → 回传
```

特点：
- **Fill 逻辑在 Security 级别** — 不同品种有不同的 FillModel（EquityFillModel, FutureFillModel, etc.）
- **一个时间步延迟** — 市价单在当前 Bar 提交，下一个 Bar 成交
- **价格使用 OHLC** — LimitFill 检查 `Low < limitPrice`，非常精细

### TradingStudio：ExecutionHandler 统一入口

```
StrategyContext.MarketBuy(instrument, quantity)
  → ExecutionHandler.Submit(order)
    → RiskController.CheckPreOrder() → 阻断/放行
    → IsBacktest:
        → 下个 Bar: ProcessBar() → MatchBar()
          使用 Bar.Open 作为成交价 (保守估计)
    → IsLive:
        → CtpTraderBridge.SendOrder()
          → CTP TraderApi.InsertOrder()
```

特点：
- **一个 ExecutionHandler 处理所有品种** — 不做品种特定的成交模型
- **保守成交价** — 使用 Bar.Open 而非更精细的 OHLC 匹配
- **风控在成交前** — `CheckPreOrder` 是硬阻断，比 Lean 的 BuyingPower 检查更前置

**评价：TradingStudio 的成交模型比 Lean 简化很多——这是最大的一项"技术债务"。Lean 的 FillModel 可以模拟限价单在当日 K 线内的最优成交（如在最低价成交限价买单），这对回测精度影响很大。TradingStudio 使用 Bar.Open 统一成交是一种保守但粗略的近似。这是 Phase 2b 值得优化的方向。**

---

## 七、指标体系对比

### Lean：150+ 指标 + 事件驱动自动接线

```csharp
// Lean 方式：自动接线
var sma = SMA("SPY", 20, Resolution.Daily);  // 自动创建 consolidator + 订阅数据
var rsi = RSI("SPY", 14);                     // 同上

// 指标链：事件驱动
var smaOfRsi = rsi.SMA(9);  // SMA 订阅 RSI 的 Updated 事件
var bb = BB("SPY", 20, 2);  // 6 个子指标自动创建 + 接线

// 指标可以这样用：
if (sma > 100) { ... }     // 隐式转换 + 比较运算符
if (bb.UpperBand.IsReady) { ... }
```

核心设计：
- **IndicatorBase<T>** — 泛型基类，`ComputeNextValue(T input)` 是唯一的抽象方法
- **RollingWindow** — 环形缓冲，`Window[0]` = 当前值，`Window[1]` = 前值
- **自动接线** — `RegisterIndicator` 解析/创建 Consolidator，自动将数据泵入指标
- **指标组合** — `CompositeIndicator` + `.Of()` + `.Plus()`/`.Minus()` 等运算符
- **前向保护** — `Update` 拒绝乱序数据，防止回测 bug

### TradingStudio：5 个指标 + 手动管理

```csharp
// TradingStudio 方式：手动管理
var sma = new SimpleMovingAverage(20);
var rsi = new RelativeStrengthIndex(14);

// 在策略中手动更新：
public void OnBar(BarEvent bar) {
    sma.Update(bar.Bar);
    rsi.Update(bar.Bar);
    
    if (sma.CurrentValue > bar.Bar.Close) { ... }
}

// 指标注册到 IndicatorManager（共享实例、预热）：
context.RegisterIndicator("SMA_20", sma);
```

核心设计：
- **IIndicator** — 简单接口，`Update(Bar)` + `CurrentValue` + `IsReady` + `WarmupPeriod`
- **无自动接线** — 策略在 `OnBar` 中手动调用 `Update`
- **IndicatorManager** — 管理共享实例 + 预热
- **无指标组合机制** — 需要手动计算复合指标

### 差距分析

| 维度 | Lean | TradingStudio | 差距 |
|------|------|---------------|------|
| 指标数量 | 150+ | 5 | **巨大** |
| 自动接线 | Consolidator→Indicator | 手动 Update | **中等** (对当前策略规模可接受) |
| 指标链 | `.Of()` + 运算符 | 无 | **中低** |
| 类型安全 | 泛型 IndicatorBase<T> | 非泛型 | **低** |
| 前向保护 | 拒绝乱序数据 | 无 | **中等** (手动管理时容易出错) |
| 预热 | IndicatorHistory 自动 | WarmupIndicator 手动 | **中低** |

**评价：这是最大的功能差距。** 但务实地说——一个人不可能维护 150 个指标。策略决定指标需求，指标需求驱动实现。当需要 ATR、ADX、布林带宽度、Keltner 通道等指标时，逐个实现即可。Lean 的 `IndicatorBase<T>` + `ComputeNextValue` 模式非常值得借鉴，TradingStudio 的 `IIndicator` 接口可以升级到这个模式。

---

## 八、数据管理对比

### Lean：SecurityIdentifier + SymbolPropertiesDatabase

```
SecurityIdentifier (SID)
├── 永久ID，编码了 SecurityType + Market + Date + Strike 等
├── 字符串表示: "AAPL R36P5QI8ST91"
└── 不变 → 适合做字典Key

Symbol
├── SID (永久)
├── Value (可变, 随映射而变)
└── Underlying (衍生品)

SymbolPropertiesDatabase (CSV驱动)
├── 所有品种的合约乘数/最小变动价位/手数等
└── 从 symbol-properties-database.csv 加载

MarketHoursDatabase (JSON驱动)
├── 每个交易所的交易日历/交易时间/时区
└── 从 market-hours-database.json 加载
```

### TradingStudio：Exchange + Future + FutureRegistry

```
ExchangeCode (enum)
├── 6 个交易所: SHFE/INE/DCE/CZCE/CFFEX/GFEX
└── FromCtp()/ToCtp() 字符串转换

Future (record)
├── 品种级别规则: 保证金率/最小变动价位/手续费/涨跌停板
├── Months[] → ContractCodeGenerator 生成合约代码
└── 75 个品种，从 symbols.json 加载

FutureRegistry (Dictionary)
├── Resolve(string instId) → 剥数字得品种代码
└── 字典查找
```

### 关键差异

| 维度 | Lean | TradingStudio |
|------|------|---------------|
| 品种覆盖 | 全球多市场/多品种 | 国内期货6交易所 |
| 数据规格源 | CSV+JSON 数据库文件 | symbols.json (Python脚本生成) |
| 合约代码生成 | 不需要 (交易所标准代码) | ContractCodeGenerator (处理郑商所短码等) |
| 交易日历 | MarketHoursDatabase (精确到交易所) | tradingHours 字符串 (简单) |
| 时区 | NodaTime DateTimeZone | 北京时间 (单一) |

**评价：TradingStudio 的数据模型刚好匹配国内期货市场的需求。** `ContractCodeGenerator` 处理了郑商所的短合约代码（如TA007→TA407）和 CFFEX 的月份规则，这是 Lean 不需要的。`Future` record 的设计很干净——品种级别的交易规则数据驱动，符合设计原则。

---

## 九、引擎架构对比

### Lean：分层 Handler 架构

```
Engine
├── SetupHandler (ISetupHandler)
│   ├── BacktestingSetupHandler
│   └── BrokerageSetupHandler (Live)
├── DataFeed (IDataFeed)
│   ├── FileSystemDataFeed (Backtest)
│   └── LiveTradingDataFeed
├── Synchronizer (ISynchronizer)
│   ├── Synchronizer (Backtest)
│   └── LiveSynchronizer
├── TransactionHandler (ITransactionHandler)
│   ├── BacktestingTransactionHandler
│   └── BrokerageTransactionHandler (Live, 多线程)
├── RealTimeHandler (IRealTimeHandler)
│   ├── BacktestingRealTimeHandler
│   └── LiveTradingRealTimeHandler (独立线程)
└── ResultHandler (IResultHandler)
    ├── BacktestingResultHandler
    └── LiveTradingResultHandler
```

每个 Handler 有独立的 Backtest/Live 实现，通过配置切换。

### TradingStudio：统一定义的引擎

```
TradingEngine (单类, ~500行)
├── IDataFeed → 数据源 (历史回放 or CTP实时)
├── ExecutionHandler → 订单执行 (模拟撮合 or CTP下单)
├── PortfolioManager → 投资组合簿记
├── RiskController → 风控规则链
├── StrategyContainer → 多策略管理
├── IndicatorManager → 指标管理
├── FeedbackMonitor → 四维监控
└── StrategyFactory → 策略实例化

执行模式:
  if (_isLive) { ... } else { ... }
```

### 关键差异

| 维度 | Lean | TradingStudio |
|------|------|---------------|
| Handler 分离 | 每层独立接口+双实现 | 单类内 if/else 分支 |
| 线程模型 | Live 专用多线程处理 | Channel 异步解耦 |
| 配置切换 | Handler 工厂+DI | Program.cs DI 注册 |
| 可扩展性 | 新增 Brokerage 只需实现接口 | 新增市场需要修改引擎 |

**评价：Lean 的 Handler 架构更"企业级"——每层清晰的职责边界、独立的 Backtest/Live 实现、通过配置切换。TradingStudio 的方案对当前阶段（一个人、一个市场、一个券商）是正确的——不需要为"可能永远不会发生的扩展"支付架构税。但如果未来要接 IB 或其他券商，Handler 分离是更优的模式。**

---

## 十、TradingStudio 相比 Lean 的创新/优势

### 1. 多策略子账户隔离
Lean 没有原生的多策略隔离——一个算法实例对应一个 Portfolio。TradingStudio 的 `PortfolioManager` 支持多个策略独立运行、各自拥有子账户和资金分配，这在 Lean 里需要手动模拟。

### 2. FeedbackMonitor 四维模型
TradingStudio 有一个独立的 `FeedbackMonitor`，持续监控：
- **执行健康** — 订单延迟/拒绝率/滑点
- **策略健康** — 信号频率/持仓时间
- **风险健康** — 保证金使用率/集中度
- **系统健康** — 数据流/CPU/内存

Lean 没有对应的独立监控组件——这分散在各个 Handler 的日志里。

### 3. ContractActivityTracker
针对国内期货 928 个合约但只有 30-50 个活跃的现实，TradingStudio 实现了一个"软过滤"——观察 60 秒，只把活跃品种推送给策略引擎。Lean 要处理全球市场，没有这个优化需求。

### 4. C++/CLI 自封装 CTP
这是 TradingStudio 最大的技术壁垒——完全自研的 C++/CLI 封装层，不依赖任何第三方 CTP 封装库。这意味着对 CTP 的行为有完全控制权。

### 5. Channel-based 异步架构
使用 `System.Threading.Channels` 作为核心数据管道，比 Lean 的枚举器栈 + 后台 Worker 线程更简洁，内存效率更高。

---

## 十一、TradingStudio 应该从 Lean 学习的关键设计

### 1. IndicatorBase<T> 的泛型设计模式
```
当前: IIndicator { Update(Bar) → decimal CurrentValue }
建议: IndicatorBase<T> { abstract ComputeNextValue(T) → IndicatorResult }
```
好处：类型安全、前向保护、RollingWindow 历史、隐式转换、比较运算符。

### 2. FillModel 的精细成交模拟
```
当前: Bar.Open 统一成交价
建议: FillModel { MarketFill(Open), LimitFill(检查High/Low是否触发限价) }
```
这是回测精度的关键差距。限价单在 Lean 里检查 K 线最高/最低价是否触发了限价，TradingStudio 目前做不到。

### 3. SecurityIdentifier 的不可变 ID 设计
```
当前: string instId = "ag2608"
建议: Symbol { PermanentId + CurrentTicker }
```
当前用字符串做 Key，当合约换月时需要额外处理。Lean 的 `SecurityIdentifier`（编码了品种类型+市场+到期日）是更好的设计。

### 4. DataNormalizationMode
```
当前: 原始价格
建议: Raw/SplitAdjusted/TotalReturn 模式
```
期货需要处理主力换月的价格跳空（Panama Canal 调整），这是 Lean 已有的能力。

### 5. History() 方法
```
当前: 策略通过 IBarStore 直接查询
建议: StrategyContext.History(symbol, period, resolution)
```
Lean 的 `History()` 是一个统一的、支持 warmup 的历史数据查询接口。TradingStudio 的策略目前直接依赖 `IBarStore`，耦合度较高。

---

## 十二、架构决策回顾：TradingStudio 做对了什么

### ✅ 独立风控横切层
这是最正确的架构决策之一。风控不依赖策略框架、不依赖执行层，任何订单到达 CTP 之前必须过风控。Lean 的风控在框架管线内，opt-in 的，可以被绕过。

### ✅ 行情与交易物理分离
CTP 的 MdApi 和 TraderApi 是两套独立连接，代码也完全分离。这符合"行情和交易应该独立故障域"的原则。

### ✅ IDataFeed 统一抽象
回测和历史数据回放使用同一个接口，保证了策略在回测和实盘中的行为一致性（除了成交模型差异）。

### ✅ Facade 隔离策略
`StrategyContext` 正面限制了策略能做什么——不能直接访问引擎内部、不能绕过风控下单。Lean 的 `QCAlgorithm` 暴露太多了。

### ✅ 数据驱动配置
`symbols.json` → `FutureRegistry` + `gen_final_specs.py` → 知识库。从第一天起就没有硬编码品种规则。

### ✅ 保持单一市场、单一语言
不试图支持多市场、多语言。这大幅降低了复杂度，让一个人能实际完成这个系统。

---

## 十三、阶段路线图建议

基于以上对比，Phase 2 回测引擎完成后的优先级建议：

### 短期（Phase 2b）
1. **[高优] 升级成交模型** — 参考 Lean FillModel，至少支持 `MarketFill(Open)` + `LimitFill(检查High/Low)`
2. **[高优] 升级指标基类** — 参考 `IndicatorBase<T>`，增加前向保护 + RollingWindow
3. **[中优] History() 统一接口** — 在 StrategyContext 上提供，隔离 IBarStore

### 中期（Phase 3）
4. **[中优] 数据规范化** — 主力换月 Panama Canal 调整
5. **[低优] Symbol 不可变 ID** — 当品种数量或合约管理复杂度超过字符串方案承载能力时重构

### 长期（Phase 4+）
6. **[观望] Handler 分离架构** — 只有在需要接入第二个券商（如 IB）时才做
7. **[观望] 多语言支持** — 只在策略研发遇到 C# 瓶颈时才考虑 Python

---

## 附录：关键文件对照表

| 概念 | Lean 位置 | TradingStudio 位置 |
|------|----------|-------------------|
| 引擎入口 | `Engine/Engine.cs` | `TradingStudio/Program.cs` |
| 主循环 | `Engine/AlgorithmManager.cs` | `Engine/TradingEngine.cs` |
| 策略基类 | `Algorithm/QCAlgorithm.cs` | `Core/Strategy/IStrategy.cs` |
| 策略上下文 | `QCAlgorithm` 自身 (胖基类) | `Engine/EngineStrategyContext.cs` (Facade) |
| 投资组合 | `Common/Securities/SecurityPortfolioManager.cs` | `Engine/PortfolioManager.cs` |
| 持仓 | `Common/Securities/SecurityHolding.cs` | `Core/Engine/Models/Position.cs` |
| 订单 | `Common/Orders/Order.cs` | `Core/Engine/Models/Order.cs` |
| 成交模拟 | `Common/Orders/Fills/FillModel.cs` | `Engine/ExecutionHandler.cs` |
| 风控 | `Algorithm.Framework/Risk/` | `Engine/RiskController.cs` |
| 指标基类 | `Indicators/IndicatorBase.cs` | `Core/Indicators/IIndicator.cs` |
| 数据订阅配置 | `Common/Data/SubscriptionDataConfig.cs` | 无对应 (直接 Channel) |
| 数据同步 | `Engine/DataFeeds/SubscriptionSynchronizer.cs` | 无对应 (单源) |
| 数据Feed | `Engine/DataFeeds/FileSystemDataFeed.cs` | `Data/Engine/HistoricalBarFeed.cs` |
| 品种配置 | `SymbolPropertiesDatabase` (CSV) | `symbols.json` → `FutureRegistry` |
| 交易所定义 | `MarketHoursDatabase` (JSON) | `Core/Models/ExchangeCode.cs` (enum) |
| 回测成交 | `Brokerages/Backtesting/BacktestingBrokerage.cs` | `Engine/ExecutionHandler.cs` (ProcessBar/MatchBar) |
| 实盘成交 | `Brokerages/` (多券商) | `Live/CtpTraderBridge.cs` |
| 结果/统计 | `Engine/Results/` | `Engine/Statistics/` |
| 报告 | `Report/` | `Statistics/PerformanceReport.cs` |

---

> **最后的话：** TradingStudio 不是 Lean 的"简化版"，而是一个针对国内期货市场的精准工具。它做出了一些 Lean 没有的设计选择（多策略子账户、独立风控横切层、Channel 异步管道），也必然在某些方面不及 Lean（成交模型精度、指标丰富度、多市场支持）。看清差距，保持方向，继续打磨。
