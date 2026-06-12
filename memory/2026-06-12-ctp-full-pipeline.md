---
name: 2026-06-12-ctp-full-pipeline
description: CTP 全市场实时数据管线完成 — Quote→Tick CSV→1min Bar→Day Bar→SQLite，7x24 自动重连
metadata:
  type: project
---

## 完成内容

### 1. 数据实体模型 (TradingStudio.Core/Models/)

- **ExchangeCode** enum — 六大交易所 (SHFE/INE/DCE/CZCE/CFFEX/GFEX)
- **Future** record — 75 品种 + 交易规则 (tradingUnit/tickSize/marginRate/months)
- **FutureRegistry** — JSON 加载 → Dictionary，O(1) 查询
- **Bar** record struct — OHLCV + tickCount，价格 ×10⁷
- **ContractCodeGenerator** — 从品种 months 模式生成合约代码，支持 CZCE 短码 (SR609) 和标准化 (→SR2609)
- **TickRecord** (已有) — 80B struct + 10 个计算属性

### 2. 聚合层 (TradingStudio.Core/Aggregation/)

- **BarAggregator** — TickRecord → 1min Bar，按 InstrumentID 分组，分钟边界发射，30s 超时兜底
- **DailyBarAggregator** — 实时日线，每分钟覆盖更新 OHLCV

### 3. 存储层 (TradingStudio.Core/Storage/)

- **BarStore** — SQLite 写入，动态区分 bars_1min/bars_day 表，批量 upsert
- **TickCsvWriter** — 金数源 42 列 CSV，按 `{交易所}/{合约}_{交易日}.csv` 组织，6交易所自动归类

### 4. C++/CLI 封装改进

- `CTP.Tick` → `CTP.Quote`（匹配天勤命名）
- `CreateFtdcMdApi(..., true)` TLS 加密连接
- 全字段映射 (42字段 + 五档深度)

### 5. 7×24 管线 Demo (test/CtpBarDemo/)

- 928 合约全市场订阅（12 批次，每批 80 个）
- 指数退避自动重连（1s→2s→...→60s）
- 断连自动重订阅
- 每分钟健康日志
- 异常隔离（单 Quote 失败不崩溃）
- Ctrl+C 优雅退出

### 6. 已修复的关键 Bug

- `HasBar` 标志位未设 true → Bar 不发射
- `BarTime` init-only 无法更新 → 移除冗余赋值
- `lock (struct)` → 改为 lock object
- CFFEX/INE/CZCE 交易所误归类 → 修正 GuessExchange
- CTP `reserve1` (InstrumentID) 字段映射
- `double.MaxValue` 脏数据 → 过滤

### 验证结果

运行 2 分钟：1min bars 1072 条，Day bars 544 条，Tick CSV 544 files (2.4MB)
6 交易所全部正确归类，CZCE 为 0（SimNow 环境不支持）

### 项目结构

```
src/TradingStudio.Core/
  Models/     Exchange, Future, FutureRegistry, TickRecord, Bar, ContractCodeGenerator
  Aggregation/ BarAggregator, DailyBarAggregator
  Storage/    BarStore (SQLite), TickCsvWriter (金数源 CSV)

src/CTP/Wrapper/  C++/CLI (CTP.Quote, CTP.MdApi, CTP.TraderApi)

test/CtpBarDemo/  7x24 全市场管线 Demo
test/CtpDemo/     行情 + 交易 Demo
```
