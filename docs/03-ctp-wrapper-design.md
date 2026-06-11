> ⚠️ **已废弃** — 本文档描述纯 P/Invoke 方案。实际代码采用「C++ 原生桥接 → C 导出 → P/Invoke」混合方案。替代文档：[09-ctp-interface-analysis.md](09-ctp-interface-analysis.md)（CTP 字段分析）、[11-implementation-roadmap-v2.md](11-implementation-roadmap-v2.md)（当前代码状态）

# CTP 封装设计（已废弃）

> 将 C++ DLL 的回调式接口封装为 C# 的异步管道。这是系统与外部世界的唯一桥梁。

---

## 1. 封装方案选择

### 候选方案

| 方案 | 原理 | 优劣 |
|---|---|---|
| **P/Invoke 自封装** | 直接调用 CTP DLL 的 C 导出函数 | 完全控制、零中间层、细节可控 |
| C++/CLI 混合程序集 | C++ 托管类桥接 | 已过时、调试困难、跨平台差 |
| CTP.NET 第三方库 | 社区维护的 .NET 封装 | 省时间，但依赖外人、更新滞后 |
| QuantBox 等框架 | 完整的交易框架封装 | 太重、学习成本高 |

**选择：P/Invoke 自封装**

```
理由：
  1. 20 年 C# 经验 → P/Invoke 是舒适区
  2. 个人项目 → 不需要团队协作的通用接口
  3. 只需要 MdApi（行情）+ TraderApi（交易）子集，不需要全部 300+ 个结构体
  4. 封装层代码量不大（~1000 行），可控
  5. CTP 版本偶有更新 → 自己封装可以精确适配当前版本
```

---

## 2. CTP DLL 架构

```
CTP 提供了两个独立的 DLL：

  thostmduserapi.dll      行情 API (MdApi)
  ├── CreateFtdcMdApi()   创建行情实例
  ├── CreateMdSpi()       创建回调接口
  ├── RegisterSpi()       注册回调
  ├── RegisterFront()     设置前置地址
  ├── Login()             登录
  ├── SubscribeMarketData() 订阅合约
  └── → 回调 OnRtnDepthMarketData()  行情推送

  thosttraderapi.dll      交易 API (TraderApi)
  ├── CreateFtdcTraderApi() 创建交易实例
  ├── RegisterSpi()       注册回调
  ├── RegisterFront()     设置前置地址
  ├── Login()             登录
  ├── ReqOrderInsert()    下单
  └── → 回调 OnRtnOrder() / OnRtnTrade()  订单/成交回报

关键约束：
  - 创建实例时传入 data 目录路径(存放流文件，用于断线恢复)
  - Login 是异步的，结果通过 OnRspUserLogin 回调返回
  - 连接断开 → OnFrontDisconnected 回调
  - 需要定期确认心跳
```

---

## 3. 封装架构

```
┌──────────────────────────────────────────────────────┐
│                 TradingStudio.Ctp                     │
├──────────────────────────────────────────────────────┤
│                                                      │
│  ┌──────────────────┐    ┌──────────────────┐       │
│  │ CtpMdProvider    │    │ CtpTdProvider    │       │
│  │ : IMarketData    │    │ : IExecution     │       │
│  │   Provider       │    │   Gateway        │       │
│  │                  │    │                  │       │
│  │ · ConnectAsync() │    │ · ConnectAsync() │       │
│  │ · Subscribe()    │    │ · InsertOrder()  │       │
│  │ · OnTick 事件     │    │ · CancelOrder()  │       │
│  │                  │    │ · OnOrder 事件    │       │
│  └────────┬─────────┘    │ · OnFill 事件     │       │
│           │              └────────┬─────────┘       │
│           │                       │                  │
│  ┌────────▼────────────────────────▼──────────┐    │
│  │         CtpNativeInterop (internal)         │    │
│  │                                             │    │
│  │  P/Invoke 层：                               │    │
│  │  · DllImport("thostmduserapi.dll")          │    │
│  │  · DllImport("thosttraderapi.dll")          │    │
│  │  · C 结构体 ↔ C# struct 映射                 │    │
│  │  · 回调委托 → .NET 事件                      │    │
│  └────────────────────────────────────────────┘    │
│                                                      │
└──────────────────────────────────────────────────────┘
```

---

## 4. P/Invoke 核心实现

### 4.1 C# 结构体（与 CTP C++ 结构体一一对应）

```csharp
// 只定义我们需要的字段，不是全部 300+ 字段。其他字段用 byte[] padding 占位。

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct CtpDepthMarketData
{
    // 我们需要的字段（按 CTP 结构体顺序）
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string TradingDay;        // 交易日

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)]
    public string InstrumentID;       // 合约代码

    public double LastPrice;
    public double PreSettlementPrice;
    public double PreClosePrice;
    public double OpenInterest;
    public double OpenPrice;
    public double HighestPrice;
    public double LowestPrice;
    public int Volume;
    public double Turnover;
    public double UpperLimitPrice;
    public double LowerLimitPrice;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string UpdateTime;         // HH:mm:ss

    public int UpdateMillisec;

    public double BidPrice1;
    public int BidVolume1;
    public double AskPrice1;
    public int AskVolume1;

    // 深度 L2-L5 也需要占位（P/Invoke 需要完整结构体大小）
    public double BidPrice2; public int BidVolume2;
    public double AskPrice2; public int AskVolume2;
    public double BidPrice3; public int BidVolume3;
    public double AskPrice3; public int AskVolume3;
    public double BidPrice4; public int BidVolume4;
    public double AskPrice4; public int AskVolume4;
    public double BidPrice5; public int BidVolume5;
    public double AskPrice5; public int AskVolume5;

    public double AveragePrice;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string ActionDay;          // 业务日期
}
```

### 4.2 函数导入

```csharp
// 行情 API
internal static class CtpMdNative
{
    [DllImport("thostmduserapi.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr CreateFtdcMdApi(string flowPath);

    [DllImport("thostmduserapi.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RegisterSpi(IntPtr api, CtpMdSpiDelegate spi);

    [DllImport("thostmduserapi.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RegisterFront(IntPtr api, string frontAddress);

    [DllImport("thostmduserapi.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Login(IntPtr api, ref CtpLoginField login);

    [DllImport("thostmduserapi.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SubscribeMarketData(
        IntPtr api,
        [In, Out] string[] instruments,
        int count);

    [DllImport("thostmduserapi.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Release(IntPtr api);
}

// 交易 API 结构类似，函数名不同（thosttraderapi.dll）
```

### 4.3 回调委托

```csharp
// CTP 的 C++ 虚函数 → C# 委托

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void CtpOnRtnDepthMarketDataDelegate(
    IntPtr api,
    ref CtpDepthMarketData data);

// 在 C++ 层注册后，CTP 每收到一个 tick 就调用此委托
// → 我们在委托中转换为 TickRecord → 推入 Channel
```

---

## 5. 托管封装层（对外接口）

### 5.1 CtpMdProvider

```csharp
public class CtpMdProvider : IMarketDataProvider, IDisposable
{
    // IMarketDataProvider 实现
    public ConnectionState State { get; private set; }
    public event EventHandler<TickRecord>? OnTick;
    public event EventHandler<ConnectionState>? OnConnectionChanged;

    public async ValueTask ConnectAsync(CancellationToken ct)
    {
        // 1. CreateFtdcMdApi(flowPath)
        // 2. RegisterSpi → 绑定所有回调
        // 3. RegisterFront → tcp://broker_ip:port
        // 4. Login → 异步等待 OnRspUserLogin 回调
        //    → 用 TaskCompletionSource 将回调转为 async
        // 5. State = Connected
    }

    public async ValueTask SubscribeAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        // SubscribeMarketData(symbols.ToArray(), symbols.Count)
        // CTP 要求先登录、再订阅、一次最多 100 个合约
    }

    // --- P/Invoke 回调 → .NET 事件 转换 ---

    private void OnRtnDepthMarketData(IntPtr api, ref CtpDepthMarketData data)
    {
        var tick = new TickRecord
        {
            ExchangeTimestamp = ParseTimestamp(data.TradingDay, data.UpdateTime, data.UpdateMillisec),
            LocalTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastPrice = (long)(data.LastPrice * TickRecord.PriceScale),
            Volume = data.Volume,   // 累计值
            Turnover = data.Turnover,
            OpenInterest = data.OpenInterest,
            BidPrice1 = (long)(data.BidPrice1 * TickRecord.PriceScale),
            AskPrice1 = (long)(data.AskPrice1 * TickRecord.PriceScale),
            BidVolume1 = data.BidVolume1,
            AskVolume1 = data.AskVolume1,
            Flags = BuildFlags(data)
        };

        OnTick?.Invoke(this, tick);
    }
}
```

### 5.2 线程安全

```
CTP 回调线程:
  → OnRtnDepthMarketData 在 CTP 内部工作线程上调用
  → 我们需要最快速度消耗，不要阻塞 CTP 的回调线程
  → 最简方案：回调中只做 TickRecord 转换 + Channel.Writer.TryWrite()
  → 所有重型操作在 Channel reader 侧处理

CTP 线程安全要求:
  → Subscribe/Unsubscribe 必须在同一个线程调用
  → Login 结果通过独立回调返回
  → 不同 API 实例（MdApi vs TraderApi）是独立的，不共享状态
```

---

## 6. 连接生命周期

```
                    ┌─────────────┐
                    │ Disconnected│
                    └──────┬──────┘
                           │ CreateFtdcMdApi()
                           ▼
                    ┌─────────────┐
                    │ Initialized │
                    └──────┬──────┘
                           │ RegisterSpi + RegisterFront
                           ▼
                    ┌─────────────┐
                    │ Connecting  │
                    └──────┬──────┘
                           │ Login() ... 等待回调
                    ┌──────┴──────┐
                    ▼             ▼
            ┌──────────┐  ┌──────────────┐
            │LoggedIn  │  │ LoginFailed  │
            │(Connected)│  │              │
            └────┬─────┘  └──────┬───────┘
                 │               │ 重试
                 │ Subscribe()   ▼
                 ▼          ┌──────────┐
            ┌─────────┐    │ Retrying │
            │ Active  │    └──────────┘
            │(收行情中) │
            └────┬────┘
                 │ 断线
                 ▼
            ┌──────────┐
            │Reconnect │ ← 指数退避, 最多 N 次
            └──────────┘
                 │
                 ▼ (放弃重连)
            ┌──────────────┐
            │Disconnected  │ → 报警
            └──────────────┘
```

---

## 7. 错误处理

```csharp
// CTP 所有函数返回 0 = 成功，非 0 = 错误码
// 错误码可以通过 GetApiErrorMsg() 转为中文描述

// 常见错误：
//   -1: 网络连接失败
//   -2: 未处理请求数超限
//   -3: 每秒请求数超限
//   3:  未登录
//   4:  未授权
//   5:  合约不存在

// 断线处理：
void OnFrontDisconnected(int reason)
{
    // reason = 0x1001 → 网络读错误
    // reason = 0x1002 → 网络写错误
    // reason = 0x2001 → 心跳超时
    // reason = 0x2002 → 发送心跳失败
    // reason = 0x2003 → 收到错误流数据

    State = ConnectionState.Disconnected;
    OnConnectionChanged?.Invoke(this, State);

    // 自动重连（交易时段）或等待下一时段（非交易时段）
    if (IsTradingSession)
        _ = ReconnectAsync();
}
```

---

## 8. 与数据管道的对接

```
CTP 回调线程:
  OnRtnDepthMarketData(CtpDepthMarketData data)
    │
    │  ① 转换: CtpDepthMarketData → TickRecord
    │  ② 推入: Channel.Writer.TryWrite(tickRecord)
    │  ← 耗时 < 1μs，绝不阻塞 CTP 回调线程
    ▼
Channel<TickRecord> (Bounded, Capacity=10000)
    │
    ├──→ [Reader Thread 1] TickStore.AppendAsync  (.tick 文件异步写)
    ├──→ [Reader Thread 2] BarAggregator.OnTick   (内存 Bar 聚合)
    └──→ [Reader Thread 3] SignalR Hub.SendAsync  (前端推送)
```

---

## 9. 测试策略

```
单元测试（不需 CTP DLL）：
  □ TickRecord 转换：CtpDepthMarketData → TickRecord 字段验证
  □ 时间解析：TradingDay + UpdateTime + Millisec → Unix ms
  □ 价格缩放：double → long (×10⁷) → 往返无精度损失
  □ 状态机：ConnectionState 转换逻辑

集成测试（需要 SimNow 模拟环境）：
  □ 连接 → 登录 → 订阅 → 接收 tick（至少 100 条）
  □ Tick 时间连续性检测（无乱序）
  □ 断线 → 自动重连 → tick 恢复
  □ 登录失败 → 错误处理
  □ 订阅失败合约（如不存在的合约代码）
```

---

## 10. CTP 版本兼容

```
CTP API 版本会更新（每年 1-2 次），但核心结构体保持向后兼容。

策略：
  - 结构体定义来自 CTP 官方发布的头文件 (ThostFtdcMdApi.h)
  - 版本升级时只需更新结构体字段偏移
  - 生产环境锁定版本，不频繁升级
  - 安全更新（如 TLS 升级）必须跟进

当前推荐的 CTP 版本：6.7.2 或以上（支持 TLS 1.2）
```
