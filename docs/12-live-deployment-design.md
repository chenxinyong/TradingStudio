# TradingStudio 实盘部署设计

> 最终实盘部署的全景规划。从当前 v0.1.0（纯行情采集）到完整自动交易的演进路线。
>
> **版本**: v1.0 | **日期**: 2026-06-13 | **状态**: 设计文档，随 Phase 2-4 推进持续细化

---

## 目录

1. [核心原则](#1-核心原则)
2. [部署拓扑](#2-部署拓扑)
3. [双进程架构](#3-双进程架构)
4. [信号→成交完整链路](#4-信号成交完整链路)
5. [交易日时序](#5-交易日时序)
6. [风控横切层](#6-风控横切层)
7. [数据存储](#7-数据存储)
8. [告警通道](#8-告警通道)
9. [渐进演进路线](#9-渐进演进路线)
10. [回测与实盘统一](#10-回测与实盘统一)
11. [成本估算](#11-成本估算)
12. [运维清单](#12-运维清单)

---

## 1. 核心原则

所有实盘部署决策锚定以下四条原则：

| # | 原则 | 说明 |
|---|------|------|
| 1 | **行情与交易物理分离** | CTP MdApi 和 TraderApi 是两套独立连接，架构必须反映这一点 |
| 2 | **风控引擎是横切层** | 任何订单在到达 CTP 之前必须过风控，不是事后检查 |
| 3 | **所有事件必须可回放** | Tick/Order/Trade 全部持久化，这是回测质量的根基 |
| 4 | **合约规格/保证金/手续费数据驱动** | 从第一天起就不能硬编码 |

---

## 2. 部署拓扑

```
┌──────────────────────────────────────────────────┐
│              云服务器 (2C4G Windows)               │
│                                                  │
│  ┌────────────────────────────────────────────┐  │
│  │          TradingStudio.exe (引擎进程)        │  │
│  │          Windows Service · 7×24             │  │
│  │                                            │  │
│  │  ┌──────────┐  ┌──────────┐  ┌─────────┐  │  │
│  │  │ MdService│  │ TdService│  │ Engine  │  │  │
│  │  │ 行情采集  │  │ 交易执行  │  │ 策略引擎 │  │  │
│  │  └────┬─────┘  └────┬─────┘  └────┬────┘  │  │
│  │       │              │              │       │  │
│  │  ┌────▼──────────────▼──────────────▼────┐  │  │
│  │  │         RiskController (横切层)        │  │  │
│  │  │   Pre-Order阻断 / Post-Fill预警       │  │  │
│  │  │   Periodic强平 / 保证金监控            │  │  │
│  │  └───────────────────────────────────────┘  │  │
│  │                                            │  │
│  │  ┌──────────────────────────────────────┐   │  │
│  │  │  EngineMonitorApi (localhost:5199)   │   │  │
│  │  │  GET  /api/health                    │   │  │
│  │  │  GET  /api/portfolio                 │   │  │
│  │  │  GET  /api/strategies                │   │  │
│  │  │  GET  /api/orders                    │   │  │
│  │  │  GET  /api/trades                    │   │  │
│  │  │  POST /api/pause                     │   │  │
│  │  │  POST /api/resume                    │   │  │
│  │  │  POST /api/tighten                   │   │  │
│  │  │  POST /api/close-position            │   │  │
│  │  │  POST /api/reload-config             │   │  │
│  │  │  SSE  /api/stream/alerts             │   │  │
│  │  │  SSE  /api/stream/orders             │   │  │
│  │  └──────────────────────────────────────┘   │  │
│  └────────────────────────────────────────────┘  │
│                                                  │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐       │
│  │ Tick CSV │  │ DuckDB   │  │PostgreSQL│       │
│  │ 原始落盘  │  │ 时序OLAP │  │ 关系OLTP │       │
│  └──────────┘  └──────────┘  └──────────┘       │
│                                                  │
│  ┌──────────────────────────────────────────┐   │
│  │     TradingStudio.UI.exe (UI进程)         │   │
│  │     WPF 监控面板 · 按需启动 · 可远程      │   │
│  │     通过 localhost:5199 与引擎通信        │   │
│  └──────────────────────────────────────────┘   │
└──────────────────────────────────────────────────┘
         │                          │
         ▼                          ▼
    CTP MdApi                  CTP TraderApi
  (tcp://券商前置机)           (tcp://券商前置机)
```

**网络说明**：CTP 是出站 TCP 连接，服务器主动连期货公司前置机。不需要公网 IP，不需要固定 IP。

---

## 3. 双进程架构

### 3.1 角色定义

| | 引擎进程 (`TradingStudio.exe`) | UI进程 (`TradingStudio.UI.exe`) |
|---|---|---|
| **角色** | 主人 | 临时访客 |
| **运行模式** | Windows Service，7×24 | 桌面应用，按需启停 |
| **职责** | 行情采集 + 策略执行 + 下单 + 风控 | 监控面板 + 手动干预 |
| **感知对方** | 不知道 UI 是否存在 | 通过 HTTP API 查询引擎 |
| **崩溃影响** | 致命 — 系统停摆 | 无影响 — 引擎继续运行 |
| **启动方式** | 系统启动自动拉起 | 远程桌面手动打开 |

### 3.2 引擎进程 DI 注册

```csharp
// TradingStudio.exe — Program.cs
var builder = Host.CreateApplicationBuilder(args);

// 行情采集 — 全时段运行
builder.Services.AddHostedService<MdService>();

// 交易执行 — 仅盘中活跃
builder.Services.AddHostedService<TdService>();

// 策略引擎 — 仅盘中活跃
builder.Services.AddHostedService<EngineService>();

// 风控横切 — 全时段运行
builder.Services.AddSingleton<RiskController>();

// Session 调度 — 驱动连接/断开时序
builder.Services.AddSingleton<SessionScheduler>();

// 健康监控 — 每分钟刷新
builder.Services.AddSingleton<HealthMonitor>();

// HTTP API — 供 UI 进程查询
builder.Services.AddSingleton<EngineMonitorApi>();

// 数据存储
builder.Services.AddSingleton<TickCsvWriter>();
builder.Services.AddSingleton<BarStore>();          // DuckDB (Phase 2)
builder.Services.AddSingleton<PostgresRepository>(); // PostgreSQL (Phase 2)

var host = builder.Build();
host.Run();
```

### 3.3 通信协议

**引擎 ↔ UI 通信**：ASP.NET Core Minimal API，仅监听 localhost。

```
引擎进程 (localhost:5199)
  │
  ├── GET  /api/health          → { status, session, quotes, bars, uptime, ... }
  ├── GET  /api/portfolio       → PortfolioSnapshot (权益/保证金/可用)
  ├── GET  /api/strategies      → StrategySnapshot[] (状态/信号/持仓/盈亏)
  ├── GET  /api/orders          → OrderSnapshot[] (当日所有订单)
  ├── GET  /api/trades          → TradeSnapshot[] (当日所有成交)
  ├── GET  /api/alerts          → AlertSnapshot[] (最近告警)
  │
  ├── POST /api/pause           → 暂停指定策略
  ├── POST /api/resume          → 恢复指定策略
  ├── POST /api/tighten         → 收紧风控参数
  ├── POST /api/close-position  → 手动平仓
  ├── POST /api/reload-config   → 重新加载策略配置
  │
  ├── SSE  /api/stream/alerts   → 实时告警推送
  └── SSE  /api/stream/orders   → 实时订单推送
```

**关键原则**：
- **UI 是临时访客，引擎是主人** — 引擎不知道 UI 是否存在
- **API 查询只是瞬时快照** — 不可变 Snapshot 对象，非阻塞
- **API 是薄壳** — 不包含引擎逻辑，只读代理

### 3.4 快照类型

```csharp
public record PortfolioSnapshot(
    decimal TotalEquity,      // 总权益
    decimal AvailableCash,    // 可用资金
    decimal MarginUsed,       // 已用保证金
    decimal MarginRate,       // 保证金占用率
    decimal DailyPnl,         // 当日盈亏
    decimal DailyReturn,      // 当日收益率
    DateTime Timestamp
);

public record StrategySnapshot(
    string StrategyId,
    string StrategyName,
    string Status,            // Running / Paused / Error
    int PositionCount,
    decimal AllocatedCapital,
    decimal DailyPnl,
    decimal TotalPnl,
    int SignalsToday,
    int OrdersToday,
    DateTime Timestamp
);

public record AlertSnapshot(
    string AlertId,
    string Level,             // Info / Warning / Critical
    string Source,            // Risk / Engine / Connection
    string Message,
    DateTime Timestamp
);
```

---

## 4. 信号→成交完整链路

### 4.1 数据流向

```
  CTP MdApi (tick)
       │
       ▼
  CtpLiveFeed : IDataFeed
       │
       ▼
  IAsyncEnumerable<DataEvent>  ←── 统一事件流 (TickEvent + BarEvent)
       │
       ├──→ BarAggregator (共享，Tick → 1min → Day)
       │         │
       │         ▼
       │    IndicatorManager (去重计算，多策略共享)
       │         │
       │         ▼
       ├──→ StrategyContainer.Dispatch(evt)
       │         │
       │         ├──→ StrategyA.OnBar(bar)      → Signal
       │         ├──→ StrategyB.OnBar(bar)      → Signal
       │         └──→ StrategyC.OnTick(tick)    → Signal
       │                    │
       │                    ▼
       │              Signal → Order
       │                    │
       │                    ▼
       │         ╔══════════════════════╗
       │         ║   RiskController     ║  ← 横切层，必经之路
       │         ║   · 仓位上限检查      ║
       │         ║   · 单笔金额限制      ║
       │         ║   · 日内频次限制      ║
       │         ║   · 保证金充足性      ║
       │         ║   · 涨跌停板保护      ║
       │         ╚══════╤═══════════════╝
       │                │ (通过)
       │                ▼
       └──→ CtpTraderGateway : ITradeGateway
                    │
                    ▼
              CTP TraderApi → 交易所
                    │
                    ▼
              OnRtnTrade → Trade (持久化 PostgreSQL)
                    │
                    ▼
              PortfolioManager.Update()
                    │
                    ▼
              Strategy.OnOrderEvent(Fill)
```

### 4.2 信号生命周期

```
Signal (策略生成)
  → Order (经过风控)
    → OrderEvent.Submitted (CTP 已受理)
      → OrderEvent.PartiallyFilled (部分成交)
        → OrderEvent.Filled (全部成交)
          或
        → OrderEvent.Rejected (被拒)
          或
        → OrderEvent.Cancelled (已撤单)
```

### 4.3 Order 状态机

```
                    ┌─────────┐
                    │  Signal  │
                    └────┬─────┘
                         │ 策略生成买入/卖出意图
                         ▼
                    ┌─────────┐
                    │  Created │
                    └────┬─────┘
                         │ 过风控
                         ▼
            ┌───────────────────────┐
            │      Submitted         │
            └───┬───────┬───────┬───┘
                │       │       │
       ┌────────▼──┐ ┌──▼───┐ ┌─▼────────┐
       │PartiallyFilled│ │Filled│ │Rejected │
       └────────┬──┘ └──┬───┘ └──────────┘
                │       │
                ▼       ▼
           ┌──────────────┐
           │    Filled    │
           └──────────────┘
```

### 4.4 Order.Tag 可追溯

每个 Order 携带 `Tag` 字段，贯穿整个生命周期：

```csharp
public record OrderTag(
    string StrategyId,       // 哪个策略
    string SignalId,         // 哪个信号
    string FactorName,       // 哪个因子触发
    DateTime SignalTime,     // 信号产生时间
    decimal FactorValue      // 触发时的因子值
);
```

---

## 5. 交易日时序

### 5.1 日盘

| 时间 | 动作 | 触发方 |
|------|------|--------|
| 08:30 | 连接 CTP MdApi + TraderApi | SessionScheduler |
| 08:30 | 策略引擎预热（IndicatorManager 加载历史 Bar） | EngineService |
| 08:30 | `health.json`: `session=day` | HealthMonitor |
| 08:55-09:00 | 集合竞价 — Tick 接收但不触发策略 | EngineService 过滤 |
| 09:00-10:15 | 早盘第一节 — 策略正常执行，Tick 持续落盘 | 全系统 |
| 10:15-10:30 | 休盘（大商所/郑商所） — Flush 未闭合 Bar | BarAggregator |
| 10:30-11:30 | 早盘第二节 | 全系统 |
| 11:30-13:30 | 午休 — Flush 所有 Bar + 风控定时检查 + 健康刷新 | 全系统 |
| 13:30-15:00 | 下午盘 | 全系统 |

### 5.2 收盘处理 (15:00)

| 步骤 | 动作 |
|------|------|
| 1 | `BarAggregator.FlushAll()` — 强制闭合所有未完成 Bar |
| 2 | `DailyBarAggregator.Finalize()` — 日线 Bar 聚合入库 |
| 3 | `PortfolioManager.Snapshot()` — 当日结算快照 |
| 4 | `StrategyContainer.Settle()` — 各策略当日结算 |
| 5 | `TraderApi.Disconnect()` — 断开交易连接 |
| 6 | MdApi 保持连接（夜盘继续使用） |

### 5.3 夜盘

| 时间 | 动作 |
|------|------|
| 20:30 | 连接 CTP TraderApi（如有夜盘品种） |
| 20:30 | `health.json`: `session=night` |
| 21:00-02:30 | 夜盘交易（各品种收盘时间不同） |
| 02:30-03:00 | 最后一批品种收盘 — FlushAll() |
| 03:00 | TraderApi + MdApi 断开 |
| 03:00 | 数据库备份 (`pg_dump`) |

### 5.4 周末/节假日

```
SessionScheduler 检测非交易日
  → 不连接 CTP
  → 不启动策略引擎
  → health.json: status=idle, session=weekend
  → 到下一个交易日 08:30 自动恢复
```

---

## 6. 风控横切层

### 6.1 设计原则

> 风控是横切层。任何订单在到达 CTP TraderApi 之前必须过风控。
> 一条规则拒绝 = 整单拒绝。不是事后检查，不是可选项。

### 6.2 Pre-Order 阻断规则

| # | 规则 | 检查内容 | 动作 |
|---|------|---------|------|
| 1 | 单笔金额上限 | 合约价值 × 手数 ≤ 总资金 × 15% | 拒绝 |
| 2 | 总仓位上限 | 所有品种保证金之和 ≤ 总资金 × 60% | 拒绝 |
| 3 | 单品种仓位上限 | 单品种保证金 ≤ 总资金 × 20% | 拒绝 |
| 4 | 日内开仓次数 | 单策略 ≤ N 次/日 | 拒绝 |
| 5 | 涨跌停板保护 | 市价单在涨跌停板附近 → 拒绝 | 拒绝 |
| 6 | 保证金充足性 | 账户可用 ≥ 所需保证金 × 1.3 | 拒绝 |
| 7 | 自成交保护 | 同策略同品种未成交挂单 | 合并或拒绝 |

### 6.3 Post-Fill 预警规则

| # | 规则 | 条件 | 动作 |
|---|------|------|------|
| 1 | 滑点超标 | 成交价与信号价差 > 2 tick | 推送通知 |
| 2 | 部分成交超时 | 挂单 > 30s 未完全成交 | 推送通知 |
| 3 | 连续亏损 | 连续 N 笔亏损 | 建议暂停策略 |
| 4 | 手续费异常 | 单笔手续费 > 预期 3 倍 | 告警 |

### 6.4 Periodic 定时扫描（每 30s）

| # | 检查 | 动作 |
|---|------|------|
| 1 | 权益回撤 > 当日最大允许 | 强平所有仓位，暂停所有策略 |
| 2 | 保证金不足 | 强平亏损最大的仓位 |
| 3 | 单策略回撤 > 配额 | 暂停该策略，平该策略持仓 |

### 6.5 风控参数运行时调整

```csharp
// 策略逻辑参数：只读（实盘下交易日生效）
// 风控参数：可收紧，不可放宽

public interface IRiskController
{
    // 收紧风控（运行时即时生效）
    void Tighten(string rule, decimal newLimit);
    
    // 放宽风控（需要重启或下交易日生效）
    // 不提供运行时放宽接口 —— 这是安全设计
}
```

---

## 7. 数据存储

### 7.1 两库分工

| 数据库 | 类型 | 存储内容 | 用途 |
|--------|------|---------|------|
| **DuckDB** | 列存 OLAP | Bar (1min/5min/30min/Day)、Tick 索引 | 回测查询、指标计算、时序分析 |
| **PostgreSQL** | 行存 OLTP | 订单/成交/持仓/资金曲线/合约规格/策略配置 | 交易记录、审计追溯、配置管理 |

### 7.2 选择理由

- **DuckDB**：嵌入式列存引擎，时序聚合查询比 SQLite 快 10-100×。适合 "某品种过去一年所有 1min Bar" 这种 OLAP 查询。
- **PostgreSQL**：成熟的关系数据库，适合订单/持仓这类需要 ACID 保证的 OLTP 场景。`pg_dump` 备份成熟可靠。
- **不需要 ClickHouse/TDengine**：中低频策略，数据量在 DuckDB 覆盖范围内。
- **CSV 不替代**：Tick 原始数据仍以 CSV（金数源 42 列格式）落盘，作为不可变源数据。DuckDB/PostgreSQL 是派生索引。

### 7.3 目录结构

```
D:\TradingStudio\
├── app\                          ← 应用程序
│   ├── TradingStudio.exe         ← 引擎进程
│   ├── TradingStudio.UI.exe      ← UI 进程
│   ├── CTPWrapper.dll            ← C++/CLI 封装
│   ├── thostmduserapi_se.dll     ← CTP 行情 DLL
│   ├── thosttraderapi_se.dll     ← CTP 交易 DLL
│   ├── appsettings.json          ← 配置
│   └── symbols.json              ← 品种数据 (75 futures)
│
├── data\
│   ├── ticks\                    ← Tick CSV 原始落盘
│   │   └── {exchange}\
│   │       └── {contract}_{tradingDay}.csv
│   │
│   ├── bars.duckdb               ← DuckDB (Bar 时序数据)
│   │
│   └── tradingstudio.pg          ← PostgreSQL 数据目录
│
├── config\
│   ├── strategies\               ← 策略 JSON 配置
│   │   ├── ma-cross-01.json
│   │   └── rsi-mean-revert-01.json
│   │
│   └── risk-rules.json           ← 风控规则配置
│
├── logs\                         ← Serilog 日志 (每日滚动)
│   └── log{yyyyMMdd}.txt
│
├── health.json                   ← 健康监控 (每分钟刷新)
└── crash.log                     ← 未处理异常兜底日志
```

### 7.4 存储量估算

| 数据 | 每天 | 每年 |
|------|------|------|
| Tick CSV（10 个活跃品种） | ~38 MB | ~9.5 GB |
| Tick CSV（全部 928 合约） | ~280 MB | ~70 GB |
| DuckDB（1min + Day Bar） | ~8 MB | ~2 GB |
| PostgreSQL（订单+成交+配置） | ~1 MB | ~100 MB |
| 日志文件 | ~2 MB | ~500 MB |
| **合计（全市场采集）** | **~290 MB/天** | **~74 GB/年** |

> 100 GB 数据盘可用 5-8 年。

### 7.5 备份策略

| 内容 | 频率 | 方式 |
|------|------|------|
| PostgreSQL | 每天 03:00 | `pg_dump` → 压缩 → 本地 + 异地 |
| DuckDB | 每周 | 文件复制备份 |
| Tick CSV | 实时 | 源数据，不可恢复（CTP 不提供历史 Tick），建议异地同步 |
| 策略配置 JSON | 每次修改 | Git 版本控制 + 备份 |
| 日志 | 保留 30 天 | 自动轮转，过期删除 |

---

## 8. 告警通道

### 8.1 告警分级

| 级别 | 含义 | 通道 |
|------|------|------|
| **Info** | 状态变更（开盘/收盘/重连成功） | 日志 + health.json |
| **Warning** | 需要关注（部分成交超时/滑点超标/连续亏损） | 日志 + 手机推送 |
| **Critical** | 需要立即处理（断线重连失败/风控拒单/保证金不足/强平触发） | 日志 + 手机推送 + UI 面板闪烁 |

### 8.2 告警路由

```
引擎检测到异常
    │
    ├──→ health.json 状态变更
    │
    ├──→ EngineMonitorApi → SSE → UI 面板 (如果开着)
    │
    ├──→ 手机推送 (Server酱 / Pushover / 企业微信)
    │     · CTP 断线重连失败 > 3 次
    │     · 风控拒绝订单
    │     · 持仓权益回撤 > 5%
    │     · 保证金不足
    │     · 强平触发
    │     · 磁盘剩余 < 10GB
    │     · 进程崩溃 (crash.log 兜底)
    │
    └──→ logs/ 持久化 (Serilog 每日滚动)
```

### 8.3 关键告警场景

```csharp
public enum AlertType
{
    // 连接
    CtpMdDisconnected,        // 行情断线
    CtpTdDisconnected,        // 交易断线
    CtpReconnectFailed,       // 重连失败 > 3次
    CtpReconnectSuccess,      // 重连成功

    // 风控
    RiskOrderRejected,        // 风控拒绝订单
    RiskMarginInsufficient,   // 保证金不足
    RiskDrawdownLimit,        // 回撤超限
    RiskForceLiquidation,     // 强平触发

    // 策略
    StrategyError,            // 策略异常
    StrategyPaused,           // 策略暂停
    StrategyConsecutiveLoss,  // 连续亏损

    // 系统
    SystemCrash,              // 进程崩溃
    DiskSpaceLow,             // 磁盘不足
    BackupFailed              // 备份失败
}
```

---

## 9. 渐进演进路线

```
现在 (v0.1.0)         Phase 2a-c               Phase 3               Phase 4
   │                      │                       │                    │
   │ 行情采集 ✅           │ 回测引擎               │ 实盘对接           │ 最终部署
   │ Bar聚合 ✅           │ · Bar回放+双均线       │ · CtpLiveFeed      │ · Windows Service
   │ Tick CSV ✅          │ · Tick回放(K-way)      │ · CtpTraderGateway │ · 风控全量启用
   │ 健康监控 ✅           │ · 绩效报告             │ · simnow模拟盘     │ · 手机告警
   │ 自动重连 ✅           │ · 参数优化             │ · 小合约验证       │ · 每日自动备份
   │                      │ · StrategyContainer    │ · 风控灰度测试     │ · 全市场运行
   └──────────────────────┴───────────────────────┴────────────────────┘
```

### 9.1 各阶段交付物

| 阶段 | 时间 | 交付物 |
|------|------|--------|
| **v0.1.0** (当前) | ✅ | 全市场行情采集 + Bar聚合 + Tick CSV + 健康监控 + 自动重连 |
| **Phase 2a** | 2-3 天 | Bar回放引擎 + 双均线策略 + 绩效报告 |
| **Phase 2b** | 2-3 天 | Tick回放 (K-way merge CSV) + ProcessTick 撮合 |
| **Phase 2c** | 1-2 周 | 涨跌停/部分成交/手续费/参数优化/多策略隔离 |
| **Phase 3** | 2-3 周 | CtpLiveFeed + CtpTraderGateway + simnow 模拟盘 |
| **Phase 4** | 1-2 周 | Windows Service 部署 + 风控全量 + 手机告警 + 小合约实盘 |

### 9.2 实盘启用条件

在切换到实盘之前，必须满足：

- [ ] simnow 模拟盘连续运行 2 周无异常
- [ ] 回测结果与 simnow 实盘偏差 < 5%
- [ ] 风控规则全部通过单元测试 + 场景测试
- [ ] 断线重连演练：手动断网 → 自动恢复 → 数据无缺口
- [ ] 极端行情回放：涨跌停板/集合竞价/交割月异常 Tick
- [ ] 小合约（甲醇/PTA）先跑 1 周，确认无误再上主力合约

---

## 10. 回测与实盘统一

### 10.1 核心设计

> 回测和实盘用同一套 `TradingEngine`，只换 `IDataFeed` 和 `ITradeGateway` 的实现。

```csharp
// 回测模式
var engine = new TradingEngine(
    dataFeed: new HistoricalBarFeed(csvDir),   // 从 CSV/DB 回放
    gateway: new SimulatedGateway(portfolio)    // 模拟撮合
);

// 实盘模式 — 同一份引擎代码
var engine = new TradingEngine(
    dataFeed: new CtpLiveFeed(mdApi),           // CTP 实时行情
    gateway: new CtpTraderGateway(tdApi)        // CTP 真实交易
);
```

### 10.2 可替换接口

```csharp
// 数据源接口 — 回测/实盘各自实现
public interface IDataFeed
{
    IAsyncEnumerable<DataEvent> StreamAsync(CancellationToken ct);
    string Source { get; }  // "CTP-Live" / "CSV-Replay" / "DuckDB-Replay"
}

// 交易网关接口 — 回测/实盘各自实现
public interface ITradeGateway
{
    Task<OrderEvent> SubmitOrderAsync(Order order, CancellationToken ct);
    Task<OrderEvent> CancelOrderAsync(string orderId, CancellationToken ct);
    IAsyncEnumerable<OrderEvent> OrderEvents { get; }
    IAsyncEnumerable<Trade> TradeEvents { get; }
}
```

### 10.3 差异仅在一处

```csharp
// 引擎入口 — 通过 DI 切换模式
builder.Services.AddSingleton<IDataFeed>(sp =>
    isLive
        ? new CtpLiveFeed(sp.GetRequiredService<CtpMdAdapter>())
        : new HistoricalBarFeed(Path.Combine(dataDir, "ticks"))
);

builder.Services.AddSingleton<ITradeGateway>(sp =>
    isLive
        ? new CtpTraderGateway(sp.GetRequiredService<CtpTdAdapter>())
        : new SimulatedGateway(sp.GetRequiredService<PortfolioManager>())
);
```

---

## 11. 成本估算

### 11.1 云服务器方案

| 方案 | 配置 | 月费 | 适用阶段 |
|------|------|------|----------|
| 轻量云服务器 | 2C2G 40GB | ~60 元 | 开发测试 |
| **标准云服务器** | **2C4G 100GB** | **~100 元** | **实盘运行** |
| 轻量云服务器（大） | 4C8G 150GB | ~200 元 | 实盘 + 回测并行 |

### 11.2 家用服务器方案

| 项目 | 说明 |
|------|------|
| 硬件 | 迷你主机 Intel N100 16GB+512GB，~2000 元一次性 |
| 电费 | ~10 元/月 |
| 优点 | 零月费，数据完全在本地 |
| 缺点 | 家庭网络不稳定（断网=断行情）、停电=系统中断 |
| 建议 | 实盘阶段用云服务器（网络稳定优先），回测开发随便用什么机器 |

### 11.3 其他费用

| 项目 | 费用 |
|------|------|
| Server酱 / Pushover | 免费额度够用 |
| 域名 | 不需要 |
| SSL 证书 | 不需要（仅本地监听） |
| CTP 行情/交易 | 期货公司免费提供 |
| **月费合计** | **~100 元** |

---

## 12. 运维清单

### 12.1 每日检查

- [ ] 看一眼 `health.json`：status=healthy, session 正确
- [ ] 检查日志最后几行，无异常
- [ ] 确认 Tick CSV 文件在增长
- [ ] 确认 bars.duckdb 在更新

### 12.2 每周检查

- [ ] 磁盘剩余空间
- [ ] PostgreSQL 备份是否成功
- [ ] 策略绩效与预期是否一致
- [ ] 合约规格检查（是否有新品种上市/老品种退市）

### 12.3 每月检查

- [ ] 系统更新 (Windows Update)
- [ ] .NET Runtime 安全更新
- [ ] CTP SDK 版本检查（期货公司是否升级）
- [ ] 清理过期日志（> 30 天）

### 12.4 环境搭建速查

```powershell
# 1. 安装 .NET Runtime
winget install Microsoft.DotNet.Runtime.10

# 2. 安装 PostgreSQL
winget install PostgreSQL.PostgreSQL
# 创建 tradingstudio 库，执行 DDL

# 3. 安装 DuckDB (嵌入式，仅需 NuGet 包，无需安装)

# 4. 发布应用
dotnet publish -c Release -r win-x64 --self-contained -o dist/

# 5. 复制到服务器
# dist/ → D:\TradingStudio\app\
# CTP DLL → D:\TradingStudio\app\

# 6. 配置 appsettings.json
# { "Ctp": { "MdFront": "...", "TdFront": "...", ... } }

# 7. 注册 Windows Service
New-Service -Name "TradingStudio" `
  -BinaryPathName "D:\TradingStudio\app\TradingStudio.exe" `
  -StartupType Automatic

# 8. 启动
Start-Service TradingStudio
```

### 12.5 常见问题

**Q: 为什么不用 Docker？**
Windows + Docker = 额外复杂度。.NET 自带独立发布（self-contained），一个文件夹拷贝过去就能跑。比 Docker 少一层抽象。

**Q: 为什么不用 Linux？**
CTP DLL 是 Windows 原生 DLL。虽然有 CTP Linux SDK，但 Windows 是舒适区。

**Q: 云服务器断网怎么办？**
CTP 行情断了 = 数据缺口。应用层检测断线后自动重连；缺口数据只能在恢复后用前向填充补齐 K 线（Tick 无法回补）。关键：有持仓时断网 → 立即手机告警 → 电话期货公司。

**Q: 需要固定 IP 吗？**
不需要。CTP 是出站 TCP 连接，服务器主动连期货公司前置机。

**Q: PostgreSQL 在哪台机器？**
同一台服务器。本地连接（localhost），零网络开销。备份额外存一份到异地。

---

## 附录 A：待验证的开放问题

这些问题在进入 Phase 3/4 前需要逐一确认：

1. **HistoricalTickFeed 数据源**：直接读金数源 CSV vs 先导入 SQLite？
2. **集合竞价 Tick**：引擎层过滤还是策略层判断？
3. **换月处理**：策略层还是引擎层负责？
4. **StrategyContext.GetBarHistory()** 内存上限策略
5. **多品种并发回放** 性能边界
6. **回测确定性**：同步执行保证两次跑结果一致
7. **CTP TraderApi 限流**：实际下单频率限制是多少？
8. **simnow 与实盘环境差异**：哪些行为 simnow 模拟不了？

---

## 附录 B：关联文档索引

| 文档 | 内容 |
|------|------|
| [phase2-backtest-design-v2.md](phase2-backtest-design-v2.md) | Phase 2 回测系统详细设计 (1873 行) |
| [部署环境指南.md](部署环境指南.md) | 部署环境详细配置 |
| [10-data-model-reconciled.md](10-data-model-reconciled.md) | PostgreSQL DDL + 数据模型 |
| [TradingStudio架构设计-精简版.md](TradingStudio架构设计-精简版.md) | 5 项目精简架构 |
| [11-implementation-roadmap-v2.md](11-implementation-roadmap-v2.md) | 实施路线图 v2 |
| [实际行情数据分析-CU1603.md](实际行情数据分析-CU1603.md) | Tick 数据实测 + 存储量计算 |
| [[phase2-backtest-design]] | 内存：回测设计四条原则 |
| [[phase2-multi-strategy-and-deployment]] | 内存：多策略 + 部署架构 |
| [[database-architecture]] | 内存：DuckDB + PostgreSQL 选型 |
| [[phase2-architecture-critical-phase]] | 内存：当前关键决策期注意事项 |
