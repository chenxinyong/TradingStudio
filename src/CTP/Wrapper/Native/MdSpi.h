#pragma once

#pragma managed(push, off)
#include "ThostFtdcMdApi.h"
#pragma managed(pop)

#include <vcclr.h>

namespace CTP { ref class MdApi; }

/// Native SPI — bridges CTP C++ callbacks to managed MdApi events
class MdSpi : public CThostFtdcMdSpi
{
public:
    explicit MdSpi(CTP::MdApi^ wrapper);
    virtual ~MdSpi();

    void OnFrontConnected() override;
    void OnFrontDisconnected(int nReason) override;
    void OnHeartBeatWarning(int nTimeLapse) override;

    void OnRspUserLogin(CThostFtdcRspUserLoginField* pRspUserLogin,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;
    void OnRspUserLogout(CThostFtdcUserLogoutField* pUserLogout,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;

    void OnRspSubMarketData(CThostFtdcSpecificInstrumentField* pSpecificInstrument,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;
    void OnRspUnSubMarketData(CThostFtdcSpecificInstrumentField* pSpecificInstrument,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;

    void OnRtnDepthMarketData(CThostFtdcDepthMarketDataField* pDepthMarketData) override;

    void OnRspError(CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;

private:
    gcroot<CTP::MdApi^> _wrapper;
};
