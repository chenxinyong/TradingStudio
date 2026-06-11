#include "TraderSpi.h"
#include "TraderApi.h"
#include "StringHelper.h"
#include "DTO/LoginInfo.h"
#include "DTO/Trade.h"
#include "DTO/Position.h"

TraderSpi::TraderSpi(CTP::TraderApi^ wrapper)
    : _wrapper(wrapper)
{
}

TraderSpi::~TraderSpi()
{
}

// ==================== Connection ====================

void TraderSpi::OnFrontConnected()
{
    _wrapper->RaiseFrontConnected();
}

void TraderSpi::OnFrontDisconnected(int nReason)
{
    _wrapper->RaiseFrontDisconnected(nReason);
}

void TraderSpi::OnHeartBeatWarning(int nTimeLapse)
{
    _wrapper->RaiseHeartBeatWarning(nTimeLapse);
}

// ==================== Auth & Login ====================

void TraderSpi::OnRspAuthenticate(CThostFtdcRspAuthenticateField* pRspAuthenticateField,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;

    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseAuth(err);
}

void TraderSpi::OnRspUserLogin(CThostFtdcRspUserLoginField* p,
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
        info->MaxOrderRef = 0;  // MaxOrderRef is char[11], not easily convertible to int
        info->SHFETime   = CTP::StringHelper::ToManaged(p->SHFETime);
        info->DCETime    = CTP::StringHelper::ToManaged(p->DCETime);
        info->CZCETime   = CTP::StringHelper::ToManaged(p->CZCETime);
        info->FFEXTime   = CTP::StringHelper::ToManaged(p->FFEXTime);
        info->INETime    = CTP::StringHelper::ToManaged(p->INETime);
    }
    _wrapper->RaiseLogin(err, info);
}

void TraderSpi::OnRspUserLogout(CThostFtdcUserLogoutField* pUserLogout,
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

// ==================== Order ====================

void TraderSpi::OnRspOrderInsert(CThostFtdcInputOrderField* pInputOrder,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;
    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseOrderInsertRsp(err, nRequestID);
}

void TraderSpi::OnRspOrderAction(CThostFtdcInputOrderActionField* pInputOrderAction,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;
    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseOrderActionRsp(err, nRequestID);
}

void TraderSpi::OnRtnOrder(CThostFtdcOrderField* pOrder)
{
    if (!pOrder) return;

    auto order = gcnew CTP::Order();
    order->BrokerID      = CTP::StringHelper::ToManaged(pOrder->BrokerID);
    order->InvestorID    = CTP::StringHelper::ToManaged(pOrder->InvestorID);
    order->InstrumentID  = CTP::StringHelper::ToManaged(pOrder->reserve1);
    order->OrderRef      = CTP::StringHelper::ToManaged(pOrder->OrderRef);
    order->OrderSysID    = CTP::StringHelper::ToManaged(pOrder->OrderSysID);
    order->ExchangeID    = CTP::StringHelper::ToManaged(pOrder->ExchangeID);
    order->OrderLocalID  = CTP::StringHelper::ToManaged(pOrder->OrderLocalID);
    order->Direction     = pOrder->Direction;
    order->OffsetFlag    = pOrder->CombOffsetFlag[0];
    order->LimitPrice    = pOrder->LimitPrice;
    order->VolumeTotalOriginal = pOrder->VolumeTotalOriginal;
    order->VolumeTraded  = pOrder->VolumeTraded;
    order->VolumeTotal   = pOrder->VolumeTotal;
    order->OrderStatus   = pOrder->OrderStatus;
    order->OrderSubmitStatus = pOrder->OrderSubmitStatus;
    order->StatusMsg     = CTP::StringHelper::ToManaged(pOrder->StatusMsg);
    order->InsertDate    = CTP::StringHelper::ToManaged(pOrder->InsertDate);
    order->InsertTime    = CTP::StringHelper::ToManaged(pOrder->InsertTime);
    order->UpdateTime    = CTP::StringHelper::ToManaged(pOrder->UpdateTime);
    order->TradingDay    = CTP::StringHelper::ToManaged(pOrder->TradingDay);

    _wrapper->RaiseOrder(order);
}

void TraderSpi::OnRtnTrade(CThostFtdcTradeField* pTrade)
{
    if (!pTrade) return;

    auto trade = gcnew CTP::Trade();
    trade->BrokerID     = CTP::StringHelper::ToManaged(pTrade->BrokerID);
    trade->InvestorID   = CTP::StringHelper::ToManaged(pTrade->InvestorID);
    trade->InstrumentID = CTP::StringHelper::ToManaged(pTrade->reserve1);
    trade->OrderRef     = CTP::StringHelper::ToManaged(pTrade->OrderRef);
    trade->OrderSysID   = CTP::StringHelper::ToManaged(pTrade->OrderSysID);
    trade->ExchangeID   = CTP::StringHelper::ToManaged(pTrade->ExchangeID);
    trade->TradeID      = CTP::StringHelper::ToManaged(pTrade->TradeID);
    trade->Direction    = pTrade->Direction;
    trade->OffsetFlag   = pTrade->OffsetFlag;
    trade->Price        = pTrade->Price;
    trade->Volume       = pTrade->Volume;
    trade->TradeDate    = CTP::StringHelper::ToManaged(pTrade->TradeDate);
    trade->TradeTime    = CTP::StringHelper::ToManaged(pTrade->TradeTime);
    trade->TradingDay   = CTP::StringHelper::ToManaged(pTrade->TradingDay);

    _wrapper->RaiseTrade(trade);
}

// ==================== Position & Account ====================

void TraderSpi::OnRspQryInvestorPosition(CThostFtdcInvestorPositionField* p,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (p && p->reserve1[0] != '\0') {
        auto pos = gcnew CTP::PosDetail();
        pos->InstrumentID     = CTP::StringHelper::ToManaged(p->reserve1);
        pos->BrokerID         = CTP::StringHelper::ToManaged(p->BrokerID);
        pos->InvestorID       = CTP::StringHelper::ToManaged(p->InvestorID);
        pos->Direction        = p->PosiDirection;
        pos->HedgeFlag        = p->HedgeFlag;
        pos->Position         = p->Position;
        pos->PositionToday    = p->Position;     // PositionDate distinguishes today vs history
        pos->PositionYesterday = p->YdPosition;
        pos->FrozenPosition   = p->ShortFrozen + p->LongFrozen;
        pos->OpenCost         = p->OpenCost;
        pos->PositionCost     = p->PositionCost;
        pos->UseMargin        = p->UseMargin;
        pos->Commission       = p->Commission;
        pos->CloseProfit      = p->CloseProfit;
        pos->PositionProfit   = p->PositionProfit;
        pos->PreSettlementPrice = p->PreSettlementPrice;
        pos->SettlementPrice  = p->SettlementPrice;
        pos->TradingDay       = CTP::StringHelper::ToManaged(p->TradingDay);
        _wrapper->RaisePosition(pos, bIsLast);
    }

    if (bIsLast) {
        auto err = gcnew CTP::CtpError();
        if (pRspInfo) {
            err->ErrorID = pRspInfo->ErrorID;
            err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
        }
        _wrapper->RaisePositionQueryDone(err);
    }
}

void TraderSpi::OnRspQryTradingAccount(CThostFtdcTradingAccountField* p,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (p) {
        auto acc = gcnew CTP::AccountInfo();
        acc->BrokerID        = CTP::StringHelper::ToManaged(p->BrokerID);
        acc->AccountID       = CTP::StringHelper::ToManaged(p->AccountID);
        acc->Balance         = p->Balance;
        acc->Available       = p->Available;
        acc->CurrMargin      = p->CurrMargin;
        acc->CloseProfit     = p->CloseProfit;
        acc->PositionProfit  = p->PositionProfit;
        acc->Commission      = p->Commission;
        acc->FrozenMargin    = p->FrozenMargin;
        acc->FrozenCommission = p->FrozenCommission;
        acc->WithdrawQuota   = p->WithdrawQuota;
        acc->Reserve         = p->Reserve;
        acc->TradingDay      = CTP::StringHelper::ToManaged(p->TradingDay);

        auto err = gcnew CTP::CtpError();
        if (pRspInfo) {
            err->ErrorID = pRspInfo->ErrorID;
            err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
        }
        _wrapper->RaiseAccount(acc, err, bIsLast);
    }
}

// ==================== Settlement ====================

void TraderSpi::OnRspSettlementInfoConfirm(CThostFtdcSettlementInfoConfirmField* p,
    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;
    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseSettlementConfirm(err);
}

// ==================== Error ====================

void TraderSpi::OnRspError(CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast)
{
    if (!bIsLast) return;
    auto err = gcnew CTP::CtpError();
    if (pRspInfo) {
        err->ErrorID = pRspInfo->ErrorID;
        err->ErrorMsg = CTP::StringHelper::ToManaged(pRspInfo->ErrorMsg);
    }
    _wrapper->RaiseError(err, nRequestID);
}
