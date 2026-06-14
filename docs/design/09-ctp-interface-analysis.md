# 09 — CTP 接口分析：v6.7.13 完整盘点与数据管线的落差

> 分析 CTP SDK 6.7.13 SE x64 的行情数据结构，
> 评估当前 bridge 的传递完整度，定位关键 gap。

---

## 一、CTP 行情数据全貌

### 1.1 CThostFtdcDepthMarketDataField 完整字段清单（42+ 字段）

| # | CTP 字段 | C 类型 | 含义 | 当前 bridge 是否传递 |
|---|---|---|---|---|
| 1 | TradingDay | char[9] | 交易日（YYYYMMDD） | ❌ **丢失** |
| 2 | reserve1 (原InstrumentID) | char[31] | 合约代码（如 cu2607） | ❌ **丢失** |
| 3 | ExchangeID | char[9] | 交易所代码 | ❌ |
| 4 | reserve2 | char[81] | — | ❌ |
| 5 | LastPrice | double | 最新价 | ✅ |
| 6 | PreSettlementPrice | double | 上次结算价 | ❌ |
| 7 | PreClosePrice | double | 昨收盘 | ❌ |
| 8 | PreOpenInterest | double | 昨持仓量 | ❌ |
| 9 | OpenPrice | double | 今开盘 | ❌ |
| 10 | HighestPrice | double | 最高价 | ❌ |
| 11 | LowestPrice | double | 最低价 | ❌ |
| 12 | Volume | int | 成交量 | ✅ |
| 13 | Turnover | double | 成交额 | ✅ |
| 14 | OpenInterest | double | 持仓量 | ✅ |
| 15 | ClosePrice | double | 今收盘 | ❌ |
| 16 | SettlementPrice | double | 本次结算价 | ❌ |
| 17 | UpperLimitPrice | double | 涨停价 | ✅ |
| 18 | LowerLimitPrice | double | 跌停价 | ✅ |
| 19 | PreDelta | double | 昨虚实度 | ❌ |
| 20 | CurrDelta | double | 今虚实度 | ❌ |
| 21 | UpdateTime | char[9] | 更新时间 HH:MM:SS | ✅ (转换为 timestamp) |
| 22 | UpdateMillisec | int | 更新毫秒 | ✅ (转换为 timestamp) |
| 23-32 | BidPrice1~5, BidVolume1~5, AskPrice1~5, AskVolume1~5 | double/int × 20 | 五档买卖盘口 | ⚠️ **仅传 L1**（买一/卖一各一笔） |

**传递率：12/42（29%）。关键字段丢失 2 个（TradingDay、InstrumentID）。五档深度只传了一档。**

---

## 二、当前 Bridge 传递内容

### 2.1 CtpOnTickCallback 签名

```c
typedef void (*CtpOnTickCallback)(
    double lastPrice,           // ✅ CTP: LastPrice
    int volume,                 // ✅ CTP: Volume
    double turnover,            // ✅ CTP: Turnover
    double openInterest,        // ✅ CTP: OpenInterest
    double bidP1, int bidV1,    // ✅ CTP: BidPrice1, BidVolume1
    double askP1, int askV1,    // ✅ CTP: AskPrice1, AskVolume1
    long long exchangeTimestamp,// ✅ 从 TradingDay + UpdateTime + Millisec 合成
    long long localTimestamp,   // ✅ 系统本地时间
    double upperLimit,          // ✅ CTP: UpperLimitPrice
    double lowerLimit           // ✅ CTP: LowerLimitPrice
);
```

### 2.2 回调实现（CtpMdBridge.cpp:42-61）

```cpp
void OnRtnDepthMarketData(CThostFtdcDepthMarketDataField* pData) override
{
    auto ts = ParseTimestamp(pData->TradingDay, pData->UpdateTime, pData->UpdateMillisec);
    long long localTs = GetCurrentUnixMs();

    OnTickCb(
        pData->LastPrice,
        pData->Volume,
        pData->Turnover,
        pData->OpenInterest,
        pData->BidPrice1, pData->BidVolume1,
        pData->AskPrice1, pData->AskVolume1,
        ts, localTs,
        pData->UpperLimitPrice,
        pData->LowerLimitPrice
    );
}
```

**TradingDay 被 bridge 消费掉了（用于合成 timestamp），但没有原样传给 C#。**

---

## 三、关键 Gap 分析

### 3.1 Gap 1：TradingDay 丢失 — .tick 文件的命名基石

**问题：** TradingDay 用于夜盘归属——周一凌晨 1:00 的 tick，其 TradingDay 仍是上周五。如果管线用系统本地时间判断交易日，夜盘 tick 会被错误归属。

**当前状态：** bridge 把 TradingDay + UpdateTime + Millisec 合成为一个 `exchangeTimestamp`（Unix 毫秒），然后丢弃了 TradingDay 字符串。C# 侧收到的是 Unix 毫秒，无法直接知道"这个 tick 属于哪个交易日"。

**影响：**
- `.tick` 文件命名 `{tradingDay}_{symbol}.tick` 需要 TradingDay
- Bar 聚合需要 TradingDay 来判断日切（15:00 日盘收盘 / 2:30 夜盘收盘）
- 回测时需要按 TradingDay 加载数据

**修复方案：** 在回调中添加 `const char* tradingDay` 和 `const char* instrumentId` 参数。

### 3.2 Gap 2：InstrumentID 丢失 — 合约路由的依据

**问题：** 一个 CTP 连接可以同时订阅多个合约。tick 到达 C# 管线时，如果不知道合约代码，无法路由到正确的 Channel 或 `.tick` 文件。

**当前状态：** bridge 不传 InstrumentID。C# 侧 `CtpMdProvider` 只创建了一个全局的 `Channel<TickRecord>`，所有合约的 tick 混在一起。

**影响：**
- 无法按合约拆分 tick 流
- `.tick` 文件写入器无法判断该写入哪个文件

**修复方案：**
```c
// 建议的回调签名扩展
typedef void (*CtpOnTickCallback)(
    const char* instrumentId,   // ✅ 新增：合约代码
    const char* tradingDay,     // ✅ 新增：交易日
    double lastPrice, ...       // 原有字段保持不变
);
```

### 3.3 Gap 3：五档深度只传一档 — 盘口分析受限

**问题：** 完整盘口有 5 档买卖（Bid1~5, Ask1~5），当前只传 L1。

**影响分析：**
- 日内交易策略需要看盘口深度来判断流动性
- 高频策略需要多档价格
- 回测时只能回放 L1，无法模拟真实盘口

**决策：** Phase 1 保持 L1（与当前 bridge 一致）。80 字节 `TickRecord` 本来就是为 L1 设计的。如果将来需要 L2-L5，可以通过扩展 `Flags` 位来标记"多档"模式，在 `.tick` 文件的 Header 中标识。

### 3.4 Gap 4：缺失的 OHLC 字段 — Bar 聚合的第一步

**问题：** CTP 提供了 `OpenPrice`、`HighestPrice`、`LowestPrice`、`ClosePrice`（当日累计 OHLC），但 bridge 没传。

**影响：** 如果数据写入晚了（比如开盘后 30 分钟才连接），会丢失前面 tick 的信息，但 CTP 的当日累计 OHLC 仍然正确。传过来可以作为交叉验证。

**决策：** Phase 1 不传——Bar 聚合会从 tick 流自己计算 OHLC，CTP 的当日累计值仅做验证用，不替代 tick 级计算。

---

## 四、TradingDay vs ActionDay

这是 CTP 接口中最容易搞错的概念：

| 字段 | 含义 | 举例（周一凌晨 1:00） |
|---|---|---|
| TradingDay | **交易日**，夜盘归属前一天 | 上周五（如 20260605） |
| ActionDay | **业务日期**（日历日） | 周一（20260608） |

CTP SDK v6.7.13 的 `DepthMarketDataField` **不包含 ActionDay**（ActionDay 在 Order/Trade 结构体中）。Tick 数据的日期归属以 TradingDay 为准。

---

## 五、Bridge 实施状态

### 5.1 当前可工作的 Bridge

| 文件 | 状态 | 说明 |
|---|---|---|
| `CtpMdBridge.h` | ✅ 已完成 | C API 声明，7 个导出函数 |
| `CtpMdBridge.cpp` | ✅ 已完成 | 179 行，连接 CThostFtdcMdApi |
| `CtpInterop.cs` | ✅ 已完成 | P/Invoke 声明，匹配 .h 签名 |
| `CtpMdProvider.cs` | ⚠️ 可用但单实例 | 只创建一个 Channel，不区分合约 |

### 5.2 已验证的功能路径

```
CTP tcp://180.168.146.187:10211 (SimNow)
  → thostmduserapi_se.dll (创建 MdApi 实例)
    → CtpMdImpl::OnRtnDepthMarketData (CTP 回调线程)
      → CtpOnTickCallback (跨 C/C# 边界的函数指针调用)
        → CtpMdProvider (C# 端，写入 Channel<TickRecord>)
          → Channel Reader (待实现：→ ITickStore → BarAggregator → IBarStore)
```

### 5.3 需要修复的内容

1. **扩展回调签名**：添加 `instrumentId` 和 `tradingDay` 两个字符串参数
2. **更新 C# P/Invoke**：同步更新 `CtpInterop.cs` 的委托定义
3. **CtpMdProvider 改造**：从单一 Channel 变为 `Dictionary<string, Channel<TickRecord>>`（按合约代码路由）

---

## 六、推荐的 TickRecord 修订

基于 CTP 接口分析和管线需求，TickRecord 需要新增 2 个字段：

```csharp
public struct TickRecord
{
    // === 现有字段（保持不变）===
    public long ExchangeTimestamp;    // CTP 交易所时间（Unix ms）
    public long LocalTimestamp;       // 本地接收时间（Unix ms）
    public long LastPrice;            // 最新价 × 10^7
    public long BidPrice1;            // 买一价 × 10^7
    public long AskPrice1;            // 卖一价 × 10^7
    public long Volume;               // 累计成交量
    public double Turnover;           // 累计成交额
    public double OpenInterest;       // 持仓量
    public int BidVolume1;            // 买一量
    public int AskVolume1;            // 卖一量
    public int Flags;                 // 标志位

    // === 新增字段 ===
    // 不在 80 字节结构体内——存在文件 Header 中
    // 合约代码 → 文件路径编码
    // 交易日   → 文件名编码
}
```

**决策：TickRecord 保持 80 字节不变。** InstrumentID 和 TradingDay 通过 `.tick` 文件路径编码（`{tradingDay}_{instrumentId}.tick`），不在每条 tick 记录中重复存储。管线的路由逻辑在 `TickRecord` 到达之前就已根据 InstrumentID 分流。

---

## 七、接口演进路径

| 阶段 | 内容 |
|---|---|
| Phase 1（当前桥） | L1 tick，12 字段，80 字节 TickRecord |
| Phase 1 修复 | 回调加 instrumentId + tradingDay，管线路由 |
| Phase 2（可选） | 回调加 PreSettlementPrice + OpenPrice，用于启动时的 OHLC 修正 |
| Phase 3（可选） | 完整 5 档深度，TickRecord 扩展为多档模式 |

---

*分析日期：2026-06-10*
*CTP SDK 版本：6.7.13 SE x64*
*Bridge 文件：src/CTP/CTPWrapper/CtpMdBridge.cpp (179 行)*
