#pragma once

#pragma managed(push, off)
#include "ThostFtdcTraderApi.h"
#pragma managed(pop)

#include <vcclr.h>

namespace CTP { ref class TraderApi; }

/// Native SPI — bridges CTP C++ callbacks to managed TraderApi events
class TraderSpi : public CThostFtdcTraderSpi
{
public:
    explicit TraderSpi(CTP::TraderApi^ wrapper);
    virtual ~TraderSpi();

    void OnFrontConnected() override;
    void OnFrontDisconnected(int nReason) override;
    void OnHeartBeatWarning(int nTimeLapse) override;

    void OnRspAuthenticate(CThostFtdcRspAuthenticateField* pRspAuthenticateField,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;
    void OnRspUserLogin(CThostFtdcRspUserLoginField* pRspUserLogin,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;
    void OnRspUserLogout(CThostFtdcUserLogoutField* pUserLogout,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;

    void OnRspOrderInsert(CThostFtdcInputOrderField* pInputOrder,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;
    void OnRspOrderAction(CThostFtdcInputOrderActionField* pInputOrderAction,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;
    void OnRtnOrder(CThostFtdcOrderField* pOrder) override;
    void OnRtnTrade(CThostFtdcTradeField* pTrade) override;

    void OnRspQryInvestorPosition(CThostFtdcInvestorPositionField* pInvestorPosition,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;
    void OnRspQryTradingAccount(CThostFtdcTradingAccountField* pTradingAccount,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;

    void OnRspSettlementInfoConfirm(CThostFtdcSettlementInfoConfirmField* pSettlementInfoConfirm,
        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;

    void OnRspError(CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override;

private:
    gcroot<CTP::TraderApi^> _wrapper;
};
