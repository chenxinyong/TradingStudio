# TradingStudio Phase 2 — 回测系统设计草案 v2.1

> 2026-06-13 | 架构设计关键期合并版
>
> v2.0 基础框架 + 概念分层(指标/因子/信号) + 多策略隔离 + 策略配置管理 + 反馈监控 + 部署架构
>
> **原则 1**: 回测需接近实盘 — Tick 级撮合，Bid/Ask 滑点
> **原则 2**: 回测与实盘同一套逻辑 — 统一引擎，只换 IDataFeed
> **原则 3**: 先搞定最简单策略 — 双均线，Tick + Bar 双入口
> **原则 4**: 支持 Tick + Bar — DataEvent 统一事件流，BarAggregator 复用
> **原则 5**: 风险与反馈分离 — RiskController 是闸门，FeedbackMonitor 是仪表盘
> **原则 6**: 策略间完全隔离 — 各自独立的 StrategyContext，共享引擎服务

---

## 目录

1. [核心理念：一套引擎，两种模式](#1-核心理念一套引擎两种模式)
2. [统一事件流](#2-统一事件流)
3. [总体架构：七层模型](#3-总体架构七层模型)
4. [核心抽象](#4-核心抽象)
5. [指标引擎：数据变换层](#5-指标引擎数据变换层)
6. [交易信号—执行—反馈链路](#6-交易信号执行反馈链路)
7. [反馈监控](#7-反馈监控)
8. [引擎主循环](#8-引擎主循环)
9. [数据源实现](#9-数据源实现)
10. [撮合引擎](#10-撮合引擎)
11. [仓位与资金管理（多策略分账）](#11-仓位与资金管理多策略分账)
12. [策略配置管理](#12-策略配置管理)
13. [绩效报告](#13-绩效报告)
14. [策略示例](#14-策略示例)
15. [部署架构：引擎与 UI 分离](#15-部署架构引擎与-ui-分离)
16. [与现有代码的集成](#16-与现有代码的集成)
17. [实施路线](#17-实施路线)

---

## 1. 核心理念：一套引擎，两种模式

### 1.1 回测 vs 实盘 —— 区别只在数据源

```
                              ┌─────────────────────┐
                              │    TradingEngine     │  ← 同一份代码
                              │  (主循环 + 撮合 + 仓位) │
                              └──────┬──────────────┘
                                     │
                              IDataFeed 接口
                              ┌────────┴────────┐
                              │                 │
                        回测模式             实盘模式
                    ┌───────┴───────┐   ┌─────┴──────┐
                    │ HistoricalFeed│   │ CtpLiveFeed │
                    │ (SQLite/CSV)  │   │ (CtpMdAdapter│
                    └───────────────┘   └────────────┘
                          │                   │
                    ┌─────┴─────┐      ┌─────┴──────┐
                    │ BarAggre- │      │ BarAggre-   │
                    │ gator     │      │ gator       │
                    │ (已有!)   │      │ (已有!)     │
                    └───────────┘      └────────────┘
```

### 1.2 两种回放精度

| 模式 | 数据源 | 产生事件 | 撮合精度 | 速度 | 用途 |
|------|--------|---------|---------|------|------|
| **Tick 回放** | CSV → `TickRecord` | `TickEvent` + `BarEvent` | Tick 级（Bid/Ask 滑点） | 慢（一天 ~5 万条） | 策略验证、实盘前终检 |
| **Bar 回放** | SQLite → `Bar` | `BarEvent` | Bar 级（下根 Bar Open 成交） | 快（一年 ~10 万条） | 快速筛选、参数粗调 |
| **实盘** | CTP MdApi | `TickEvent` + `BarEvent` | Tick 级（真实成交） | 实时 | 实盘交易 |

三种模式跑的是**同一个 `TradingEngine`**。

---

## 2. 统一事件流

### 2.1 概念分层：从数据到成交

```
原始行情 ──→ 指标 ──→ 因子 ──→ 信号 ──→ 订单 ──→ 成交
 (Tick/Bar) (Indicator) (Factor)  (Signal)  (Order)  (Fill)
     │            │         │         │        │       │
   数据        数学变换   预测变量  交易决策  执行指令  已发生事实
```

| 概念 | 一句话 | 层归属 |
|------|--------|--------|
| **指标** | "这个数字是多少" — 纯计算，无观点 | **引擎层**（IndicatorManager） |
| **因子** | "这个数字高意味着什么" — 有预测方向 | **引擎层**（FactorManager, Phase 2c） |
| **信号** | "现在该干什么" — 可执行的动作指令 | **策略层** |
| **订单** | 执行意图 | StrategyContext → ExecutionHandler |
| **成交** | 已发生的事实 | ExecutionHandler → PortfolioManager |

### 2.2 DataEvent 类型

```csharp
// TradingStudio.Core/Engine/DataEvent.cs

/// <summary>统一数据事件 —— Tick 和 Bar 的共同载体</summary>
public abstract record DataEvent
{
    public DateTimeOffset Time { get; init; }
}

public sealed record TickEvent : DataEvent
{
    public required TickRecord Tick { get; init; }
    public required string InstrumentId { get; init; }
    public required DateOnly TradingDay { get; init; }
}

public sealed record BarEvent : DataEvent
{
    public required Bar Bar { get; init; }
    public bool IsNewBar { get; init; }
}
```

### 2.3 IDataFeed

```csharp
public interface IDataFeed
{
    IReadOnlyList<string> Instruments { get; }
    DateTime StartTime { get; }
    DateTime EndTime { get; }
    void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments);
    IAsyncEnumerable<DataEvent> StreamAsync(CancellationToken ct);
}
```

---

## 3. 总体架构：七层模型

### 3.1 七层结构

```
┌─────────────────────────────────────────────────────────────┐
│  7. API 层     EngineMonitorApi (localhost:5199)    [Phase 3]│
│                GET 快照查询 + POST 控制 + SignalR 实时推送    │
├─────────────────────────────────────────────────────────────┤
│  6. 策略层     StrategyContainer                   [Phase 2a]│
│                多策略隔离、事件路由、独立 StrategyContext      │
├─────────────────────────────────────────────────────────────┤
│  5. 监控层     FeedbackMonitor                     [Phase 2b]│
│                执行质量 / 策略健康 / 风险状态 / 系统健康       │
│                Phase 2a: 空桩（数据结构定义，统计逻辑 stub）   │
├─────────────────────────────────────────────────────────────┤
│  4. 风控层     RiskController                      [Phase 2a]│
│                Pre-Order 阻断 + Post-Fill 警告 + Periodic    │
├─────────────────────────────────────────────────────────────┤
│  3. 执行层     ExecutionHandler + PortfolioManager [Phase 2a]│
│                多策略订单队列 + 总账/分账                     │
├─────────────────────────────────────────────────────────────┤
│  2. 变换层     BarAggregator + IndicatorManager     [Phase 2a]│
│                Tick→Bar + Bar→衍生值 + 衍生值→因子   [2c]    │
├─────────────────────────────────────────────────────────────┤
│  1. 数据层     IDataFeed (HistoricalTick/Bar/Live)  [Phase 2a]│
│                统一事件流: TickEvent + BarEvent              │
└─────────────────────────────────────────────────────────────┘
```

### 3.2 项目结构

```
src/
├── CTP/                                    — (已有) C++/CLI 封装
│   ├── SDK/                                CTP 6.7.13 原生库 (include/lib/dll)
│   └── Wrapper/                            MdApi + TraderApi (C++/CLI)
│
├── TradingStudio.Core/                     — 核心抽象
│   ├── TradingStudio.Core.csproj
│   ├── Models/                             — (已有) 数据模型
│   │   ├── Bar.cs                          namespace: TradingStudio.Core.Models
│   │   ├── ContractCodeGenerator.cs
│   │   ├── Exchange.cs
│   │   ├── Future.cs
│   │   ├── FutureRegistry.cs
│   │   └── Tick.cs
│   ├── Engine/                             — (NEW) 引擎层抽象
│   │   ├── DataEvent.cs                    namespace: TradingStudio.Core.Engine
│   │   ├── IDataFeed.cs
│   │   ├── IExecutionHandler.cs
│   │   └── Models/
│   │       ├── MonitorAlert.cs
│   │       ├── Order.cs
│   │       ├── OrderEvent.cs
│   │       ├── OrderTicket.cs
│   │       ├── Position.cs
│   │       └── Trade.cs
│   ├── Indicators/                         — (NEW) 指标接口
│   │   └── IIndicator.cs                  namespace: TradingStudio.Core.Indicators
│   ├── Strategy/                           — (NEW) 策略层
│   │   ├── IStrategy.cs                   namespace: TradingStudio.Core.Strategy
│   │   ├── StrategyConfig.cs              (含 StrategyParameters + RiskRuleConfig)
│   │   ├── StrategyContext.cs
│   │   └── StrategyParameterAttribute.cs
│   └── Risk/                               — (NEW) 风控层
│       ├── IPortfolioState.cs             namespace: TradingStudio.Core.Risk
│       ├── IRiskRule.cs
│       └── RiskCheckResult.cs
│
├── TradingStudio.Ctp/                      — (已有) C# 适配层
│   ├── TradingStudio.Ctp.csproj
│   └── CtpMdAdapter.cs                    namespace: TradingStudio.Ctp
│
├── TradingStudio.Data/                     — 数据聚合 + 存储 + 导入
│   ├── TradingStudio.Data.csproj
│   ├── Aggregation/                        — (已有) K线合成
│   │   ├── BarAggregator.cs               namespace: TradingStudio.Data.Aggregation
│   │   └── DailyBarAggregator.cs
│   ├── Storage/                            — (已有) 持久化
│   │   ├── BarStore.cs                    namespace: TradingStudio.Data.Storage
│   │   └── TickCsvWriter.cs
│   ├── Import/                             — (已有) 数据导入
│   │   ├── CsvTickImporter.cs             namespace: TradingStudio.Data.Import
│   │   ├── JinshuyuanEntryFilter.cs
│   │   ├── JinshuyuanImportService.cs
│   │   ├── JinshuyuanOptions.cs
│   │   └── TickImportService.cs
│   └── Engine/                             — (NEW) 数据源实现
│       ├── HistoricalTickFeed.cs          (Stub — Phase 2b 实现)
│       ├── HistoricalBarFeed.cs           (Stub — Phase 2a 实现)
│       └── CtpLiveFeed.cs                (Stub — Phase 3 实现)
│
├── TradingStudio.Engine/                   — (NEW 项目) 主引擎
│   ├── TradingStudio.Engine.csproj         → net10.0, refs Core + Data
│   ├── TradingEngine.cs                   (Stub — Phase 2a 实现)
│   ├── EngineOptions.cs
│   ├── EngineReport.cs                    (Stub — Phase 2a 实现)
│   ├── StrategyContainer.cs               (Stub — Phase 2a 实现)
│   ├── StrategyFactory.cs                 namespace: TradingStudio.Engine
│   ├── ExecutionHandler.cs                (Stub — Phase 2a 实现)
│   ├── PortfolioManager.cs                (Stub — Phase 2a 实现, 含 SubPortfolio)
│   ├── IndicatorManager.cs                namespace: TradingStudio.Engine
│   ├── FeedbackMonitor.cs                 (Stub — Phase 2b 实现, 含 MonitorSummary)
│   ├── Statistics/
│   │   ├── PerformanceReport.cs           namespace: TradingStudio.Engine.Statistics
│   │   ├── TradeStatistics.cs             (Stub)
│   │   └── DrawdownCalculator.cs          (Stub)
│   └── Examples/
│       └── MaCrossStrategy.cs             (Stub — Phase 2a 实现)
│
├── TradingStudio.UI/                        — (NEW) WPF 监控客户端
│   ├── TradingStudio.UI.csproj              → net10.0-windows, UseWPF, SignalR.Client
│   ├── App.xaml / App.xaml.cs               WPF 应用入口
│   ├── MainWindow.xaml / MainWindow.xaml.cs  Dashboard 主窗口 (Phase 3 实现)
│   └── (面板: 总览 / 策略列表 / 订单监控 / 告警中心 / K线图表)
│
├── TradingStudio.Research/                  — (NEW) C# 研究环境
│   ├── TradingStudio.Research.csproj        → net10.0, refs Engine + ScottPlot 5
│   ├── ResearchContext.cs                   一行加载: new ResearchContext("bars.db")
│   ├── BarReader.cs                         SQLite 直读 (SELECT + DateTime 范围)
│   ├── BarSeries.cs                         Bar 集合 + SMA/Returns/LogReturns
│   ├── Stats/
│   │   ├── ReturnsAnalyzer.cs               Mean/StdDev/Sharpe/VaR/CVaR
│   │   └── DrawdownAnalyzer.cs              MaxDD/Duration/Curve
│   ├── Viz/
│   │   └── ChartHelper.cs                   OHLC/Equity/Histogram/SMA (ScottPlot)
│   └── Notebooks/
│       └── ma-cross-research.dib            .NET Interactive 示例
│
├── TradingStudio/                          — 主程序 (单 exe, 双模式)
│   ├── TradingStudio.csproj               → net10.0, Web SDK, WindowsService
│   ├── Program.cs                         入口: live | backtest | collect | import
│   ├── EngineMonitorApi.cs                REST 端点 (快照查询 + 控制命令)
│   ├── Hubs/
│   │   └── EngineHub.cs                   SignalR Hub (双向 + 按策略分组推送)
│   ├── appsettings.json                   Serilog + CTP 连接 + 路径
│   ├── symbols.json                       品种数据 (75 品种)
│   ├── Options/
│   │   └── CollectOptions.cs             namespace: TradingStudio.Options
│   ├── Services/
│   │   ├── CollectService.cs             namespace: TradingStudio.Services
│   │   ├── HealthMonitor.cs
│   │   └── SessionScheduler.cs
│   └── Commands/
│       └── BacktestCommand.cs             (Stub — Phase 2a 实现)
│
└── Scripts/                                — Python 数据脚本
    └── gen_symbols_json.py (+14 others)

test/
├── CtpDemo/                                — (已有) 行情 + 交易 Demo
├── CtpBarDemo/                             — (已有) 管线 Demo (Quote→Bar→SQLite)
└── TradingStudio.Engine.Tests/             — (NEW) 引擎测试
    └── TradingStudio.Engine.Tests.csproj   → net10.0, xUnit, refs Engine
```

### 3.3 依赖关系

```
TradingStudio.Engine (主引擎)
  ├── TradingStudio.Data   (HistoricalTickFeed, HistoricalBarFeed, BarAggregator)
  │     └── TradingStudio.Core (模型 + 引擎接口 + 策略接口 + 指标接口)
  └── TradingStudio.Core

TradingStudio (主程序 — 单 exe 双模式)
  ├── TradingStudio.Engine (回测 + 实盘引擎)
  └── ASP.NET Core (Web SDK) → REST API + SignalR Hub

TradingStudio.Research (C# 研究环境)
  ├── TradingStudio.Engine (BarSeries 分析 + 指标计算)
  └── ScottPlot (图表) + Microsoft.Data.Sqlite (直读 Bar)

TradingStudio.UI (WPF 监控客户端 — Phase 3)
  └── SignalR.Client → TradingStudio (localhost:5199/hubs/engine)
```

### 3.4 引擎内部结构（组件关系总图）

```
TradingEngine
  │
  ├── IDataFeed              ← 外部注入
  ├── BarAggregator          ← 已有，Tick→1min Bar
  ├── DailyBarAggregator     ← 已有，Tick→Day Bar
  │
  ├── IndicatorManager       ← NEW，指标注册/feed/预热/查询
  ├── FactorManager          ← Phase 2c，因子归一化/方向/衰减
  │
  ├── StrategyContainer      ← NEW，多策略订阅路由
  │     └── StrategySlot[] (IStrategy + StrategyConfig + SubPortfolio)
  │
  ├── IExecutionHandler      ← 多策略订单队列 + 优先级
  ├── PortfolioManager       ← 总账 + 各策略分账
  ├── RiskController         ← 全局 + 策略级规则
  │
  ├── FeedbackMonitor        ← NEW，四维观测
  │
  └── EngineMonitorApi       ← Phase 3，localhost HTTP + SignalR
```

---

## 4. 核心抽象

### 4.1 IStrategy — 策略接口

```csharp
// TradingStudio.Core/Strategy/IStrategy.cs

public interface IStrategy
{
    string Name { get; }

    /// <summary>初始化。注册指标、设置参数。</summary>
    void Initialize(StrategyContext context);

    /// <summary>每个 Tick 调用一次。</summary>
    void OnTick(TickRecord tick, string instrumentId);

    /// <summary>每根 Bar 闭合时调用。此 Bar 已 feed 给所有注册指标。</summary>
    void OnBar(Bar bar);

    /// <summary>订单状态变更回调。</summary>
    void OnOrderEvent(OrderEvent orderEvent);

    /// <summary>反馈监控告警回调（可选实现）。</summary>
    void OnAlert(MonitorAlert alert) { }

    /// <summary>引擎结束回调。</summary>
    void OnEndOfAlgorithm();
}
```

### 4.2 StrategyContext — 策略工具箱

策略与引擎之间的**唯一触点**。策略不知道引擎内部结构。

```csharp
// TradingStudio.Core/Strategy/StrategyContext.cs

public class StrategyContext
{
    // ═══ 标识 ═══
    public string StrategyId { get; }

    // ═══ 行情数据 ═══
    public IReadOnlyList<Bar> GetBarHistory(string instrumentId);
    public IReadOnlyList<Bar> GetRecentBars(string instrumentId, int count);
    public IReadOnlyList<TickRecord> GetTickHistory(string instrumentId, int maxCount = 1000);
    public DateTimeOffset CurrentTime { get; }

    // ═══ 指标查询（Phase 2a 定义接口，2b 实现） ═══
    public T RegisterIndicator<T>(string instrumentId, T indicator, string tag = "")
        where T : IIndicator;
    public double GetIndicatorValue(string instrumentId, string indicatorName, string tag = "");
    public T? GetIndicator<T>(string instrumentId, string tag = "") where T : class, IIndicator;

    // ═══ 交易 ═══
    public OrderTicket MarketBuy(string instrumentId, int quantity, string? tag = null);
    public OrderTicket MarketSell(string instrumentId, int quantity, string? tag = null);
    public OrderTicket ClosePosition(string instrumentId);
    public OrderTicket LimitBuy(string instrumentId, int quantity, decimal limitPrice);
    public OrderTicket LimitSell(string instrumentId, int quantity, decimal limitPrice);
    public OrderTicket StopBuy(string instrumentId, int quantity, decimal stopPrice);
    public OrderTicket StopSell(string instrumentId, int quantity, decimal stopPrice);
    public bool CancelOrder(long orderId);

    // ═══ 仓位与资金（只看到自己的分账） ═══
    public Position? GetPosition(string instrumentId);
    public IReadOnlyList<Position> Positions { get; }
    public decimal Equity { get; }
    public decimal AvailableCash { get; }
    public decimal AllocatedCapital { get; }      // 策略分配资金（NEW）

    // ═══ 品种信息 ═══
    public Future GetFuture(string instrumentId);
    public IReadOnlyList<string> SubscribedInstruments { get; }

    // ═══ 风控收紧（运行时，只能收紧不能放宽） ═══
    public bool TightenRisk(string ruleName, object newValue);  // NEW

    // ═══ 日志 ═══
    public void Log(string message);
    public void LogWarning(string message);
    public void LogError(string message);
}
```

### 4.3 策略配置模型

#### StrategyParameterAttribute — 策略自描述参数

```csharp
// TradingStudio.Core/Strategy/StrategyParameterAttribute.cs

[AttributeUsage(AttributeTargets.Property)]
public class StrategyParameterAttribute : Attribute
{
    public string Description { get; init; }
    public object? DefaultValue { get; init; }
    public double Min { get; init; } = double.MinValue;
    public double Max { get; init; } = double.MaxValue;
    public bool Required { get; init; }
    public string Category { get; init; } = "General";  // Entry / Exit / Risk / Position
}
```

#### StrategyConfig — 完整策略配置

```csharp
// TradingStudio.Core/Strategy/StrategyConfig.cs

public class StrategyConfig
{
    // ═══ 标识 ═══
    public string StrategyId { get; init; }
    public string StrategyType { get; init; }
    public string? Description { get; init; }
    public int Version { get; init; } = 1;

    // ═══ 订阅 ═══
    public IReadOnlyList<string> Instruments { get; init; }
    public BarType PrimaryBarType { get; init; } = BarType.Minute1;

    // ═══ 资金分配 ═══
    public decimal AllocatedCapital { get; init; }
    public decimal MaxDrawdownPct { get; init; } = 0.20m;
    public int MaxPositionPerInstrument { get; init; } = 5;

    // ═══ 执行优先级 ═══
    public int Priority { get; init; } = 0;

    // ═══ 策略参数（类型安全字典） ═══
    public StrategyParameters Parameters { get; init; }

    // ═══ 风控规则 ═══
    public IReadOnlyList<RiskRuleConfig> RiskRules { get; init; } = [];

    // ═══ 调度 ═══
    public TradingSessionFilter SessionFilter { get; init; } = TradingSessionFilter.All;
    public bool SkipAuction { get; init; } = true;
}

public class StrategyParameters : IEnumerable<KeyValuePair<string, object>>
{
    private readonly Dictionary<string, object> _values;
    public T Get<T>(string name) => (T)_values[name];
    public bool TryGet<T>(string name, out T value);
    public bool Contains(string name) => _values.ContainsKey(name);
    public ValidationResult Validate();
}
```

### 4.4 StrategyContainer — 多策略容器

```csharp
// TradingStudio.Engine/StrategyContainer.cs

/// <summary>
/// 多策略容器。管理策略注册、事件路由、生命周期。
/// 策略之间完全隔离，各自持有独立的 StrategyContext。
/// </summary>
public class StrategyContainer
{
    // instrumentId → 订阅该品种的策略列表。
    // 使用 SortedDictionary 保证迭代顺序确定（回测可重复性）。
    // 同一品种内策略按注册顺序（List）排列，即 Priority 相同时先进先出。
    private readonly SortedDictionary<string, List<StrategySlot>> _subscriptions = new();

    public IReadOnlyList<StrategySlot> AllSlots { get; }

    public void Register(IStrategy strategy, StrategyConfig config, StrategyContext context);
    public void Pause(string strategyId);
    public bool Resume(string strategyId);
    public bool IsActive(string strategyId);

    // 事件路由 — 只推送给订阅了该品种的策略
    public void DispatchTick(TickEvent tickEvt);
    public void DispatchBar(BarEvent barEvt);
    public void DispatchAlert(IReadOnlyList<MonitorAlert> alerts);

    // 快照（供 API/报告用）
    public IReadOnlyList<StrategySnapshot> GetAllSnapshots();
    public StrategySnapshot? GetSnapshot(string strategyId);
    public bool TightenRisk(string strategyId, string ruleName, object newValue);
}

public class StrategySlot
{
    public IStrategy Strategy { get; }
    public StrategyConfig Config { get; }
    public StrategyContext Context { get; }
    public bool IsActive { get; set; }
}
```

### 4.5 IExecutionHandler（多策略扩展）

与 v2.0 相同，增加策略级订单队列：

```csharp
public interface IExecutionHandler
{
    OrderTicket Submit(Order order, string strategyId);   // NEW: 记录策略归属
    bool Cancel(long orderId);
    IReadOnlyList<OrderEvent> ProcessTick(TickRecord tick, string instrumentId, Future future);
    IReadOnlyList<OrderEvent> ProcessBar(Bar bar, Future future);
    IReadOnlyList<Order> ActiveOrders { get; }
    IReadOnlyList<Order> GetActiveOrders(string strategyId);  // NEW: 按策略查询
    IReadOnlyList<OrderEvent> OrderHistory { get; }
}
```

### 4.6 订单与持仓模型

与 v2.0 相同：`Order`, `OrderTicket`, `OrderEvent`, `Position`, `Trade`。

---

## 5. 指标引擎：数据变换层

### 5.1 核心类比

```
BarAggregator : Tick → Bar      （数据升维）
Indicator     : Bar  → 衍生值    （数据变换）
Factor        : 衍生值 → 预测变量 （Phase 2c）

三者都在引擎层，都在策略 OnBar 之前执行。
```

### 5.2 IIndicator 接口

```csharp
// TradingStudio.Core/Indicators/IIndicator.cs

/// <summary>
/// 指标接口 — 纯数学变换，无观点。
/// 类似 Lean 的 IndicatorBase，有 IsReady 约定。
/// </summary>
public interface IIndicator
{
    string Name { get; }              // "SMA"、"RSI"
    string Tag { get; }               // "period=20"，用于 registry key
    bool IsReady { get; }             // 是否有足够历史数据
    double CurrentValue { get; }      // 最新值
    int WarmupPeriod { get; }         // 预热期还差多少根 Bar
    IReadOnlyList<double> Values { get; }  // 历史值序列（默认上限 100K，环形覆盖）

    void Update(Bar bar);             // 喂入新 Bar
    void Reset();
}
```

> **Values 上限**: 默认 `MaxValues = 100_000`，超出后环形覆盖最旧值。策略不应假设 `Values[0]` 是回测起始值。
```

### 5.3 IndicatorManager

```csharp
// TradingStudio.Engine/IndicatorManager.cs

/// <summary>
/// 指标管理器 — 引擎层的数据变换服务。
/// 策略注册指标需求，引擎 feed 数据，策略只读访问结果。
/// 同一品种同一指标只算一次，所有订阅策略共享。
/// </summary>
public class IndicatorManager
{
    // 按品种 + 名称索引，保证同品种同指标只算一次
    // key = "rb2605:SMA:fast" → 指标实例（全局唯一）
    private readonly SortedDictionary<string, IIndicator> _indicators = new();

    // 按品种快速查找（Feed 时避免遍历全部）
    // instrumentId → 该品种的所有指标
    private readonly Dictionary<string, List<IIndicator>> _byInstrument = new();

    /// <summary>策略初始化时注册</summary>
    public T Register<T>(string instrumentId, T indicator, string strategyId, string tag = "")
        where T : IIndicator
    {
        var key = $"{instrumentId}:{indicator.Name}:{tag}";
        if (_indicators.ContainsKey(key))
            return (T)_indicators[key];  // 已存在 → 复用

        _indicators[key] = indicator;
        if (!_byInstrument.ContainsKey(instrumentId))
            _byInstrument[instrumentId] = new();
        _byInstrument[instrumentId].Add(indicator);
        return indicator;
    }

    /// <summary>引擎主循环中每根 Bar 闭合时调用。只更新对应品种的指标。</summary>
    public void Feed(Bar bar)
    {
        if (!_byInstrument.TryGetValue(bar.InstrumentId, out var indicators))
            return;
        foreach (var indicator in indicators)
            indicator.Update(bar);
    }

    /// <summary>批量预热（Bar 回放模式）</summary>
    public void Warmup(IReadOnlyList<Bar> history, string instrumentId);

    /// <summary>查询指标当前值</summary>
    public double GetValue(string instrumentId, string indicatorName, string tag = "");

    /// <summary>获取指标实例</summary>
    public T? Get<T>(string instrumentId, string tag = "") where T : class, IIndicator;

    /// <summary>重置所有指标（回测切换品种时）</summary>
    public void Reset();
}
```

### 5.4 指标注册与查询路径

```
策略 Initialize:
  _ctx.RegisterIndicator("rb2605", new SMA(20), tag: "slow")
       │
       ▼
  StrategyContext → IndicatorManager.Register("rb2605", sma20, strategyId: "ma-cross", "slow")
       │
       ▼
  若 "rb2605:SMA:slow" 已存在 → 复用（多策略共享）
  否则 → 新建 → 预热 → 存入字典

引擎主循环:
  Bar闭合 → IndicatorManager.Feed(bar)
       │
       ├── 遍历所有指标, MatchesInstrument 的 → Update(bar)
       └── 策略 OnBar 被调用时, 指标已是最新值

策略 OnBar:
  var slowMa = _ctx.GetIndicatorValue("rb2605", "SMA", "slow")
```

### 5.5 因子层（Phase 2c 留桩）

```csharp
// TradingStudio.Core/Factors/IFactor.cs (Phase 2c)

/// <summary>
/// 因子接口 — 将指标（或原始数据）转化为具有预测能力的变量。
/// 因子回答："这个值偏高时，未来收益倾向于涨还是跌？"
/// </summary>
public interface IFactor
{
    string Name { get; }
    bool IsReady { get; }
    double Value { get; }
    double ZScore(int lookback);
    FactorDirection Direction { get; }  // PositiveMomentum / MeanReversion / Risk
    double Decay { get; }

    void Update(IIndicator indicator);
}

enum FactorDirection { PositiveMomentum, MeanReversion, Risk }
```

### 5.6 概念对比

| | 指标 | 因子 | 信号 |
|---|------|------|------|
| 回答什么 | "这个数字是多少" | "这个数字高意味着什么" | "现在该干什么" |
| 有无观点 | 无 | 有预测方向 | 有具体动作 |
| 跨品种可比 | 否（原始值不可比） | 是（归一化后可比） | N/A |
| 层归属 | 引擎层 | 引擎层 | 策略层 |

---

## 6. 交易信号—执行—反馈链路

> 回测系统的心脏。v2.1 增加反馈监控集成点。

### 6.1 四阶段生命周期

```
信号(Signal) → 订单(Order) → 成交(Fill) → 交易记录(Trade)
   ↑                                          │
   └────── OnOrderEvent 反馈 ──────────────────┘
                │
   └────── FeedbackMonitor.RecordFill/Trade ───┘  (NEW)
```

| 阶段 | 实体 | 产生者 | 含义 |
|------|------|--------|------|
| **信号** | 策略内部决策 | `Strategy.OnBar/OnTick` | "我认为应该做多" |
| **订单** | `Order` + `OrderTicket` | `StrategyContext.MarketBuy()` | "以市价买入 2 手" |
| **成交** | `OrderEvent(Filled)` | `IExecutionHandler.ProcessTick/Bar` | "2 手已在 3250.50 成交" |
| **交易** | `Position` + `Trade` | `PortfolioManager.ProcessFill` | "持多 2 手, 均价 3250.50" |

### 6.2 Order 状态机

```
                    ┌──────────┐
                    │ Submitted │
                    └────┬─────┘
                         │
               ┌─────────┼──────────┐
               ▼         ▼          ▼
          ┌────────┐ ┌────────┐ ┌──────────┐
          │ Filled │ │Partial │ │ Rejected │
          └────────┘ │Filled  │ └──────────┘
                     └───┬────┘
                         │
                    ┌────▼────┐
                    │ Filled  │
                    └─────────┘

取消路径: Submitted → Cancelled
         PartiallyFilled → Cancelled
```

### 6.3 完整交易流程（增加反馈记录点）

```
09:01:00  Bar 闭合 → OnBar(bar) 触发
            │
09:01:00  策略判断：做多信号
            │
09:01:00  _ctx.MarketBuy("rb2605", 2, tag: "金叉做多")
            │
            ├── ① 生成 OrderId = 1001
            ├── ② 创建 Order(Id=1001, Market, Buy, 2手, StrategyId="ma-cross")
            ├── ③ 保证金检查
            ├── ④ RiskController.CheckPreOrder → Pass
            ├── ⑤ Order 进入 _activeOrders 队列
            ├── ⑥ 返回 OrderTicket(Id=1001, Status=Submitted)
            │
09:01:00  OnOrderEvent(Submitted) ← 策略收到受理回执
            │
09:01:01  下一个 Tick 到达
            │
            ├── ExecutionHandler.ProcessTick(tick, "rb2605", future)
            │      Order #1001: Market Buy → 吃 AskPrice1 + 1跳
            │      成交价 = 3250.50
            │
            ├── OrderEvent(Filled, qty=2, price=3250.50, fee=65.01, slippage=10.00)
            │
            ├── ⑦ FeedbackMonitor.RecordFill(fill)          ← NEW
            │      记录滑点、延迟等执行质量指标
            │
            ├── ⑧ PortfolioManager.ProcessFill(fill)
            │      Position(rb2605, qty=+2, avgPrice=3250.50)
            │      Cash -= 65.01, Margin = 6,501.00
            │
            └── ⑨ Strategy.OnOrderEvent(Filled)
                   ／ Strategy.OnAlert()  // 若有告警
```

### 6.4 多策略订单优先级

```
时间 T: Tick 到达 → 多个策略同时下单同一品种

  策略 A (Priority=1): MarketBuy("rb2605", 2)
  策略 B (Priority=2): MarketBuy("rb2605", 1)
  策略 C (Priority=3): MarketSell("rb2605", 2)

ExecutionHandler:
  1. 收集所有策略的下单请求
  2. 按策略 Priority 排序 → A → B → C
  3. 依次撮合，检查剩余流动性（Tick.Volume 约束）
  4. 流动性不足 → 后面的策略部分成交或废单
```

### 6.5 风控横切层（不变，从 v2.0 继承）

与 v2.0 第 5.7 节完全一致：`RiskController`、`IRiskRule`、Pre-Order/Post-Fill/Periodic 三级检查。

**新增**：策略级风控规则 — `MaxStrategyDrawdownRule`，检查单个策略的回撤是否超限。

```csharp
public class MaxStrategyDrawdownRule : IRiskRule
{
    public string Name => "策略最大回撤";
    private readonly decimal _maxDrawdown;

    public RiskCheckResult CheckPeriodic(IPortfolioState portfolio)
    {
        // 各策略分账独立检查
        foreach (var subPortfolio in portfolio.SubPortfolios)
        {
            var drawdown = 1 - (subPortfolio.Equity / subPortfolio.PeakEquity);
            if (drawdown > _maxDrawdown)
                return RiskCheckResult.Reject(Name,
                    $"策略 {subPortfolio.StrategyId} 回撤 {drawdown:P1} 超限");
        }
        return RiskCheckResult.Pass;
    }
}
```

### 6.6 与 Lean 的对比

| | Lean | TradingStudio V2.1 |
|---|------|-------------------|
| 信号表达 | `OrderTicket` | `OrderTicket` + `Order.Tag` (可追溯信号源) |
| 订单状态推送 | `OnOrderEvent(OrderEvent)` | 相同 |
| 同步/异步 | 回测同步, 实盘异步 | 相同 |
| 保证金检查 | `BuyingPowerModel` per-security | Submit 前统一检查 |
| 成交模型 | `IFillModel` per-security | `ExecutionHandler.ProcessTick/Bar` |
| 反馈链路 | Orders → OrderEvents → Portfolio | + FeedbackMonitor.RecordFill/Trade |
| 风控层 | 分散在各 Model | 独立横切层 (全局 + 策略级) |
| 多策略 | 单算法 | StrategyContainer 多策略隔离 |

---

## 7. 反馈监控

> **核心原则**: RiskController 是闸门（阻断），FeedbackMonitor 是仪表盘（观测）。
> 两者独立但互补。

### 7.1 四维模型

```
                    ┌─────────────────────────────────────┐
                    │        FeedbackMonitor              │
                    │  统一反馈中心（引擎层）                │
                    └──────────────┬──────────────────────┘
                                   │
        ┌──────────────┬───────────┼───────────┬──────────────┐
        ▼              ▼           ▼           ▼              ▼
  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐
  │执行质量   │  │策略健康   │  │风险状态   │  │系统健康   │
  │Execution │  │Strategy  │  │Risk      │  │System    │
  │Quality   │  │Health    │  │State     │  │Health    │
  └────┬─────┘  └────┬─────┘  └────┬─────┘  └────┬─────┘
       │              │              │              │
  滑点/拒单/      胜率/盈亏比/   回撤/敞口/     连接状态/
  部分成交        连续亏损/      日内亏损       行情延迟/
                  信号准确率                     Bar延迟
```

### 7.2 FeedbackMonitor 接口

```csharp
// TradingStudio.Engine/FeedbackMonitor.cs

public class FeedbackMonitor
{
    // ═══ 实时快照 ═══
    public ExecutionQualitySnapshot Execution { get; }
    public StrategyHealthSnapshot GetStrategyHealth(string strategyId);
    public EquityCurveSnapshot Equity { get; }

    // ═══ 事件记录 ═══
    public void RecordFill(OrderEvent fill, string strategyId);
    public void RecordTrade(Trade trade, string strategyId);
    public void SamplePortfolio(IPortfolioState portfolio);

    // ═══ 告警检查 ═══
    public IReadOnlyList<MonitorAlert> CheckAlerts();

    // ═══ SignalR 集成（Phase 3） ═══
    // 告警产生时 → 通过 IHubContext<EngineHub> 推送到对应策略组
    public void SetHubContext(Microsoft.AspNetCore.SignalR.IHubContext<EngineHub> hubContext);

    // ═══ 回测报告 ═══
    public MonitorSummary ToSummary();
}

public record MonitorAlert
{
    public AlertType Type { get; init; }    // HighSlippage / ConsecutiveLosses / AbnormalFreq
    public string StrategyId { get; init; }
    public string Message { get; init; }
    public AlertSeverity Severity { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public enum AlertType { HighSlippage, HighRejectRate, ConsecutiveLosses, AbnormalFrequency,
    DrawdownWarning, ConnectionLost, TickDelay, BarMissing }
```

### 7.3 各维度监控项

**① 执行质量**（RecordFill 触发）

| 监控项 | 来源 | 意义 |
|--------|------|------|
| AvgSlippagePerTrade | OrderEvent | 累积滑点太高 → 策略暂停 |
| RejectRate | OrderEvent | 风控或交易所拒绝频率 |
| PartialFillRate | OrderEvent | 流动性不足信号 |
| CancelRate | OrderEvent | 策略或风控频繁撤单 |

**② 策略健康**（RecordTrade 触发）

| 监控项 | 来源 | 意义 |
|--------|------|------|
| RollingWinRate (最近N笔) | Trade.IsWin | 实时胜率趋势 |
| ConsecutiveLosses | Trade 序列 | 连败次数 → 可能策略失效 |
| RollingProfitFactor | Trade.PnL | 最近N笔总盈利/总亏损 |
| TradeFrequency | 单位时间 Trade 数 | 高频异常 → 可能 Bug |
| SignalAccuracy | Signal → Trade 对照 | 事后信号方向正确率 |

**③ 风险状态**（SamplePortfolio 触发）

互补 RiskController.CheckPeriodic。Focus on 趋势判断而非阻断。

**④ 系统健康**（已有 HealthMonitor）

从 Phase 1 继承，补上行情延迟和 Bar 延迟检测。

### 7.4 告警处理流程

```
引擎主循环中:
  1. FeedbackMonitor.RecordFill/Trade/SamplePortfolio → 更新统计
  2. FeedbackMonitor.CheckAlerts() → 产出告警列表
  3. StrategyContainer.DispatchAlerts(alerts) → 路由到触发的策略
  4. Strategy.OnAlert(alert) → 策略感知（可选）
  5. HubContext.Clients.Group(strategyId).SendAsync("Alert", alert) → UI 告警中心（Phase 3）
```

---

## 8. 引擎主循环

> v2.1 完整版：集成 BarAggregator、IndicatorManager、StrategyContainer、RiskController、FeedbackMonitor。

```csharp
// TradingStudio.Engine/TradingEngine.cs

public class TradingEngine
{
    private readonly IDataFeed _dataFeed;
    private readonly IExecutionHandler _execution;
    private readonly PortfolioManager _portfolio;
    private readonly BarAggregator _barAggregator;
    private readonly DailyBarAggregator _dayAggregator;
    private readonly IndicatorManager _indicators;     // NEW
    private readonly StrategyContainer _strategies;     // NEW (替代单 _strategy)
    private readonly RiskController _risk;
    private readonly FeedbackMonitor _feedback;         // NEW
    private readonly EngineOptions _options;

    public async Task<EngineReport> RunAsync(CancellationToken ct = default)
    {
        // 1. 初始化
        _dataFeed.Initialize(_options.StartTime, _options.EndTime, _options.Instruments);
        InitializeStrategies();

        var equityCurve = new List<(DateTimeOffset, decimal)>();
        var globalTrades = new List<Trade>();

        // 2. 主循环
        await foreach (var evt in _dataFeed.StreamAsync(ct))
        {
            switch (evt)
            {
                case TickEvent tickEvt:
                    // ① 更新持仓市价
                    _portfolio.UpdateMarketPrice(tickEvt.InstrumentId, tickEvt.Tick);

                    // ② Tick 撮合 — 处理上一轮 OnTick 中下的订单
                    //     Tick 模式下市价单用当前 Bid/Ask 撮合，限价/止损检查是否穿越。
                    var tickFills = _execution.ProcessTick(tickEvt.Tick, tickEvt.InstrumentId,
                        _registry.Resolve(tickEvt.InstrumentId)!);
                    foreach (var fill in tickFills)
                    {
                        ApplyFill(fill, globalTrades);
                        _feedback.RecordFill(fill, fill.StrategyId);
                    }

                    // ③ 数据变换层
                    _barAggregator.Feed(tickEvt.Tick, tickEvt.InstrumentId, tickEvt.TradingDay);
                    _dayAggregator.Feed(tickEvt.Tick, tickEvt.InstrumentId, tickEvt.TradingDay);

                    // ④ 策略 Tick 回调
                    _strategies.DispatchTick(tickEvt);

                    // ⑤ 策略下单后撮合 — Tick 模式下允许同 Tick 成交
                    //     理由：Tick 是离散采样，同 Tick 成交 = 这次采样点的即时执行。
                    //     保守做法（可选）：加 _options.TickFillDelay=1，延迟到下一个 Tick 撮合。
                    if (_options.TickFillDelay == 0)
                    {
                        var newFills = _execution.ProcessTick(tickEvt.Tick, tickEvt.InstrumentId,
                            _registry.Resolve(tickEvt.InstrumentId)!);
                        foreach (var fill in newFills)
                        {
                            ApplyFill(fill, globalTrades);
                            _feedback.RecordFill(fill, fill.StrategyId);
                        }
                    }

                    // ⑥ 定期采样 + 风控
                    _feedback.SamplePortfolio(_portfolio);
                    var periodicWarnings = _risk.CheckPeriodic(_portfolio);
                    ProcessRiskWarnings(periodicWarnings);
                    break;

                case BarEvent barEvt:
                    // ① 指标更新（在策略 OnBar 之前） ← NEW
                    _indicators.Feed(barEvt.Bar);

                    // ② Bar 撮合 — 处理上一轮 OnBar 中下的订单
                    //     这批订单在上一根 Bar 的 OnBar 中提交，现在用本 Bar 的价格撮合：
                    //       • Market → 本 Bar Open（= 下单后第一根 Bar 的开盘价）
                    //       • Limit  → 本 Bar 区间是否穿越限价
                    //       • Stop   → 本 Bar 区间是否触发止损价
                    //     不会产生前瞻偏差：策略在 bar_N-1 下单时看不到 bar_N 的价格。
                    var barFills = _execution.ProcessBar(barEvt.Bar,
                        _registry.Resolve(barEvt.Bar.InstrumentId)!);
                    foreach (var fill in barFills)
                    {
                        var trade = _portfolio.ProcessFill(fill, _registry);
                        if (trade != null)
                        {
                            globalTrades.Add(trade);
                            _feedback.RecordTrade(trade, fill.StrategyId);
                        }
                        _strategies.DispatchOrderEvent(fill);
                    }

                    // ③ 策略 Bar 回调（只推送到订阅该品种的策略）
                    //     策略可能在 OnBar 中下 MarketBuy/LimitBuy 等。
                    //     这些订单进入 ExecutionHandler 队列，等 NEXT BarEvent 的 ② 撮合。
                    _strategies.DispatchBar(barEvt);

                    // ⚠️ 注意：此处不再调用 ProcessBar。
                    // 策略刚下的订单必须等到下一个 BarEvent 才撮合。
                    // 如果用当前 Bar 立即撮合市价单 → 前瞻偏差（策略已看到完整 Bar）。

                    // ④ 告警检查 + 分发 ← NEW
                    var alerts = _feedback.CheckAlerts();
                    _strategies.DispatchAlerts(alerts);

                    // ⑤ 权益采样（告警检查后）
                    if (barEvt.IsNewBar)
                        equityCurve.Add((barEvt.Time, _portfolio.TotalEquity));
                    break;
            }
        }

        // 3. 结束
        _strategies.DispatchEndOfAlgorithm();

        // 4. 生成报告
        return EngineReport.Generate(_portfolio, _strategies, _feedback, equityCurve, globalTrades, _options);
    }

    private void ApplyFill(OrderEvent fill, List<Trade> trades)
    {
        var trade = _portfolio.ProcessFill(fill, _registry);
        if (trade != null) trades.Add(trade);
    }

    private void InitializeStrategies()
    {
        foreach (var config in _options.StrategyConfigs)
        {
            var strategy = StrategyFactory.Create(config);
            var context = BuildContext(config);
            strategy.Initialize(context);
            _strategies.Register(strategy, config, context);
        }
    }

    private StrategyContext BuildContext(StrategyConfig config) { /* ... */ }
}
```

### 8.2 EngineOptions

```csharp
// TradingStudio.Engine/EngineOptions.cs

public class EngineOptions
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public IReadOnlyList<string> Instruments { get; init; }
    public IReadOnlyList<StrategyConfig> StrategyConfigs { get; init; }
    public decimal StartingCapital { get; init; }

    /// <summary>Tick 模式下策略下单后延迟几个 Tick 撮合。
    /// 0 = 同 Tick 立即撮合（默认）；1 = 延迟到下一 Tick（更保守）。</summary>
    public int TickFillDelay { get; init; } = 0;
}
```

### 8.3 时序保证

| 时机 | 保证 |
|------|------|
| 策略 OnBar(bar_N) 调用时 | bar_N 已 feed 给 IndicatorManager，指标是最新值 |
| 策略 OnBar 中下的订单 | **不立即撮合**。进入队列，等 bar_N+1 到达时在 ProcessBar 中撮合 |
| 策略 OnTick 调用时 | BarAggregator 和 IndicatorManager 已经更新 |
| 策略 OnTick 中下的订单 | 默认同 Tick 撮合（`TickFillDelay=0`），可配置延迟到下一 Tick |
| 策略 OnOrderEvent 调用时 | FeedbackMonitor 已经记录了该笔成交 |
| 告警分发 | 在策略回调之后、下一次数据事件之前 |
| 权益采样 | 在告警检查之后，本数据事件结束时 |

**Bar 模式下的订单—撮合时序**：

```
bar_N-1 到达:
  ProcessBar(bar_N-1)  ← 撮合 bar_N-2 中下的订单
  DispatchBar(bar_N-1) ← 策略可能在 OnBar 中下订单
                         → 进入队列，等待 bar_N

bar_N 到达:
  ProcessBar(bar_N)    ← 撮合 bar_N-1 中下的订单
                         Market 单 → bar_N.Open 成交 ✓（策略看不到 bar_N）
                         Limit 单  → bar_N 区间穿越限价 ✓
  DispatchBar(bar_N)   ← 策略下新订单 → 进入队列，等待 bar_N+1
```

---

## 9. 数据源实现

### 9.1 HistoricalTickFeed（不变）

K-way merge CSV，产出 TickEvent + 喂 BarAggregator → 产出 BarEvent。

### 9.2 HistoricalBarFeed（不变）

SQLite → BarEvent 流。

### 9.3 CtpLiveFeed（Phase 3）

包装 CtpMdAdapter → 统一 DataEvent 流。

---

## 10. 撮合引擎

### 10.1 ProcessTick — Tick 级撮合（多策略扩展）

```csharp
public IReadOnlyList<OrderEvent> ProcessTick(TickRecord tick, string instrumentId, Future future)
{
    var fills = new List<OrderEvent>();

    // ⚠️ 流动性约束：TickRecord.Volume 是当日累计成交量，不是增量。
    //     正确的增量 = 当前 Tick.Volume - 上一 Tick.Volume。
    //     这里使用 _lastTickVolume 字典跟踪前一 Tick 的累计量。
    int incrementalVolume = GetIncrementalVolume(instrumentId, tick.Volume);
    if (incrementalVolume <= 0) return fills;  // 无增量 = 无法撮合

    int remainingVolume = incrementalVolume;

    // 按策略优先级排序
    var pending = _activeOrders
        .Where(o => o.InstrumentId == instrumentId)
        .OrderBy(o => _strategyPriorities[o.StrategyId]);

    foreach (var order in pending)
    {
        if (remainingVolume <= 0) break;  // 流动性耗尽

        var fill = TryMatch(order, tick, future, ref remainingVolume);
        if (fill != null)
            fills.Add(fill);
    }

    foreach (var fill in fills)
        _activeOrders.RemoveAll(o => o.OrderId == fill.OrderId);

    return fills;
}

// 增量成交量 = 当前 Tick 累计量 - 上一 Tick 累计量（首 Tick 直接用当前量）
private int _lastCumulativeVolume = 0;
private int GetIncrementalVolume(string instrumentId, int currentCumulative)
{
    int delta = currentCumulative - _lastCumulativeVolume;
    if (delta < 0) delta = currentCumulative;  // 跨日重置保护
    _lastCumulativeVolume = currentCumulative;
    return delta;
}
```

### 10.2 ProcessBar — Bar 级撮合

与 v2.0 相同，增加策略 ID 归属。

### 10.3 手续费计算（占位，Phase 2c 精确化）

```csharp
private decimal CalculateFee(string instrumentId, int quantity, decimal price, Future future)
{
    decimal contractValue = price * future.TradingUnit * Math.Abs(quantity);
    decimal feeRate = 0.0001m;
    return contractValue * feeRate;
}
```

---

## 11. 仓位与资金管理（多策略分账）

### 11.1 PortfolioManager — 总账 + 各策略分账

```csharp
// TradingStudio.Engine/PortfolioManager.cs

internal class PortfolioManager
{
    // ═══ 总账 ═══
    public decimal TotalStartingCapital { get; }
    public decimal TotalCash { get; private set; }
    public decimal TotalMarginUsed { get; private set; }
    public decimal TotalEquity => TotalCash + _allPositions.Values.Sum(p => p.UnrealizedPnl);

    // ═══ 各策略分账 ═══
    // 使用 SortedDictionary 保证回测可重复性
    private readonly SortedDictionary<string, SubPortfolio> _subPortfolios = new();

    public SubPortfolio GetSubPortfolio(string strategyId);
    public IReadOnlyList<SubPortfolio> SubPortfolios { get; }

    // ═══ 操作 ═══
    public void UpdateMarketPrice(string instrumentId, TickRecord tick);
    public Trade? ProcessFill(OrderEvent fill, FutureRegistry registry);
}

public class SubPortfolio
{
    public string StrategyId { get; }
    public decimal AllocatedCapital { get; }       // 分配资金
    public decimal Cash { get; internal set; }
    public decimal MarginUsed { get; internal set; }
    public decimal Equity => Cash + Positions.Sum(p => p.UnrealizedPnl);
    public decimal PeakEquity { get; internal set; }
    public IReadOnlyList<Position> Positions { get; }
}
```

### 11.2 策略隔离

策略通过 `StrategyContext` 只能看到自己的 `SubPortfolio`：
- `_ctx.Equity` → 返回自己分账的 Equity
- `_ctx.GetPosition("rb2605")` → 只返回自己持有的持仓
- `_ctx.AvailableCash` → 自己分账的可用资金

---

## 12. 策略配置管理

### 12.1 配置生命周期

```
JSON配置文件           ← 研发阶段手工维护，版本控制
    │
    ▼
StrategyConfig (POCO)  ← 强类型中间表示
    │
    ▼
ConfigValidator        ← 规则检查（品种存在？参数范围？资金合理？）
    │
    ▼
StrategyFactory        ← 反射发现 [StrategyParameter] + 注入值 + 验证 → 创建策略
    │
    ▼
IStrategy.Initialize   ← 策略拿到参数，可注册指标、覆盖默认值
    │
    ▼
运行时 (只读)           ← Parameters 不可变，风控可收紧
    │
    ▼
EngineReport           ← 内嵌 config_snapshot (可追溯)
```

### 12.2 StrategyFactory

```csharp
// TradingStudio.Engine/StrategyFactory.cs

public static class StrategyFactory
{
    // 显式注册表 — 策略类型必须预先注册。
    // 不需要程序集扫描，简单可靠。
    private static readonly Dictionary<string, Type> _registry = new();

    public static void Register<T>(string name) where T : IStrategy
        => _registry[name] = typeof(T);

    public static IStrategy Create(StrategyConfig config)
    {
        // ① 从注册表查找策略类型
        if (!_registry.TryGetValue(config.StrategyType, out var type))
            throw new InvalidOperationException(
                $"未注册的策略: {config.StrategyType}。" +
                $"请先调用 StrategyFactory.Register<{config.StrategyType}>(\"{config.StrategyType}\")");

        // ② 反射发现所有 [StrategyParameter] 属性
        var parameters = DiscoverParameters(type);

        // ③ 实例化（策略类需要无参构造函数；初始化通过 Initialize 完成）
        var strategy = (IStrategy)Activator.CreateInstance(type)!;

        // ④ 应用配置覆盖
        foreach (var (key, value) in config.Parameters)
        {
            if (parameters.TryGetValue(key, out var param))
                param.SetValue(strategy, value);
        }

        // ⑤ 验证参数范围
        ValidateParameters(strategy, parameters);

        return strategy;
    }
}

// 使用示例:
// StrategyFactory.Register<MaCrossStrategy>("MaCross");
// StrategyFactory.Register<BollingerBreakout>("BollingerBreakout");
```

### 12.3 三层存储

| 层 | 内容 | 存储 |
|----|------|------|
| 策略定义 | 参数声明、类型 | 代码 (`[StrategyParameter]`) |
| 策略配置 | 参数值 + 品种 + 资金 | JSON 文件 (`configs/strategies/*.json`) |
| 运行时快照 | 完整配置 + 结果 | DB `backtest_runs` 表 |

### 12.4 参数策略示例

```csharp
public class MaCrossStrategy : IStrategy
{
    [StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60, Category = "Entry")]
    public int FastPeriod { get; set; } = 5;

    [StrategyParameter(Description = "慢线周期", DefaultValue = 20, Min = 5, Max = 200, Category = "Entry")]
    public int SlowPeriod { get; set; } = 20;

    [StrategyParameter(Description = "每笔交易手数", DefaultValue = 1, Min = 1, Max = 100, Category = "Position")]
    public int Quantity { get; set; } = 1;

    [StrategyParameter(Description = "最大亏损百分比", DefaultValue = 0.05, Min = 0.01, Max = 0.20, Category = "Risk")]
    public double MaxLossPct { get; set; } = 0.05;
}
```

### 12.5 运行时修改规则

| 场景 | 可改性 |
|------|--------|
| 回测单次运行 | ❌ 完全不可变 |
| 回测参数扫描 | 每次运行不同参数（GridSearch 外部循环） |
| 实盘运行中 | ⚠️ 只能收紧风控参数，逻辑参数只读 |
| 实盘日终 | ✅ 下一交易日新配置生效 |

### 12.6 参数优化（Phase 2c 留桩）

```csharp
public class GridSearchConfig
{
    public Dictionary<string, object[]> ParameterGrid { get; init; }
    public OptimizationMetric Metric { get; init; }  // SharpeRatio / TotalReturn / MaxDrawdown
    public decimal MinWinRate { get; init; }
    public int MinTrades { get; init; }
}
```

---

## 13. 绩效报告

### 13.1 EngineReport — 引擎级报告

```csharp
public class EngineReport
{
    // 总览
    public PortfolioSnapshot FinalPortfolio { get; init; }
    public decimal TotalReturn { get; init; }
    public decimal MaxDrawdown { get; init; }

    // 各策略子报告
    public IReadOnlyList<PerformanceReport> StrategyReports { get; init; }

    // 监控摘要
    public MonitorSummary MonitorSummary { get; init; }    // NEW

    // 可追溯性
    public IReadOnlyList<StrategyConfig> ConfigSnapshots { get; init; }  // NEW
}
```

### 13.2 PerformanceReport（单策略）

与 v2.0 基本一致，新增 `TotalSlippage` 和 `SlippagePerTrade`：

```csharp
public class PerformanceReport
{
    public string StrategyId { get; init; }                // NEW
    // 收益
    public decimal StartingCapital { get; init; }
    public decimal FinalEquity { get; init; }
    public decimal TotalNetProfit { get; init; }
    public decimal CompoundingAnnualReturn { get; init; }
    // 风险
    public decimal MaxDrawdown { get; init; }
    public decimal SharpeRatio { get; init; }
    public decimal SortinoRatio { get; init; }
    // 交易
    public int TotalOrders { get; init; }
    public int TotalTrades { get; init; }
    public decimal WinRate { get; init; }
    public decimal AverageWin { get; init; }
    public decimal AverageLoss { get; init; }
    public decimal ProfitLossRatio { get; init; }
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }
    // 曲线
    public List<(DateTimeOffset, decimal)> EquityCurve { get; init; }
    public List<Trade> Trades { get; init; }
}
```

### 13.3 输出示例

```
===== 回测报告: 2025-01-02 ~ 2025-12-31 =====
模式: Bar 回放 | Bar: 1min | 品种: rb2605, i2605

══════ 总览 ══════
总初始资金: ¥200,000.00 | 总最终权益: ¥232,500.00 | 总收益: 16.25%

══════ 策略报告 ══════
── ma-cross-rb (MaCross on rb2605) ──
分配资金: ¥100,000 → 最终: ¥108,432 | 收益: 8.43%
最大回撤: 12.35% | 夏普: 0.34 | 胜率: 42.3% | 交易: 78次

── bollinger-i (BollingerBreakout on i2605) ──
分配资金: ¥60,000 → 最终: ¥65,800 | 收益: 9.67%
最大回撤: 8.21% | 夏普: 0.51 | 胜率: 38.5% | 交易: 52次

── spread-rb-i (SpreadArbitrage on rb2605+i2605) ──
分配资金: ¥40,000 → 最终: ¥58,268 | 收益: 45.67%
最大回撤: 5.12% | 夏普: 1.82 | 胜率: 65.0% | 交易: 120次

══════ 监控摘要 ══════
总告警: 12 | 高滑点: 3 | 连续亏损: 5 | 异常频率: 0 | 风控拒绝: 4

报告: backtest_results/2025_full.json
```

---

## 14. 策略示例

### 14.1 双均线突破策略（使用注册指标）

```csharp
// TradingStudio.Engine/Examples/MaCrossStrategy.cs

public class MaCrossStrategy : IStrategy
{
    [StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60, Category = "Entry")]
    public int FastPeriod { get; set; } = 5;

    [StrategyParameter(Description = "慢线周期", DefaultValue = 20, Min = 5, Max = 200, Category = "Entry")]
    public int SlowPeriod { get; set; } = 20;

    [StrategyParameter(Description = "每笔交易手数", DefaultValue = 1, Min = 1, Max = 100, Category = "Position")]
    public int Quantity { get; set; } = 1;

    public string Name => "MA双均线突破";

    private StrategyContext _ctx = null!;
    private string _instrument = "";

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        _instrument = context.SubscribedInstruments[0];

        // 注册指标（引擎自动 feed，多策略共享）
        _ctx.RegisterIndicator(_instrument, new SMA(FastPeriod), tag: "fast");
        _ctx.RegisterIndicator(_instrument, new SMA(SlowPeriod), tag: "slow");

        _ctx.Log($"策略初始化: {Name} on {_instrument} " +
                 $"(快线={FastPeriod}, 慢线={SlowPeriod})");
    }

    public void OnTick(TickRecord tick, string instrumentId)
    {
        // 异常价差告警
        if (tick.SpreadDouble > 5 * _ctx.GetFuture(instrumentId).TickSize)
            _ctx.LogWarning($"价差异常: {instrumentId} Spread={tick.SpreadDouble:F2}");
    }

    public void OnBar(Bar bar)
    {
        if (bar.InstrumentId != _instrument) return;

        // 指标已经在 IndicatorManager.Feed 中更新，直接读值
        var fastVal = _ctx.GetIndicatorValue(_instrument, "SMA", "fast");
        var slowVal = _ctx.GetIndicatorValue(_instrument, "SMA", "slow");
        if (double.IsNaN(fastVal) || double.IsNaN(slowVal)) return;

        // 需要前一 Bar 的指标值来判断交叉 → 从指标 Values 历史获取
        var fastMa = _ctx.GetIndicator<SMA>(_instrument, "fast")!;
        var slowMa = _ctx.GetIndicator<SMA>(_instrument, "slow")!;
        if (fastMa.Values.Count < 2 || slowMa.Values.Count < 2) return;

        var prevFast = fastMa.Values[^2];  // 倒数第二个值 = 上一 Bar
        var prevSlow = slowMa.Values[^2];
        var currFast = fastMa.CurrentValue;
        var currSlow = slowMa.CurrentValue;

        var pos = _ctx.GetPosition(_instrument);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // 金叉做多
        if (prevFast <= prevSlow && currFast > currSlow && !hasLong)
        {
            if (hasShort) _ctx.ClosePosition(_instrument);
            var ticket = _ctx.MarketBuy(_instrument, Quantity, "金叉做多");
            _ctx.Log($"金叉做多 @ {bar.CloseDouble:F2} [Order #{ticket.OrderId}]");
        }
        // 死叉做空
        else if (prevFast >= prevSlow && currFast < currSlow && !hasShort)
        {
            if (hasLong) _ctx.ClosePosition(_instrument);
            var ticket = _ctx.MarketSell(_instrument, Quantity, "死叉做空");
            _ctx.Log($"死叉做空 @ {bar.CloseDouble:F2} [Order #{ticket.OrderId}]");
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"  ✓ 成交: {evt.InstrumentId} {evt.Quantity}手 @ {evt.FillPrice:F2}");
        else if (evt.Type == OrderEventType.Rejected)
            _ctx.LogError($"  ✗ 拒绝: {evt.Message}");
    }

    public void OnAlert(MonitorAlert alert)
    {
        if (alert.Type == AlertType.ConsecutiveLosses)
        {
            _ctx.LogWarning($"连续亏损告警 → 暂停交易");
            // 策略可以自行减仓或暂停（Phase 2b）
        }
    }

    public void OnEndOfAlgorithm()
    {
        _ctx.Log($"回测结束. 最终权益: {_ctx.Equity:C}");
    }
}
```

### 14.2 CLI 使用方式

```bash
# 单策略 Bar 回放
dotnet run -- backtest --config configs/strategies/ma-cross-rb.json

# 多策略 Bar 回放
dotnet run -- backtest --configs configs/strategies/*.json --mode bar

# Tick 回放
dotnet run -- backtest --configs configs/strategies/*.json --mode tick
```

---

## 15. 部署架构

### 15.1 单 exe，双模式

只有一个可执行文件 `TradingStudio.exe`，通过第一个命令行参数切换模式：

```
TradingStudio.exe                  → 实盘引擎 (Windows Service + API + SignalR)
TradingStudio.exe backtest [args]  → 回测 (一次性运行, 产出报告)
TradingStudio.exe collect [args]   → 行情采集 (Phase 1)
TradingStudio.exe import [args]    → CSV 导入 (Phase 1)
TradingStudio.exe import-jinshuyuan [args] → RAR 导入 (Phase 1)
```

### 15.2 实盘部署拓扑

```
┌──────────────────────────────────────────────────────────┐
│  交易服务器 (Windows, 7×24)                                │
│                                                           │
│  ┌──────────────────────────────────────────────┐        │
│  │  TradingStudio.exe                            │        │
│  │  (Windows Service, 开机自启)                   │        │
│  │                                               │        │
│  │  ┌─────────────────────────────────────┐      │        │
│  │  │  ASP.NET Core (localhost:5199)       │      │        │
│  │  │  ├── REST API (EngineMonitorApi)     │      │        │
│  │  │  └── SignalR Hub (EngineHub)         │      │        │
│  │  └────────────┬────────────────────────┘      │        │
│  │               │                                │        │
│  │  ┌────────────▼────────────────────────┐      │        │
│  │  │  TradingEngine                       │      │        │
│  │  │  ├── CtpLiveFeed ──── CTP MdApi     │      │        │
│  │  │  ├── ExecutionHandler ─ CTP TraderApi│      │        │
│  │  │  ├── StrategyContainer               │      │        │
│  │  │  ├── RiskController + FeedbackMonitor│      │        │
│  │  │  └── PortfolioManager                │      │        │
│  │  └──────────────────────────────────────┘      │        │
│  │                                               │        │
│  │  ┌──────────┐  ┌──────────┐                   │        │
│  │  │ DuckDB   │  │PostgreSQL│                   │        │
│  │  │ 时序数据  │  │ 关系数据  │                   │        │
│  │  └──────────┘  └──────────┘                   │        │
│  └──────────────────────────────────────────────┘        │
│                                                           │
│  ┌──────────────────────────────────────────────┐        │
│  │  TradingStudio.UI.exe  (桌面快捷方式, 按需)    │        │
│  │  SignalR Client → localhost:5199/hubs/engine │        │
│  │  面板: 总览 / 策略列表 / 订单 / 告警 / K线    │        │
│  └──────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────┘
```

### 15.3 安装

```powershell
# 一键发布 + 注册 Windows Service + 启动
.\deploy\install.ps1

# 更新策略配置（不重新发布, 重启服务即可）
.\deploy\install.ps1 -ConfigOnly

# 卸载
.\deploy\uninstall.ps1
```

**Windows Service 配置**:
- 服务名: `TradingStudio`
- 启动类型: 自动
- 失败恢复: 10s → 30s → 60s 退避, 24h 后重置计数器

### 15.4 EngineMonitorApi + SignalR Hub

> 代码文件位于 `src/TradingStudio/EngineMonitorApi.cs` + `src/TradingStudio/Hubs/EngineHub.cs`
> Phase 3 实现, 当前为 Stub。

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/health` | GET | 引擎健康状态 |
| `/api/portfolio` | GET | 总账快照 |
| `/api/strategies` | GET | 所有策略状态 |
| `/api/strategies/{id}` | GET | 单策略详情 |
| `/api/orders` | GET | 活跃订单 |
| `/api/trades` | GET | 今日成交 |
| `/api/alerts` | GET | 告警列表 |
| `/api/indicators/{strategyId}` | GET | 指标快照 |
| `/api/strategies/{id}/pause` | POST | 暂停策略 |
| `/api/strategies/{id}/resume` | POST | 恢复策略 |
| `/api/strategies/{id}/tighten` | POST | 收紧风控 |
| `/api/orders/close-position` | POST | 手动平仓 |
| `/api/config/reload` | POST | 重新加载配置 |
| `/hubs/engine` | SignalR | 实时推送 (告警 + 订单状态) |

### 15.5 核心原则

- **UI 是临时访客，引擎是主人** — 引擎不知道 UI 是否存在
- **API 查询只是瞬时快照** — 不可变 Snapshot 对象，非阻塞
- **控制命令最小化** — 只能暂停/恢复/收紧风控/平仓
- **SignalR 做双向推送** — 重连内置、按策略分组、WPF 原生支持
- **单 exe，零外部依赖** — `dotnet publish` 产出一个文件

---

## 16. 与现有代码的集成

### 16.1 已有组件（从 Phase 1 复用）

| 已有组件 | 位置 | V2.1 用法 |
|---------|------|----------|
| `TickRecord` | Core/Models/ | ✅ Tick 回放 + 实盘事件载体 |
| `Bar` | Core/Models/ | ✅ Bar 回放 + Indicator feed |
| `BarAggregator` | Data/Aggregation/ | ✅ 引擎复用 — Tick→1min Bar |
| `DailyBarAggregator` | Data/Aggregation/ | ✅ 引擎复用 — Tick→Day Bar |
| `Future` + `FutureRegistry` | Core/Models/ | ✅ 保证金/合约乘数/TickSize |
| `Exchange` | Core/Models/ | ✅ 交易所识别 |
| `ContractCodeGenerator` | Core/Models/ | ✅ 合约代码规则 |
| `BarStore` | Data/Storage/ | ✅ HistoricalBarFeed 数据源 |
| `TickCsvWriter` | Data/Storage/ | 金数源格式落盘（回测不写但格式一致） |
| `CsvTickImporter` | Data/Import/ | ✅ HistoricalTickFeed 读 CSV |
| `JinshuyuanImportService` | Data/Import/ | ⬜ 历史数据批量导入 |
| `TickImportService` | Data/Import/ | ⬜ Phase 2b 导入管线 |
| `CtpMdAdapter` | TradingStudio.Ctp/ | ⬜ Phase 3 CtpLiveFeed 包装 |
| `CollectService` | TradingStudio/Services/ | ⬜ Phase 3 实时采集集成 |
| `SessionScheduler` | TradingStudio/Services/ | ✅ Phase 3 交易时段识别 |
| `HealthMonitor` | TradingStudio/Services/ | ⬜ Phase 3 系统健康监控 |
| `CollectOptions` / `appsettings.json` | TradingStudio/ | ✅ 配置模式参考 |
| `symbols.json` | TradingStudio/ | ✅ 品种数据驱动 |
| `Microsoft.Data.Sqlite` | NuGet | ✅ 已有依赖 |
| `Serilog` | NuGet | ✅ 已有依赖 |

### 16.2 新增组件

| 新增项 | 位置 | 命名空间 | 状态 |
|--------|------|---------|------|
| `DataEvent / TickEvent / BarEvent` | Core/Engine/ | `TradingStudio.Core.Engine` | ✅ 完整 |
| `IDataFeed` | Core/Engine/ | `TradingStudio.Core.Engine` | ✅ 完整 |
| `IExecutionHandler` | Core/Engine/ | `TradingStudio.Core.Engine` | ✅ 完整 |
| `Order / OrderTicket / OrderEvent` | Core/Engine/Models/ | `TradingStudio.Core.Engine` | ✅ 完整 |
| `Position / Trade` | Core/Engine/Models/ | `TradingStudio.Core.Engine` | ✅ 完整 |
| `MonitorAlert` | Core/Engine/Models/ | `TradingStudio.Core.Engine` | ✅ 完整 |
| `IIndicator` | Core/Indicators/ | `TradingStudio.Core.Indicators` | ✅ 完整 |
| `IStrategy` | Core/Strategy/ | `TradingStudio.Core.Strategy` | ✅ 完整 |
| `StrategyContext` | Core/Strategy/ | `TradingStudio.Core.Strategy` | ✅ 完整 |
| `StrategyConfig + Parameters` | Core/Strategy/ | `TradingStudio.Core.Strategy` | ✅ 完整 |
| `StrategyParameterAttribute` | Core/Strategy/ | `TradingStudio.Core.Strategy` | ✅ 完整 |
| `IRiskRule / RiskCheckResult` | Core/Risk/ | `TradingStudio.Core.Risk` | ✅ 完整 |
| `IPortfolioState` | Core/Risk/ | `TradingStudio.Core.Risk` | ✅ 完整 |
| `HistoricalBarFeed` | Data/Engine/ | `TradingStudio.Data.Engine` | Stub — 2a |
| `HistoricalTickFeed` | Data/Engine/ | `TradingStudio.Data.Engine` | Stub — 2b |
| `CtpLiveFeed` | Data/Engine/ | `TradingStudio.Data.Engine` | Stub — 3 |
| `TradingEngine` | Engine/ | `TradingStudio.Engine` | Stub — 2a |
| `EngineOptions` | Engine/ | `TradingStudio.Engine` | ✅ 完整 |
| `StrategyContainer` | Engine/ | `TradingStudio.Engine` | Stub — 2a |
| `StrategyFactory` | Engine/ | `TradingStudio.Engine` | ✅ 完整 |
| `ExecutionHandler` | Engine/ | `TradingStudio.Engine` | Stub — 2a |
| `PortfolioManager + SubPortfolio` | Engine/ | `TradingStudio.Engine` | Stub — 2a |
| `IndicatorManager` | Engine/ | `TradingStudio.Engine` | ✅ 完整 |
| `FeedbackMonitor + MonitorSummary` | Engine/ | `TradingStudio.Engine` | Stub — 2b |
| `EngineReport / PortfolioSnapshot` | Engine/ | `TradingStudio.Engine` | Stub — 2a |
| `PerformanceReport` | Engine/Statistics/ | `TradingStudio.Engine.Statistics` | Stub — 2a |
| `MaCrossStrategy` | Engine/Examples/ | `TradingStudio.Engine.Examples` | Stub — 2a |
| `BacktestCommand` | TradingStudio/Commands/ | `TradingStudio.Commands` | Stub — 2a |
| `EngineMonitorApi` | TradingStudio/ | `TradingStudio` | REST 端点已建 (Stub) |
| `EngineHub` | TradingStudio/Hubs/ | `TradingStudio` | SignalR Hub 已建 (Stub) |
| `ResearchContext + BarReader` | Research/ | `TradingStudio.Research` | ✅ 完整 |
| `BarSeries + ReturnsAnalyzer` | Research/ | `TradingStudio.Research.Stats` | ✅ 完整 |
| `ChartHelper` | Research/Viz/ | `TradingStudio.Research.Viz` | ✅ 完整 (ScottPlot) |
| `TradingStudio.UI` | src/TradingStudio.UI/ | `TradingStudio.UI` | ✅ 项目已建 (Phase 3) |
| `TradingStudio.Engine.Tests` | test/ | — | ✅ 项目已建 |

---

## 17. 实施路线

### Phase 2a: 最小可用回测 — 单策略 Bar 回放（2-3 天）

```
Day 1: 核心模型 + 接口
  ├── DataEvent / TickEvent / BarEvent
  ├── IDataFeed / IExecutionHandler
  ├── IStrategy (OnTick + OnBar + OnAlert)
  ├── StrategyContext (含 RegisterIndicator 接口)
  ├── IIndicator 接口定义
  ├── StrategyParameterAttribute + StrategyConfig + StrategyParameters
  ├── Order / OrderTicket / OrderEvent / Position / Trade
  ├── MonitorAlert / MonitorSummary (数据结构)
  └── EngineOptions

Day 2: Engine + Bar Feed + Execution + IndicatorManager + Portfolio
  ├── TradingEngine (主循环 — v2.1 完整版，StrategyContainer只装一个策略)
  ├── StrategyContainer (结构就位，单策略)
  ├── StrategyFactory (反射 + 参数注入)
  ├── IndicatorManager (注册/feed/预热/查询 — 完整实现)
  ├── HistoricalBarFeed (SQLite → BarEvent)
  ├── ExecutionHandler (ProcessBar — 市价/限价/止损)
  ├── PortfolioManager (单账 — Phase 2b 加分账)
  └── 单元测试 (每个组件独立可测)

Day 3: 策略示例 + 绩效报告 + CLI
  ├── PerformanceReport.Generate()
  ├── FeedbackMonitor (空桩 — 数据结构+接口，统计逻辑Phase 2b)
  ├── MaCrossStrategy (使用注册指标)
  ├── SMA 指标实现
  ├── BacktestCommand (CLI: --config strategy.json)
  └── 端到端: rb2605 Bar 回放 → 双均线 → 绩效报告
```

### Phase 2b: 多策略 + Tick 回放 + 反馈监控（2-3 天）

```
Day 1: 多策略
  ├── StrategyContainer 多策略注册 + 事件路由
  ├── PortfolioManager 分账 (SubPortfolio)
  ├── ExecutionHandler 多策略优先级撮合
  ├── 策略级风控 (MaxStrategyDrawdownRule)
  └── 多策略 Bar 回放端到端验证

Day 2: Tick 回放 + 反馈监控
  ├── HistoricalTickFeed (K-way merge CSV)
  ├── ExecutionHandler.ProcessTick (Bid/Ask 滑点 + 流动性约束)
  ├── FeedbackMonitor 完整统计 (RecordFill/RecordTrade/CheckAlerts)
  ├── FeedbackMonitor + StrategyContainer 告警分发
  └── Tick vs Bar 回放结果对比

Day 3: 端到端验证
  ├── 同一策略 Bar vs Tick 结果对比
  ├── 多策略并发 Tick 回放
  ├── 滑点统计 + 分析
  └── 回归测试 (确保 Bar 回放结果不受改动影响)
```

### Phase 2c: 精度提升 + 因子 + 参数优化（1-2 周）

```
├── 涨跌停板成交限制
├── 部分成交 (Tick.Volume 约束)
├── 平今/平昨差异化手续费
├── IFactor 接口 + FactorManager
├── 参数优化 (GridSearch)
└── 策略对比框架
```

### Phase 3: 实盘对接（2-3 周）

```
├── CtpLiveFeed (CtpMdAdapter → DataEvent 流)
├── ExecutionHandler 实盘模式 (CTP TraderApi)
├── TradingStudio 实盘模式 (Windows Service + localhost API + SignalR)
├── TradingStudio.UI (WPF/Blazor 监控面板)
├── 策略配置热重载 (日终)
├── Simnow 模拟盘验证
└── 小合约实盘
```

---

## 附录 A：设计决策记录

### A1. 为什么 DataEvent 用 record？
`abstract record` 提供 `Time` 公共字段和模式匹配。`TickEvent` 和 `BarEvent` 是值语义。

### A2. 为什么 PortfolioManager 不对外暴露接口？
策略通过 `StrategyContext` 访问。多策略下通过 `SubPortfolio` 隔离。

### A3. 为什么引擎内部持有 BarAggregator 和 IndicatorManager？
- 回测和实盘用同一份代码
- 指标多策略共享，引擎去重
- DataFeed 保持纯粹

### A4. 为什么策略隔离？
策略之间不应该互相知道对方存在。独立分账、独立风控、独立统计。降低耦合，防止策略 A 的 bug 影响策略 B。

### A5. 为什么 FeedbackMonitor 和 RiskController 分开？
RiskController 是闸门（阻断），FeedbackMonitor 是仪表盘（观测）。职责不同、触发时机不同、输出不同。

### A6. 为什么指标在引擎层而非策略层？
BarAggregator : Tick→Bar = IndicatorManager : Bar→衍生值。多策略共享去重。策略注册需求，引擎负责计算。

### A7. 为什么策略配置用 [StrategyParameter] 而非平铺字段？
策略自描述参数，编译时类型安全。StrategyFactory 反射 + 验证，配置文件只需覆盖值。

### A8. 为什么 UI 和引擎分成两个进程？
引擎 7×24 常驻，UI 按需启动。REST API 做查询/控制，SignalR Hub 做双向实时推送（内置重连、按策略分组）。UI 断开不影响引擎运行，重连后自动恢复订阅。

### A9. 为什么 Bar 模式下策略刚下的订单不能立即撮合？
Bar 回放中，策略在 OnBar(bar_N) 中看到的是已闭合的 Bar。如果立即用 bar_N 的价格撮合市价单 → 前瞻偏差（策略用完整 OHLC 做决策，却用同一根 Bar 的 Open 成交）。正确做法：订单进入队列，等 bar_N+1 到达时用 bar_N+1.Open 成交。这保证了策略在决策时看不到成交价。

### A10. 为什么 Tick.Volume 不能直接用于流动性约束？
CTP 的 TickRecord.Volume 是当日累计成交量。必须用增量（当前 Tick.Volume - 上一 Tick.Volume）来约束单笔 Tick 的最大可成交量。否则越往后流动性约束越松。

### A11. 为什么使用 SortedDictionary 而非 Dictionary？
回测的确定性要求：同一数据源跑两次，结果必须完全一致。普通 Dictionary 的迭代顺序在 .NET 不同版本间可能不同。关键路径（策略订阅、分账遍历、指标注册）使用 `SortedDictionary` 或 `List`（保持插入顺序）保证可重复性。

---

## 附录 B：待讨论的开放问题

1. **HistoricalTickFeed 数据源**：直接用金数源 CSV（K-way merge + GBK） vs 先导入 SQLite？CSV 更直接但 IO 较大。
2. **集合竞价 Tick**：策略默认收到竞价 Tick 还是引擎层过滤？`FlagAuction` 为此准备。
3. **换月处理**：主力连续回测由策略层还是引擎层负责？
4. **策略间通信**：Phase 2c 是否需要？默认完全隔离。
5. **API 安全**：localhost only，但控制命令（暂停/平仓）是否需要确认机制？
6. **TickFillDelay 默认值**：Tick 模式下同 Tick 成交（0）vs 延迟 1 Tick（1）？默认 0（即时），保守场景可配置 1。
