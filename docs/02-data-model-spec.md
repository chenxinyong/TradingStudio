# 数据模型规格：Tick → Bar 完整链路

> 本文档是 TradingStudio 数据层的权威规格。实现代码时以此为准，不做偏离。
> 覆盖：Tick 结构、.tick 二进制格式、Bar 模型、数据管道、完整性保证。

---

## 1. Tick 数据模型

### 1.1 数据来源

CTP MdApi 回调 `OnRtnDepthMarketData`，推送结构体 `CThostFtdcDepthMarketDataField`。

### 1.2 内存模型 `TickRecord`

```csharp
using System.Runtime.InteropServices;

// 紧凑值类型。80 bytes，固定大小。用于 Channel 传输和 .tick 文件存储。
// 字段顺序不可变（与二进制格式一一对应）。
// StructLayout 确保跨平台和跨编译器的内存布局一致性。

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 80)]
public readonly record struct TickRecord
{
    // ---- 时间 (16 bytes) ----
    public long ExchangeTimestamp { get; init; }  // Unix ms, CTP ActionDay+UpdateTime+UpdateMillisec
    public long LocalTimestamp { get; init; }     // Unix ms, 本机收到时间 (latency = Local - Exchange)

    // ---- 成交数据 (24 bytes) ----
    public long LastPrice { get; init; }          // × 10^7 (PriceScale)
    public long Volume { get; init; }             // 累计成交量（手）
    public double Turnover { get; init; }         // 累计成交额（元）
    public double OpenInterest { get; init; }     // 持仓量（手）

    // ---- 买一卖一 (24 bytes) ----
    public long BidPrice1 { get; init; }          // × 10^7
    public int BidVolume1 { get; init; }          // 手
    public long AskPrice1 { get; init; }          // × 10^7
    public int AskVolume1 { get; init; }          // 手

    // ---- 标记 (4 bytes) ----
    public int Flags { get; init; }               // Bit 0=涨停, Bit 1=跌停, Bit 2=集合竞价, Bit 3=开盘瞬时

    // ---- 常量 ----
    public const long PriceScale = 10_000_000;    // 价格精度：7 位小数
    public const int RecordSize = 80;             // 每条记录固定字节数

    // ---- Flags 位定义 ----
    public const int FLAG_UPPER_LIMIT = 1 << 0;   // 涨停价触及
    public const int FLAG_LOWER_LIMIT = 1 << 1;   // 跌停价触及
    public const int FLAG_AUCTION     = 1 << 2;   // 集合竞价时段 (08:55-08:59, 20:55-20:59)
    public const int FLAG_OPEN_INSTANT = 1 << 3;  // 开盘瞬间 (前 3 秒, 价格可能剧烈波动)

    // ---- 计算属性 ----
    public DateTime ExchangeTime => DateTime.UnixEpoch.AddMilliseconds(ExchangeTimestamp);
    public DateTime LocalTime => DateTime.UnixEpoch.AddMilliseconds(LocalTimestamp);
    public double LatencyMs => LocalTimestamp - ExchangeTimestamp;  // 延迟（> 0 = 正常，< 0 = 时钟偏差）
    public double LastPriceDouble => LastPrice / (double)PriceScale;
    public double BidPrice1Double => BidPrice1 / (double)PriceScale;
    public double AskPrice1Double => AskPrice1 / (double)PriceScale;
    public double Spread => (AskPrice1 - BidPrice1) / (double)PriceScale;
    public bool IsUpperLimit => (Flags & FLAG_UPPER_LIMIT) != 0;
    public bool IsLowerLimit => (Flags & FLAG_LOWER_LIMIT) != 0;
    public bool IsAuction => (Flags & FLAG_AUCTION) != 0;
    public bool IsOpenInstant => (Flags & FLAG_OPEN_INSTANT) != 0;
}
```

### 1.3 CTP → TickRecord 转换规则

```csharp
// CTP CThostFtdcDepthMarketDataField → TickRecord

TickRecord Map(CThostFtdcDepthMarketDataField f, string symbol, DateTime tradingDay)
{
    // 时间：ActionDay + UpdateTime + UpdateMillisec → ExchangeTimestamp
    //   ActionDay = "20260609"
    //   UpdateTime = "14:55:03"
    //   UpdateMillisec = 500
    //   → DateTime(2026,06,09,14,55,03,500) → Unix ms

    // 价格：decimal/double → long (× 10^7)
    //   3522.00 → 35_220_000_000

    // Volume/OpenInterest：CTP 字段是 int，直接转 long

    // Flags：
    //   涨停 if LastPrice >= UpperLimitPrice
    //   跌停 if LastPrice <= LowerLimitPrice
    //   集合竞价 if UpdateTime 在集合竞价时段
}
```

---

## 2. .tick 二进制文件格式

### 2.1 设计约束

```
- 固定记录长度 → O(1) 随机访问（record_index * RecordSize + HeaderSize）
- 崩溃可恢复 → Header + Footer 双重校验
- 零外部依赖 → 纯 FileStream 操作
- 单文件单合约单日 → 删除 = 删文件
```

### 2.2 文件命名

```
{tradingDay:yyyyMMdd}_{symbol}.tick

示例:
  20260609_rb2510.tick        ← 2026-06-09 交易日，rb2510 合约
  20260609_IF2606.tick
  20260610_rb2510.tick        ← 夜盘 tick 归属于次日（交易日）

注意：
  - 用 TradingDay（交易日），不是自然日！
  - 周一 21:00 的夜盘 tick → TradingDay = 周二 → 文件 = 周二.tick
```

### 2.3 文件存储路径

```
{DataPath}/ticks/{symbol}/{tradingDay:yyyyMMdd}_{symbol}.tick

示例:
  ./data/ticks/rb2510/20260609_rb2510.tick
  ./data/ticks/IF2606/20260609_IF2606.tick
```

### 2.4 二进制布局

```
┌─────────────────────────────────────────────────────────────┐
│ Header (128 bytes)                                          │
├─────────────────────────────────────────────────────────────┤
│ Offset │ Size  │ Field            │ Type    │ Value         │
├────────┼───────┼──────────────────┼─────────┼───────────────┤
│ 0      │ 4     │ Magic            │ byte[4] │ "FQTK"        │
│ 4      │ 2     │ Version          │ ushort  │ 1             │
│ 6      │ 2     │ Flags            │ ushort  │ 0             │
│ 8      │ 2     │ RecordSize       │ ushort  │ 80            │
│ 10     │ 1     │ Compression      │ byte    │ 0 = None      │
│ 11     │ 5     │ Reserved         │ byte[5] │ 0             │
│ 16     │ 8     │ CreatedAt        │ long    │ Unix ms       │
│ 24     │ 20    │ Symbol           │ byte[20]│ UTF-8, \0 pad │
│ 44     │ 4     │ TradingDay       │ int     │ days since    │
│        │       │                  │         │ 2000-01-01    │
│ 48     │ 4     │ RecordCount      │ int     │ 初始 0        │
│ 52     │ 8     │ FirstTimestamp   │ long    │ 初始 0        │
│ 60     │ 8     │ LastTimestamp    │ long    │ 初始 0        │
│ 68     │ 4     │ Checksum         │ int     │ Header CRC32  │
│ 72     │ 56    │ Reserved         │ byte[56]│ 0             │
├────────┴───────┴──────────────────┴─────────┴───────────────┤
│ Body: N × RecordSize (80) bytes                              │
├─────────────────────────────────────────────────────────────┤
│ Footer (32 bytes)                                            │
├────────┬───────┬──────────────────┬─────────┬───────────────┤
│ 0      │ 4     │ RecordCount      │ int     │ 最终计数       │
│ 4      │ 4     │ CRC32            │ int     │ Body CRC32    │
│ 8      │ 8     │ FirstTimestamp   │ long    │ 最早 tick 时间 │
│ 16     │ 8     │ LastTimestamp    │ long    │ 最晚 tick 时间 │
│ 24     │ 8     │ Reserved         │ byte[8] │ 0             │
└────────┴───────┴──────────────────┴─────────┴───────────────┘

总大小: 128 + N×80 + 32
```

### 2.5 读写操作

```csharp
interface ITickStore
{
    // --- 写入 ---
    
    // 追加一条 tick。内部缓冲，BatchSize 条或 500ms 刷一次盘。
    ValueTask AppendAsync(string symbol, DateOnly tradingDay, TickRecord tick, CancellationToken ct);

    // 强制刷盘（收盘时调用）
    ValueTask FlushAsync(string symbol, DateOnly tradingDay, CancellationToken ct);

    // --- 读取 ---

    // 按时间范围流式读取
    IAsyncEnumerable<TickRecord> ReadAsync(
        string symbol, DateOnly tradingDay,
        DateTime? from = null, DateTime? to = null,
        CancellationToken ct = default);

    // --- 管理 ---

    // 获取文件信息（不打开文件）
    ValueTask<TickFileInfo?> GetInfoAsync(string symbol, DateOnly tradingDay, CancellationToken ct);

    // 列出某 Symbol 所有可用日期
    ValueTask<DateOnly[]> ListDatesAsync(string symbol, CancellationToken ct);

    // 删除 tradingDay 之前的 .tick 文件
    ValueTask<int> PruneAsync(DateOnly before, CancellationToken ct);
}
```

### 2.6 崩溃恢复

```
文件打开时自动检测并修复：

  1. 文件不存在 → 新建，写 Header
  2. 文件存在，大小 < 128 → 损坏，删除，新建
  3. 文件存在，大小 >= 128:
     a. 读 Header → 校验 Magic + Version + Header CRC32
        → 不通过 → 文件损坏，标记，报警
     b. 读 Footer → 校验 Footer CRC32
        → 通过 → 文件完整，正常打开
        → 不通过 → 重建:
           - 实际 RecordCount = (FileSize - 128 - 32) / 80
           - 扫描所有 Record 的 Timestamp → 填 First/Last
           - 重算 Body CRC32 → 写 Footer
           - 日志记录恢复事件
  4. 最坏情况：正在写入的最后一条 record 不完整
     → 实际 RecordCount 向下取整，丢弃不完整的最后一条
     → 数据损失 ≤ 1 tick

恢复时间: 10 MB 文件 ≈ < 10ms（纯顺序读）
```

---

## 3. Bar 数据模型

### 3.1 原子 Bar = 1 分钟

```
1min Bar 是唯一的存储粒度。所有更高周期的 Bar 都从 1min 合成。

存储:
  1min Bar → bars_1min 表 (PostgreSQL) → 永久
  Day Bar  → bars_day 表 (PostgreSQL)  → 永久（从 1min 合成后显式存储）

不存储:
  5min / 15min / 30min / 60min → 查询时从 1min 按需合成
```

### 3.2 Bar 模型

```csharp
public sealed record Bar
{
    public required string Symbol { get; init; }
    public required ExchangeCode Exchange { get; init; }
    public required DateTime Timestamp { get; init; }    // Bar 起始时间（如 09:00:00）
    public required DateTime TradingDay { get; init; }    // 交易日

    // OHLCV — 使用 decimal 保证精度
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long Volume { get; init; }         // 增量（非累计）
    public decimal Turnover { get; init; }    // 增量（非累计）
    public long OpenInterest { get; init; }   // 快照（Bar 闭合时的持仓量）

    public int TickCount { get; init; }       // 组成此 Bar 的 tick 数量

    // 计算属性
    public bool IsBullish => Close >= Open;
    public decimal Range => High - Low;
    public decimal Body => Math.Abs(Close - Open);
    public decimal? Vwap => Volume > 0 ? Turnover / Volume : null;
}
```

### 3.3 1min → 更高周期合成规则

```
给定 N 个连续的 1min Bar: [B1, B2, ..., BN]

合成一个 N-min Bar:
  Open        = B1.Open
  High        = MAX(B1.High ... BN.High)
  Low         = MIN(B1.Low ... BN.Low)
  Close       = BN.Close
  Volume      = SUM(B1.Volume ... BN.Volume)
  Turnover    = SUM(B1.Turnover ... BN.Turnover)
  OpenInterest = BN.OpenInterest       (最后一个的快照)
  TickCount   = SUM(B1.TickCount ... BN.TickCount)
  Timestamp   = B1.Timestamp
  TradingDay  = B1.TradingDay

合成日线:
  日盘 + 夜盘（如果该品种有夜盘）的 1min Bar 全部合并
  TradingDay 统一为当前交易日
```

### 3.4 Bar 接口

```csharp
interface IBarStore
{
    // 批量写入（upsert — 同 (Symbol, Timestamp) 不可重复）
    ValueTask WriteAsync(IReadOnlyList<Bar> bars, CancellationToken ct);

    // 查询 1min Bar
    IAsyncEnumerable<Bar> Query1MinAsync(
        string[] symbols, DateTime from, DateTime to, CancellationToken ct);

    // 查询日线 Bar
    IAsyncEnumerable<Bar> QueryDayAsync(
        string[] symbols, DateTime from, DateTime to, CancellationToken ct);

    // 按需合成更高周期（不查 DB，从 1min 内存/缓存合成）
    IAsyncEnumerable<Bar> SynthesizeAsync(
        string[] symbols, int periodMinutes, DateTime from, DateTime to, CancellationToken ct);

    // 列出所有有 Bar 数据的 Symbol
    ValueTask<string[]> ListSymbolsAsync(CancellationToken ct);
}
```

---

## 4. 数据管道

### 4.1 管道架构

```
CTP MdApi (C++ 回调)
    │
    ▼
CtpMdProvider : IMarketDataProvider
    │  CTP 结构体 → TickRecord 转换
    │  TradingDay 归属判断
    │
    ▼
Channel<TickRecord>  (Bounded, Capacity=10000)
    │
    ├──→ TickStore.AppendAsync()      .tick 文件（异步，不阻塞）
    │
    ├──→ BarAggregator.OnTick()       1min Bar（内存，实时）
    │       │
    │       ├──→ BarStore.WriteAsync()  PG（异步，不阻塞）
    │       ├──→ 内存缓存追加           环形缓冲区
    │       └──→ StrategyManager       策略 OnBar（未来）
    │
    └──→ SignalR Hub                   前端实时推送
```

### 4.2 Channel 配置

```csharp
var tickChannel = Channel.CreateBounded<TickRecord>(
    new BoundedChannelOptions(capacity: 10000)
    {
        FullMode = BoundedChannelFullMode.Wait,  // 背压保护：满了等待
        SingleWriter = false,                     // CTP 可能多线程回调
        SingleReader = false                      // 多个消费者
    });
```

### 4.3 Bar 聚合算法

```
每个 Symbol 维护一个进行中的 1min Bar:

  OnTick(tick):
    if (currentBar == null):
      currentBar = new Bar { Open=tick.LastPrice, Timestamp=AlignToMinute(tick.ExchangeTime) }
    
    currentBar.High   = MAX(currentBar.High, tick.LastPrice)
    currentBar.Low    = MIN(currentBar.Low, tick.LastPrice)
    currentBar.Close  = tick.LastPrice
    
    // ⚠️ Volume/Turnover 是累计值 → 计算增量
    if (currentBar.FirstVolume == 0):
      currentBar.FirstVolume  = tick.Volume
      currentBar.FirstTurnover = tick.Turnover
    else:
      currentBar.Volume   = tick.Volume - currentBar.FirstVolume    // 增量
      currentBar.Turnover = tick.Turnover - currentBar.FirstTurnover
    
    currentBar.OpenInterest = tick.OpenInterest   // 快照
    currentBar.TickCount++

⚠️ 重要：如果 tick.Volume < currentBar.FirstVolume（新交易日开始累计归零）：
    → currentBar.FirstVolume = tick.Volume（重置基准，正常现象）
    → 或者 CTP 在跨日后累计归零，此时增量 = tick.Volume（第一条 tick 的 Volume = 增量）

Bar 闭合后：
    Bar.Volume   = Delta(Volume)   // 该分钟内的成交手数
    Bar.Turnover = Delta(Turnover) // 该分钟内的成交额
    → 存入 PG 时这些都是增量值
```

### 4.4 Bar 闭合触发

```
两种闭合触发条件：

1. Tick 驱动闭合（正常路径）：
   收到 tick.ExchangeTimestamp >= currentBarEndTime → 闭合当前 Bar，开始新 Bar

2. 定时器兜底闭合（低流动性/休盘）：
   每 10 秒检查一次：
   ⚠️ 仅在"激活态"（交易时段内）启动定时器检查
   ⚠️ "待命态"（非交易时段）→ 不启动定时器 → 不生成空 Bar
   - 如果 currentBarEndTime 已过 30 秒且当前 Bar 未闭合 → 强制闭合（TickCount=0, Volume=0）

交易时段边界处理：
   - 遇到 TradingSession 定义的时段结束时间 → 强制闭合
   - 下个时段开始时重新开始 Bar
   - 午休前后(11:30→13:30)、日盘/夜盘之间(15:00→21:00) gap 自然断开
   - 结算时段(15:00-16:00)系统处于待命态 → 不触发闭合

Bar 时间戳规范：
   - Timestamp = Bar 起始时间（如 09:00:00）
   - TradingDay = 该 Bar 所属交易日（夜盘 Bar 归属于次日）
```

### 4.5 Bar 产生与存储的精确时序

```
以 rb2510 的 09:00 Bar 为例：

  09:00:00.000  ← Bar 起始时间（理论）
  09:00:00.123  ← 第一个 tick 到达 → BarAggregator 创建 09:00 Bar
                   Open = 3520, High = 3520, Low = 3520, Close = 3520
  09:00:00.456  ← tick → High = 3522, Volume 更新
  09:00:00.789  ← tick → Close = 3522
  ... 持续接收 tick ...
  09:00:59.891  ← tick → Close = 3524, TickCount = 47

  ─── 09:01:00 ─── Bar 边界 ───

  09:01:00.234  ← 第一个 ≥ 09:01:00 的 tick 到达
                  │
                  ├─→ ① 闭合 09:00 Bar（内存）
                  │     Bar { O=3520, H=3525, L=3518, C=3524, V=156, TickCount=47 }
                  │
                  ├─→ ② 推入内存缓存（环形缓冲区，策略立即可读）
                  │     延迟: < 1μs（指针操作）
                  │
                  ├─→ ③ StrategyManager.OnBar(bar)（策略信号评估）
                  │     延迟: < 1ms（策略计算）
                  │
                  ├─→ ④ PostgresBarStore.WriteAsync(bar)
                  │     见 §4.7 "Bar 写入 PG 的时机"
                  │
                  ├─→ ⑤ SignalR Hub 推送前端（实时 K 线图更新）
                  │     延迟: ~10ms
                  │
                  └─→ ⑥ 创建新的 09:01 Bar（开始新一轮累积）
                        Open = 3524（以上一个 tick 的 Close 为起始参考）
```

### 4.7 Bar 写入 PG 的时机

```
策略不直接从 PG 读数据。PG 只承担持久化角色。因此写入时机不需要"实时"。

写入策略：逐条异步 + 微批量

  每条 Bar 闭合后：
    1. await PostgresBarStore.WriteAsync(bar)
    2. → 内部缓冲（Channel<Bar>, 容量 1000）
    3. → 后台 Writer 每 1 秒或累积 20 条时 flush 一次

  实际写入 PG 的延迟：
    - 正常情况：Bar 闭合后 0.1-1 秒写入 PG
    - 批量 flush 减少了 PG 连接开销
    - 如果进程崩溃，最多丢失最后 1 秒的 Bar（~2 条）

  为什么不是每条立即写 PG？
    - 80 品种 × 1 bar/min = 1.3 条/秒 → 逐条写完全可行
    - 但 80 条在整分钟交界同时闭合 → 瞬间 80 条 INSERT
    - 微批量合并这 80 条为 1 个事务 → 更高效
    - 延迟 1 秒对持久化无影响（策略不读 PG）

  为什么不是收盘后才写？
    - 如果进程白天崩溃，全天的 Bar 丢失
    - 每 1 秒 flush → 最多丢失 1 秒数据
    - 这是持久化延迟 vs 数据安全的最优平衡

特殊情况 — 强制立即 flush：
  - 收盘/时段结束时：立即 flush 缓冲区
  - 系统关闭时：立即 flush
  - 策略触发信号时：不强制 flush（信号评估读内存，不需要 PG）
```

```
PG 写入时间线：

  09:01:00.234  Bar 闭合
  09:01:00.235  WriteAsync(bar) → 放入 buffer
  09:01:01.000  定时器触发 → flush buffer → 批量 INSERT
  09:01:01.050  PG 确认写入 → Bar 被持久化

  如果 09:01:00.500 进程崩溃：
  → buffer 中的 Bar 丢失（1 条）
  → 重启后从 .tick 文件重新聚合这段时间的 Bar → 恢复
```

### 4.8 Tick 写入 .tick 文件的时机

```
Tick 实时接收 → Channel → TickStore.AppendAsync()
                                │
                                ▼
                          FileStream 缓冲区
                          （内存，Write() 写入，瞬时返回）
                                │
                          每 500ms / 50 条 tick
                          FileStream.Flush()
                                │
                                ▼
                          OS 文件系统缓存
                          （位于 OS 内存，进程崩溃数据不丢）
                                │
                          时段结束时 FlushAsync()
                          （强制 fsync → 物理磁盘）
                                │
                                ▼
                          物理磁盘

三层缓冲：

  层 1: FileStream buffer (.NET 进程内存)
    → 每条 tick 写入 < 1μs
    → 进程崩溃 → 丢失（≤ 50 条 tick, 最多 500ms 数据）

  层 2: OS 文件系统缓存 (OS 内核内存)
    → Flush() 推入，延迟 ~100μs
    → 进程崩溃 → 不丢（OS 缓存还在）
    → 断电 → 丢失

  层 3: 物理磁盘
    → fsync (FlushAsync) 推入，延迟 ~1-5ms
    → 进程崩溃/断电 → 都不丢
    → 仅在时段结束时调用（1-3 次/天）
```

```
为什么不是每条 tick 都 fsync？

  100 tick/s × ~1ms fsync = 100ms/s 的 I/O 阻塞
  → 这会成为整个管道的瓶颈
  → 且对 SSD 有磨损

为什么 500ms flush（层 2）就够了？

  1. tick 的主要用途是实时 Bar 聚合 ← 在内存中完成，不依赖 .tick 文件
  2. .tick 文件的用途：
     a. Bar 丢失时恢复 ← 500ms 窗口内的数据损失可接受
     b. 执行质量分析（30 天内） ← 少量 tick 丢失无影响
  3. 如果进程崩溃：
     → 丢失 ≤ 500ms 的 tick（约 50 条）
     → 重启后扫描 .tick 文件，找到最后一条成功写入的 tick
     → BarAggregator 从该点重新聚合 → 最多丢失 1 个 1min Bar
     → 下一分钟恢复正常

极端情况——整点 80 个 Bar 同时闭合 + tick flush 同时触发：
  → 整分钟时刻（如 09:01:00）：80 个 Bar 闭合 + tick flush
  → Bar 写入 PG（异步，不阻塞）
  → tick flush 到 OS 缓存（每 500ms 自然触发，不加剧）
  → 无争抢。各走各的异步路径。
```

```
Tick 写入的完整时间线（以 rb2510 为例）：

  09:00:00.123  tick 1  → FileStream.Write(80 bytes)   < 1μs   (层1, 内存)
  09:00:00.456  tick 2  → FileStream.Write(80 bytes)   < 1μs
  09:00:00.789  tick 3  → FileStream.Write(80 bytes)   < 1μs
  ...
  09:00:00.500  定时器  → FileStream.Flush()           ~100μs  (层1→层2, OS缓存)
                         → 前 0.5 秒的 tick 现在 OS 层安全了
  ...
  09:00:01.000  定时器  → FileStream.Flush()
  ...
  15:00:00.000  日盘收盘 → FlushAsync()                 ~5ms    (层2→层3, 物理磁盘)
                         → 写入 Footer + CRC32
                         → .tick 文件完整关闭

  如果 09:00:00.700 进程崩溃：
    09:00:00.000-500 的 tick → 已 flush 到 OS 缓存 → 安全 ✅
    09:00:00.500-700 的 tick → 在 FileStream buffer → 丢失 ❌
    → 约 200ms / ~20 条 tick 丢失
    → 重启后 .tick 文件自动修复（Footer 重建）
    → Bar 从最后成功的 tick 重新聚合 → 该分钟 Bar 可能 TickCount 偏低
    → 下一分钟完整恢复
```

**特殊 Bar 的闭合时序**：

```
第一个 Bar（开盘 Bar）:
  09:00:00.123  ← 第一个 tick → 创建 09:00 Bar
  → Timestamp = 09:00:00（对齐到分钟边界）
  → 即使第一个 tick 在 09:00:01 到达，Bar.Timestamp 仍是 09:00:00

最后一个 Bar（收盘 Bar）:
  14:59:59.789  ← 最后一个 tick（仍然在 14:59 分钟内）
  
  无 tick 到达 ≥ 15:00:00（市场已关闭）
  
  ─── 15:00:30 ─── 30 秒超时触发 ───
  → 强制闭合 14:59 Bar（TickCount 可能较少）
  
  ⚠️ 或：15:00:00.xxx 交易所发送收盘快照 tick
  → 如果这个 tick 的 Timestamp ≥ 15:00:00
  → 触发 Bar 闭合 + 短暂创建 15:00 Bar
  → TradingSession 检测时段结束 → 强制闭合 15:00 Bar

午休边界 Bar (11:30):
  11:30:00 ← TradingSession 定义的上午结束时间
  
  → BarAggregator 在 11:30:00 收到 tick 时：
    a. 如果是 ≥ 11:30:00 的 tick → 闭合 11:29 Bar
  
  → 11:30-13:30 无 tick（待命态，定时器不触发）
  → 13:30:00 第一个下午 tick → 创建 13:30 Bar（跳过 11:30, 11:31...13:29）
  
  → bars_1min 表中：09:00-11:29 连续，下一个是 13:30
  → gap 是正常的（代表午休），数据质量检查标记但不算错误

夜盘开盘 Bar (21:00):
  20:59 tick（集合竞价）→ FLAG_AUCTION 标记
  21:00:00.xxx 第一个正式 tick → 创建 21:00 Bar
  → TradingDay = 次日（如周一 21:00 → TradingDay = 周二）
```

### 4.6 日线合成时机

```
不同品种的最后一个交易时段不同，不能统一在某个时间合成：

  IF (CFFEX):  15:00 日盘收盘 →  15:05 合成日线
  rb (SHFE):   23:00 夜盘收盘 →  23:05 合成日线
  au (SHFE):   02:30 夜盘收盘 →  02:35 合成日线（次日凌晨）

合成逻辑：
  1. 品种的交易时段结束后 5 分钟触发合成
  2. 等待该品种当日所有 1min Bar 写入 PG 完毕
  3. 从 PG 读取当日全部 1min Bar（日盘 + 夜盘，以 TradingDay 过滤）
  4. 合并为一条日线 Bar → INSERT INTO bars_day
  5. 日线 Bar 的 TradingDay = 当日

示例：au2612 的 2026-06-09 日线
  日盘 09:00-15:00 (TradingDay=2026-06-09) → 375 个 1min Bar
  夜盘 21:00-02:30 (TradingDay=2026-06-09) → 330 个 1min Bar
  → 02:35 合成日线 = 合并 375+330 个 Bar
  → bars_day: { Symbol="au2612", Timestamp=2026-06-09, O=..., C=..., TradingDay=2026-06-09 }

⚠️ 如果合成时发现夜盘的 1min Bar 尚未全部写入 PG（如 02:30 的 Bar 正在写入）：
   → 等待最多 60 秒 → 超时则用已写入的 Bar 合成 → 标记不完整 → 下次启动时重算
```

---

## 5. 数据完整性保证

### 5.1 .tick 文件完整性

```
写入侧：
  - 每条 AppendAsync 写入后不立即 fsync（性能优先）
  - 每 500ms 或 50 条 tick，flush 一次文件缓冲区
  - FlushAsync() 强制 fsync + 写 Footer（收盘时调用）
  - 异常时：最后一次 flush 之后的 tick 可能丢失（≤ 50 条）

读取侧：
  - 打开文件时自动检测 + 修复（见 2.6 崩溃恢复）
  - 恢复后 RecordCount 向下取整

防御层：
  - 收盘后立即 FlushAsync + 关闭文件
  - 次日打开时自动验证 Footer CRC32
  - CRC32 不匹配 → 重建 + 日志报警
```

### 5.2 Bar 完整性

```
去重：
  INSERT ... ON CONFLICT (symbol, timestamp) DO NOTHING
  → 同一个 1min Bar 不会重复写入

幂等：
  Bar 的合成是幂等的——同样的 1min Bar 集合 → 同样的 N-min Bar
  日线合成同理

完整性检查（每日收盘后自动运行）：
  1. 今日 bars_1min 的 TickCount 总和 > 0？（空 Bar 日 → 报警）
  2. 今日日线 Bar 已生成？未生成 → 合成
  3. 今日 .tick 文件 RecordCount > 0？（与 Bar 对比）
```

### 5.3 交易日归属正确性

```
CTP 的 TradingDay 字段由交易所设置，系统直接使用。

规则：
  - 周一至周五 09:00-15:00 的 tick → TradingDay = 当天
  - 周一至周四 21:00-02:30 的 tick → TradingDay = 次日
  - 周五 21:00-02:30 的 tick → TradingDay = 下周一
  - 长假前最后交易日无夜盘

验证：
  - 系统不做交易日推断——直接使用 CTP 返回的 TradingDay
  - .tick 文件名使用此 TradingDay
  - Bar.TradingDay 使用此 TradingDay
```

### 5.4 数据质量自检

```
每日收盘后自动检查：

1. Bar 连续性：
   bars_1min 中相邻 Bar 的 Timestamp 间隔 = 60 秒？
   → 如果有 gap > 60 秒 → 检查是否为交易时段边界 → 不是 → 报警

2. OHLC 合理性：
   O/H/L/C 都在涨跌停范围内？
   → 超出 → 数据异常 → 报警

3. Volume 一致性：
   Σ(1min Bar.Volume) ≈ 日线 Bar.Volume？
   → 不一致 → 合成错误 → 报警

4. TickCount 非零：
   1min Bar 的 TickCount = 0 → 该分钟无 tick → 可能是系统异常或市场暂停
   → 统计并汇报（不一定是错误）

5. 跨品种对比：
   同一交易所的品种是否有类似的数据 gap？
   → 如果有 → CTP 断连 → 不是数据错误，但需要标记
```

---

## 6. 外部 Tick 导入与历史数据回填

### 6.1 为什么需要

```
场景 1：系统初次部署
  → PG 中没有任何 1min Bar
  → 需要导入过去 2-3 年的历史数据来填充 Bar 数据库
  → 这样策略研究/回测才能立即开始

场景 2：补充缺失数据
  → 实盘采集因网络/系统问题缺失了某段时间的 tick
  → 从备份/外部源获取并导入

场景 3：研究新品种
  → 增加了新品种，需要导入该品种的历史数据
```

### 6.2 导入管道

```
外部 Tick 文件 (CSV/Parquet/其他格式)
    │
    ▼
TickImporter (格式转换)
    │ 外部格式 → TickRecord (标准化)
    │ 验证: 价格范围、时间范围、字段完整性
    │
    ▼
Channel<TickRecord> (同一个 Channel！)
    │
    ├──→ TickStore.AppendAsync()     → .tick 文件（可选，默认开启）
    │
    └──→ BarAggregator.OnTick()      → 1min Bar → BarStore
         （与实盘路径完全相同）
```

### 6.3 关键设计

```
1. 复用管道：
   导入的 TickRecord 走与实盘完全相同的 BarAggregator + BarStore 路径
   → 保证导入生成的 Bar 与实盘采集的 Bar 格式一致
   → Bar 的 upsert (ON CONFLICT DO NOTHING) 处理重叠数据

2. 离线模式：
   导入不需要 CTP 连接
   → TradingStudio.App 可以以"导入模式"启动
   → 或者作为独立 CLI 工具: TradingStudio.Tools import

3. 小批量补充 vs 大批量回填：
   补充缺失（几小时数据）:
   → 流式模式：直接推入 Channel
   → 走标准 Bar 闭合逻辑（tick 驱动 + 超时兜底）

   历史回填（3 年数据，数百 GB）:
   → 批处理模式：不经过 Channel
   → 按(交易日, Symbol)分组 → 排序 → 直接批量生成 1min Bar → 写入 PG
   → 跳过 .tick 文件写入（导入的原始文件本身就是备份）
   → 性能：比实时模拟快 100-1000 倍
```

### 6.4 大批量历史回填流程

```
Phase A — 扫描与分组：
  遍历历史数据目录 → 按 (Symbol, TradingDay) 分组
  → 构建导入清单：哪些日期已有 Bar（从 PG 查）→ 跳过
  → 哪些日期缺失 → 加入导入队列

Phase B — 批量导入（逐 Symbol × 逐日）：
  For each (Symbol, TradingDay) in 导入队列:
    1. 加载该日所有 tick 文件 → 解析 → 排序（按 ExchangeTimestamp）
    2. 验证：时间范围合理？价格范围合理？
    3. 直接聚合为 1min Bar（不经过 Channel，不模拟实时）：
       - 遍历排序后的 tick 列表
       - 按分钟分组 → 生成 Bar
       - 日盘/夜盘时段边界 → 强制分段
    4. 批量写入 bars_1min (PG COPY 或批量 INSERT)
    5. 日线合成 → 写入 bars_day

Phase C — 验证：
  导入完成后运行数据质量检查（见 5.4）
  对比：导入的 Bar 数量 vs 理论值（交易日 × 交易时段 × 60）

性能估算：
  1 个 Symbol × 1 年 × 250 交易日 ≈ 2 GB tick 数据
  批量处理：每 Symbol/年 ≈ 30 秒（解析+聚合+写入）
  70 Symbol × 3 年 ≈ 70 × 3 × 30s ≈ 1 小时
  → 一个晚上可以完成全部历史回填
```

### 6.5 增量导入（补充缺失）

```
与批量回填不同，增量导入走实时管道：

  1. 扫描目标日期的 .tick 或 CSV 文件
  2. 按时间排序后推入 Channel（模拟实时接收）
  3. BarAggregator 正常聚合
  4. 导入后自动数据质量检查

  适用场景：
  - 实盘缺失的某个交易日
  - 新上市品种的历史回填（通常只有几个月数据）
```

### 6.6 导入的 .tick 文件存储策略

```
选项 A — 同时保存 .tick（推荐）：
  导入的 tick 也写入 .tick 文件
  → 数据完整：原始 tick 和合成的 Bar 都保留
  → 可追溯：任何时候可以从 .tick 重新合成 Bar
  → 代价：额外磁盘占用（30 天滚动，长期影响小）

选项 B — 只导入 Bar（节省空间）：
  只合成 Bar 并写入 PG，不保留原始 tick
  → 适合历史数据（3 年前的数据不太需要 tick 精度）
  → CLI flag: --bars-only

决策：
  30 天内的 tick → 选项 A（保留 .tick，方便执行质量分析）
  30 天外的 tick → 选项 B（只生成 Bar，不保留 .tick）
```

### 6.4 支持的外部格式

#### 格式 1 — 金数源 Tick CSV（主要历史数据源）

```
文件:  金数源_商品tick快照样本_{symbol}_{date}_CTP格式.csv
编码:  GBK (Windows 中文默认)
分隔:  逗号
结构:  每行 = 一个 tick 快照，~47 列 CTP 全字段

列映射 (列号从 1 开始):

  列号  │ CTP 字段               │ TickRecord 映射
  ──────┼────────────────────────┼──────────────────────────
  1     │ TradingDay             │ → 交易日归属 (20160111)
  2     │ InstrumentID           │ → Symbol (cu1603)
  3-4   │ ExchangeID, (预留)      │ → 忽略 (Symbol 已确定合约)
  5     │ LastPrice              │ → LastPrice (×10^7)
  6     │ PreSettlementPrice     │ → 参考（不存 TickRecord）
  7     │ PreClosePrice          │ → 参考
  8     │ OpenInterest           │ → OpenInterest
  9     │ OpenPrice              │ → 参考（Bar 使用）
  10    │ HighestPrice           │ → 参考
  11    │ LowestPrice            │ → 参考
  12    │ Volume                 │ → Volume ⚠️ 累计值！需转增量
  13    │ Turnover               │ → Turnover ⚠️ 累计值！
  14    │ OpenInterest           │ → OpenInterest（更新值）
  15    │ PreOpenInterest        │ → 忽略
  17    │ UpperLimitPrice        │ → 用于 Flags (涨停判断)
  18    │ LowerLimitPrice        │ → 用于 Flags (跌停判断)
  21    │ UpdateTime             │ → HH:mm:ss → 合成 ExchangeTimestamp
  22    │ UpdateMillisec         │ → 毫秒部分
  23-24 │ BidPrice1, BidVolume1   │ → BidPrice1, BidVolume1
  25-26 │ AskPrice1, AskVolume1   │ → AskPrice1, AskVolume1
  27-42 │ Bid/Ask 2-5            │ → 不存 (TickRecord 只保留 L1)
  43    │ AveragePrice           │ → 参考（均价 = Turnover/Volume）
  44    │ ActionDay              │ → 业务日期

⚠️ 关键：Volume 和 Turnover 是累计值（与 CTP 实时 tick 一致）。
   Bar 聚合时走相同的 4.3 算法: Bar.Volume = Volume[last] - Volume[first]
   不需要特殊处理——TradingStudio 的数据管道天然处理累计 Volume

⚠️ 如果 CSV 文件跨多个交易日：
   每个交易日 CTP Volume 会归零 → FirstVolume 重置 → 正常
   导入前按 TradingDay 分组 → 每天独立聚合

⚠️ LocalTimestamp 不存在于 CSV 中。
   导入时用文件修改时间或当前时间填充
```

#### 格式 2 — Sina 日K线 JSON

```
来源: stock2.finance.sina.com.cn/futures/api/
字段: d(日期), o(开), h(高), l(低), c(收), v(量), s(结算价)
导入: 直接写入 bars_day (已是日线级别，不需要 BarAggregator)
```

#### 格式 3 — 已有的 .tick 文件

```
从其他 TradingStudio 实例或备份复制
→ data/ticks/{symbol}/ 目录 → import tick-files 命令
```

### 6.5 CLI 接口

```bash
# 导入 CSV 历史数据
TradingStudio.Tools import csv \
  --input ./data/history/rb_2024.csv \
  --symbol rb \
  --format ohlcv \          # 或 "tick"
  --date-column DateTime \
  --price-column Price

# 从已有的 .tick 文件目录批量导入
TradingStudio.Tools import tick-files \
  --path ./backup/ticks/ \
  --symbols rb,cu,IF,MA,TA \
  --from 2024-01-01 \
  --to 2024-12-31

# 导入后验证
TradingStudio.Tools verify \
  --symbols rb,cu \
  --from 2024-01-01 \
  --to 2024-12-31
```

### 6.6 导入模式的数据流

```
┌─────────────────────────────────────────────────┐
│                导入模式 (离线)                      │
│                                                 │
│  ┌──────────────────┐                           │
│  │ TickImporter      │  读取外部文件              │
│  │ (CSV/Parquet/.tick)│  验证 + 转换 → TickRecord │
│  └────────┬─────────┘                           │
│           │                                      │
│           ▼                                      │
│  Channel<TickRecord> ← 与实盘使用同一个 Channel    │
│           │                                      │
│     ┌─────┴─────┐                                │
│     ▼           ▼                                │
│  TickStore  BarAggregator                        │
│  (.tick)    (1min Bar)                           │
│                │                                  │
│                ▼                                  │
│           BarStore (PG)                          │
│                                                 │
│  ⚠️ 导入模式下不需要 CTP 连接                     │
│  ⚠️ 不触发策略（导入 ≠ 交易）                     │
└─────────────────────────────────────────────────┘
```

---

## 7. 数据管道启动与关闭

### 6.1 启动序列

```
1. 加载配置（DataPath、CTP 连接信息）
2. 连接 PostgreSQL → 验证连接
3. 加载 Symbol 列表（从 PG symbols 表）
4. 加载 TradingCalendar + TradingSession
5. 确定当前状态（激活 or 待命）
6. 如果是激活态：
   a. 连接 CTP MdApi → 登录 → 订阅 Symbol 列表
   b. 启动 BarAggregator
   c. 启动定时器（Bar 超时闭合、数据质量检查）
7. 进入主循环
```

### 6.2 关闭序列

```
1. CTP MdApi 取消订阅 → 断开
2. Channel.Writer.Complete() → 等待消费者排空
3. TickStore.FlushAsync() 全部 Symbol
4. BarAggregator 闭合所有未完成 Bar → 写入 PG
5. 日线合成
6. 数据质量检查
7. 清理 > 30 天 .tick 文件
```

---

## 7. 配置

```json
{
  "DataPath": "./data",
  "TickRetentionDays": 30,
  "TickFlushIntervalMs": 500,
  "TickFlushBatchSize": 50,
  "BarCloseTimeoutSeconds": 30,
  "Ctp": {
    "MarketFrontAddress": "tcp://180.168.146.187:10131",
    "BrokerId": "",
    "UserId": "",
    "Password": ""
  },
  "Subscriptions": [
    "rb", "hc", "i", "j", "jm", "cu", "al", "zn", "au", "ag",
    "sc", "fu", "bu", "ru", "sp", "IF", "IC", "IM", "IH",
    "m", "y", "a", "p", "c", "MA", "TA", "SA", "FG"
  ]
}
```

---

## 8. 测试清单

```
□ TickRecord 序列化/反序列化（80 bytes 完整往返）
□ .tick 文件创建 → 写入 10000 tick → 关闭 → 读取 → 逐条验证
□ .tick 文件崩溃恢复（手动截断文件 → 打开 → 验证自动修复）
□ .tick 文件 CRC32 校验
□ BarAggregator: 连续 tick 流 → 正确的 1min Bar (OHLCV)
□ BarAggregator: 跨整分钟边界 → Bar 正确闭合+新 Bar 开始
□ BarAggregator: 30 秒无 tick → 超时强制闭合
□ BarAggregator: 待命态 → 不触发超时闭合（不生成空 Bar）
□ BarAggregator: 交易时段边界 → Bar 强制闭合不跨 gap
□ BarAggregator: 累计 Volume 归零（跨交易日）→ 基准重置
□ BarAggregator: 集合竞价 tick (Flags=FLAG_AUCTION) → 正确标记但不影响聚合
□ 日线合成: 日盘+夜盘 1min Bar → 一条日线
□ 1min → 5min/15min/60min 合成
□ 80 品种并行: 同时 tick 流 → 各自独立 Bar 聚合
□ PostgreSQL 写入: upsert 不重复
□ Channel 背压: 生产者快于消费者 → 阻塞不丢数据
□ 7×24 连续运行 48 小时 → 内存无泄漏（WorkingSet 稳定）
□ 交易日归属: 夜盘 tick 归入次日 .tick 文件
```

---

> 本文档是 TradingStudio 数据层的唯一权威规格。后续所有实现以本文档为准。
