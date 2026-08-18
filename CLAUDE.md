# TradingStudio — 个人量化交易工作室

> 一个人的量化交易工作室。Trading 锁定交易领域，Studio 承载研究、实验与手工打磨的气质。
> 不急不躁，安静地做有分量的事。

---

## 用户背景

- 20年C#工程师，50岁，长期关注股票与期货交易
- 技术舒适区：C# (.NET 10)
- 定位：个人发烧友，研究与量化并重，工匠工作室而非企业级产品

---

## 个人知识库

路径：`docs/trading/`（git 管理）— 原 Obsidian vault `C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading` 的交易知识主体于 2026-08-18 迁入；vault 现仅保留 `02-Learning/` 学习笔记。

这是本项目的**交易领域知识来源**，所有交易规则、市场认知、研究结论以此为准。

| 目录 | 内容 | 与 TradingStudio 的关系 |
|------|------|------------------------|
| `docs/trading/Notes/期货/` | 每日期货交易日志 | 实盘经验输入，策略迭代的反馈来源 |
| `docs/trading/Notes/股票/` | 每日股票交易日志 | 股票侧交易认知 |
| `docs/trading/交易系统/` | 交易纪律、期货交易系统、**六大交易所合约规格表**、自选股系统 | **核心输入**——交易规则和数据规格 |
| `docs/trading/策略/` | 策略设计 | 策略规格输入 |
| `docs/trading/行业分析/总纲/` | AI产业链、A股/全球Top分析 | 宏观认知框架 |
| `docs/trading/行业分析/产业链分层/` | L01-L09 产业链分析 | 品种基本面研究 |
| `docs/trading/个股研究/` | 20+ 个股深度研究 | 股票侧研究 |
| `docs/trading/品种研究/` | 品种基本面研究 | 品种研究 |
| `docs/trading/因子研究/` | 量化因子研究 | 因子研发输入 |
| `docs/trading/外部资料/` | 外部参考资料 | 参考 |
| `docs/trading/模板/` | 交易日志模板 | 规范化记录 |
| `02-Learning/`（vault） | 数学/ML/Python/交易系统学习笔记 | 策略研发的理论基础 |

**工作约定：** 当需要交易规则、品种特性、市场认知时，先查知识库再动手。知识库里的交易纪律和规则是系统的"需求文档"。

> **2026-08-18 变更**：交易知识库（交易规则/策略/个股/行业分析/品种/因子/模板）已从 Obsidian vault 迁至本仓库 `docs/trading/`，随 git 版本管理；vault 仅保留 `02-Learning/` 学习笔记。日志生成/追加脚本（`scripts/daily/full_generator.py`、`run_chan_daily.py` 等）已改写到 `docs/trading/Notes/`。

---

## 核心设计原则（所有决策的锚点）

1. **行情与交易物理分离** — CTP 的 MdApi（行情）和 TraderApi（交易）是两套独立连接，架构必须反映这一点
2. **风控引擎是横切层** — 任何订单在到达 CTP 之前必须过风控，不是事后检查
3. **所有事件必须可回放** — Tick/Order/Trade 全部持久化，这是回测质量的根基
4. **合约规格/保证金/手续费必须数据驱动** — 从第一天起就不能硬编码

---

## 命名空间结构

```
TradingStudio.Core           — 核心抽象、领域模型、公共接口
TradingStudio.Data           — 行情接入、数据存储、K线合成
TradingStudio.ToolBox        — 数据工具 CLI（导入/导出/验证/转换），独立控制台项目
TradingStudio.Risk            — 风控引擎
TradingStudio.Strategy      — 策略引擎
TradingStudio.Execution    — CTP 执行网关
TradingStudio.Backtest     — 回测引擎
TradingStudio.Mind           — LLM 模块（研究助手、策略解释、异常诊断）
TradingStudio.Terminal              — 监控与管理界面
```

> **实际实现映射（与代码对齐）**：`Risk` / `Execution` / `Backtest` 三层目前统一在 `TradingStudio.Engine` 内，未拆为独立项目；另有 `TradingStudio.Research`（统计/可视化）与 `TradingStudio`（.NET Host 主程序）。CTP 适配在 `TradingStudio/Live/`（CtpLiveFeed / CtpTraderBridge，基于 FtdcNet.CTP NuGet P/Invoke）；C++/CLI 旧版已删除，`TradingStudio.Ctp` 占位项目也已清理。

### 信号管线（信号/仓位解耦，v1 骨架）

```
Strategy.EmitSignal(TradeSignal)  →  SignalChannel  →  ITargetCombiner.Combine  →  Rebalancer.GenerateOrders  →  ExecutionHandler.Submit
        (只发信号, 不含手数)                (有界通道 128)     (冲突消解 + 仓位计算)          (Δ=目标-当前 → 增量订单)          (风控 + 下单)
```

- 设计文档：[18-trade-signal-portfolio-target-decoupling.md](docs/design/18-trade-signal-portfolio-target-decoupling.md)
- **状态**：`TradeSignal`（record + Priority）/ `PortfolioTarget` / `ITargetCombiner` / `SimpleTargetCombiner` / `Rebalancer` / `EmitSignal` / `ProcessSignals` 均已实现；但 `ExecutionHandler.TargetCombiner/.Rebalancer` **尚未接线**（组合根未注入），信号管线当前是死代码。
- **迁移**：现有策略仍走 `MarketBuy/MarketSell/ClosePosition` 直连 `Submit`，0 个策略迁移至 `EmitSignal`。迁移前需先接线 combiner + rebalancer。

### 当前实现 (2026-08-07)

```
src/
├── TradingStudio.Core/    核心模型 + 抽象 (Models, Strategy, Risk, Indicators, Position, Engine)
│   ├── Models/            Exchange, Future, FutureRegistry, TickRecord, Bar, ContractCodeGenerator
│   └── Engine/            TradeSignal (record, 信号), PortfolioTarget (目标), SignalDirection
├── TradingStudio.Data/    数据聚合 + 存储
│   ├── Aggregation/       BarAggregator, DailyBarAggregator, MultiBarAggregator
│   ├── Import/            CsvTickImporter, TickImportService, JinshuyuanImportService
│   └── Storage/           DuckDBStore, SqliteBarStore, TickCsvWriter, BuildPeriodsService
├── TradingStudio.Engine/    回测/实盘引擎 (TradingEngine, ExecutionHandler, PortfolioManager, RiskController, StrategyContainer, StrategyParam\<T\>, ITargetCombiner, SimpleTargetCombiner, Rebalancer)
├── TradingStudio.Strategy/  策略库 (ChanLun 缠论: 分型/笔/中枢, DonchianTrend, SmaMacd, MtfChanLun)
│   └── Engine/Examples/     更多策略 (MaCross, BollingerReversion, IntradayMomentum, CrossSectionalIntradayMom, CompositeFactor, MaCrossMultiTf)
├── TradingStudio.Mind/      LLM 模块 (Anthropic/OpenAI 客户端, BacktestAnalyst, ChanLunAnalyst)
├── TradingStudio.Research/  研究工具 (BarReader, ReturnsAnalyzer, DrawdownAnalyzer, ScottPlot 可视化)
├── TradingStudio.Terminal/  WPF 监控客户端 (MVVM + SignalR 实时, Dashboard/Chart/Replay)
├── TradingStudio.ToolBox/   数据工具 CLI（独立项目，不依赖主程序）
│   └── 命令: import / import-jinshuyuan / import-url / verify / merge / append / build-periods / analyze / continuous / chanlun-analyze / bar-export / mind
├── TradingStudio/           引擎主程序 (.NET Host + DI + Serilog)
│   ├── Program.cs           入口（live / collect / backtest）
│   ├── Live/                CtpLiveFeed (FtdcNet.CTP P/Invoke), CtpTraderBridge (FtdcNet.CTP P/Invoke), ContractActivityTracker, CtpOptions
│   ├── Services/            CollectService, LiveDataCollector, QuotePipeline, EngineHost, SessionScheduler, HealthMonitor, OrderEventPump, OrderPersistenceService, EngineHubPushService
│   ├── Commands/            BacktestCommand
│   ├── Options/             CollectOptions
│   ├── appsettings.json     Serilog + CTP + DuckDB 默认配置
│   └── symbols.json         品种数据
├── scripts/                 daily_import.ps1, data_status.py, gen_symbols_json.py
└── test/
    ├── TradingStudio.Core.Tests/   88 tests — TickRecord, Bar, CsvTickRecord, 技术指标数学, 手续费, 保证金动态调整
    ├── TradingStudio.Data.Tests/   28 tests — BarAggregator, MultiBarAggregator, CsvTickImporter, K线一致性, 垃圾bar过滤
    ├── TradingStudio.Engine.Tests/ 151 tests — 引擎(端到端黄金验证+每日盯市结算+基准回测+Live模式), 风控/撮合(涨跌停/滑点/爆仓/购买力+DailyRiskTracker), 事件持久化, Portfolio
    ├── TradingStudio.Strategy.Tests/ 14 tests — 缠论 包含/分型/笔/中枢
    ├── ChanLunTest/                手动 demo (Program.cs，非自动化，不计入 281)
    └── TradingStudio.SignalRContractTest/  手动 demo (SignalR 连通性，非自动化)
```

### 三种运行模式

| 模式 | 命令 | 默认 DB | 用途 |
|------|------|---------|------|
| **Live** | `dotnet run -- live` | `data/bars_live.duckdb` | 实盘交易+监控+数据落盘，HTTP API :5001 |
| **Collect** | `dotnet run -- collect` | `bars.duckdb` | 纯行情采集，交易时段自动启停，无 HTTP |
| **Backtest** | `dotnet run -- backtest --config x.json` | `data/bars_history.duckdb` | 历史数据回测，默认指向历史库 |

### 数据管线（Live / Collect 共用）

```
CTP 行情 ──→ Tick CSV (GBK/金数源 44 列)  →  data/TickData/
         ──→ bars_1min (全部合约)         →  DuckDB
         ──→ bars_day  (全部合约)         →  DuckDB

PeriodMaintainer (每 5min / 收盘):
         ──→ BuildContinuousContracts     →  生成 xxx000 连续合约
         ──→ bars_5min  (连续合约)        →  DuckDB
         ──→ bars_15min (连续合约)        →  DuckDB
         ──→ bars_week (连续合约)         →  DuckDB
```

### 日终补齐

```powershell
.\scripts\daily_import.ps1 -TickDataDir .\src\TradingStudio\data\TickData
  → 金数源 RAR 下载 + 导入 → append 历史库 → build-periods 多周期 → verify 验证
```

### 历史数据库

`C:\Works\Datas\bars_history.duckdb` — **~28 GB，3.6 亿 Bar** (2026-08-07 实测)，2020-01-02 ~ 2026-06-30，覆盖 80 品种连续合约 + 5363 单月合约，全周期（1min/5min/15min/day/week）。Backtest 模式默认使用。

### 调度逻辑

| 时段 | 北京时间 | 动作 |
|------|---------|------|
| 日盘 | 08:30-15:30 | 自动连接 CTP，采集数据 |
| 收盘 | 15:30 | Flush Bar，断开 CTP |
| 夜盘 | 20:30-03:00 | 自动连接 CTP，采集数据 |
| 收盘 | 03:00 | Flush Bar，断开 CTP |
| 周末/节假日 | 全天 | 休市等待，到期自动恢复 |

### 健康监控

`health.json` 每分钟刷新：status / session / quotes / bars / csv / reconnects / uptime

### 数据命名约定

| 层 | 命名 | 意义 |
|----|------|------|
| CTP 原始 | **Quote** | 行情快照 (42字段 + 五档深度) |
| 精简落盘 | **TickRecord** | 80B 结构体，核心交易字段 |
| K线聚合 | **Bar** | OHLCV, 价格 ×10⁷ |

---

## 技术栈

| 层次 | 技术 | 说明 |
|------|------|------|
| 运行时 | .NET 10 | VS 2026 (v18), x64 |
| CTP 封装 | **FtdcNet.CTP P/Invoke** | NuGet 包 `FtdcNet.CTP 1.4.0`，纯托管调用 `ftdc2c_ctp.dll` 原生桥接 |
| 时序数据 | **DuckDB** | 列存 OLAP，bars_1min/5min/15min/day/week |
| 关系数据 | SQLite (Phase 1) → PostgreSQL (Phase 3) | 品种配置、订单记录 |
| 回测框架 | 自研 | 通用回测框架不适合期货特性 |
| 研究环境 | Python + Jupyter（可选） | pandas/numpy 做策略探索 |
| 前端 | WPF | C# 生态，MVVM + OxyPlot + SignalR ([设计: 14-wpf-monitoring-client-design](docs/design/14-wpf-monitoring-client-design.md)) |

---

## 国内期货市场关键约束（影响系统设计）

- **T+0 双向交易** — 策略不需要隔夜持仓，可以做日内高频
- **杠杆（5-20倍）** — 仓位管理是核心，不是附属功能
- **涨跌停板** — 回测必须模拟极端行情下无法成交的情况
- **CTP 接口** — 唯一的事实标准，C++ API，需要 C# 封装
- **多交易所** — 上期所/大商所/郑商所/中金所/广期所/上能源，合约代码、交易时间、保证金规则各不相同
- **夜盘** — 系统需要接近 7×24 运行
- **保证金动态调整** — 节假日、临近交割月都会调整
- **手续费复杂** — 单边/双边/平今免/平今加倍，回测不扣真实手续费 = 实盘亏损

---

## 总纲文档

> **`02-Learning/从零构建量化交易系统方案.md`** — 本项目的源头文档。所有架构决策、技术选型、分阶段路线均以此为准。遇到方向性问题，先回查这份文档。

---

## 分阶段路线

### 第一阶段：数据基建 ✅ 已完成 (2026-06-16)

| 功能 | 状态 |
|------|------|
| CTP 封装 (C++/CLI → FtdcNet.CTP P/Invoke 迁移完成) | ✅ |
| 全市场 Quote 实时接收 (883 合约, 74 品种) | ✅ |
| Tick CSV 持续化 (金数源 44 列格式, GBK) | ✅ |
| 1min Bar + Day Bar 聚合入库 | ✅ |
| 5min/15min/Week 多周期表 | ✅ PeriodMaintainer 自动维护 |
| 连续合约 (xxx000) 自动生成 | ✅ BuildContinuousContracts |
| 7×24 自动重连 + 健康日志 | ✅ 指数退避 5s→300s |
| 金数源历史数据导入 2020-2026 | ✅ 3.6 亿 Bar (360M), DuckDB ~30 GB |
| 全量数据验证 (6 维度, 0 硬伤) | ✅ 0倒挂/0负价/0重复; ⚠️ Day vs 1min聚合有差异 |
| 三种运行模式: Live / Collect / Backtest | ✅ |
| 每日导入管线: 下载→追加→多周期→验证 | ✅ daily_import.ps1 |

> **2026-07-25 架构升级**：CTP 封装从 C++/CLI 全面迁移至 FtdcNet.CTP P/Invoke。
> `src/CTP/` 目录已删除（Wrapper + SDK），`CtpLiveFeed`/`CtpTraderBridge` 直接调用 `FtdcNet.CTP.dll`。

### 第二阶段：回测基础 ✅ 基本完成
历史数据回放 → 模拟撮合 → 仓位资金管理 → 绩效指标。Engine 151 测试覆盖核心链路。
> 设计文档：[phase2-backtest-design-v2.md](docs/design/phase2-backtest-design-v2.md)
> 数据就绪：DuckDB ~28 GB，3.6 亿 Bar，2020-2026 全周期，0 硬伤
> ⚠️ 待做：与文华/博易 K线交叉验证

### 第三阶段：策略研发（进行中）
趋势跟踪（海龟/均线）→ 均值回归（布林带/RSI）→ 因子挖掘 → 横截面策略。目标：2-3个正期望值策略雏形。
> ✅ MaCross、BollingerReversion、DonchianTrend 已实现
> ✅ **MaCross+ADX 120组合参数扫描** → 全品种亏损~-20%，趋势策略无正期望 (2026-08-04)
> ✅ **因子挖掘 Phase 1** — 16因子×72品种 IC+Quantile+回测 (2026-08-05)
>   - **有效因子**: IntradayMom (IC_IR=+0.96), VWAP_Dev (IC_IR=-2.08)
>   - **传统趋势因子全部失效**: TSMOM, MA偏差, HV 在中国期货上无预测力
>   - **截面优于时序**: 横截面排名才是因子的α来源
>   - **IntradayMom 是同日内因子**: 预测盘中走势(O2C Sharpe 1.79)，非隔夜(C2C Sharpe -0.90)
> ✅ **C#横截面+日内策略**: CrossSectionalIntradayMomStrategy (v2: C# Factor 直连, 无 Python 依赖)
> ✅ **因子管线 C# 化** (2026-08-07): IntradayMomFactor+VwapDevFactor 直连策略, 消除 Python→CSV 中转
> ✅ **StrategyParam\<T\> 全量迁移** (2026-08-07): 6 策略 × 42 参数全部从 [StrategyParameter] attribute 迁移至泛型 StrategyParam\<T\>，编译时类型安全
> ⚠️ IntradayMom执行质量敏感: Python Sharpe 1.16 vs C# 含成本≈0

### 第四阶段：实盘对接
TraderApi 风控规则引擎 → simnow 模拟盘 → 小合约实盘验证。
> ✅ CtpTraderBridge 下单链路（认证→登录→InsertOrder result=0）
> ✅ RiskController + ExecutionHandler Live 模式
> ✅ Live 模式 Tick→Bar→策略→订单 全链路跑通
> ✅ **ATR 止损/止盈触发率统计** — ExitReason全链路 (Trade←Order←Strategy, 2026-08-05)
> ✅ **CtpTraderBridge 自动重连修复** — _pendingReconnect竞态修复 (2026-08-05)
> ✅ **OrderEvent 持久化** — bars_live.duckdb order_events 表写入确认 (5条, 8/4-8/5)
> ✅ **FillChannel 竞态根治** (2026-08-07) — OrderEventPump (借鉴StockSharp回调泵) 单消费者→串行Sink
> ✅ **SHFE/INE PnL 修复** (2026-08-07) — PositionCost 含合约乘数，除以 TradingUnit 后正常
> ✅ **simnow 平今/平昨修复** (2026-08-07) — CTP PositionDate 传递链路修复，CloseToday/CloseYesterday 正确区分
> ✅ **StrategyParam\<T\> 全量迁移** (2026-08-07) — 6 策略 × 42 参数全部迁移，0 旧 [StrategyParameter] 残留
> ✅ **SlippageAtrFactor** — 市价单滑点按 ATR 缩放，替代固定 1 跳
> ⚠️ 待验证: 连续交易日 PnL 正常 + simnow 不再拒单 (需实际跑盘 2-3 天)

---

## 老兵忠告（写入系统基因）

1. **数据质量会杀死策略** — 集合竞价 tick 不真实、夜盘-日盘交易日归属、交割月异常 tick、不同软件 K 线可能不一致。第一个测试：你的 5 分钟 K 线收盘价与文华/博易一致吗？
2. **实盘和回测的差距比想象的大** — 用 2-3 倍回测滑点作为实盘预期滑点
3. **交易系统是工程问题，不是策略问题** — 20年 C# 工程经验是核心优势。一个赚钱的策略 + 不稳定的系统 = 亏钱；普通策略 + 稳健系统 = 至少不会死

---

## 工作约定

- 代码风格以可读性和健壮性优先，不追求"聪明"
- 关键路径（下单、风控、数据写入）必须有错误处理和日志
- 配置项不硬编码，从外部配置读取
- 先跑通再优化，不提前做过度抽象

### 项目依赖关系

```
TradingStudio.ToolBox (exe, 独立 CLI)       TradingStudio (exe, SelfContained)
  ├─ TradingStudio.Data                       ├─ TradingStudio.Data
  ├─ TradingStudio.Core                       ├─ TradingStudio.Core
  └─ (独立，不依赖主程序)                       ├─ TradingStudio.Engine
                                              └─ TradingStudio.Strategy

共享逻辑 (无循环依赖):
  TradingStudio.Data.Storage.BuildPeriodsService  ← 两份 exe 共用
  TradingStudio.Data.Storage.DuckDBStore          ← 两份 exe 共用
```

ToolBox 是独立的控制台工具，TradingStudio 不依赖它。多周期聚合（`BuildPeriodsService`）和连续合约生成（`BuildContinuousContracts`）在 Data 层实现，两边共享。

### Git 分支策略

```
main ← feat/* ← fix/* ← chore/*
```

| 分支类型 | 命名 | 用途 | 生命周期 |
|----------|------|------|----------|
| `main` | — | 唯一主线，功能合并目标 | 永久 |
| `feat/*` | `feat/chanlun-backtest`, `feat/grid-search` | 新功能/策略开发 | 合并后删除 |
| `fix/*` | `fix/sqlite-vuln`, `fix/bar-overlap` | 缺陷修复 | 合并后删除 |
| `chore/*` | `chore/clean-configs` | 工程卫生/重构 | 合并后删除 |

**规则：**
- 从 `main` 创建分支，完成后合并回 `main`
- 不在 `main` 上直接开发大于单 commit 的功能
- 合并前确保测试全绿（当前：281/281 — Core 88 + Data 28 + Engine 151 + Strategy 14）
- 小修复（<20行、单文件、编译器可验证）可直接在 `main` 提交
- 实验性工作（网格搜索、策略探索）产出放在 gitignored 目录（`configs/grid/`、`configs/batch/`）

### 合约规格文档更新

`gen_final_specs.py` 从 AKShare 拉取合约规格 → 知识库。`gen_symbols_json.py` 生成 `symbols.json`（品种 + 交易规则，75 个品种）。

```bash
python src/Scripts/gen_final_specs.py    # 拉取合约规格 → 知识库 md
python src/Scripts/gen_symbols_json.py   # 生成品种 JSON → src/TradingStudio/symbols.json
```
