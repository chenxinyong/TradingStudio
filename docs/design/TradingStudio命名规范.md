# TradingStudio 命名规范

> 参考三家成熟量化交易系统（Lean / 天勤 TqSdk / 掘金量化）的命名实践，
> 结合 TradingStudio 现有代码，制定统一的命名规范。

---

## 目录

1. [三家系统命名风格对比](#1-三家系统命名风格对比)
2. [TradingStudio 命名总则](#2-tradingstudio-命名总则)
3. [数据模型命名](#3-数据模型命名)
4. [接口与抽象命名](#4-接口与抽象命名)
5. [方法命名](#5-方法命名)
6. [事件与回调命名](#6-事件与回调命名)
7. [配置与枚举命名](#7-配置与枚举命名)
8. [文件名与目录命名](#8-文件名与目录命名)
9. [当前代码合规检查](#9-当前代码合规检查)

---

## 1. 三家系统命名风格对比

### 1.1 语言 → 命名风格

| 系统 | 语言 | 命名风格 |
|------|------|---------|
| **Lean** | C# | PascalCase（C# 标准） |
| **天勤 TqSdk** | Python | snake_case（Python 标准） |
| **掘金量化** | C# + Python | C# 端 PascalCase，Python 端 snake_case |

**结论：** 各系统遵循所在语言的命名惯例。TradingStudio 是 C# 项目，应遵循 C# PascalCase 标准，与 Lean 和掘金 C# SDK 一致。

### 1.2 核心类型名称对比

| 概念 | Lean (C#) | 天勤 (Python) | 掘金 (C#) | TradingStudio |
|------|-----------|---------------|-----------|---------------|
| Tick 数据 | `Tick` | `tick_serial` → DataFrame | `Tick` | `TickRecord` ✅ |
| K线/Bar | `TradeBar` | `kline_serial` → DataFrame | `Bar` | `Bar` ✅ |
| 行情快照 | `QuoteBar` | `quote` | — | — |
| 订单 | `Order` | `order` | — | （待定） |
| 订单跟踪 | `OrderTicket` | — | — | （待定） |
| 合约/品种 | `Security` | `symbol` (str) | — | `Future` ✅ |
| 交易所 | `Exchange` (class) | `exchange_id` (str) | — | `ExchangeCode` ✅ |
| 组合/持仓 | `SecurityPortfolioManager` | `position` | `Position` | （待定） |
| 策略基类 | `QCAlgorithm` | （无基类概念） | `Strategy` | （待定） |

### 1.3 字段命名风格对比

| 字段 | Lean (C#) | 天勤 (Python) | 掘金 (C#) |
|------|-----------|---------------|-----------|
| 最新价 | `Price` / `Value` | `last_price` | `price` |
| 开盘价 | `Open` | `open` | `open` |
| 最高价 | `High` | `high` | `high` |
| 最低价 | `Low` | `low` | `low` |
| 收盘价 | `Close` | `close` | `close` |
| 成交量 | `Volume` | `volume` | `cumVolume` / `volume` |
| 成交额 | — | `amount` | `cumAmount` / `amount` |
| 持仓量 | `OpenInterest` | `open_interest` | `cumPosition` / `position` |
| 买一价 | `BidPrice` | `bid_price1` | `bidPrice` (Quote) |
| 卖一价 | `AskPrice` | `ask_price1` | `askPrice` (Quote) |
| 时间戳 | `Time` / `EndTime` | `datetime` | `createdAt` / `bob` / `eob` |
| 涨跌停 | — | `upper_limit` / `lower_limit` | — |

### 1.4 方法/事件命名对比

| 概念 | Lean (C#) | 天勤 (Python) | 掘金 (C#) |
|------|-----------|---------------|-----------|
| 初始化 | `Initialize()` | — | `OnInit()` |
| 数据到达 | `OnData(Slice)` | `wait_update()` | `OnTick()` / `OnBar()` |
| 下单 | `Buy()` / `Sell()` / `Order()` | `insert_order()` | `OrderVolume()` / `OrderValue()` |
| 撤单 | `Cancel()` (on OrderTicket) | `cancel_order()` | `OrderCancel()` |
| 订单状态 | `OnOrderEvent(OrderEvent)` | `is_changing(order)` | `OnOrderStatus()` |
| 成交回报 | （OnOrderEvent 内含） | — | `OnExecutionReport()` |
| 策略停止 | `OnEndOfAlgorithm()` | — | `OnStop()` |

### 1.5 关键设计哲学差异

| | Lean | 天勤 | 掘金 |
|------|------|------|------|
| **驱动模型** | 回调式 `OnData(Slice)` | 轮询式 `wait_update()` | 回调式 `OnTick()` / `OnBar()` |
| **数据获取** | 订阅 → 自动推送 | `get_*` 返回引用 → wait_update 更新 | 订阅 → 自动推送 |
| **订单模型** | Order → OrderTicket → OrderEvent | 简单 dict 引用 | OrderVolume 等方法 → 状态回调 |
| **品种体系** | Security 品种层次（10+子类） | symbol 字符串 | symbol 字符串 |
| **时间处理** | NodaTime，多时区 | 纳秒级 Unix 时间戳 | DateTime |

---

## 2. TradingStudio 命名总则

### 2.1 核心原则

```
C# 标准 PascalCase ──── 所有公有成员
结构体扁平命名 ──────── 不套 Security/Model 等冗余前缀
与领域术语一致 ──────── 期货领域用语优先
简洁优先 ────────────── 能用 1 个词不用 2 个
```

### 2.2 命名风格速查表

| 元素 | 风格 | 示例 |
|------|------|------|
| Namespace | PascalCase | `TradingStudio.Core.Models` |
| Class / Record / Struct | PascalCase | `TickRecord`, `Bar`, `Future` |
| Interface | `I` 前缀 + PascalCase | `IMarketDataHandler`, `ITradeGateway` |
| Enum 类型 | PascalCase | `ExchangeCode`, `OrderStatus` |
| Enum 成员 | 全大写缩写 | `SHFE`, `DCE`, `CFFEX` |
| Property / Field | PascalCase | `LastPrice`, `BidPrice1`, `Open` |
| Method | PascalCase，动词开头 | `GetHistory()`, `PlaceOrder()`, `CancelOrder()` |
| 事件 / 回调 | `On` 前缀 | `OnTick()`, `OnBar()`, `OnOrderStatus()` |
| 私有字段 | `_camelCase` | `_lastPrice`, `_isRunning` |
| 常量 | PascalCase | `PriceScale`, `RecordSize` |
| 本地变量 | camelCase | `tickCount`, `barTime` |
| 参数 | camelCase | `symbol`, `quantity`, `price` |
| 泛型参数 | `T` 前缀 | `TIndicator`, `TData` |

### 2.3 禁止事项

| ❌ 禁止 | ✅ 正确 | 原因 |
|---------|---------|------|
| `_camelCase` 公有字段 | `PascalCase` 属性 | C# 规范 |
| `m_` 或 `s_` 前缀 | `_camelCase` 私有字段 | 过时的 C++ 风格 |
| `clsTickRecord` / `objOrder` | `TickRecord` / `OrderTicket` | 匈牙利命名法已淘汰 |
| `TickInfo` / `TickData` | `TickRecord` | `Info`/`Data` 是噪声词 |
| `get_Tick()` / `SetPrice()` | `GetTick()` / `SetPrice()` 或属性 | C# 不用下划线分隔 |
| `shfe` / `Shfe` | `SHFE` | 交易所代码全大写是期货行业惯例 |
| `volume_cum` / `cumVolume` | `Volume` + 注释说明语义 | 结构体内保持字段名简洁，累计语义通过文档说明 |
| `CTickRecord` | `TickRecord` | 不用 C 前缀（无类/结构区分必要） |

---

## 3. 数据模型命名

### 3.1 Tick

```csharp
// 结构体: TickRecord (80B 固定大小)
// 位置: TradingStudio.Core.Models

public readonly record struct TickRecord
{
    // === 时间 (2字段, 16B) ===
    long ExchangeTimestamp  // 交易所时间 (毫秒 UTC)
    long LocalTimestamp     // 本地接收时间

    // === 价格 (4字段, 32B) ×10^7 ===
    long LastPrice          // 最新价
    long BidPrice1          // 买一价
    long AskPrice1          // 卖一价

    // === 量仓 (4字段, 24B) ===
    long Volume             // 当日累计成交量 (CTP 语义)
    double Turnover         // 当日累计成交额
    double OpenInterest     // 持仓量
    int BidVolume1          // 买一量
    int AskVolume1          // 卖一量

    // === 状态 (1字段, 4B) ===
    int Flags               // 位标志

    // === 计算属性 (零存储) ===
    double LastPriceDouble  // 最新价 (double)
    long Spread             // 买卖价差 (tick 数)
    DateTimeOffset ExchangeTime  // 交易所时间 (UTC)
    double LatencyMs        // 网络+处理延迟 (毫秒)
}
```

**命名决策：**
- `TickRecord` 而非 `Tick` —— 强调这是落盘用的持久化记录，区分于 CTP 的 `CThostFtdcDepthMarketDataField`（Quote）
- `LastPrice` 而非 `Price` —— Lean 用 `Price`/`Value`，掘金用 `price`。但期货领域强调"最新价"以区分昨收/开盘，**保留 `LastPrice`**（天勤也用 `last_price`）
- `BidPrice1` / `AskPrice1` —— 与天勤 `bid_price1`/`ask_price1` 对齐；数字后缀表示盘口深度档位
- `Volume` 而非 `CumVolume` —— 掘金用 `cumVolume` 强调累计。TradingStudio 通过文档+注释说明语义，字段名保持简洁
- `ExchangeTimestamp` 而非 `Timestamp` —— 区别交易所时间和本地时间

### 3.2 Bar

```csharp
// 结构体: Bar (1分钟/日线 K 线)
// 位置: TradingStudio.Core.Models

public record struct Bar
{
    // === 标识 ===
    string InstrumentId     // "ag2608"
    DateOnly TradingDay     // CTP 交易日（夜盘归属前一天）
    DateTime BarTime        // Bar 起始时间

    // === OHLCV (5字段) ===
    long Open               // 开盘价 ×10^7
    long High               // 最高价
    long Low                // 最低价
    long Close              // 收盘价
    long Volume             // 成交量 (delta，不是累计)
    double Turnover         // 成交额 (delta)
    double OpenInterest     // 持仓量 (快照)
    int TickCount           // 构成此 Bar 的 tick 数
}
```

**命名决策：**
- `Bar` 而非 `KLine` —— Lean 用 `TradeBar`，掘金用 `Bar`。国际通用 `Bar`/`Candlestick`，**用 `Bar`**。中文注释写"K线"，对外用 `Bar`
- `BarTime` 而非 `Time` —— 掘金用 `bob`/`eob`。为清晰区分，用 `BarTime` 表示 Bar 起点时间
- `Open`/`High`/`Low`/`Close` —— 四大系统统一命名，无争议
- `InstrumentId` 而非 `Symbol` —— Lean/JQ 用 `Symbol` 表示证券标识对象，TradingStudio 用 `string InstrumentId` 更简单直接
- `TradingDay` —— 天勤和掘金都不显式存储交易日。**TradingStudio 保留**，因为 CTP 夜盘的交易日归属是核心业务逻辑

### 3.3 品种/合约

```csharp
// 品种 (品种是交易规则的载体)
public sealed record Future
{
    // === 标识 ===
    int Id                  // 数据库主键
    ExchangeCode Exchange   // 交易所
    string Code             // "cu", "IF"
    string Name             // "铜", "沪深300"

    // === 分类 ===
    string Category         // 有色金属/黑色金属/...
    string DeliveryType     // PHYSICAL | CASH

    // === 交易规则 ===
    decimal TradingUnit     // 5 吨, 300 元/点...
    string UnitName         // "吨", "克", "元/点"
    decimal TickSize        // 最小变动价位
    decimal TickValue       // 1跳价值
    decimal PriceLimitPct   // 涨跌停板 %
    decimal MarginRate      // 交易所基准保证金率
    string Months           // 合约月份
    string TradingHours     // 交易时间描述
}
```

**命名决策：**
- `Future` 而非 `Security`/`Product`/`Instrument` —— Lean 用 `Security`（太通用），天勤/掘金不建模直接用 string。TradingStudio 用 **`Future`** 明确指期货品种
- `Code` 而非 `Symbol`/`Ticker` —— 天勤用 `symbol` 字符串，掘金用 `symbol`。TradingStudio 区分品种代码(Code)和合约代码(InstrumentId)
- `TradingUnit` —— 掘金/天勤都不显式建模。来自期货术语"交易单位"
- `TickSize` —— 三家系统都用此术语，无争议

### 3.4 订单（待实现）

```
OrderBase        ← 抽象基类 (PascalCase)
  ├── MarketOrder     ← 市价单
  ├── LimitOrder      ← 限价单
  ├── StopOrder       ← 止损单 (StopMarketOrder 简化)

OrderTicket      ← 线程安全的订单跟踪器
OrderEvent       ← 订单生命周期事件
OrderStatus      ← enum: New, Submitted, Accepted, PartiallyFilled, Filled, Canceled, Rejected
```

**命名决策：**
- 参考 Lean 的命名：`Order` → `OrderTicket` → `OrderEvent` 三层模型
- 砍掉 Lean 的 `Combo*` / `TrailingStop*` / `MarketOnClose*` 等（期货不需要）
- `OrderBase` 而非 `Order` —— 避免与 CTP 的 order 概念混淆

### 3.5 持仓/资金（待实现）

```
Position         ← 持仓 (天勤: position, JQ: Position)
Account          ← 账户资金 (天勤: account, JQ: Account)
PositionSide     ← enum: Long, Short
```

### 3.6 完整类型对照表

| 概念 | 建议命名 | Lean | 天勤 | 掘金 |
|------|---------|------|------|------|
| 逐笔行情 | `TickRecord` | `Tick` | `tick` (DataFrame) | `Tick` |
| 行情快照 | `Quote` | `QuoteBar` | `quote` | — |
| K线 | `Bar` | `TradeBar` | `kline` (DataFrame) | `Bar` |
| 品种 | `Future` | `Security` (Future子类) | symbol str | — |
| 交易所 | `ExchangeCode` | `Exchange` class | exchange_id str | — |
| 合约代码 | `InstrumentId` (string) | `Symbol` (class) | symbol str | symbol str |
| 订单 | `OrderBase` | `Order` | order dict | — |
| 订单跟踪 | `OrderTicket` | `OrderTicket` | — | — |
| 订单事件 | `OrderEvent` | `OrderEvent` | — | — |
| 持仓 | `Position` | `SecurityHolding` | position dict | `Position` |
| 账户 | `Account` | `CashBook` | account dict | `Account` |
| 策略基类 | `StrategyBase` | `QCAlgorithm` | — | `Strategy` |
| 数据切面 | `MarketSlice` | `Slice` | — | — |

---

## 4. 接口与抽象命名

### 4.1 接口

```csharp
// Handler 模式 (参考 Lean)
IMarketDataHandler   // 行情数据处理器 (回测: 文件回放, 实盘: CTP MdApi)
ITradeGateway        // 交易网关 (回测: 模拟撮合, 实盘: CTP TraderApi)
IRiskEngine          // 风控引擎 (横切层)
IResultHandler       // 结果处理器

// 数据提供者 (参考 Lean IDataFeed / IDataProvider)
ITickProvider        // Tick 数据提供者
IBarProvider         // Bar 数据提供者

// 存储
ITickStore           // Tick 持久化
IBarStore            // Bar 持久化
IOrderStore          // 订单持久化

// 策略
IStrategy            // 策略接口
IOrderProcessor      // 订单处理器接口 (参考 Lean ITransactionHandler)
```

**命名决策：**
- 遵循 C# `I` 前缀规范，与 Lean 一致
- 使用行业术语：`MarketData` 而非 `Quote`/`Feed`（更通用），`TradeGateway` 而非 `Brokerage`
- Handler 后缀 — 直接参考 Lean，表示"可替换的处理模块"

### 4.2 抽象基类

```csharp
// 带 "Base" 后缀
StrategyBase              // 策略基类
IndicatorBase             // 指标基类 (参考 Lean IndicatorBase)
IndicatorBase<T>          // 泛型指标基类
WindowIndicator<T>        // 滑动窗口指标 (参考 Lean WindowIndicator)
OrderBase                 // 订单基类
SlippageModelBase         // 滑点模型基类
CommissionModelBase       // 手续费模型基类
```

---

## 5. 方法命名

### 5.1 动词选择

| 场景 | 动词 | 示例 | 参考 |
|------|------|------|------|
| 获取数据 | `Get` | `GetTick()`, `GetBar()`, `GetHistory()` | Lean: `GetSource()`, 天勤: `get_tick_serial()` |
| 订阅 | `Subscribe` | `Subscribe(symbol)` | 掘金: `Subscribe(symbol, freq)` |
| 下单 | `Place` / `Send` | `PlaceOrder()`, `SendOrder()` | Lean: `Order()`, 天勤: `insert_order()`, 掘金: `OrderVolume()` |
| 撤单 | `Cancel` | `CancelOrder(id)` | 天勤: `cancel_order()`, 掘金: `OrderCancel()` |
| 修改订单 | `Modify` / `Update` | `ModifyOrder()` | Lean: `Update()` on OrderTicket |
| 平仓 | `Close` | `ClosePosition()` | 掘金: `OrderCloseAll()` |
| 开始 | `Start` | `StartBacktest()` | — |
| 停止 | `Stop` | `Stop()`, `Quit()` | Lean: `Quit()`, 掘金: `OnStop()` |
| 重置 | `Reset` | `Reset()` | Lean: `Reset()` on Indicator |
| 计算 | `Compute` | `ComputeNextValue()` | Lean: `ComputeNextValue()` |
| 更新 | `Update` | `Update(input)` | Lean: `Update()` on Indicator |

### 5.2 命名模式

```csharp
// ✅ 好
PlaceOrder(string instrumentId, decimal price, int volume)
CancelOrder(int orderId)
GetHistory(string instrumentId, DateTime start, DateTime end)
Subscribe(string instrumentId)

// ❌ 不好
insert_order(instrument_id, price, volume)   // Python 风格，C# 中不用
NewOrder(instrumentId, price, volume)        // New 是形容词，不是动词
DoOrder(cmd)                                 // 不够具体
```

---

## 6. 事件与回调命名

### 6.1 规则

```
On + 事件名（PascalCase）
```

### 6.2 对标

| TradingStudio | Lean | 掘金 | 天勤 |
|---------------|------|------|------|
| `OnTick(TickRecord)` | `OnData(Slice)` | `OnTick(Tick)` | （轮询式，无回调） |
| `OnBar(Bar)` | `OnData(Slice)` | `OnBar(Bar)` | — |
| `OnOrderStatus(OrderEvent)` | `OnOrderEvent(OrderEvent)` | `OnOrderStatus(Order)` | — |
| `OnExecutionReport(Execution)` | `OnOrderEvent` (内含 Fill) | `OnExecutionReport(ExecRpt)` | — |
| `OnPositionChanged(Position)` | `OnSecuritiesChanged()` | — | — |
| `OnAccountChanged(Account)` | — | `OnAccountStatus(Account)` | — |
| `OnInit()` | `Initialize()` | `OnInit()` | — |
| `OnStart()` | — | （通过 OnInit 替代） | — |
| `OnStop()` | `OnEndOfAlgorithm()` | `OnStop()` | — |
| `OnError(Exception)` | `OnError()` (python only) | `OnError(int, string)` | — |
| `OnMarginCall(List<Order>)` | `OnMarginCall(orders)` | — | — |

**命名决策：**
- 掘金风格 `OnTick`/`OnBar` 而非 Lean 的统一 `OnData(Slice)` — 因为: (1) 期货品种数远少于股票，按数据粒度分别回调更直观；(2) 掘金同样面向期货，已用此模式验证可行
- 保留 Lean 的 `Initialize()` 作为策略入口方法名（而非 `OnInit`）— 因为 `Initialize` 语义更准确（初始化而非事件响应）

---

## 7. 配置与枚举命名

### 7.1 枚举

```csharp
// 交易所 — 全大写缩写 (行业惯例)
enum ExchangeCode { SHFE, INE, DCE, CZCE, CFFEX, GFEX }

// 行情周期 — PascalCase
enum BarFrequency { Tick, Second1, Minute1, Minute5, Minute15, Minute30, Hour1, Day1 }

// 订单状态
enum OrderStatus { New, Submitted, Accepted, PartiallyFilled, Filled, Canceled, Rejected, Expired }

// 订单方向
enum OrderDirection { Buy, Sell }

// 开平标志
enum PositionEffect { Open, Close, CloseToday }

// 订单类型
enum OrderType { Market, Limit, Stop }

// 策略状态
enum StrategyStatus { Created, Initializing, Running, Stopping, Stopped, Error }
```

**命名决策：**
- `ExchangeCode` 而非 `Exchange` — 避免与 Lean 的 `Exchange` 类冲突，后者是类而非枚举
- `BarFrequency` 命名方式参考掘金的 frequency 字符串 (`"60s"`, `"1d"`)
- `PositionEffect` — 期货特有的开平标志（天勤: `offset` → `"OPEN"`/`"CLOSE"`/`"CLOSETODAY"`）

### 7.2 配置类

```csharp
// 强类型配置 (参考 .NET IOptions<T> 模式)
CollectOptions         // 行情采集配置
ConnectionOptions      // CTP 连接配置
StorageOptions         // 存储路径配置
BacktestOptions        // 回测配置
```

---

## 8. 文件名与目录命名

```
src/
├── TradingStudio.Core/
│   ├── Models/               ← 数据模型 (TickRecord.cs, Bar.cs, Future.cs)
│   ├── Interfaces/           ← 接口
│   └── Aggregation/          ← 聚合逻辑
│
├── TradingStudio.Data/
│   ├── CtpAdapter.cs          ← CTP 适配
│   ├── TickStore.cs           ← Tick 存储
│   └── BarStore.cs            ← Bar 存储
│
├── TradingStudio.Strategy/
│   ├── StrategyBase.cs        ← 策略基类
│   ├── Indicators/           ← 指标
│   │   ├── IndicatorBase.cs
│   │   ├── Sma.cs
│   │   └── Macd.cs
│   └── BacktestEngine.cs     ← 回测引擎
│
├── TradingStudio.Execution/
│   ├── CtpTraderAdapter.cs   ← CTP 交易适配
│   ├── SimulatedExecution.cs ← 模拟成交
│   └── RiskEngine.cs         ← 风控引擎
│
└── TradingStudio.UI/         ← 界面
```

**命名决策：**
- 文件名与类名一致：`TickRecord.cs` → `class TickRecord`
- 目录用 PascalCase 复数：`Models/`, `Interfaces/`, `Indicators/`
- 与 Lean 不同的简化：Lean 有 `Engine/DataFeeds/Enumerators/`, `Common/Securities/Future/` 等深层嵌套 → TradingStudio 扁平化

---

## 9. 当前代码合规检查

### 9.1 已合规 ✅

| 位置 | 命名 | 判定 |
|------|------|------|
| `TickRecord` | struct + 所有字段 | ✅ PascalCase, 无匈牙利前缀, 字段名简洁 |
| `Bar` | struct + 所有字段 | ✅ PascalCase |
| `Future` | record + 所有字段 | ✅ PascalCase |
| `ExchangeCode` | enum + 成员 | ✅ 全大写交易所缩写 |
| `FutureRegistry` | class 名 | ✅ PascalCase |
| `ContractCodeGenerator` | class 名 | ✅ PascalCase |
| `BarAggregator` | class 名 | ✅ PscalCase |
| `BarStore` | class 名 | ✅ PascalCase |
| `CtpMdAdapter` | class 名 | ✅ PascalCase |
| `CollectService` | class 名 | ✅ PascalCase |

### 9.2 待统一 🟡

| 当前 | 建议 | 原因 |
|------|------|------|
| `TradingUnit` (decimal) | 保持不变 | 无争议 |
| `ContractValue()` (方法) | 保持不变 | 期货领域常用 |
| `FLAG_UPPER_LIMIT` (const) | `FlagUpperLimit` | 当前全大写是 C/C++ 宏风格，C# 应 PascalCase |

### 9.3 待实现的类型 💡

| 类型 | 建议命名 | 参考 |
|------|---------|------|
| 策略基类 | `StrategyBase` | Lean `QCAlgorithm`, 掘金 `Strategy` |
| 指标基类 | `IndicatorBase` | Lean `IndicatorBase` |
| 订单基类 | `OrderBase` | Lean `Order` |
| 订单 Ticket | `OrderTicket` | Lean `OrderTicket` |
| 回测引擎 | `BacktestEngine` | Lean `AlgorithmManager` |
| 行情切片 | `MarketSlice` | Lean `Slice` / `TimeSlice` |
| 滑点模型 | `SlippageModel` | Lean `ISlippageModel` |
| 手续费模型 | `CommissionModel` | Lean `IFeeModel` |

---

## 10. 快速决策表

遇到命名犹豫时，按以下优先级决策：

```
1. C# 规范 (PascalCase, I前缀, _camel 私有字段)
2. 期货行业术语 (Spread, OpenInterest, PositionEffect)
3. Lean 框架惯例 (OrderTicket, OrderEvent, Handler 后缀)
4. 简洁原则 (Volume 而非 CumVolume, Code 而非 SymbolCode)
5. 中文注释补充语义 (结构体名用英文，业务含义写在注释里)
```

---

> 📁 **相关文档：**
> - [Lean引擎架构分析.md](Lean引擎架构分析.md)
> - [Lean深度分析-核心子系统.md](Lean深度分析-核心子系统.md)
> - [[tradingstudio-lean-architecture]] — 精简架构5项目设计决策
> - [[tradingstudio-design-reference-lean]] — 设计参考 Lean 的工作约定
