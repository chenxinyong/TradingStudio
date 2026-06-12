#include "MdSpi.h"
#include "MdApi.h"
#include "StringHelper.h"
#include "DTO/Quote.h"
#include "DTO/LoginInfo.h"
#include <chrono>

MdSpi::MdSpi(CTP::MdApi^ wrapper)
    : _wrapper(wrapper)
{
}

MdSpi::~MdSpi()
{
}

// ==================== Connection ====================

void MdSpi::OnFrontConnected()
{
    _wrapper->RaiseFrontConnected();
}

void MdSpi::OnFrontDisconnected(int nReason)
{
    _wrapper->RaiseFrontDisconnected(nReason);
}

void MdSpi::OnHeartBeatWarning(int nTimeLapse)
{
    _wrapper->RaiseHeartBeatWarning(nTimeLapse);
}

// ==================== Login ====================

void MdSpi::OnRspUserLogin(CThostFtdcRspUserLoginField* p,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;

    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }

    auto info = gcnew CTP::LoginInfo();
    if (p && err->ErrorID == 0) {
        info->TradingDay = CTP::StringHelper::ToManaged(p->TradingDay);
        info->LoginTime  = CTP::StringHelper::ToManaged(p->LoginTime);
        info->BrokerID   = CTP::StringHelper::ToManaged(p->BrokerID);
        info->UserID     = CTP::StringHelper::ToManaged(p->UserID);
        info->FrontID    = p->FrontID;
        info->SessionID  = p->SessionID;
    }
    _wrapper->RaiseLogin(err, info);
}

void MdSpi::OnRspUserLogout(CThostFtdcUserLogoutField* pUserLogout,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;
    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseLogout(err);
}

// ==================== Subscribe ====================

void MdSpi::OnRspSubMarketData(CThostFtdcSpecificInstrumentField* pSpecificInstrument,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    auto err = gcnew CTP::CtpError();
    String^ inst = nullptr;
    if (pSpecificInstrument) {
        inst = CTP::StringHelper::ToManaged(pSpecificInstrument->InstrumentID);
    }
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseSubscribeRsp(inst, err, bIsLast);
}

void MdSpi::OnRspUnSubMarketData(CThostFtdcSpecificInstrumentField* pSpecificInstrument,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    auto err = gcnew CTP::CtpError();
    String^ inst = nullptr;
    if (pSpecificInstrument) {
        inst = CTP::StringHelper::ToManaged(pSpecificInstrument->InstrumentID);
    }
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseUnsubscribeRsp(inst, err, bIsLast);
}

// ==================== Market Data ====================

// Helper: parse TradingDay(YYYYMMDD) + UpdateTime(HH:MM:SS) + Millisec → Unix ms
static long long ParseTimestamp(const char* tradeDay, const char* updateTime, int millisec)
{
    if (!tradeDay || !updateTime) return 0;

    int year, month, day, hour, min, sec;
    if (sscanf_s(tradeDay, "%4d%2d%2d", &year, &month, &day) != 3) return 0;
    if (sscanf_s(updateTime, "%2d:%2d:%2d", &hour, &min, &sec) != 3) return 0;

    // Days since Unix epoch
    auto daysFromEpoch = [](int y, int m, int d) -> int {
        if (m <= 2) { y--; m += 12; }
        int era = (y >= 0 ? y : y - 399) / 400;
        int yoe = y - era * 400;
        int doy = (153 * (m - 3) + 2) / 5 + d - 1;
        int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
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
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch()).count();
}

void MdSpi::OnRtnDepthMarketData(CThostFtdcDepthMarketDataField* pData)
{
    if (!pData) return;

    auto tick = gcnew CTP::Quote();

    // === 标识字段 (Gap 1 & 2 FIXED — now passed to C#!) ===
    // NOTE: CTP 6.7.13 uses reserve1 (not InstrumentID) for the instrument code field
    tick->InstrumentID   = CTP::StringHelper::ToManaged(pData->reserve1);
    tick->ExchangeID     = CTP::StringHelper::ToManaged(pData->ExchangeID);
    tick->TradingDay     = CTP::StringHelper::ToManaged(pData->TradingDay);
    tick->UpdateTime     = CTP::StringHelper::ToManaged(pData->UpdateTime);
    tick->UpdateMillisec = pData->UpdateMillisec;

    // === 价格/量/仓 ===
    tick->LastPrice         = pData->LastPrice;
    tick->PreSettlementPrice = pData->PreSettlementPrice;
    tick->PreClosePrice     = pData->PreClosePrice;
    tick->PreOpenInterest   = pData->PreOpenInterest;
    tick->OpenPrice         = pData->OpenPrice;
    tick->HighestPrice      = pData->HighestPrice;
    tick->LowestPrice       = pData->LowestPrice;
    tick->Volume            = pData->Volume;
    tick->Turnover          = pData->Turnover;
    tick->OpenInterest      = pData->OpenInterest;
    tick->ClosePrice        = pData->ClosePrice;
    tick->SettlementPrice   = pData->SettlementPrice;
    tick->UpperLimitPrice   = pData->UpperLimitPrice;
    tick->LowerLimitPrice   = pData->LowerLimitPrice;

    // === 五档深度 (all 5 levels — Gap 3 FIXED) ===
    tick->BidPrice1 = pData->BidPrice1;  tick->BidVolume1 = pData->BidVolume1;
    tick->AskPrice1 = pData->AskPrice1;  tick->AskVolume1 = pData->AskVolume1;
    tick->BidPrice2 = pData->BidPrice2;  tick->BidVolume2 = pData->BidVolume2;
    tick->AskPrice2 = pData->AskPrice2;  tick->AskVolume2 = pData->AskVolume2;
    tick->BidPrice3 = pData->BidPrice3;  tick->BidVolume3 = pData->BidVolume3;
    tick->AskPrice3 = pData->AskPrice3;  tick->AskVolume3 = pData->AskVolume3;
    tick->BidPrice4 = pData->BidPrice4;  tick->BidVolume4 = pData->BidVolume4;
    tick->AskPrice4 = pData->AskPrice4;  tick->AskVolume4 = pData->AskVolume4;
    tick->BidPrice5 = pData->BidPrice5;  tick->BidVolume5 = pData->BidVolume5;
    tick->AskPrice5 = pData->AskPrice5;  tick->AskVolume5 = pData->AskVolume5;

    tick->AveragePrice = pData->AveragePrice;

    // === 合成时间戳 ===
    tick->ExchangeTimestamp = ParseTimestamp(pData->TradingDay, pData->UpdateTime, pData->UpdateMillisec);
    tick->LocalTimestamp    = GetCurrentUnixMs();

    _wrapper->RaiseQuote(tick);
}

// ==================== Error ====================

void MdSpi::OnRspError(CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;
    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseError(err, nRequestID);
}
