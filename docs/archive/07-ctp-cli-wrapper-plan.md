> ⚠️ **已废弃** — 本文档描述 C++/CLI 方案，实际代码采用更简单的「C++ 原生桥接 → C 导出 → P/Invoke」路线。C++/CLI 实验代码保留在 `src/CTP/CTPWrapper.CLI/` 作为备选参考。替代文档：[11-implementation-roadmap-v2.md](11-implementation-roadmap-v2.md)（当前代码状态）

# C++/CLI CTP 封装执行方案（已废弃）

> 用 C++/CLI 直接链接最新 CTP DLL，生成 .NET 10 可引用的托管 DLL。

---

## 1. 为什么不继续用 FtdcNet.CTP

```
FtdcNet.CTP 1.4.0:
  ftdc2c_ctp.dll  →  C-wrapper（2023编译，v6.7.2）
  thostmduserapi_se.dll  →  随包附带（v6.7.2）

问题:
  - ftdc2c_ctp.dll 是闭源的 C 中间层，无法升级
  - 替换 thostmduserapi_se.dll 为新版 → struct 布局不兼容 → 崩溃
  - 绑死 2023 年的 CTP DLL，无法跟进最新版

C++/CLI 方案:
  - 直接从 C++ include 最新 CTP header
  - 链接最新 thostmduserapi_se.dll (2026-02-25)
  - 生成一个 .NET DLL，C# 直接引用，无中间层
```

---

## 2. 架构

```
┌─────────────────────────────────────────────────────┐
│              TradingStudio.Ctp.dll                    │
│              (C++/CLI 混合程序集)                      │
├─────────────────────────────────────────────────────┤
│                                                      │
│  ┌──────────────────────────────────────┐           │
│  │  Managed Layer (C++/CLI ref class)    │           │
│  │  CtpMdProvider                       │           │
│  │    · event Action<TickRecord> OnTick │           │
│  │    · ConnectAsync(addr, broker,user,pwd)        │
│  │    · SubscribeAsync(symbols[])       │           │
│  │    · DisconnectAsync()              │           │
│  └──────────────┬───────────────────────┘           │
│                 │ 持有 gcroot<>                       │
│  ┌──────────────▼───────────────────────┐           │
│  │  Native Layer (C++)                   │           │
│  │  CtpMdSpi : CThostFtdcMdSpi          │           │
│  │    · OnRtnDepthMarketData() override │           │
│  │    · OnFrontConnected() override     │           │
│  │    · OnRspUserLogin() override       │           │
│  │    · 持有 CThostFtdcMdApi*           │           │
│  └──────────────┬───────────────────────┘           │
│                 │                                     │
└─────────────────┼─────────────────────────────────────┘
                  │ 链接
┌─────────────────▼─────────────────────────────────────┐
│         thostmduserapi_se.dll (CTP 2026-02-25)          │
│         原生 C++ DLL，期货公司/SimNow 提供               │
└───────────────────────────────────────────────────────┘
```

---

## 3. 项目文件

### 3.1 目录结构

```
TradingStudio.Ctp/
├── TradingStudio.Ctp.vcxproj       ← C++/CLI 项目文件
├── Native/
│   ├── CtpMdSpi.h                  ← 实现 CThostFtdcMdSpi
│   └── CtpMdSpi.cpp
├── Managed/
│   ├── CtpMdProvider.h             ← 托管 ref class
│   └── CtpMdProvider.cpp
├── Mapping/
│   └── TickMapper.h                ← CTP struct → TickRecord 转换
├── include/                         ← CTP 官方头文件
│   ├── ThostFtdcMdApi.h
│   ├── ThostFtdcUserApiStruct.h
│   └── ThostFtdcUserApiDataType.h
└── lib/                             ← CTP 官方 DLL + lib
    ├── thostmduserapi_se.dll
    └── thostmduserapi_se.lib
```

### 3.2 vcxproj 关键配置

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <CLRSupport>NetCore</CLRSupport>       <!-- 关键: C++/CLI for .NET -->
    <ConfigurationType>DynamicLibrary</ConfigurationType>
  </PropertyGroup>
  <ItemGroup>
    <!-- CTP 头文件 -->
    <ClInclude Include="include\ThostFtdcMdApi.h" />
    <ClInclude Include="include\ThostFtdcUserApiStruct.h" />
    <ClInclude Include="include\ThostFtdcUserApiDataType.h" />
    <!-- 我们的代码 -->
    <ClInclude Include="Native\CtpMdSpi.h" />
    <ClInclude Include="Managed\CtpMdProvider.h" />
    <ClCompile Include="Native\CtpMdSpi.cpp" />
    <ClCompile Include="Managed\CtpMdProvider.cpp" />
  </ItemGroup>
  <ItemGroup>
    <!-- CTP DLL 导入库 -->
    <Link Include="lib\thostmduserapi_se.lib" />
  </ItemGroup>
  <ItemGroup>
    <!-- 引用 TradingStudio.Core (获取 TickRecord 等) -->
    <ProjectReference Include="..\TradingStudio.Core\TradingStudio.Core.csproj" />
  </ItemGroup>
</Project>
```

---

## 4. 核心代码

### 4.1 Native Layer — CtpMdSpi.h

```cpp
#pragma once
#include "ThostFtdcMdApi.h"
#include <functional>

// C++ 原生回调实现
class CtpMdSpi : public CThostFtdcMdSpi
{
public:
    // 回调 → 函数指针（无堆分配，最快路径）
    std::function<void()> OnFrontConnectedCallback;
    std::function<void(int)> OnFrontDisconnectedCallback;
    std::function<void(CThostFtdcRspUserLoginField*)> OnLoginCallback;
    std::function<void(CThostFtdcDepthMarketDataField*)> OnTickCallback;

    void OnFrontConnected() override
    {
        if (OnFrontConnectedCallback) OnFrontConnectedCallback();
    }

    void OnFrontDisconnected(int nReason) override
    {
        if (OnFrontDisconnectedCallback) OnFrontDisconnectedCallback(nReason);
    }

    void OnRspUserLogin(CThostFtdcRspUserLoginField *pRspUserLogin,
        CThostFtdcRspInfoField *pRspInfo, int nRequestID, bool bIsLast) override
    {
        if (OnLoginCallback) OnLoginCallback(pRspUserLogin);
    }

    void OnRtnDepthMarketData(CThostFtdcDepthMarketDataField *pData) override
    {
        if (OnTickCallback) OnTickCallback(pData);
    }

    // 连接管理
    bool Connect(const char* frontAddr, const char* brokerId,
                 const char* userId, const char* password, const char* flowPath);
    int Subscribe(const char** symbols, int count);
    void Disconnect();

private:
    CThostFtdcMdApi* _api = nullptr;
};
```

### 4.2 Managed Layer — CtpMdProvider.h

```cpp
#pragma once
#include <vcclr.h>  // gcroot
#include "Native/CtpMdSpi.h"

using namespace System;
using namespace System::Threading::Tasks;
using namespace TradingStudio::Core::Models;

namespace TradingStudio::Ctp
{
    // C++/CLI ref class — C# 直接使用
    public ref class CtpMdProvider
    {
    public:
        // .NET 事件
        event Action<TickRecord>^ OnTick;
        event Action<String^>^ OnConnected;
        event Action<String^>^ OnDisconnected;
        event Action<String^>^ OnLoginResult;

        // 异步连接
        Task<bool>^ ConnectAsync(String^ frontAddr, String^ brokerId,
                                 String^ userId, String^ password);

        // 订阅行情（可多次调用，每次最多 100 个合约）
        Task<int>^ SubscribeAsync(array<String^>^ symbols);

        // 断开
        void Disconnect();

    private:
        // gcroot 让原生 C++ 对象安全持有托管对象的引用
        gcroot<CtpMdSpi*> _native;
        bool _disposed = false;
        ~CtpMdProvider() { this->!CtpMdProvider(); }
        !CtpMdProvider() { Disconnect(); }
    };
}
```

### 4.3 回调桥接 — CtpMdProvider.cpp（核心）

```cpp
#include "Managed/CtpMdProvider.h"
#include "Mapping/TickMapper.h"
#include <msclr/marshal.h>

using namespace msclr::interop;

Task<bool>^ CtpMdProvider::ConnectAsync(String^ frontAddr, String^ brokerId,
                                         String^ userId, String^ password)
{
    auto tcs = gcnew TaskCompletionSource<bool>();

    _native = new CtpMdSpi();

    // === 关键: C++ 回调 → .NET 事件 的桥接 ===

    _native->OnFrontConnectedCallback = [this, tcs]()
    {
        OnConnected("Connected");
    };

    _native->OnLoginCallback = [this, tcs](CThostFtdcRspUserLoginField* rsp)
    {
        auto msg = gcnew String(rsp->TradingDay);
        OnLoginResult(msg);
        tcs->SetResult(true);  // 登录成功 → Task 完成
    };

    _native->OnTickCallback = [this](CThostFtdcDepthMarketDataField* data)
    {
        // C++ struct → C# TickRecord（栈分配，零 GC）
        auto tick = TickMapper::Convert(data);
        OnTick(tick);  // 触发 .NET 事件
    };

    // 调用原生连接
    auto front = marshal_as<std::string>(frontAddr);
    auto broker = marshal_as<std::string>(brokerId);
    auto user = marshal_as<std::string>(userId);
    auto pwd = marshal_as<std::string>(password);
    auto flow = marshal_as<std::string>("./ctp_flow");

    bool ok = _native->Connect(front.c_str(), broker.c_str(),
                                user.c_str(), pwd.c_str(), flow.c_str());
    if (!ok)
        tcs->SetResult(false);

    return tcs->Task;
}
```

### 4.4 TickMapper — C++ struct → C# TickRecord

```cpp
// Mapping/TickMapper.h
#pragma once
#include "ThostFtdcUserApiStruct.h"
#include <TradingStudio.Core/Models/Tick.h>  // C# TickRecord

static TickRecord TickMapper::Convert(CThostFtdcDepthMarketDataField* data)
{
    TickRecord tick;
    tick.ExchangeTimestamp = ParseTimestamp(data->TradingDay, data->UpdateTime, data->UpdateMillisec);
    tick.LocalTimestamp = DateTimeOffset::UtcNow.ToUnixTimeMilliseconds();
    tick.LastPrice = (long long)(data->LastPrice * 10000000.0);
    tick.Volume = data->Volume;
    tick.Turnover = data->Turnover;
    tick.OpenInterest = (long long)data->OpenInterest;
    tick.BidPrice1 = (long long)(data->BidPrice1 * 10000000.0);
    tick.BidVolume1 = data->BidVolume1;
    tick.AskPrice1 = (long long)(data->AskPrice1 * 10000000.0);
    tick.AskVolume1 = data->AskVolume1;
    // Flags: 涨停/跌停/集合竞价
    int flags = 0;
    if (data->LastPrice >= data->UpperLimitPrice) flags |= TickRecord::FLAG_UPPER_LIMIT;
    if (data->LastPrice <= data->LowerLimitPrice) flags |= TickRecord::FLAG_LOWER_LIMIT;
    tick.Flags = flags;
    return tick;
}
```

### 4.5 C# 调用端（最终使用方式）

```csharp
// 在 TradingStudio.App 中使用——与之前完全相同的接口！
using TradingStudio.Ctp;

var md = new CtpMdProvider();
md.OnTick += tick => _channel.Writer.TryWrite(tick);  // → 数据管道
md.OnConnected += msg => _logger.Info("CTP {msg}");

bool ok = await md.ConnectAsync(
    "tcp://180.168.146.187:10131",
    "9999", "13961193449", "***");

if (ok)
    await md.SubscribeAsync(new[] { "cu2607", "ag2608", "rb2610" });
```

---

## 5. 编译环境

```
必需:
  - Visual Studio 2022 (17.8+) 含 "C++/CLI support for .NET" 组件
  - Windows SDK 10.0+
  - .NET 10 SDK

检查:
  - VS Installer → "Desktop development with C++"
  - 确保勾选 "C++/CLI support for v143 build tools (.NET 10)"
```

---

## 6. 实施步骤

```
Step 1 — 环境验证 (30 min)
  □ 安装 VS2022 C++/CLI 工作负载
  □ 创建空白 C++/CLI Class Library (.NET 10)
  □ 确认能编译并引用到 C# 项目

Step 2 — Native Layer (2 h)
  □ 拷贝 CTP 头文件到 include/
  □ 拷贝 CTP DLL + lib 到 lib/
  □ 实现 CtpMdSpi (继承 CThostFtdcMdSpi)
  □ 实现 Connect/Subscribe/Disconnect
  □ 在 C++ 控制台测试: 连接 SimNow → 接收 tick

Step 3 — Managed Layer (2 h)
  □ 实现 CtpMdProvider (C++/CLI ref class)
  □ 实现回调桥接 (std::function → .NET event)
  □ 实现 TickMapper (C++ struct → C# TickRecord)
  □ 实现 Task<T> 异步接口

Step 4 — 集成测试 (1 h)
  □ C# 引用 TradingStudio.Ctp.dll
  □ 连接 SimNow → 订阅 → 接收 tick → 验证
  □ TickRecord 字段正确性验证

Step 5 — 管道对接 (1 h)
  □ CtpMdProvider.OnTick → Channel<TickRecord>
  □ → TickStore + BarAggregator
  □ 完整数据管道跑通
```

---

## 7. 关键优势

| | FtdcNet.CTP | C++/CLI 自封装 |
|---|---|---|
| CTP 版本 | 绑死 v6.7.2 (2023) | **任意版本**（改 header 重编译即可） |
| 中间层 | ftdc2c_ctp.dll (闭源) | **无**（直接链接 CTP DLL） |
| TickRecord 转换 | 需要额外映射 | **直接在 C++ 层完成**，零额外分配 |
| 调试 | 无法进入 C++ 层 | **完全可调试**（源码在手） |
| 维护 | 依赖第三方更新 | **自己控制** |
| 代码量 | ~1000 行（FtdcNet.CTP） | ~500 行（我们只需要 MdApi） |
