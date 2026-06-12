# TradingStudio — 个人量化交易工作室

> 一个人的量化交易工作室。Trading 锁定交易领域，Studio 承载研究、实验与手工打磨的气质。
> 不急不躁，安静地做有分量的事。

---

## 用户背景

- 20年C#工程师，50岁，长期关注股票与期货交易
- 技术舒适区：C# (.NET 8+)
- 定位：个人发烧友，研究与量化并重，工匠工作室而非企业级产品

---

## 个人知识库

路径：`C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading`（Obsidian 管理）

这是本项目的**交易领域知识来源**，所有交易规则、市场认知、研究结论以此为准。

| 目录 | 内容 | 与 TradingStudio 的关系 |
|------|------|------------------------|
| `01-Daily/期货/` | 每日期货交易日志 | 实盘经验输入，策略迭代的反馈来源 |
| `01-Daily/股票/` | 每日股票交易日志 | 股票侧交易认知 |
| `02-Learning/` | 数学/ML/Python/交易系统学习笔记 | 策略研发的理论基础 |
| `03-Strategies/` | 策略设计 | 策略规格输入 |
| `04-Research/00-总纲/` | AI产业链、A股/全球Top分析 | 宏观认知框架 |
| `04-Research/01-产业链分层/` | L01-L09 产业链分析 | 品种基本面研究 |
| `04-Research/02-个股研究/` | 20+ 个股深度研究 | 股票侧研究 |
| `04-Research/04-交易系统/` | 交易纪律、期货交易系统、**六大交易所合约规格表**、自选股系统 | **核心输入**——交易规则和数据规格 |
| `scripts/` | Python 分析脚本（行情、扫描、选股） | 已有工具，可参考或集成 |
| `00-Templates/` | 交易日志模板 | 规范化记录 |

**工作约定：** 当需要交易规则、品种特性、市场认知时，先查知识库再动手。知识库里的交易纪律和规则是系统的"需求文档"。

---

## 核心设计原则（所有决策的锚点）

1. **行情与交易物理分离** — CTP 的 MdApi（行情）和 TraderApi（交易）是两套独立连接，架构必须反映这一点
2. **风控引擎是横切层** — 任何订单在到达 CTP 之前必须过风控，不是事后检查
3. **所有事件必须可回放** — Tick/Order/Trade 全部持久化，这是回测质量的根基
4. **合约规格/保证金/手续费必须数据驱动** — 从第一天起就不能硬编码

---

## 命名空间结构

```
TradingStudio.Core           — 核心抽象、消息总线、公共接口
TradingStudio.Data           — 行情接入、数据存储、K线合成
TradingStudio.Risk            — 风控引擎
TradingStudio.Strategy      — 策略引擎
TradingStudio.Execution    — CTP 执行网关
TradingStudio.Backtest     — 回测引擎
TradingStudio.Mind           — LLM 模块（研究助手、策略解释、异常诊断）
TradingStudio.UI              — 监控与管理界面
```

### 当前实现 (2026-06-12)

```
src/
├── CTP/
│   ├── SDK/               CTP 6.7.13 原生库 (include/lib/dll)
│   └── Wrapper/           C++/CLI 封装 (CTP.Quote, CTP.MdApi, CTP.TraderApi)
├── TradingStudio.Core/    核心模型
│   └── Models/            Exchange, Future, FutureRegistry, TickRecord, Bar, ContractCodeGenerator
├── TradingStudio.Data/    数据聚合 + 存储
│   ├── Aggregation/       BarAggregator, DailyBarAggregator
│   └── Storage/           BarStore (SQLite), TickCsvWriter (金数源格式)
├── TradingStudio.Ctp/     C# 适配层 (CtpMdAdapter: Quote→Channel<TickRecord>)
├── TradingStudio/         主程序 (.NET Host + DI + Serilog)
│   ├── Program.cs         入口（8行）
│   ├── Services/          CollectService, SessionScheduler, HealthMonitor
│   ├── Options/           CollectOptions
│   ├── appsettings.json   Serilog + CTP 连接 + 路径
│   └── symbols.json       品种数据
└── Scripts/               gen_symbols_json.py

test/
├── CtpDemo/               行情 + 交易 Demo
└── CtpBarDemo/            管线 Demo (Quote→Bar→SQLite)
```

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
| CTP 封装 | **C++/CLI 自封装** | `src/CTP/Wrapper/` — MdApi + TraderApi 完整封装 |
| 时序数据 | SQLite (Phase 1) → ClickHouse (Phase 2) | bars_1min + bars_day，后续切 |
| 关系数据 | SQLite (Phase 1) → PostgreSQL (Phase 2) | 品种配置、订单记录 |
| 回测框架 | 自研 | 通用回测框架不适合期货特性 |
| 研究环境 | Python + Jupyter（可选） | pandas/numpy 做策略探索 |
| 前端 | WPF 或 Blazor Server | C# 生态 |

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

### 第一阶段：数据基建 ✅ 已完成 (2026-06-12)

| 功能 | 状态 |
|------|------|
| CTP C++/CLI 封装 (MdApi + TraderApi) | ✅ |
| 全市场 Quote 实时接收 (928 合约) | ✅ |
| Tick CSV 持续化 (金数源 42 列格式) | ✅ |
| 1min Bar + Day Bar 聚合入库 (SQLite) | ✅ |
| 7×24 自动重连 + 健康日志 | ✅ |
| 品种数据实体 + JSON 65 驱动 | ✅ |

### 第二阶段：回测基础（3-4周）
历史数据回放 → 模拟撮合 → 仓位资金管理 → 绩效指标。交付物：结果可信的回测系统。

### 第三阶段：策略研发（4-8周）
趋势跟踪（海龟/均线）→ 均值回归（布林带/RSI）→ 套利 → 组合优化。目标：2-3个正期望值策略雏形。

### 第四阶段：实盘对接（3-4周）
TraderApi 风控规则引擎 → simnow 模拟盘 → 小合约实盘验证。

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

### 合约规格文档更新

`gen_final_specs.py` 从 AKShare 拉取合约规格 → 知识库。`gen_symbols_json.py` 生成 `symbols.json`（品种 + 交易规则，75 个品种）。

```bash
python src/Scripts/gen_final_specs.py    # 拉取合约规格 → 知识库 md
python src/Scripts/gen_symbols_json.py   # 生成品种 JSON → src/TradingStudio/symbols.json
```
