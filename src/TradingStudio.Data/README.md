# Data 层 — 数据管线

## 完整数据流

```
实盘路径 (Live):
  CTP MdApi (原生 C++)
    │ OnRtnDepthMarketData 回调 (~2000 Tick/s, 928 合约)
    ▼
  CtpMdAdapter (C#)
    │ 42字段 CTP Quote → 80B TickRecord 结构体
    ▼
  Channel<TickRecord> (有界 8192, 背压保护)
    │
    ├─→ TickCsvWriter           ← 金数源 42列 CSV 落盘 (按合约/交易日分文件)
    │
    ├─→ BarAggregator           ← 1min Bar 聚合 (按 InstrumentID 分组)
    │     │ OnMinuteChange / 30s超时兜底
    │     ▼
    │   Channel<Bar> → SqliteBarStore / DuckDBStore
    │                     │ CREATE TABLE IF NOT EXISTS bars_{inst}_1min (...)
    │                     │ Channel 缓冲 4096, 异步批量写入
    │                     ▼
    │                   bars_history.duckdb (7.9 GB)
    │
    └─→ DailyBarAggregator      ← 日线 Bar 聚合 (实时更新)
          │ TradingDay 切换时发射上一日 Bar
          ▼
        SqliteBarStore / DuckDBStore → bars_day 表

回测路径 (Backtest):
  bars_history.duckdb / bars_20XX.db
    │
    ▼
  IBarStore.QueryBarsAsync (DuckDB SQL 或 SqliteDataReader)
    │
    ├─ 1min 直接输出
    └─ Nmin: MultiBarAggregator (1min → 5min/15min/30min)
    │
    ▼
  HistoricalBarFeed : IDataFeed
    │ K-way merge 多品种排序
    ▼
  IAsyncEnumerable<EngineEvent> → TradingEngine 主循环
```

## 关键组件

| 组件 | 职责 | 线程安全 |
|------|------|----------|
| `TickCsvWriter` | 金数源 44 列 CSV 落盘 (GBK)，按合约/交易日分文件 | ConcurrentDictionary + Timer 刷新 |
| `BarAggregator` | TickRecord → 1min Bar，30 秒无数据兜底 | ConcurrentDictionary 按品种分组 |
| `DailyBarAggregator` | 实时更新当日 OHLCV，交易日切换时发射 | ConcurrentDictionary |
| `MultiBarAggregator` | 1min → Nmin 合成 (5min/15min/30min) | 单线程，纯函数 |
| `SqliteBarStore` | Bar → SQLite，Channel 异步写入 | Channel + 单消费者线程 |
| `DuckDBStore` | Bar → DuckDB (默认)，支持直接查询 + 多周期 | 读多写少，写入有锁 |
| `BuildPeriodsService` | 1min → 5min/15min/day/week 连续合约 + xxx000 自动生成 | DuckDB SQL，增量 upsert |
| `ContinuousBarStore` | 主力连续合约适配，按仓位权重计算价格 | 读适配器，不写 |
| `HistoricalBarFeed` | 回测数据源，实现 IDataFeed，K-way merge | 单线程流式输出 |
| `HistoricalTickFeed` | Tick 模式回测，CSV 回放 + GB2312 解码 | 单线程流式输出 |

## 数据表

| 表名 | 含义 | 品种覆盖 |
|------|------|----------|
| `bars_1min` | 全合约 1min Bar | 4163 合约 |
| `bars_5min` | 连续合约 5min Bar | 50+ 品种 xxx000 |
| `bars_15min` | 连续合约 15min Bar | 50+ 品种 xxx000 |
| `bars_day` | 全合约日线 Bar | 4163 合约 |
| `bars_week` | 连续合约周线 Bar | 50+ 品种 xxx000 |

## 价格精度

所有价格以 `×10⁷` (long) 存储，避免浮点误差:

```
CTP 报价 3456.0 → TickRecord.Price = 34560000000
Bar.Open/High/Low/Close  = long (×10⁷)
Bar.OpenDouble             = Open / 1e7 = 3456.0
```

TickSize 对齐也在 ×10⁷ 尺度进行，确保撮合价格与交易所一致。
