# TradingStudio

> 个人量化交易工作室 — 从零构建期货量化交易系统，20 年 C# 工程师的 AI 时代手艺活。

[![.NET](https://img.shields.io/badge/.NET-10-blueviolet)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-281%20passed-brightgreen)](test/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)](https://github.com/cxbug/TradingStudio)
[![Phase](https://img.shields.io/badge/phase-4%20实盘对接-orange)](#路线图)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 这是什么

TradingStudio 是一个**从头自研**的国内期货量化交易系统。不使用现成的量化框架——每一行代码都在实践"理解原理，自己实现"。

- **市场**: 国内六大期货交易所（上期所/大商所/郑商所/中金所/广期所/上能源）
- **接口**: CTP 官方原生 C++ API，通过 FtdcNet.CTP (NuGet P/Invoke) 接入
- **数据**: 2020-2026 全市场连续+单月合约 K 线，3.6 亿条 Bar (~28 GB)，0 硬伤验证通过
- **语言**: 90%+ C# (.NET 10)，策略研究用 Python，前端 WPF

> 更多项目理念、用户背景、工作约定见 [CLAUDE.md](CLAUDE.md)

---

## 架构总览

```
┌──── 行情层 ────┐    ┌──── 引擎层 ────┐    ┌──── 应用层 ────┐
│                │    │                │    │                │
│  CTP MdApi ──►│    │  Core/Models   │    │  ToolBox CLI   │
│  (FtdcNet.CTP) │──►│  Data/Storage   │──►│  Terminal(WPF) │
│       │        │    │  Strategy       │    │  Strategy R&D  │
│  CTP TraderApi │    │  Risk           │    │  (Python)      │
│  (FtdcNet.CTP) │    │  Backtest       │    │                │
│       │        │    │  Engine (Live)  │    │                │
│  P/Invoke 桥接  │    │                │    │                │
└────────────────┘    └────────────────┘    └────────────────┘

  行情 & 交易物理分离              引擎 & UI 进程分离
  MdApi ≠ TraderApi              SignalR 实时通信
```

### 核心设计原则

1. **行情与交易物理分离** — CTP MdApi / TraderApi 两套独立连接，架构必须反映
2. **风控引擎是横切层** — 任何订单到 CTP 前过风控，不是事后检查
3. **所有事件可回放** — Tick / Order / Trade 全部持久化，回测质量的根基
4. **合约规格数据驱动** — 保证金、手续费、交易时间不硬编码

架构详解见 [TradingStudio架构设计-精简版](docs/design/TradingStudio架构设计-精简版.md)

---

## 当前状态

| 模块 | 状态 | 说明 |
|------|------|------|
| CTP 封装 | ✅ 完成 | MdApi + TraderApi，FtdcNet.CTP P/Invoke |
| 实时行情采集 | ✅ 完成 | 全市场 74 品种，7×24 自动重连 |
| Bar 聚合入库 | ✅ 完成 | 1min/5min/15min/Day/Week → DuckDB |
| 历史数据验证 | ✅ 完成 | 2020-2026，3.6 亿条 Bar (~28 GB)，0 硬伤 |
| 连续合约 | ✅ 完成 | xxx000 自动生成，PeriodMaintainer 维护 |
| 缠论引擎 | ✅ 完成 | C# 实现：包含/分型/笔/线段/中枢/买卖点 |
| ToolBox CLI | ✅ 完成 | 12 命令：import/import-jinshuyuan/import-url/verify/merge/append/build-periods/analyze/continuous/chanlun-analyze/bar-export/mind |
| 🔥 回测引擎 | ✅ 基本完成 | Phase 2 — 事件驱动，Tick/Bar 级精度，281 测试覆盖 |
| WPF 监控客户端 | ✅ 完成 | Dashboard + Chart + Replay，MVVM + SignalR 实时 |
| 🔥 策略研发 | 进行中 | Phase 3 — 10 策略 (趋势/均值回归/缠论/日内动量/横截面因子)，StrategyParam\<T\> |
| 🔥 实盘对接 | 当前冲刺 | Phase 4 — Simnow Live 全链路跑通，下单/风控/成交/持久化闭环，PnL+平仓修复 |

---

## 快速开始

### 前置要求

- Windows x64, .NET 10 SDK
- Visual Studio 2026 (v18) 或 VS Code
- 建议安装 Python 3.11+ 用于策略研究脚本

### 构建

```powershell
# 构建解决方案（8 项目，FtdcNet.CTP P/Invoke 零原生依赖）
dotnet build src/TradingStudio.slnx

# 或自包含发布构建
./_build.bat .\out

# 运行测试（281 通过）
dotnet test test/TradingStudio.Core.Tests/TradingStudio.Core.Tests.csproj
dotnet test test/TradingStudio.Data.Tests/TradingStudio.Data.Tests.csproj
dotnet test test/TradingStudio.Engine.Tests/TradingStudio.Engine.Tests.csproj
dotnet test test/TradingStudio.Strategy.Tests/TradingStudio.Strategy.Tests.csproj
```

### 发布

```powershell
# 按模式发布（自包含，零依赖）
./release-collect.bat    # 数据采集 + 聚合 + 入库
./release-live.bat       # 实时交易引擎
./release-backtest.bat   # 回测引擎
./release-all.bat        # 全部模式
```

### 运行

```powershell
# 三种运行模式（从 src/TradingStudio/ 目录执行）
dotnet run -- live       # 实盘交易（CTP 行情+交易，Simnow 模拟）
dotnet run -- collect    # 纯行情采集（交易时段自动启停）
dotnet run -- backtest --config strategies/backtest/ma-cross-ag-1h-adx.json

# 发布后运行
./release/live/start.bat        # Live 引擎（HTTP :59661）
./release/collect/start.bat     # 采集引擎
./release/backtest/start-bar.bat strategies/backtest/ma-cross-ag-1h-adx.json

# ToolBox 数据工具
dotnet run --project src/TradingStudio.ToolBox -- verify --db data/bars_history.duckdb
dotnet run --project src/TradingStudio.ToolBox -- import-jinshuyuan --rar-dir D:\期货数据\
```

> 历史数据库 `bars_history.duckdb` (~28 GB, 2020-2026, 3.6 亿 Bar) 位于 `C:\Works\Datas\`，回测时通过 `--db` 参数指定。

---

## 项目结构

```
TradingStudio/
├── CLAUDE.md                  ← 项目完整说明（架构/技术栈/约定/路线）
├── README.md                  ← 本文件（项目名片）
│
├── src/
│   ├── TradingStudio.Core/    核心抽象（Exchange, Future, Bar, TickRecord, Risk, Indicators）
│   ├── TradingStudio.Data/    数据聚合 + 存储（BarAggregator, DuckDBStore）
│   ├── TradingStudio.Engine/  回测/实盘引擎（TradingEngine, ExecutionHandler, PortfolioManager, RiskController）
│   ├── TradingStudio.Strategy/ 策略库（ChanLun 缠论, DonchianTrend, SmaMacd, MtfChanLun + Engine/Examples: MaCross/Bollinger/IntradayMomentum/CrossSectional/CompositeFactor 共 10 策略）
│   ├── TradingStudio.Mind/    LLM 模块（Anthropic/OpenAI 客户端, BacktestAnalyst, ChanLunAnalyst）
│   ├── TradingStudio.Research/ 研究工具（统计指标 + ScottPlot 可视化）
│   ├── TradingStudio.Terminal/ WPF 监控客户端（Dashboard + 实时图表 + 回放）
│   ├── TradingStudio.ToolBox/ 数据工具 CLI（独立控制台，12 命令）
│   └── TradingStudio/         引擎主程序（.NET Host + DI + Serilog + CtpLiveFeed/CtpTraderBridge + 3 种运行模式）
│
├── test/
│   ├── TradingStudio.Core.Tests/    核心模型 + 技术指标 + 手续费 + 保证金动态 + RiskTracker（88 通过）
│   ├── TradingStudio.Data.Tests/    数据聚合 + K线一致性验证（28 通过）
│   ├── TradingStudio.Engine.Tests/  引擎/端到端黄金/每日盯市/基准回测/风控/爆仓/购买力/事件持久化/Live模式（151 通过）
│   ├── TradingStudio.Strategy.Tests/ 缠论 分型/笔/中枢（14 通过）
│   ├── ChanLunTest/                 缠论算法手动 demo（非自动化）
│   └── TradingStudio.SignalRContractTest/ SignalR 连通性 demo（非自动化）
│
├── docs/
│   ├── design/                设计文档（18 篇，架构/数据/UI/部署/策略）
│   ├── trading/               交易知识库（交易规则/策略/个股/行业分析/品种研究/每日日志）
│   └── README.md             文档索引
│
├── configs/                   策略配置 + 批量回测 + 参数扫描
├── scripts/                   运维 + 研究脚本（data 导入·验证、backtest 批量回测、viz 可视化、research 品种研究、chanlun 缠论、daily 日志、specs 合约规格、factor_research 因子）
├── reports/                   品种研判可视化报告（HTML）
├── data/                      本地数据（bars_live.duckdb、Tick CSV；历史库见 C:\Works\Datas\）
├── release/                   发布输出（分 collect/live/backtest 三模式）
├── tools/                     外部工具（ChanAnalysis 缠论 C# 版、UnRAR 解压）
├── deploy/                    部署配置（appsettings.cloud.*）
└── dev-notes/                 开发里程碑笔记（2026-06）
```

---

## 技术栈

| 层 | 技术 | 说明 |
|---|------|------|
| 运行时 | .NET 10 | C#, x64 |
| CTP 接口 | FtdcNet.CTP P/Invoke | NuGet 包 `FtdcNet.CTP 1.4.0` |
| 时序存储 | DuckDB | 列存 OLAP，bars_1min/5min/15min/day/week，3.6 亿 Bar |
| 关系存储 | SQLite（远期 PostgreSQL） | 品种配置、订单记录 |
| 实时通信 | SignalR | 引擎 ↔ WPF 前端 |
| 前端 | WPF | MVVM + OxyPlot，SignalR 实时推送 |
| 策略研究 | Python + pandas/numpy | Jupyter 可选 |
| 日志 | Serilog | 结构化日志 |

---

## 路线图

### Phase 1 — 数据基建 ✅ 已完成
CTP 封装 → 实时采集 → Bar 聚合 → 连续合约 → 历史数据导入 → 全量验证

### Phase 2 — 回测引擎 ✅ 基本完成
Tick 级事件驱动回放 → 模拟撮合 → 仓位资金管理 → 绩效指标 → 281 测试覆盖
> 设计：[phase2-backtest-design-v2.md](docs/design/phase2-backtest-design-v2.md)
> 待做：与文华/博易 K线交叉验证

### Phase 3 — 策略研发 🔥 进行中
10 策略（趋势/均值回归/缠论/日内动量/横截面因子）+ StrategyParam\<T\> 类型安全参数 → 目标 2-3 个正期望策略

### Phase 4 — 实盘对接 🔥 当前冲刺
Simnow Live 全链路跑通：下单/风控/成交/持仓同步/事件持久化，PnL + 平今平昨修复，待连续交易日验证

---

## 老兵忠告

> 这些经验写入了系统基因，每条都有生产事故背书。

1. **数据质量会杀死策略** — 第一个测试：你的 5 分钟 K 线收盘价与文华/博易一致吗？
2. **实盘和回测的差距比想象的大** — 用 2-3 倍回测滑点作为实盘预期
3. **交易系统是工程问题，不是策略问题** — 赚钱策略 + 不稳定系统 = 亏钱；普通策略 + 稳健系统 = 不会死

---

## 相关资源

- [个人交易知识库](docs/trading/) — 交易规则、策略、个股/行业/品种研究、每日日志（原 Obsidian vault，2026-08-18 迁入 git）
- [QuantConnect Lean](https://github.com/QuantConnect/Lean) — 架构设计参考

---

## License

MIT © 2025-2026 陈新勇
