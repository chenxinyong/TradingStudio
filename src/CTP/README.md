# CTP 封装层架构

## 层级

```
┌─────────────────────────────────────────┐
│  TradingStudio.Ctp (C# 适配层)          │  ← CtpMdAdapter: Quote → Channel<TickRecord>
├─────────────────────────────────────────┤
│  CTP.Wrapper (C++/CLI 托管封装)          │  ← managed ref class MdApi / TraderApi
│  ├── MdApi.h/.cpp      行情接口          │
│  ├── TraderApi.h/.cpp  交易接口          │
│  ├── Native/MdSpi      原生回调→托管事件  │
│  ├── Native/TraderSpi  原生回调→托管事件  │
│  └── DTO/              C# 可见数据对象    │
├─────────────────────────────────────────┤
│  CTP.SDK (原生 C++)                      │
│  ├── include/          CTP 头文件 (6.7.13)│
│  ├── lib/              静态链接库          │
│  └── dll/              运行时 DLL         │
└─────────────────────────────────────────┘
```

## 数据流

```
CTP行情前置 (tcp://...)                        CTP交易前置 (tcp://...)
  │                                                │
  ▼                                                ▼
thostmduserapi_se.dll                      thosttraderapi_se.dll
  │                                                │
  ▼ (原生回调)                                     ▼ (原生回调)
MdSpi::OnRtnDepthMarketData                TraderSpi::OnRtnOrder/Trade
  │                                                │
  ▼ (gcroot 封送)                                  ▼ (gcroot 封送)
MdApi::OnQuote event (托管)                TraderApi::OnOrder/OnTrade event (托管)
  │                                                │
  ▼ (C# 订阅)                                      ▼ (C# 订阅)
CtpMdAdapter.OnQuote                       CtpTraderBridge.OnOrder/OnTrade
  │                                                │
  ▼                                                ▼
Channel<TickRecord>                        Channel<OrderEvent>
  │                                                │
  ▼                                                ▼
BarAggregator                              ExecutionHandler.FillChannel
  │                                                │
  ▼                                                ▼
BarStore (SQLite/DuckDB)                   PortfolioManager.ProcessFill
```

## C++/CLI 关键设计

### 托管对象与原生回调的桥接

CTP API 通过虚函数回调（SPI 模式）。C++/CLI 将原生 C++ 对象包装在 managed `ref class` 中：

```
CThostFtdcMdSpi (原生纯虚接口)
    ↑ 继承
MdSpi (非托管 C++)
    │ gcroot<MdApi^> 持有托管引用
    ▼ OnRtnDepthMarketData 中封送
MdApi::OnQuote (托管 event)
```

### 内存管理

- `gcroot<T>` 用于从原生 C++ 上下文持有托管引用
- `auto_gcroot` 自动释放
- DTO 使用 `value struct`（栈分配），避免 GC 压力

### 字符串编码

- CTP 使用 GBK 编码的 `char*`
- C# 使用 UTF-16 的 `System::String^`
- `StringHelper.h` 提供 `GBKToString` / `StringToGBK` 转换

## 构建

- **VS 2026**: `CTPWrapper.vcxproj` 编译 C++/CLI
- **dotnet CLI**: 使用预编译的 `CTPWrapper.dll`（`src/CTP/Wrapper/bin/Release/`）
- **发布**: `dotnet publish` 自动包含 `thostmduserapi_se.dll` 和 `thosttraderapi_se.dll`

## C# 适配层

`TradingStudio.Ctp/CtpMdAdapter.cs` — 唯一的 C# 适配代码：

```csharp
var md = new CTP.MdApi();
md.OnQuote += (instId, quote) => {
    var tick = ConvertToTickRecord(quote);  // 42字段 → 80B
    _channel.Writer.TryWrite(tick);         // → BarAggregator
};
md.Connect(front, brokerId, userId, password);
```

交易侧适配在 `TradingStudio.Live/CtpTraderBridge.cs`，直接使用 `CTP.TraderApi`。
