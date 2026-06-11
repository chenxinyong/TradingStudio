> ⚠️ **已过时** — 本文档的项目结构（3 项目：Core/Data/Ctp）写于代码重构前，与实际代码不符。请以 [TradingStudio架构设计-精简版.md](TradingStudio架构设计-精简版.md)（5 项目架构）和 [11-implementation-roadmap-v2.md](11-implementation-roadmap-v2.md)（当前状态）为准。

# 数据层架构设计（过时）

> Tick 实时接收 + 历史导入 → 统一的 Bar 产出管道

---

## 1. 项目结构

```
TradingStudio/
├── src/
│   ├── TradingStudio.Core/               # 模型 + 接口（零外部依赖）
│   │   ├── Models/
│   │   │   ├── Tick.cs                   # TickRecord (80B struct)
│   │   │   ├── Bar.cs                    # Bar record
│   │   │   ├── TickFileInfo.cs           # .tick 文件元数据
│   │   │   ├── Symbol.cs                 # 品种规格
│   │   │   ├── Contract.cs               # 具体合约
│   │   │   ├── CommissionRule.cs         # 手续费规则
│   │   │   ├── MarginRule.cs             # 保证金规则
│   │   │   ├── TradingSession.cs         # 交易时段
│   │   │   ├── TradingCalendar.cs        # 交易日历
│   │   │   └── Enums.cs                  # ExchangeCode, BarFrequency, FeeMode, ...
│   │   │
│   │   └── Interfaces/
│   │       ├── ITickStore.cs             # Tick 二进制文件读写
│   │       ├── IBarStore.cs              # Bar PostgreSQL 读写
│   │       ├── IMarketDataProvider.cs    # 行情源抽象
│   │       └── ITickImporter.cs          # 历史数据导入
│   │
│   ├── TradingStudio.Data/               # 数据层实现
│   │   ├── Storage/
│   │   │   ├── BinaryTickStore.cs        # ITickStore 实现
│   │   │   └── PostgresBarStore.cs       # IBarStore 实现
│   │   ├── Aggregation/
│   │   │   └── BarAggregator.cs          # Tick → 1min Bar
│   │   ├── Import/
│   │   │   └── CsvTickImporter.cs        # CSV + RAR → TickRecord 流
│   │   └── Capture/
│   │       └── MarketDataService.cs       # 管道编排
│   │
│   └── TradingStudio.Ctp/                # CTP P/Invoke
│       ├── Interop/
│       │   ├── CtpStructs.cs             # C++ 结构体映射
│       │   └── CtpNative.cs              # DllImport 声明
│       └── CtpMdProvider.cs              # IMarketDataProvider 实现
```

---

## 2. 接口契约

### 2.1 ITickStore — Tick 二进制文件

```csharp
public interface ITickStore
{
    // 追加一条 tick。内部缓冲，500ms 或 50 条 flush 一次
    ValueTask AppendAsync(string symbol, DateOnly tradingDay, TickRecord tick, CancellationToken ct = default);

    // 强制刷盘（收盘/关闭时）
    ValueTask FlushAsync(string symbol, DateOnly tradingDay, CancellationToken ct = default);

    // 流式读取 tick
    IAsyncEnumerable<TickRecord> ReadAsync(string symbol, DateOnly tradingDay,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default);

    // 文件信息
    ValueTask<TickFileInfo?> GetInfoAsync(string symbol, DateOnly tradingDay, CancellationToken ct = default);

    // 列出某 Symbol 的所有日期
    ValueTask<DateOnly[]> ListDatesAsync(string symbol, CancellationToken ct = default);

    // 删除指定日期之前的文件（月度滚动）
    ValueTask<int> PruneAsync(DateOnly before, CancellationToken ct = default);
}
```

### 2.2 IBarStore — Bar PostgreSQL

```csharp
public interface IBarStore
{
    // 批量写入（upsert: ON CONFLICT DO NOTHING）
    ValueTask Write1MinBarsAsync(IReadOnlyList<Bar> bars, CancellationToken ct = default);
    ValueTask WriteDayBarAsync(Bar bar, CancellationToken ct = default);

    // 查询 1min Bar
    IAsyncEnumerable<Bar> Query1MinAsync(string[] symbols, DateTime from, DateTime to, CancellationToken ct = default);

    // 查询日线
    IAsyncEnumerable<Bar> QueryDayAsync(string[] symbols, DateTime from, DateTime to, CancellationToken ct = default);

    // 按需合成 N-min Bar（从 1min 内存合成，不查 PG）
    IAsyncEnumerable<Bar> SynthesizeAsync(string[] symbols, int periodMinutes,
        DateTime from, DateTime to, CancellationToken ct = default);

    // 列出 Symbol
    ValueTask<string[]> ListSymbolsAsync(CancellationToken ct = default);
}
```

### 2.3 IMarketDataProvider — 行情源

```csharp
public interface IMarketDataProvider
{
    ConnectionState State { get; }
    event Action<TickRecord>? OnTick;
    event Action<ConnectionState>? OnConnectionChanged;

    // 生命周期
    ValueTask ConnectAsync(CancellationToken ct = default);
    ValueTask DisconnectAsync(CancellationToken ct = default);

    // 订阅/取消订阅
    ValueTask SubscribeAsync(string[] symbols, CancellationToken ct = default);
    ValueTask UnsubscribeAsync(string[] symbols, CancellationToken ct = default);
}

public enum ConnectionState { Disconnected, Connecting, Connected, LoggedIn, Error }
```

### 2.4 ITickImporter — 历史导入

```csharp
public interface ITickImporter
{
    // 导入单个 CSV 文件（流式，不落盘）
    IAsyncEnumerable<TickRecord> ImportCsvAsync(string filePath, CancellationToken ct = default);

    // 导入 RAR 压缩包（流式解密 → CSV → TickRecord）
    IAsyncEnumerable<TickRecord> ImportRarAsync(string rarPath, string password, CancellationToken ct = default);

    // 批量导入 金数源 2020 目录结构
    // path: ".../FutAC_TickKZ_CTP_Daily_2020/"
    // 返回导入进度
    IAsyncEnumerable<ImportProgress> ImportJsyDirectoryAsync(string path, CancellationToken ct = default);
}

public record ImportProgress
{
    public string Symbol { get; init; }
    public DateOnly TradingDay { get; init; }
    public int TickCount { get; init; }
    public int BarCount { get; init; }
    public string Status { get; init; }  // "OK", "Skipped", "Error"
}
```

---

## 3. 数据管道

### 3.1 实时管道（CTP → Bar）

```
┌─────────────┐
│ CtpMdProvider│  IMarketDataProvider
│ (CTP C++ → C#)│
└──────┬──────┘
       │ OnTick(TickRecord)
       ▼
┌──────────────────┐
│ Channel<TickRecord>│  Bounded(10000)
└──────┬───────────┘
       │
  ┌────┴────────────────────┐
  ▼                         ▼
┌──────────────┐    ┌────────────────┐
│BinaryTickStore│    │ BarAggregator   │
│ .tick 文件     │    │ 内存状态机      │
│ 异步 I/O      │    │ Tick → 1min Bar │
└──────────────┘    └───────┬────────┘
                            │ OnBar(Bar)
                            ▼
                     ┌──────────────┐
                     │PostgresBarStore│
                     │ bars_1min +    │
                     │ bars_day       │
                     └──────────────┘
```

### 3.2 历史导入管道（CSV/RAR → Bar）

```
┌────────────────┐
│ CsvTickImporter │  ITickImporter
│ (CSV/RAR →     │
│  TickRecord)   │
└──────┬─────────┘
       │ IAsyncEnumerable<TickRecord>
       ▼
┌──────────────────┐
│ BarAggregator     │  ← 批量模式
│ (直接批量聚合，    │
│  不经过 Channel)  │
└──────┬───────────┘
       │ 批量 Bar[]
       ▼
┌──────────────┐
│PostgresBarStore│
│ PG COPY 批量写 │
└──────────────┘
```

### 3.3 MarketDataService（编排）

```csharp
public class MarketDataService : BackgroundService
{
    private readonly Channel<TickRecord> _tickChannel;
    private readonly ITickStore _tickStore;
    private readonly IBarStore _barStore;
    private readonly BarAggregator _aggregator;
    private readonly IMarketDataProvider _provider;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 启动消费者
        var tickWriter = Task.Run(() => WriteTicksAsync(ct), ct);
        var barBuilder = Task.Run(() => BuildBarsAsync(ct), ct);

        // 启动生产者（CTP 连接 + 订阅）
        await _provider.ConnectAsync(ct);
        await _provider.SubscribeAsync(_symbols, ct);

        // 等待取消
        await Task.WhenAll(tickWriter, barBuilder);
    }

    // 消费者 1: Tick → .tick 文件
    async Task WriteTicksAsync(CancellationToken ct)
    {
        await foreach (var tick in _tickChannel.Reader.ReadAllAsync(ct))
        {
            await _tickStore.AppendAsync(symbol, tradingDay, tick, ct);
        }
    }

    // 消费者 2: Tick → Bar → PG
    async Task BuildBarsAsync(CancellationToken ct)
    {
        await foreach (var tick in _tickChannel.Reader.ReadAllAsync(ct))
        {
            var bar = _aggregator.OnTick(tick);
            if (bar != null)  // Bar 闭合了
            {
                await _barStore.Write1MinBarsAsync(new[] { bar.Value }, ct);
                OnBarClosed?.Invoke(bar.Value);  // → 策略 / 前端
            }
        }
    }
}
```

---

## 4. 关键组件

### 4.1 BinaryTickStore

```
职责: 管理 .tick 文件的生命周期

内部状态:
  _writers: ConcurrentDictionary<string, TickFileWriter>
    key = "{symbol}_{tradingDay}"
    每个文件一个 writer，线程安全

TickFileWriter:
  - FileStream (append, FileOptions.Asynchronous)
  - 写入缓冲: byte[80 * 50] = 4KB (50 条 tick)
  - 定时器: 500ms flush
  - 自动打开/关闭文件（交易日切换时）

崩溃恢复:
  - 打开文件时检查 Header + Footer CRC32
  - 不一致 → 重建 Footer
```

### 4.2 BarAggregator

```
职责: 将 tick 流实时聚合为 1min Bar

内部状态:
  _bars: ConcurrentDictionary<string, BarBuilder>
    key = "{symbol}"
    每个 Symbol 一个 builder

BarBuilder:
  - currentBar: Bar? (进行中的 Bar)
  - firstVolume, firstTurnover (用于增量计算)
  - tickCount

OnTick(tick) → Bar?:
  1. 获取或创建 BarBuilder
  2. 更新 OHLCV
  3. 如果 tick.Timestamp >= currentBarEnd → 闭合返回 Bar
  4. 否则返回 null

定时器:
  - 每 10 秒检查：currentBarEnd + 30s 已过 → 强制闭合
  - 仅在交易时段内激活
```

### 4.3 CsvTickImporter

```
职责: CSV/RAR → TickRecord 流

CSV 读取:
  1. 检测编码（GBK BOM 或 UTF-8）
  2. CsvHelper 逐行读取
  3. 47 列 → TickRecord 映射
  4. yield return TickRecord（流式）

RAR 读取:
  1. SharpCompress.ArchiveFactory.Open(path, password)
  2. 遍历 entries
  3. entry.OpenEntryStream() → CsvReader
  4. yield return TickRecord

金数源目录批量导入:
  1. 扫描目录结构
  2. For each (year/month/exchange): 列出所有 CSV
  3. 按 (Symbol, TradingDay) 排序
  4. 已存在的跳过（查 PG bars_1min）
  5. 逐文件导入 → 批量生成 Bar → PG COPY
  6. 报告进度
```

---

## 5. 配置

```json
{
  "DataPath": "./data",
  "TickRetentionDays": 30,

  "Tick": {
    "FlushIntervalMs": 500,
    "FlushBatchSize": 50
  },

  "Bar": {
    "WriteBatchMs": 1000,
    "WriteBatchSize": 20,
    "CloseTimeoutSeconds": 30
  },

  "Ctp": {
    "FlowPath": "./data/ctp_flow",
    "MarketFrontAddress": "tcp://180.168.146.187:10131",
    "BrokerId": "",
    "UserId": "",
    "Password": ""
  },

  "Import": {
    "JsyPassword": "www.jinshuyuan.net",
    "JsyBasePath": "C:/baidunetdiskdownload"
  }
}
```

---

## 6. 依赖关系

```
TradingStudio.Core   ← 无外部依赖

TradingStudio.Data   ← Core + Npgsql + Dapper + CsvHelper + SharpCompress

TradingStudio.Ctp    ← Core（仅在 Windows 编译）

TradingStudio.App    ← Core + Data + Ctp + Microsoft.Extensions.Hosting
```

---

## 7. Phase 1 实现顺序

```
Step 1 — Core 模型 (1h)
  □ Tick.cs (TickRecord struct)
  □ Bar.cs
  □ Enums.cs
  □ Symbol.cs, Contract.cs, CommissionRule.cs, MarginRule.cs
  □ 接口: ITickStore, IBarStore, IMarketDataProvider, ITickImporter

Step 2 — Tick 存储 (2h)
  □ TickFileFormat (常量: Magic, Header/Footer 布局)
  □ BinaryTickStore (FileStream + Flush + CRC32)
  □ 单元测试: 写入 10000 tick → 读回验证

Step 3 — Bar 聚合 + 存储 (2h)
  □ BarAggregator (Tick → 1min Bar)
  □ PostgresBarStore (upsert + 查询)
  □ 集成测试: 100K tick → Bar → 验证 OHLCV

Step 4 — CSV 导入 (2h)
  □ CsvTickImporter (CSV + RAR)
  □ 金数源目录批量导入
  □ 测试: 导入 2020 全年数据

Step 5 — CTP 接入 (3h)
  □ CtpStructs + CtpNative
  □ CtpMdProvider
  □ SimNow 验证

Step 6 — App 主机 (2h)
  □ Program.cs + DI
  □ MarketDataService (编排)
  □ 7×24 运行验证
```
