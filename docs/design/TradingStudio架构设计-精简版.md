# TradingStudio 架构设计（精简版）

> 从 Lean 学习 → 对照自身需求 → 砍掉多余设计 → 保留核心骨架
>
> **约束条件：** C# / 国内期货 / 中低频 / 个人使用
>
> **当前阶段 (2026-06-13):** Phase 1 数据基建已完成 (Core/Data/Ctp)。
> 本文描述的 5 项目架构中，Execution/Strategy/UI 尚未实现。

---

## 1. Lean → TradingStudio：哪些该砍、哪些该留

### 1.1 砍掉的（Lean 有，我们不需要）

| Lean 模块 | 为什么不需要 |
|-----------|--------------|
| 10+ 品种层次（Equity/Option/Forex/Crypto/...） | 只做期货，一个 Future 品种即可 |
| 多时区/多市场 | 北京时间单一时区，六大交易所时间表固定 |
| 40+ 券商模型 | 只有 CTP，不需要 IBrokerageModel 抽象层 |
| FactorFile / Split / Dividend | 期货没有拆股复权 |
| Universe 动态选股 | 期货品种 70 个以内，手动选择 |
| MEF 可插拔架构 | 个人用，编译时确定 |
| Python 支持 | 纯 C# |
| Job Queue / Cloud API / Messaging | 本地自托管，无云需求 |
| Fundamental 数据 | 期货不需要 |
| Multi-Currency | 人民币单币种 |
| Partial Class 拆 10 文件 | 组件少不需要 |
| 170 个指标 | 选 10-20 个核心的即可 |

### 1.2 保留并简化的

| Lean 设计 | 提取什么 | 简化什么 |
|-----------|----------|----------|
| Handler 策略模式 | 行情/交易 两套接口可替换（实盘 vs 回测） | 5 Handler → 3 Handler |
| Enumerator 数据管道 | Tick → Bar 聚合、前向填充、交易日对齐 | 去掉 AuxEvent/PriceScaleFactor，期货不需要 |
| Security 合约模型 | 合约规格、保证金、手续费、交易时间 | 单一种类，代码平铺不搞继承层次 |
| Order → OrderTicket → OrderEvent | 订单状态机、线程安全 Ticket | 期货不需要组合单 |
| Consolidator | Tick → 1min → 5min → 1day K 线 | 保留核心，去掉 Renko/Range/VolumeRenko |
| Indicators | 链式组合、IsReady、Consolidator 驱动 | 精选 20 个核心指标 |

---

## 2. 精简架构：7 个项目

```
TradingStudio/
├── TradingStudio.Core/           — 核心抽象（接口 + 共享类型 + 技术指标）
├── TradingStudio.Ctp/             — C# 适配层（CTP C++ bridge → Channel<Tick>）
├── TradingStudio.Data/            — 行情接入 + 数据存储 + K 线合成
├── TradingStudio.Engine/          — 策略引擎 + 回测引擎 + 执行 + 风控
├── TradingStudio.Research/        — 量化研究（BarReader + 统计 + 可视化）
├── TradingStudio/                 — 控制台主机（collect/import/backtest/live）
└── TradingStudio.UI/              — WPF 桌面（K线图表 + 监控面板）

外部依赖:
├── CTP/Wrapper/                   — C++/CLI 封装 (CTPWrapper.dll)
└── CTP/SDK/                       — CTP 6.7.13 原生库
```

> 7 个 .NET 项目 + 1 个 C++/CLI bridge vs Lean 的 25+ 个项目。够用就好。
>
> 原设计中的 `Execution` 和 `Strategy` 合并入 `Engine`，因为期货单一品种策略不需要 Execution/Strategy 两层分离。
> `Research` 是独立 Library，不依赖 WPF，用 ScottPlot 做 headless 出图。

---

## 3. Core — 核心类型（最瘦的一层）

```
TradingStudio.Core/
├── Types.cs                    — 基础值类型（Price, Quantity, DateTime 别名等）
├── Symbol.cs                   — 合约标识：ctp://SHFE.rb2501
├── ContractSpec.cs             — 合约规格（品种、乘数、最小变动、保证金、手续费）
├── MarketTime.cs               — 交易时段（日盘/夜盘时间窗）
│
├── Data/
│   ├── Bar.cs                  — K 线（OHLCV + OI）
│   ├── Tick.cs                 — Tick（成交价 + 量 + 持仓 + 买卖盘）
│   └── BarSeries.cs            — K 线序列（按周期：1min/5min/1day）
│
├── Orders/
│   ├── Order.cs                — 订单（方向/开平/价格/数量/类型）
│   ├── OrderTicket.cs          — 订单状态跟踪（线程安全）
│   ├── OrderEvent.cs           — 订单生命周期事件
│   ├── Trade.cs                — 成交记录
│   └── Position.cs             — 持仓
│
└── Interfaces/
    ├── IDataProvider.cs         — 行情数据源接口（回测/实盘实现它）
    ├── ITradeGateway.cs         — 交易网关接口（回测/实盘实现它）
    └── IRiskRule.cs             — 风控规则接口（横切层）
```

### 3.1 Symbol 设计（从 Lean 简化）

```csharp
// Lean: Symbol.Create("SPY", SecurityType.Equity, Market.USA)
// TradingStudio: 更简单，期货只需要知道交易所+合约代码+品种

public record Symbol(
    string Exchange,    // SHFE / DCE / CZCE / CFFEX / GFEX / INE
    string Product,     // rb / m / TA / IF
    string Contract,    // 2501 / 2505
    ProductType Type    // 商品期货 / 金融期货 / 商品期权
);
```

### 3.2 ContractSpec 合约规格（数据驱动）

```csharp
public class ContractSpec
{
    public string Product { get; init; }         // rb
    public string Name { get; init; }            // 螺纹钢
    public string Exchange { get; init; }        // SHFE

    // 交易规则
    public int LotSize { get; init; }            // 10 吨/手
    public decimal TickSize { get; init; }       // 1 元
    public decimal MarginRate { get; init; }     // 7%

    // 手续费 — 直接从合约规格表取
    public FeeInfo OpenFee { get; init; }        // 开仓手续费
    public FeeInfo CloseFee { get; init; }       // 平仓手续费
    public FeeInfo CloseTodayFee { get; init; }  // 平今手续费

    // 交易时间
    public List<TradingSession> Sessions { get; init; }  // 日盘+夜盘时段
}

public record FeeInfo(string Type, decimal Value);  // Type: "按金额" / "按手数"
public record TradingSession(TimeSpan Start, TimeSpan End, string Label);
```

> 关键：这些数据从知识库 `六大交易所合约规格表.md` 驱动，不硬编码。

---

## 4. Data — 行情管道（精简版）

```
TradingStudio.Data/
├── CTP/
│   └── CTPMdProvider.cs        — CTP 行情接入（实现 IDataProvider）
├── Storage/
│   ├── TickWriter.cs            — Tick 文件写入（按日期/品种组织）
│   └── TickReader.cs            — Tick 文件读取（回测用）
├── Pipeline/
│   ├── BarAggregator.cs         — Tick → Bar 聚合（1min/5min/15min/1day）
│   ├── FillForward.cs           — 前向填充（停盘时段无数据时用前一根Bar填充）
│   ├── TimeFilter.cs            — 交易时段过滤（只保留日盘/夜盘有效时段）
│   └── BarSeriesStore.cs        — Bar 序列缓存与查询
└── Playback/
    └── HistoryDataProvider.cs   — 历史数据回放（实现 IDataProvider）
```

### 4.1 数据管道（从 Lean 的五层简化为三层）

```
Lean 的五层管道:              TradingStudio 的三层管道:
                       
FillForward                ──→  TimeFilter（交易时段过滤）
Synchronizing              ──→
Filter                     ──→  BarAggregator（Tick → Bar 聚合）
AuxiliaryData       ✂砍    ──→  
PriceScaleFactor     ✂砍    ──→  FillForward（停盘前向填充）
```

**管道入口是 IDataProvider，实盘和回测各自实现：**

```csharp
// 实盘：CTP 行情回调驱动
public class CTPMdProvider : IDataProvider
{
    // CTP OnRtnDepthMarketData → 转成 Tick → 推到管道
}

// 回测：文件读取驱动
public class HistoryDataProvider : IDataProvider
{
    // 读取 Tick 文件 → 按时间顺序推到管道
}
```

### 4.2 Enumerator 管道实现（C# 惯用法）

```csharp
// 管道就是一个 IEnumerable<T> 链：
IEnumerable<Tick> source = dataProvider.GetTicks(symbol, startDate, endDate);

// 1. 时段过滤
var filtered = source.Where(t => marketTime.IsTradingTime(t.DateTime));

// 2. 聚合（通过 Consolidator 获得 Bar 流）
var bars = new TimeBarAggregator(filtered, TimeSpan.FromMinutes(5));

// 3. 前向填充
var filled = bars.FillForward();
```

> 比 Lean 的 5 个独立 Enumerator 类简单，但效果一样。C# LINQ 和 yield return 让管道更直观。

### 4.3 存储策略（精简）

| 数据 | 存储 | 说明 |
|------|------|------|
| Tick 原始数据 | CSV：`{basePath}/{exchange}/{contract}_{tradingDay}.csv` | 金数源 42 列格式 (Phase 1)；二进制 .tick 为 Phase 2 可选 |
| Bar 缓存 | SQLite (Phase 1) → PostgreSQL (Phase 2) | 常用周期 K 线入库查询快 |
| 合约规格 | JSON (`symbols.json`) | 数据驱动，可更新 |

> Phase 1 用 SQLite + CSV；Phase 2 切 PostgreSQL。不引入 ClickHouse。

---

## 5. Execution — 交易执行 + 风控横切（合并为一个项目）

```
TradingStudio.Execution/
├── CTP/
│   └── CTPTradeGateway.cs      — CTP 交易接入（实现 ITradeGateway）
├── Risk/                         — 风控规则引擎（横切层）
│   ├── RiskEngine.cs            — 规则执行器（订单到达CTP前必须通过）
│   ├── MaxPositionSizeRule.cs   — 单品种最大持仓
│   ├── MaxDrawdownRule.cs       — 当日最大回撤
│   ├── MaxDailyLossRule.cs      — 当日最大亏损
│   ├── OrderFrequencyRule.cs    — 下单频率限制
│   └── PriceLimitRule.cs        — 涨跌停板检查
├── Simulator/                    — 回测用模拟撮合
│   └── SimulatedTradeGateway.cs — 模拟成交（实现 ITradeGateway）
└── OrderManager.cs              — 订单生命周期管理
```

### 5.1 风控是横切层

```
策略 → [风控引擎（横切层）] → CTP 网关
         │
         ├── 规则1: 单品种持仓上限
         ├── 规则2: 当日最大回撤
         ├── 规则3: 当日最大亏损
         └── 规则4: 涨跌停板检查
           （任意一条不通过 → 拒绝订单 → 通知策略）
```

```csharp
public class RiskEngine
{
    private readonly List<IRiskRule> _rules;

    public bool CanSubmit(Order order, Position current)
    {
        foreach (var rule in _rules)
            if (!rule.Check(order, current, out var reason))
                return false;  // 记录拒绝原因
        return true;
    }
}
```

### 5.2 订单状态机（从 OrderEvent 模型简化）

```
         Submit
           │
           ▼
    ┌── Pending ──→ Rejected（风控驳回）
    │      │
    │      ▼
    │   Accepted ──→ 已发往 CTP
    │      │
    │      ├──→ PartiallyFilled
    │      │       │
    │      │       └──→ Filled（全部成交）
    │      │
    │      └──→ Canceled（已撤单）
    │
    └── Error（系统异常）
```

> 比 Lean 的状态机简单很多，但覆盖了中低频策略需要的所有状态。

---

## 6. Strategy — 策略引擎（和回测共用基础设施）

```
TradingStudio.Strategy/
├── StrategyBase.cs              — 策略基类（只有几个核心钩子）
├── StrategyContext.cs           — 策略运行上下文（行情/持仓/风控入口）
├── Backtest/
│   ├── BacktestEngine.cs        — 回测引擎
│   ├── PerformanceStats.cs      — 绩效统计（夏普/最大回撤/胜率）
│   └── ReportGenerator.cs       — 文本/Markdown 报告
└── Indicators/                   — 核心指标（精选20个）
    ├── IndicatorBase.cs
    ├── SMA.cs / EMA.cs
    ├── MACD.cs
    ├── RSI.cs
    ├── BollingerBands.cs
    ├── ATR.cs
    ├── ADX.cs
    ├── KeltnerChannel.cs
    ├── DonchianChannel.cs
    └── ...（按需增加）
```

### 6.1 策略基类（极简版）

```csharp
public abstract class StrategyBase
{
    // ===== 生命周期 =====
    public virtual void OnStart(StrategyContext ctx) { }
    public virtual void OnStop() { }

    // ===== 行情驱动 =====
    public virtual void OnBar(Bar bar) { }
    public virtual void OnTick(Tick tick) { }  // 低频用不到可以不管

    // ===== 订单反馈 =====
    public virtual void OnOrderFilled(Trade trade) { }
    public virtual void OnOrderRejected(Order order, string reason) { }

    // ===== 工具方法 =====
    protected void SubmitOrder(Order order)
    {
        if (Context.Risk.CanSubmit(order, Context.Position))
            Context.Gateway.Submit(order);
        else
            Log.Warning($"风控拒绝: {order}");
    }

    protected IReadOnlyList<Bar> History(string symbol, int count) { ... }
    protected SMA SMA(int period) { ... }
    protected MACD MACD(int fast, int slow, int signal) { ... }
}
```

> 对比 Lean 的 `QCAlgorithm` 有 80+ 个成员。这个不到 20 个。

### 6.2 回测引擎（核心循环）

```csharp
public class BacktestEngine
{
    public BacktestResult Run(StrategyBase strategy, BacktestConfig config)
    {
        var dataProvider = new HistoryDataProvider(config.Symbols, config.Start, config.End);
        var gateway = new SimulatedTradeGateway();  // 模拟撮合
        var ctx = new StrategyContext(dataProvider, gateway, config);

        strategy.OnStart(ctx);

        foreach (var bar in dataProvider.GetBars(TimeSpan.FromMinutes(5)))
        {
            // 1. 更新行情
            ctx.Update(bar);

            // 2. 检查止损/止盈（如有挂单）
            gateway.ProcessPendingOrders(bar);

            // 3. 驱动策略
            strategy.OnBar(bar);

            // 4. 记录快照
            ctx.TakeSnapshot(bar.Time);
        }

        strategy.OnStop();
        return PerformanceStats.Calculate(ctx.Snapshots);
    }
}
```

> 这个循环就是简化版的 `AlgorithmManager.Run()`。不到 20 行就写清楚了。

---

## 7. 回测 vs 实盘切换

```
实盘模式:                          回测模式:
                                    
CTPMdProvider  : IDataProvider     HistoryDataProvider  : IDataProvider
CTPTradeGateway : ITradeGateway   SimulatedTradeGateway : ITradeGateway
RealRiskEngine  : IRiskRule       风控规则相同（保持一致性）
```

**切换方式：** 配置文件或启动参数决定用哪组实现。不做 MEF/插件，直接 `if (mode == "live") ... else ...` 或者用 DI 容器注册。

---

## 8. 与 Lean 的项目数量对比

| 功能域 | Lean（25+ 项目） | TradingStudio（5 项目） |
|--------|------------------|------------------------|
| 核心抽象 | Common（最大） | Core（最瘦） |
| 数据管道 | Engine.DataFeeds + Compression | Data（一个项目含存储+管道） |
| 交易执行 | Engine.TransactionHandlers + Brokerages | Execution（含风控+模拟） |
| 策略 | Algorithm + Algorithm.Framework | Strategy（含回测+指标） |
| 指标 | Indicators（独立项目） | Strategy/Indicators/（合并在策略项目） |
| 启动 | Launcher + Configuration + Queues + Messaging | Program.cs 几行 |
| UI | 无内置 | UI（WPF 监控面板） |
| 云端 | Api + Messaging + Research | 无 |
| 报告 | Report（独立） | Strategy/Backtest/（合并） |

---

## 9. 起点：最小可用骨架

如果要开始写代码，第一个文件应该是这样：

```
TradingStudio/
├── TradingStudio.sln
├── src/
│   ├── TradingStudio.Core/
│   │   ├── TradingStudio.Core.csproj
│   │   ├── Symbol.cs
│   │   ├── Tick.cs
│   │   ├── Bar.cs
│   │   ├── ContractSpec.cs
│   │   ├── Order.cs
│   │   └── Interfaces/
│   │       ├── IDataProvider.cs
│   │       └── ITradeGateway.cs
│   │
│   └── TradingStudio.Data/
│       ├── TradingStudio.Data.csproj
│       └── CTPMdProvider.cs          ← 第一个要跑通的文件
│
├── docs/
│   ├── CTP接口封装方案.md
│   ├── 六大交易所合约规格表.md
│   ├── Lean引擎架构分析.md
│   └── TradingStudio架构设计-精简版.md  ← 本文件
│
└── data/
    ├── specs/                         ← 合约规格（从知识库同步）
    └── ticks/                         ← Tick 数据文件（{symbol}/{tradingDay}_{symbol}.tick）
```

---

## 10. 总结：不要过度设计

| Lean 教我们的 | TradingStudio 需要的 |
|---------------|---------------------|
| Handler 策略模式分离回测/实盘 | ✅ 保留，核心设计 |
| Enumerator 管道处理数据流 | ✅ 保留，用 LINQ 简化 |
| Security 数据驱动的合约模型 | ✅ 保留，单一种类 |
| OrderTicket 线程安全 | ✅ 保留，期货下单需要 |
| 10+ 品种继承层次 | ❌ 单层 Future 平铺 |
| MEF 可插拔架构 | ❌ 编译时确定 |
| Alpha/Portfolio/Execution/Risk 四模型 | ❌ 一个 StrategyBase + 一个 RiskEngine 足够 |
| 40+ 券商模型 | ❌ 只有一个 CTP |
| 170 个指标 | ❌ 20 个核心指标，按需增加 |

**核心原则：用 Lean 的设计思想，写自己的规模合适的代码。**
