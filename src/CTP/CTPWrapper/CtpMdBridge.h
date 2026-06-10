#pragma once

// CTP C Bridge — 原生 C++ 封装，导出 C 函数供 P/Invoke
// 链接最新的 thostmduserapi_se.dll (v6.7.13)

#ifdef CTPBRIDGE_EXPORTS
#define CTPBRIDGE_API __declspec(dllexport)
#else
#define CTPBRIDGE_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Opaque handle
typedef void* CtpMdHandle;

// Callback types (C function pointers)
typedef void (*CtpOnTickCallback)(double lastPrice, int volume, double turnover,
    double openInterest, double bidP1, int bidV1, double askP1, int askV1,
    long long exchangeTimestamp, long long localTimestamp,
    double upperLimit, double lowerLimit);
typedef void (*CtpOnConnectedCallback)();
typedef void (*CtpOnDisconnectedCallback)(int reason);
typedef void (*CtpOnLoginCallback)(int errorId, const char* errorMsg);

// API
CTPBRIDGE_API CtpMdHandle CtpMd_Create(const char* flowPath, bool isProduction);
CTPBRIDGE_API void CtpMd_RegisterCallbacks(CtpMdHandle h,
    CtpOnTickCallback onTick,
    CtpOnConnectedCallback onConnected,
    CtpOnDisconnectedCallback onDisconnected,
    CtpOnLoginCallback onLogin);
CTPBRIDGE_API int  CtpMd_Connect(CtpMdHandle h, const char* frontAddr);
CTPBRIDGE_API int  CtpMd_Login(CtpMdHandle h, const char* brokerId, const char* userId, const char* password);
CTPBRIDGE_API int  CtpMd_Subscribe(CtpMdHandle h, const char** symbols, int count);
CTPBRIDGE_API int  CtpMd_Unsubscribe(CtpMdHandle h, const char** symbols, int count);
CTPBRIDGE_API void CtpMd_Release(CtpMdHandle h);

#ifdef __cplusplus
}
#endif
