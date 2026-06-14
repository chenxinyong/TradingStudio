# 11 — 实施路线图 v2：基于当前代码状态的 6 步推进计划

> 融合 docs/08-10 的分析结论，盘点现有代码，定义实施顺序。
>
> **更新 (2026-06-13):** 6/12 完成全市场采集管线部署，6/13 修复 6 个 Bug。
> **更新 (2026-06-14):** 技术指标引擎完成 (SMA/EMA/MACD/RSI/BOLL) → Core.Indicators。
> K线图表 Demo 完成 (OxyPlot + MVVM + 实时模拟) → TradingStudio.UI。
> Phase 2a 引擎组件全部实现 (TradingEngine/ExecutionHandler/PortfolioManager 等)。
> Step 1 (PostgreSQL + 实体) 尚未完成，其余 Step 2-3 的部分组件已超前实现。

---

## 一、当前代码状态盘点

### 1.1 存在且可工作的

| 组件 | 文件 | 状态 |
|---|---|---|
| TickRecord struct | `src/TradingStudio.Core/Models/Tick.cs` | ✅ 编译通过，80B 正确 |
| CTP C++ bridge | `src/CTP/CTPWrapper/CtpMdBridge.cpp` | ✅ 编译通过，12 字段 |
| CTP P/Invoke | `src/TradingStudio.Ctp/CtpInterop.cs` | ✅ 编译通过 |
| CTP Provider | `src/TradingStudio.Ctp/CtpMdProvider.cs` | ✅ 编译通过，单 Channel |
| 合约规格数据 | `docs/contracts/` 76 .md + 100 CSV | ✅ 完整 |
| 手续费/保证金脚本 | `src/gen_final_specs.py` | ✅ 运行正常 |
| 技术指标引擎 | `Core/Indicators/` SMA/EMA/MACD/RSI/BOLL | ✅ 已实现 (2026-06-14) |
| K线图表 Demo | `UI/ViewModels/ChartViewModel.cs` | ✅ OxyPlot + 实时模拟 |
| DI + 日志 + 配置 | `UI/App.xaml.cs` + `appsettings.json` | ✅ Serilog + IConfiguration |
| 部署脚本 | `deploy.bat` → `dist/Server/` + `dist/Desktop/` | ✅ 5 步管线 |

### 1.2 存在但需要修复

| 组件 | 问题 | 优先级 |
|---|---|---|
| CTP bridge 回调 | 缺少 instrumentId + tradingDay 参数 | **P0** |
| CtpMdProvider | 单 Channel，不区分合约 | **P0** |
| TickRecord | 缺少计算属性（doc 02 定义但代码未实现） | **P1** |

### 1.3 不存在（需要在代码中创建）

| 组件 | 文档依据 | 优先级 |
|---|---|---|
| Exchange/Symbol/Contract 实体 | doc 10 DDL | **P0** |
| CommissionRule/MarginRule 实体 | doc 10 DDL | **P1** |
| TradingSession 实体 | doc 10 DDL | **P1** |
| ITickStore 接口 + 实现 | doc 02 | **P1** |
| PostgreSQL 数据库 | doc 10 DDL | **P0** |
| HandyControl UI 组件库 | doc 13 | P2 |
| LiveCharts2 绩效面板 | doc 13 | P2 |
| SignalR 客户端实现 | doc 13 | P2 |

---

## 二、6 步推进计划

### Step 1：建立数据库 + 实体模型（3h）

**目标：** 数据库和 C# 模型可用。

**任务：**
1. 在本地 PostgreSQL 执行 doc 10 的完整 DDL
2. 创建 `TradingStudio.Core/Models/` 下的 C# record：
   - `Exchange.cs`
   - `Symbol.cs`
   - `Contract.cs`
   - `CommissionRule.cs`
   - `MarginRule.cs`
   - `TradingSession.cs`
3. 补全 `Tick.cs` 的计算属性（LastPriceDouble, BidPrice1Double, AskPrice1Double, Spread, IsAuction, IsOpenInstant, ExchangeTime, LocalTime, LatencyMs）
4. 添加种子数据生成：`gen_final_specs.py` 新增 `--sql` 参数输出 SQL INSERT

**验收标准：**
- 76 个品种可以全部 INSERT 到 PostgreSQL 的 symbols 表
- 所有 C# record 编译通过
- TickRecord 计算属性输出正确

### Step 2：修复 CTP Bridge（2h）

**目标：** Bridge 回调传递完整的合约标识信息。

**任务：**
1. 扩展 `CtpOnTickCallback` 签名：添加 `const char* instrumentId` 和 `const char* tradingDay`
2. 在 `CtpMdBridge.cpp` 的回调中传递 `pData->reserve1`（合约代码）和 `pData->TradingDay`
3. 更新 `CtpInterop.cs` 的委托签名
4. 改造 `CtpMdProvider.cs`：支持按合约代码分流（`ConcurrentDictionary<string, Channel<TickRecord>>`）
5. 重新编译 `TradingStudioCtpBridge.dll`

**验收标准：**
- 连接 SimNow，订阅 2 个合约，C# 侧能收到不同 instrumentId 的 tick
- `ExchangeTime` 和 `LocalTime` 时间戳正确

### Step 3：实现存储接口 + 二进制 Tick 存储（4h）

**目标：** `.tick` 文件可写入和读取。

**任务：**
1. 定义接口：
   - `ITickStore`（Append, Flush, Read, Prune）
   - `IBarStore`（Write1Min, Query, Synthesize）
   - `IMarketDataProvider`
2. 实现 `BinaryTickStore : ITickStore`：
   - `.tick` 文件创建/追加
   - Header 写入（symbol code, price scale, first/last timestamp）
   - 批量 flush（500ms 或 1000 tick）
   - Footer CRC32
   - O(1) 随机读取（固定记录长度 80B）
3. 单元测试：写入 1000 tick → flush → 读取验证

**验收标准：**
- 写入 1 万 tick，读取全部，字段一致
- 跨夜盘交易日的文件命名正确
- 磁盘写入吞吐 > 10MB/s

### Step 4：实现 Bar 聚合器 + PostgreSQL Bar 存储（3h）

**目标：** Tick → 1min Bar → PostgreSQL 完整链路。

**任务：**
1. 实现 `BarAggregator`：tick 流 → 1min OHLCV Bar
   - 状态机：accumulating → timed out → emit
   - 30s 超时兜底（无 tick 时仍产生 Bar）
   - 交易日切换检测（TradingDay 变化 = 日盘/夜盘边界）
2. 实现 `PostgresBarStore : IBarStore`：
   - `Write1Min`：批量 upsert（ON CONFLICT DO NOTHING）
   - `Query`：时间范围查询 + 从 1min 合成更大周期
3. 集成测试：从 CSV 导入 tick → Bar 聚合 → PG 查询

**验收标准：**
- 1 天 tick 数据 → 240 个 1min Bar（含夜盘 6h + 日盘 6h）
- Bar 的收盘价与文华/博易一致
- 查询 1min/5min/15min/1h Bar 正确

### Step 5：实现 CSV Tick 导入器（2h）

**目标：** 历史 tick CSV 数据 → `.tick` 文件 + Bar → PostgreSQL。

**任务：**
1. 实现 `CsvTickImporter`：解析金数源/盘口格式 CSV
2. 复用 Step 3-4 的 TickStore + BarStore
3. 支持批量导入（目录遍历）

**验收标准：**
- 导入 1 天样本 CSV，产生正确的 `.tick` 文件 + Bar 数据
- 支持 RAR 压缩包直接导入

### Step 6：MarketDataService 管线集成（2h）

**目标：** CTP 实时行情 → 存储 全链路。

**任务：**
1. 实现 `MarketDataService : BackgroundService`
   - 从 `CtpMdProvider` 的 Channel 读取 tick
   - 按合约路由到对应的 `ITickStore`
   - 每 500ms flush 一次
   - 对接 `BarAggregator` → `IBarStore`
2. `Program.cs`：启动 CtpMdProvider → MarketDataService

**验收标准：**
- 连接 SimNow，运行 30 分钟，产生 `.tick` 文件 + Bar 数据
- 无内存泄漏（长时间运行稳定）

---

## 三、依赖图

```
Step 1 (DB + Models)     Step 2 (Fix CTP Bridge)
       │                        │
       └────────┬───────────────┘
                │
         Step 3 (Tick Store)
                │
         Step 4 (Bar Aggregator + PG Store)
                │
         Step 5 (CSV Importer) ──────┐
                │                     │
         Step 6 (MarketDataService)  ←┘
```

Step 1 和 Step 2 可并行。Step 3 依赖 Step 1-2。Step 5 和 Step 6 可并行。

---

## 四、预计工作量

| Step | 内容 | 时间 | 累积 |
|---|---|---|---|
| 1 | 数据库 + 实体模型 | 3h | 3h |
| 2 | 修复 CTP Bridge | 2h | 5h |
| 3 | Tick 存储 | 4h | 9h |
| 4 | Bar 聚合 + 存储 | 3h | 12h |
| 5 | CSV 导入器 | 2h | 14h |
| 6 | 管线集成 | 2h | **16h** |

---

## 五、技术债务记录

这些不在 Phase 1 计划内，但已知需要后续处理：

1. C++/CLI bridge 迁移（替代 C 函数指针 bridge）
2. 5 档深度支持（TickRecord 扩展）
3. ClickHouse/TDengine 替代 PG 存 tick（tick 量大的时候）
4. 合约数据的增量更新机制（新合约上市时的自动化）
5. 交易接口（TraderApi）的封装

---

*制定日期：2026-06-10*
*基础：docs/08-10 的三份分析文档*
