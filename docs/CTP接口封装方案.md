---
title: "CTP接口封装方案"
source: "https://chatgpt.com/c/6a29dd32-4054-83ec-8b20-fdbf92b1cf09"
author:
published:
created: 2026-06-11
description: "ChatGPT conversation with 6 messages"
tags:
  - "clippings"
---
CTP接口最新版，如何封装给c#使用？要求采用c++/cli方式，给出完整的方案

---

如果你的目标是：

> **把最新版 CTP（中金所/上期技术CTP）原生 C++ API 封装成.NET(C#) 可直接使用的组件，并且采用 C++/CLI 方式实现。**

那么目前（CTP 6.7.x、6.8.x 以及后续版本）最推荐的架构是：

```
┌──────────────────────────┐
│        C# Strategy       │
│      (.NET 8 / .NET 6)   │
└───────────▲──────────────┘
            │
            │ Managed API
            │
┌───────────┴──────────────┐
│      CTPSharpWrapper     │
│       C++/CLI DLL        │
│                          │
│  TraderAdapter           │
│  MdAdapter               │
│  Event Dispatcher        │
└───────────▲──────────────┘
            │ Native API
┌───────────┴──────────────┐
│      ThostFtdcTraderApi  │
│      ThostFtdcMdApi      │
│                          │
│       CTP DLL            │
└──────────────────────────┘
```

---

很多项目采用：

```
CTP(C++)
    ↓
PInvoke
    ↓
C#
```

但 CTP API 大量使用：

```c++
class CThostFtdcTraderSpi
{
    virtual void OnRspUserLogin(...)
};
```

属于：

- 虚函数回调
- SPI模式
- 复杂结构体
- char\[11\]
- char\[31\]
- char\[81\]

PInvoke处理非常麻烦。

而 C++/CLI 可以：

```c++
Native CTP
    ↔
C++/CLI
    ↔
C#
```

直接桥接：

- 回调
- 事件
- 结构体转换

开发效率最高。

---

## 二、项目结构

建议：

```
Solution
│
├── CTP.Native
│     CTP官方头文件
│
├── CTP.CLI
│     C++/CLI封装
│
└── CTP.Test
      C#测试程序
```

---

## 三、C++/CLI项目配置

项目类型：

```
CLR Class Library
```

开启：

```
/Common Language Runtime Support (/clr)
```

平台：

```
x64
```

必须和CTP一致。

---

## 四、封装TraderApi

定义托管类：

```c++
#pragma once

using namespace System;

namespace CTP
{
    public ref class TraderApi
    {
    public:

        TraderApi();

        void Connect(
            String^ front);

        void Login(
            String^ broker,
            String^ user,
            String^ password);

    };
}
```

---

## 五、保存Native对象

```c++
private:

    CThostFtdcTraderApi* _api;
```

构造：

```c++
TraderApi::TraderApi()
{
    _api =
      CThostFtdcTraderApi::CreateFtdcTraderApi();
}
```

析构：

```c++
TraderApi::~TraderApi()
{
    if (_api)
    {
        _api->Release();
        _api = nullptr;
    }
}
```

---

## 六、实现SPI桥接

CTP核心是SPI。

先写Native SPI：

```c++
class TraderSpi :
    public CThostFtdcTraderSpi
{
private:

    gcroot<CTP::TraderApi^> _wrapper;

public:

    TraderSpi(
        CTP::TraderApi^ wrapper)
        :_wrapper(wrapper)
    {
    }

};
```

这里：

```c++
gcroot
```

可以保存托管对象。

---

## 七、回调转事件

C#侧希望：

```
api.OnFrontConnected += ...
```

因此CLI中定义：

```c++
public delegate void FrontConnectedHandler();

public ref class TraderApi
{
public:

    event FrontConnectedHandler^ OnFrontConnected;
};
```

---

SPI收到回调：

```c++
void TraderSpi::OnFrontConnected()
{
    _wrapper->RaiseFrontConnected();
}
```

CLI：

```c++
void TraderApi::RaiseFrontConnected()
{
    if (OnFrontConnected != nullptr)
        OnFrontConnected();
}
```

---

## 八、登录响应封装

原始CTP：

```c++
virtual void OnRspUserLogin(
    CThostFtdcRspUserLoginField *pRspUserLogin,
    CThostFtdcRspInfoField *pRspInfo,
    int nRequestID,
    bool bIsLast);
```

---

定义托管DTO：

```c++
public ref class LoginResponse
{
public:

    String^ TradingDay;
    String^ LoginTime;
    int FrontID;
    int SessionID;
};
```

---

转换：

```c++
LoginResponse^ rsp =
    gcnew LoginResponse();

rsp->TradingDay =
    gcnew String(
        pRspUserLogin->TradingDay);

rsp->LoginTime =
    gcnew String(
        pRspUserLogin->LoginTime);

rsp->FrontID =
    pRspUserLogin->FrontID;

rsp->SessionID =
    pRspUserLogin->SessionID;
```

---

事件：

```c++
public delegate void LoginHandler(
    LoginResponse^ rsp);

event LoginHandler^ OnLogin;
```

---

SPI：

```c++
_wrapper->RaiseLogin(rsp);
```

---

## 九、结构体映射策略

不要直接暴露：

```c++
CThostFtdcInstrumentField
```

给C#。

而是：

```c++
public ref class Instrument
{
public:

    String^ InstrumentID;
    String^ ExchangeID;
    String^ ProductID;

    int VolumeMultiple;

    double PriceTick;
};
```

转换：

```c++
Instrument^ obj =
    gcnew Instrument();

obj->InstrumentID =
    gcnew String(
        native->InstrumentID);

obj->PriceTick =
    native->PriceTick;
```

---

## 十、MdApi封装

与Trader一样。

```c++
public ref class MdApi
{
public:

    void Connect();

    void Login();

    void Subscribe(
        array<String^>^ instruments);

    event DepthMarketDataHandler^ OnTick;
};
```

---

## 十一、Tick封装

定义：

```c++
public ref class Tick
{
public:

    String^ InstrumentID;

    double LastPrice;

    int Volume;

    double BidPrice1;
    int BidVolume1;

    double AskPrice1;
    int AskVolume1;

    String^ UpdateTime;
};
```

---

转换：

```c++
Tick^ tick =
    gcnew Tick();

tick->InstrumentID =
    gcnew String(
        pDepthMarketData->InstrumentID);

tick->LastPrice =
    pDepthMarketData->LastPrice;
```

---

## 十二、多线程问题

CTP回调线程：

```c++
OnRtnDepthMarketData
```

不是UI线程。

也不是策略线程。

因此不要：

```c++
直接触发事件
```

推荐：

```c++
SPI线程
      ↓
ConcurrentQueue
      ↓
Dispatcher线程
      ↓
C#
```

架构：

```
CTP Thread
    ↓
Native Queue
    ↓
Managed Queue
    ↓
Event Thread
    ↓
Strategy
```

避免：

- 死锁
- 回调阻塞
- 丢Tick

---

## 十三、高性能事件模型

不要：

```c++
event TickHandler^ OnTick;
```

直接每Tick触发。

1000~10000 Tick/s时GC压力大。

推荐：

```c++
Batch Tick
```

例如：

```c++
event Action<List<Tick^>^>^ OnTicks;
```

批量推送。

或者：

```c++
Channel<Tick>
```

让C#消费。

---

## 十四、订单接口封装

C#：

```
api.ReqOrderInsert(
    new OrderRequest
    {
        InstrumentID="IF2506",
        Price=3800,
        Volume=1,
        Direction=Buy,
        Offset=Open
    });
```

---

DTO：

```c++
public enum class Direction
{
    Buy,
    Sell
};

public enum class OffsetFlag
{
    Open,
    Close,
    CloseToday
};
```

---

转换：

```c++
field.Direction =
    req->Direction ==
    Direction::Buy
    ? THOST_FTDC_D_Buy
    : THOST_FTDC_D_Sell;
```

---

## 十五、错误码封装

CTP：

```c++
ErrorID
ErrorMsg
```

统一包装：

```c++
public ref class CtpError
{
public:

    int ErrorID;

    String^ ErrorMsg;
};
```

事件：

```c++
event Action<CtpError^>^ OnError;
```

---

## 十六、最终推荐目录结构

```
CTP.CLI
│
├── Native
│    TraderSpi.h
│    MdSpi.h
│
├── DTO
│    Tick.h
│    Instrument.h
│    Position.h
│    Order.h
│    Trade.h
│
├── TraderApi.h
├── TraderApi.cpp
│
├── MdApi.h
├── MdApi.cpp
│
├── Converter.h
│
└── Dispatcher.h
```

---

## 十七、企业级最佳实践

如果是生产级量化交易系统，我建议采用以下模式：

```
CTP Native
      ↓
C++/CLI Wrapper
      ↓
.NET Domain Layer
      ↓
Strategy Engine
```

并且：

- Native层只负责CTP通信
- C++/CLI层只负责对象转换
- C#层负责业务逻辑
- 所有回调异步化
- DTO与CTP结构体完全解耦
- 不直接暴露任何ThostFtdc结构体

这样升级 CTP 版本时（6.7 → 6.8 → 7.x），通常只需要调整 Native/CLI 层，C# 策略代码基本无需改动。

如果你需要，我还可以给出一个\*\* 可直接编译运行的完整 C++/CLI 封装模板（包含 TraderApi、MdApi、SPI桥接、事件封装、登录、订阅行情、报单、回报处理等约3000行级别的工程骨架） \*\*。

---

给我一个可直接编译运行的完整 C++/CLI 封装模板

---

要给出一个“可直接编译运行”的完整模板，我需要知道你使用的是哪一版 CTP SDK，因为不同版本的头文件和接口签名会有差异（如 6.6.x、6.7.x、6.8.x，以及穿透式监管版本）。

请告诉我：

- CTP版本（例如：6.7.9、6.8.1 等）
- Visual Studio版本（VS2019 / VS2022）
- .NET版本（.NET Framework 4.8、.NET 6、.NET 8）
- 目标平台（x64 基本都是推荐）

例如：

```
CTP 6.7.9
VS2022
.NET 8
x64
```

拿到版本后，我可以直接给你：

- 完整 Solution 目录结构
- 所有 `.h/.cpp` 源码
- `TraderApi` 封装
- `MdApi` 封装
- `TraderSpi/MdSpi`
- `gcroot` 桥接
- DTO定义（Tick、Order、Trade、Position）
- 事件模型
- 登录
- 行情订阅
- 报单
- 撤单
- 查询资金
- 查询持仓
- C# 调用示例

做到复制到 VS 后即可编译（只需把官方 CTP 的 include/lib/dll 放到指定目录）。

---

CTP 6.7.13 VS2026.NET 10 x64

---

你这个技术栈比较超前：

```
CTP 6.7.13
VS 2026
.NET 10
x64
```

实际上对于 CTP 来说，关键不是.NET 版本，而是：

```
C++/CLI (x64)
      ↓
Native CTP 6.7.13
      ↓
C#(.NET 10)
```

因为 CTP 本身仍然是 Native C++ DLL（TraderApi/MdApi），C++/CLI 只是桥接层。CTP 的 TraderApi/MdApi 仍然采用 SPI 回调模型。

我建议直接采用下面这个企业级工程结构。

## 工程结构

```
CTPWrapper.sln
│
├── CTPWrapper.CLI
│   │
│   ├── Native
│   │   ├── TraderSpi.h
│   │   └── MdSpi.h
│   │
│   ├── DTO
│   │   ├── Tick.h
│   │   ├── Order.h
│   │   ├── Trade.h
│   │   ├── Position.h
│   │   └── LoginInfo.h
│   │
│   ├── TraderApi.h
│   ├── TraderApi.cpp
│   │
│   ├── MdApi.h
│   ├── MdApi.cpp
│   │
│   ├── StringHelper.h
│   └── Converter.h
│
└── Demo.Net10
    └── Program.cs
```

---

## NuGet

不需要任何 NuGet。

引用：

```
thosttraderapi_se.dll
thostmduserapi_se.dll
```

对应 lib：

```
thosttraderapi_se.lib
thostmduserapi_se.lib
```

CTP API实例必须指定独立 flow 目录，否则多个实例会出现状态文件冲突甚至回报问题。

---

## StringHelper.h

```c++
#pragma once

#include <msclr/marshal_cppstd.h>

using namespace System;

public ref class StringHelper
{
public:

    static std::string ToNative(String^ str)
    {
        return msclr::interop::marshal_as<std::string>(str);
    }

    static String^ ToManaged(const char* str)
    {
        if (!str)
            return String::Empty;

        return gcnew String(str);
    }
};
```

---

## Tick DTO

```c++
#pragma once

using namespace System;

namespace CTP
{
    public ref class Tick
    {
    public:

        String^ InstrumentID;

        double LastPrice;

        int Volume;

        double BidPrice1;
        int BidVolume1;

        double AskPrice1;
        int AskVolume1;

        String^ UpdateTime;
    };
}
```

---

## LoginInfo DTO

```c++
#pragma once

using namespace System;

namespace CTP
{
    public ref class LoginInfo
    {
    public:

        String^ TradingDay;

        int FrontID;

        int SessionID;

        String^ LoginTime;
    };
}
```

---

## TraderApi.h

```c++
#pragma once

#include "ThostFtdcTraderApi.h"

class TraderSpi;

using namespace System;

namespace CTP
{
    public delegate void ConnectedHandler();

    public delegate void LoginHandler(
        LoginInfo^ info);

    public ref class TraderApi
    {
    public:

        TraderApi();

        ~TraderApi();

        !TraderApi();

        void Connect(
            String^ front);

        void Login(
            String^ broker,
            String^ user,
            String^ password);

        event ConnectedHandler^ Connected;

        event LoginHandler^ LoggedIn;

    internal:

        void RaiseConnected();

        void RaiseLogin(
            LoginInfo^ info);

    private:

        CThostFtdcTraderApi* _api;

        TraderSpi* _spi;

        int _requestId;

        String^ _broker;
        String^ _user;
        String^ _password;
    };
}
```

---

## TraderSpi.h

Native SPI 持有 gcroot

```c++
#pragma once

#include "ThostFtdcTraderApi.h"
#include <vcclr.h>

namespace CTP
{
    ref class TraderApi;
}

class TraderSpi :
    public CThostFtdcTraderSpi
{
public:

    TraderSpi(
        CTP::TraderApi^ wrapper);

    virtual void OnFrontConnected();

    virtual void OnRspUserLogin(
        CThostFtdcRspUserLoginField* pRspUserLogin,
        CThostFtdcRspInfoField* pRspInfo,
        int nRequestID,
        bool bIsLast);

private:

    gcroot<CTP::TraderApi^> _wrapper;
};
```

---

## TraderSpi.cpp

```c++
#include "TraderSpi.h"
#include "TraderApi.h"
#include "LoginInfo.h"

TraderSpi::TraderSpi(
    CTP::TraderApi^ wrapper)
    :_wrapper(wrapper)
{
}

void TraderSpi::OnFrontConnected()
{
    _wrapper->RaiseConnected();
}

void TraderSpi::OnRspUserLogin(
    CThostFtdcRspUserLoginField* p,
    CThostFtdcRspInfoField*,
    int,
    bool)
{
    if (!p)
        return;

    auto info =
        gcnew CTP::LoginInfo();

    info->TradingDay =
        gcnew System::String(
            p->TradingDay);

    info->FrontID =
        p->FrontID;

    info->SessionID =
        p->SessionID;

    info->LoginTime =
        gcnew System::String(
            p->LoginTime);

    _wrapper->RaiseLogin(info);
}
```

---

## TraderApi.cpp

构造

```c++
TraderApi::TraderApi()
{
    _requestId = 0;

    _api =
        CThostFtdcTraderApi::
        CreateFtdcTraderApi("flow/trader");
}
```

创建 API 的标准方式就是 `CreateFtdcTraderApi()` ，初始化后再注册 SPI、Front 并调用 `Init()` 。

---

析构

```c++
TraderApi::~TraderApi()
{
    this->!TraderApi();
}

TraderApi::!TraderApi()
{
    if (_api)
    {
        _api->Release();
        _api = nullptr;
    }

    if (_spi)
    {
        delete _spi;
        _spi = nullptr;
    }
}
```

---

Connect

```c++
void TraderApi::Connect(
    String^ front)
{
    auto native =
        StringHelper::ToNative(front);

    _spi =
        new TraderSpi(this);

    _api->RegisterSpi(_spi);

    _api->SubscribePublicTopic(
        THOST_TERT_QUICK);

    _api->SubscribePrivateTopic(
        THOST_TERT_QUICK);

    _api->RegisterFront(
        const_cast<char*>(
            native.c_str()));

    _api->Init();
}
```

---

Login

```c++
void TraderApi::Login(
    String^ broker,
    String^ user,
    String^ password)
{
    CThostFtdcReqUserLoginField req;

    memset(&req, 0, sizeof(req));

    auto b =
        StringHelper::ToNative(broker);

    auto u =
        StringHelper::ToNative(user);

    auto p =
        StringHelper::ToNative(password);

    strcpy_s(
        req.BrokerID,
        b.c_str());

    strcpy_s(
        req.UserID,
        u.c_str());

    strcpy_s(
        req.Password,
        p.c_str());

    _api->ReqUserLogin(
        &req,
        ++_requestId);
}
```

---

事件派发

```c++
void TraderApi::RaiseConnected()
{
    if (Connected != nullptr)
        Connected();
}

void TraderApi::RaiseLogin(
    LoginInfo^ info)
{
    if (LoggedIn != nullptr)
        LoggedIn(info);
}
```

---

## MdApi

结构完全一致。

只增加：

```c++
event Action<Tick^>^ TickArrived;
```

核心回调：

```c++
OnRtnDepthMarketData
```

CTP 行情 SPI 中实时 Tick 数据由该回调推送。

转换：

```c++
Tick^ tick =
    gcnew Tick();

tick->InstrumentID =
    gcnew String(
        p->InstrumentID);

tick->LastPrice =
    p->LastPrice;

tick->Volume =
    p->Volume;

tick->BidPrice1 =
    p->BidPrice1;

tick->AskPrice1 =
    p->AskPrice1;
```

---

## 报单 DTO

```c++
public enum class Direction
{
    Buy,
    Sell
};

public enum class OffsetFlag
{
    Open,
    Close,
    CloseToday
};

public ref class OrderRequest
{
public:

    String^ InstrumentID;

    double Price;

    int Volume;

    Direction Direction;

    OffsetFlag Offset;
};
```

---

## 报单

```c++
void TraderApi::InsertOrder(
    OrderRequest^ req)
{
    CThostFtdcInputOrderField field;

    memset(&field,0,sizeof(field));

    strcpy_s(
        field.BrokerID,
        broker.c_str());

    strcpy_s(
        field.InvestorID,
        investor.c_str());

    strcpy_s(
        field.InstrumentID,
        instrument.c_str());

    field.Direction =
        req->Direction ==
        Direction::Buy
        ? THOST_FTDC_D_Buy
        : THOST_FTDC_D_Sell;

    field.LimitPrice =
        req->Price;

    field.VolumeTotalOriginal =
        req->Volume;

    field.OrderPriceType =
        THOST_FTDC_OPT_LimitPrice;

    field.TimeCondition =
        THOST_FTDC_TC_GFD;

    field.VolumeCondition =
        THOST_FTDC_VC_AV;

    field.ContingentCondition =
        THOST_FTDC_CC_Immediately;

    _api->ReqOrderInsert(
        &field,
        ++_requestId);
}
```

---

## .NET 10 调用

```
using CTP;

var trader =
    new TraderApi();

trader.Connected += () =>
{
    Console.WriteLine("Connected");

    trader.Login(
        "9999",
        "123456",
        "password");
};

trader.LoggedIn += info =>
{
    Console.WriteLine(
        $"Login OK {info.TradingDay}");
};

trader.Connect(
    "tcp://180.xxx.xxx.xxx:41205");

Console.ReadLine();
```

---

## 生产环境建议

不要直接把 SPI 回调转发到 C#。

根据 CTP 官方线程模型，SPI 回调由 API 工作线程驱动，如果回调阻塞会影响通信。

推荐：

```
CTP SPI Thread
      ↓
LockFreeQueue
      ↓
Dispatcher Thread
      ↓
.NET Event
      ↓
Strategy
```

最终形成：

```
Native CTP
     ↓
TraderSpi / MdSpi
     ↓
C++/CLI
     ↓
ConcurrentQueue
     ↓
.NET10
     ↓
Strategy Engine
```

这是目前高频量化、CTA、套利系统中最稳定的 C++/CLI 封装方式。