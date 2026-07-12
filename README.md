# TradingStudio

> 个人量化交易工作室 — 从零构建期货量化交易系统，20 年 C# 工程师的 AI 时代手艺活。

[![.NET](https://img.shields.io/badge/.NET-10-blueviolet)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-216%20passed-brightgreen)](test/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)](https://github.com/cxbug/TradingStudio)
[![Phase](https://img.shields.io/badge/phase-2%20回测引擎-orange)](#路线图)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 这是什么

TradingStudio 是一个**从头自研**的国内期货量化交易系统。不使用现成的量化框架——每一行代码都在实践"理解原理，自己实现"。

- **市场**: 国内六大期货交易所（上期所/大商所/郑商所/中金所/广期所/上能源）
- **接口**: CTP 官方原生 C++ API，自封装 C++/CLI → C# 适配层
- **数据**: 2020-2026 连续主力合约 K 线，8100 万条 Bar，0 硬伤验证通过
- **语言**: 90%+ C# (.NET 10)，策略研究用 Python，前端 WPF

> 更多项目理念、用户背景、工作约定见 [CLAUDE.md](CLAUDE.md)

---

## 架构总览

```
┌──── 行情层 ────┐    ┌──── 引擎层 ────┐    ┌──── 应用层 ────┐
│                │    │                │    │                │
│  CTP MdApi ──►│    │  Core/Models   │    │  ToolBox CLI   │
│  (C++ 原生)    │──►│  Data/Storage   │──►│  Terminal(WPF) │
│       │        │    │  Strategy       │    │  Strategy R&D  │
│  C++/CLI       │    │  Risk           │    │  (Python)      │
│  封装层         │    │  Backtest       │    │                │
│       │        │    │  Engine (Live)  │    │                │
│  C# CtpAdapter │    │                │    │                │
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
| CTP 封装 | ✅ 完成 | MdApi + TraderApi，C++/CLI 自封装 |
| 实时行情采集 | ✅ 完成 | 全市场 928 合约，7×24 自动重连 |
| Bar 聚合入库 | ✅ 完成 | 1min + Day Bar → SQLite，健康监控 |
| 历史数据验证 | ✅ 完成 | 2020-2026，8100 万条 Bar，0 硬伤 |
| 缠论引擎 | ✅ 完成 | C# 实现：包含/分型/笔/线段/中枢/买卖点 |
| ToolBox CLI | ✅ 完成 | 9 命令：import/import-jinshuyuan/import-url/verify/merge/append/build-periods/analyze/continuous |
| 🔥 回测引擎 | 开发中 | Phase 2 — 事件驱动，Tick 级精度 |
| WPF 监控客户端 | 设计完成 | Dashboard + Chart + CodeEditor，3 阶段 |
| 策略研发 | 规划中 | MTF+ChanLun 融合策略先行验证 |
| 实盘对接 | 规划中 | Simnow → 小合约实盘 |

---

## 快速开始

### 前置要求

- Windows x64, .NET 10 SDK
- Visual Studio 2026 (v18) 或 VS Code
- CTP 6.7.13 官方库（`src/CTP/SDK/` 已包含）
- 建议安装 Python 3.11+ 用于策略研究脚本

### 构建

```powershell
# 无统一 .sln — 引擎主程序自包含构建（含 C++/CLI Wrapper）
./_build.bat .\out

# 或按需构建单个项目
dotnet build src/TradingStudio/TradingStudio.csproj

# 运行测试（154 通过，需逐项目跑 — 一次传多个 csproj 会被 MSBuild 拒绝）
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
# 启动实时数据采集（日盘 8:30-15:30，夜盘 20:30-03:00）
./release/collect/TradingStudio.exe live

# ToolBox 数据工具
./release/collect/TradingStudio.ToolBox.exe verify --db bars_2025.db
./release/collect/TradingStudio.ToolBox.exe import-jinshuyuan --rar-dir D:\期货数据\
```

---

## 项目结构

```
TradingStudio/
├── CLAUDE.md                  ← 项目完整说明（架构/技术栈/约定/路线）
├── README.md                  ← 本文件（项目名片）
│
├── src/
│   ├── CTP/                   CTP 6.7.13 SDK + C++/CLI 封装
│   │   ├── SDK/               原生 include/lib/dll
│   │   └── Wrapper/           CTP.Quote / CTP.MdApi / CTP.TraderApi
│   ├── TradingStudio.Core/    核心抽象（Exchange, Future, Bar, TickRecord, Risk, Indicators）
│   ├── TradingStudio.Data/    数据聚合 + 存储（BarAggregator, DuckDBStore）
│   ├── TradingStudio.Engine/  回测/实盘引擎（TradingEngine, ExecutionHandler, PortfolioManager, RiskController）
│   ├── TradingStudio.Strategy/ 策略库（ChanLun 分型/笔/中枢, DonchianTrend, SmaMacd）
│   ├── TradingStudio.Mind/    LLM 模块（Anthropic/OpenAI 客户端, BacktestAnalyst, ChanLunAnalyst）
│   ├── TradingStudio.Research/ 研究工具（统计指标 + ScottPlot 可视化）
│   ├── TradingStudio.Terminal/ WPF 监控客户端（Dashboard + 实时图表 + 回放）
│   ├── TradingStudio.ToolBox/ 数据工具 CLI（独立控制台，9 命令）
│   ├── TradingStudio/         引擎主程序（.NET Host + DI + Serilog + Live/CtpLiveFeed/CtpTraderBridge）
│   └── Scripts/               Python 脚本（品种生成、数据导入）
│
├── test/
│   ├── TradingStudio.Core.Tests/    核心模型 + 技术指标 + 手续费（44 通过）
│   ├── TradingStudio.Data.Tests/    数据聚合（16 通过）
│   ├── TradingStudio.Engine.Tests/  引擎/端到端黄金验证/风控/撮合/爆仓/购买力（142 通过）
│   ├── TradingStudio.Strategy.Tests/ 缠论 分型/笔/中枢（14 通过）
│   ├── ChanLunTest/                 缠论算法手动 demo（非自动化）
│   └── TradingStudio.SignalRContractTest/ SignalR 连通性 demo（非自动化）
│
├── docs/
│   ├── design/                设计文档（26 篇，架构/数据/UI/部署）
│   └── README.md             文档索引
│
├── configs/                   配置（网格搜索、批量回测、部署示例）
├── scripts/                   运维脚本（每日数据导入等）
├── data/                      持久化数据（SQLite 库、Tick CSV）
├── release/                   发布输出（分 collect/live/backtest 三模式）
└── memory/                    AI 助手持久化记忆
```

---

## 技术栈

| 层 | 技术 | 说明 |
|---|------|------|
| 运行时 | .NET 10 | C#, x64 |
| CTP 接口 | C++/CLI 自封装 | 不依赖第三方 CTP 封装 |
| 时序存储 | SQLite (Phase 1) → ClickHouse/DuckDB | 8100 万 Bar 验证通过 |
| 关系存储 | SQLite → PostgreSQL (Phase 2b) | 品种配置、订单记录 |
| 实时通信 | SignalR | 引擎 ↔ WPF 前端 |
| 前端 | WPF + OxyPlot | MVVM，Dashboard-first 设计 |
| 策略研究 | Python + pandas/numpy | Jupyter 可选 |
| 日志 | Serilog | 结构化日志 |

---

## 路线图

### Phase 1 — 数据基建 ✅ 已完成
CTP 封装 → 实时采集 → Bar 聚合 → 历史数据导入 → 全量验证

### Phase 2 — 回测引擎 🔥 进行中
Tick 级事件驱动回放 → 模拟撮合 → 仓位资金管理 → 绩效指标
> 设计：[phase2-backtest-design-v2.md](docs/design/phase2-backtest-design-v2.md)

### Phase 3 — 策略研发
趋势跟踪 + 均值回归 + ChanLun → 2-3 个正期望值策略

### Phase 4 — 实盘对接
TraderApi 风控 → Simnow 模拟盘 → 小合约实盘

---

## 老兵忠告

> 这些经验写入了系统基因，每条都有生产事故背书。

1. **数据质量会杀死策略** — 第一个测试：你的 5 分钟 K 线收盘价与文华/博易一致吗？
2. **实盘和回测的差距比想象的大** — 用 2-3 倍回测滑点作为实盘预期
3. **交易系统是工程问题，不是策略问题** — 赚钱策略 + 不稳定系统 = 亏钱；普通策略 + 稳健系统 = 不会死

---

## 相关资源

- [个人交易知识库 (Obsidian)](C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading) — 交易规则、品种研究、每日日志
- [QuantConnect Lean](https://github.com/QuantConnect/Lean) — 架构设计参考

---

## License

MIT © 2025-2026 陈新勇
