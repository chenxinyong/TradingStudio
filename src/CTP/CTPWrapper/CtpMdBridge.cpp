#define CTPBRIDGE_EXPORTS
#include "CtpMdBridge.h"
#include "ThostFtdcMdApi.h"
#include <string>
#include <cstring>
#include <chrono>
#include <cstdio>

// ===== Native C++ CTP wrapper =====

class CtpMdImpl : public CThostFtdcMdSpi
{
public:
    CtpOnTickCallback        OnTickCb = nullptr;
    CtpOnConnectedCallback   OnConnectedCb = nullptr;
    CtpOnDisconnectedCallback OnDisconnectedCb = nullptr;
    CtpOnLoginCallback       OnLoginCb = nullptr;

    CThostFtdcMdApi* Api = nullptr;

    void OnFrontConnected() override
    {
        if (OnConnectedCb) OnConnectedCb();
    }

    void OnFrontDisconnected(int nReason) override
    {
        if (OnDisconnectedCb) OnDisconnectedCb(nReason);
    }

    void OnRspUserLogin(CThostFtdcRspUserLoginField* pRspUserLogin,
                        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override
    {
        if (OnLoginCb) {
            if (pRspInfo && pRspInfo->ErrorID != 0)
                OnLoginCb(pRspInfo->ErrorID, pRspInfo->ErrorMsg);
            else
                OnLoginCb(0, "OK");
        }
    }

    void OnRtnDepthMarketData(CThostFtdcDepthMarketDataField* pData) override
    {
        if (!OnTickCb || !pData) return;

        // Parse timestamp: TradingDay(YYYYMMDD) + UpdateTime(HH:MM:SS) + Millisec
        // Simplified: use a helper to convert to Unix ms
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

private:
    static long long ParseTimestamp(const char* tradeDay, const char* updateTime, int millisec)
    {
        // Input: tradeDay="20260610", updateTime="21:00:05", millisec=500
        // Output: Unix milliseconds
        if (!tradeDay || !updateTime) return 0;

        int year, month, day, hour, min, sec;
        if (sscanf(tradeDay, "%4d%2d%2d", &year, &month, &day) != 3) return 0;
        if (sscanf(updateTime, "%2d:%2d:%2d", &hour, &min, &sec) != 3) return 0;

        // Days since 1970-01-01 for given date
        auto daysFromEpoch = [](int y, int m, int d) -> int {
            if (m <= 2) { y--; m += 12; }
            int era = (y >= 0 ? y : y - 399) / 400;
            int yoe = y - era * 400;
            int doy = (153 * (m - 3) + 2) / 5 + d - 1;
            int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
            return era * 146097 + doe - 719468; // 719468 = days from 0000-03-01 to 1970-01-01
        };

        int days = daysFromEpoch(year, month, day);
        long long totalMs = (long long)days * 86400000LL
                          + (long long)hour * 3600000LL
                          + (long long)min * 60000LL
                          + (long long)sec * 1000LL
                          + millisec;
        return totalMs;
    }

    static long long GetCurrentUnixMs()
    {
        auto now = std::chrono::system_clock::now();
        return std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()).count();
    }
};

// ===== C API Implementation =====

extern "C" {

CTPBRIDGE_API CtpMdHandle CtpMd_Create(const char* flowPath, bool isProduction)
{
    auto* impl = new CtpMdImpl();
    impl->Api = CThostFtdcMdApi::CreateFtdcMdApi(flowPath, false, false, isProduction);
    if (!impl->Api) {
        delete impl;
        return nullptr;
    }
    impl->Api->RegisterSpi(impl);
    return impl;
}

CTPBRIDGE_API void CtpMd_RegisterCallbacks(CtpMdHandle h,
    CtpOnTickCallback onTick,
    CtpOnConnectedCallback onConnected,
    CtpOnDisconnectedCallback onDisconnected,
    CtpOnLoginCallback onLogin)
{
    if (!h) return;
    auto* impl = static_cast<CtpMdImpl*>(h);
    impl->OnTickCb = onTick;
    impl->OnConnectedCb = onConnected;
    impl->OnDisconnectedCb = onDisconnected;
    impl->OnLoginCb = onLogin;
}

CTPBRIDGE_API int CtpMd_Connect(CtpMdHandle h, const char* frontAddr)
{
    if (!h || !frontAddr) return -1;
    auto* impl = static_cast<CtpMdImpl*>(h);
    impl->Api->RegisterFront((char*)frontAddr);
    impl->Api->Init();
    return 0;
}

CTPBRIDGE_API int CtpMd_Login(CtpMdHandle h, const char* brokerId, const char* userId, const char* password)
{
    if (!h) return -1;
    auto* impl = static_cast<CtpMdImpl*>(h);

    CThostFtdcReqUserLoginField login = {};
    if (brokerId) strncpy(login.BrokerID, brokerId, sizeof(login.BrokerID) - 1);
    if (userId)   strncpy(login.UserID, userId, sizeof(login.UserID) - 1);
    if (password) strncpy(login.Password, password, sizeof(login.Password) - 1);

    return impl->Api->ReqUserLogin(&login, 0);
}

CTPBRIDGE_API int CtpMd_Subscribe(CtpMdHandle h, const char** symbols, int count)
{
    if (!h || !symbols || count <= 0) return -1;
    auto* impl = static_cast<CtpMdImpl*>(h);
    return impl->Api->SubscribeMarketData((char**)symbols, count);
}

CTPBRIDGE_API int CtpMd_Unsubscribe(CtpMdHandle h, const char** symbols, int count)
{
    if (!h || !symbols || count <= 0) return -1;
    auto* impl = static_cast<CtpMdImpl*>(h);
    return impl->Api->UnSubscribeMarketData((char**)symbols, count);
}

CTPBRIDGE_API void CtpMd_Release(CtpMdHandle h)
{
    if (!h) return;
    auto* impl = static_cast<CtpMdImpl*>(h);
    if (impl->Api) {
        impl->Api->RegisterSpi(nullptr);
        impl->Api->Release();
    }
    delete impl;
}

} // extern "C"
