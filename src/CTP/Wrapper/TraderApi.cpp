#include "TraderApi.h"
#include "Native/TraderSpi.h"
#include "StringHelper.h"
#include "DTO/OrderRequest.h"
#include <string>

using namespace System;
using namespace System::IO;

namespace CTP {

TraderApi::TraderApi()
{
    _requestId = 0;
    _connected = false;
    _disposed = false;

    // Create unique flow directory for this instance
    String^ flowDir = Path::Combine(
        Environment::CurrentDirectory,
        "ctp_flow\\trader_" + Guid::NewGuid().ToString("N")->Substring(0, 8));
    Directory::CreateDirectory(flowDir);

    auto flowNative = StringHelper::ToNative(flowDir);
    _api = CThostFtdcTraderApi::CreateFtdcTraderApi(flowNative.c_str(), false);
}

TraderApi::~TraderApi()
{
    this->!TraderApi();
}

TraderApi::!TraderApi()
{
    if (_disposed) return;
    _disposed = true;

    if (_api) {
        _api->RegisterSpi(nullptr);
        _api->Release();
        _api = nullptr;
    }
    if (_spi) {
        delete _spi;
        _spi = nullptr;
    }
}

// ==================== Connection ====================

void TraderApi::Connect(String^ frontAddr)
{
    if (!_api) throw gcnew ObjectDisposedException("TraderApi");

    auto nativeFront = StringHelper::ToNative(frontAddr);

    _spi = new TraderSpi(this);
    _api->RegisterSpi(_spi);

    // Subscribe to both public and private topics (penetration supervision)
    _api->SubscribePublicTopic(THOST_TERT_QUICK);
    _api->SubscribePrivateTopic(THOST_TERT_QUICK);

    _api->RegisterFront(const_cast<char*>(nativeFront.c_str()));
    _api->Init();
}

void TraderApi::Login(String^ brokerId, String^ userId, String^ password)
{
    if (!_api) throw gcnew ObjectDisposedException("TraderApi");

    _brokerId = brokerId;
    _userId   = userId;

    CThostFtdcReqUserLoginField req;
    memset(&req, 0, sizeof(req));

    StringHelper::CopyToBuffer(brokerId, req.BrokerID, sizeof(req.BrokerID));
    StringHelper::CopyToBuffer(userId,   req.UserID,   sizeof(req.UserID));
    StringHelper::CopyToBuffer(password, req.Password, sizeof(req.Password));

    _api->ReqUserLogin(&req, ++_requestId);
}

void TraderApi::Logout()
{
    if (!_api) return;

    CThostFtdcUserLogoutField req;
    memset(&req, 0, sizeof(req));
    StringHelper::CopyToBuffer(_brokerId, req.BrokerID, sizeof(req.BrokerID));
    StringHelper::CopyToBuffer(_userId,   req.UserID,   sizeof(req.UserID));

    _api->ReqUserLogout(&req, ++_requestId);
}

bool TraderApi::IsConnected()
{
    return _connected;
}

// ==================== Order ====================

int TraderApi::InsertOrder(OrderRequest^ req)
{
    if (!_api) throw gcnew ObjectDisposedException("TraderApi");
    if (String::IsNullOrEmpty(req->InstrumentID))
        throw gcnew ArgumentException("InstrumentID required");
    if (req->Volume <= 0)
        throw gcnew ArgumentException("Volume must be > 0");

    CThostFtdcInputOrderField field;
    memset(&field, 0, sizeof(field));

    // Broker & Investor
    StringHelper::CopyToBuffer(_brokerId, field.BrokerID, sizeof(field.BrokerID));
    StringHelper::CopyToBuffer(_userId,   field.InvestorID, sizeof(field.InvestorID));
    StringHelper::CopyToBuffer(req->InstrumentID, field.reserve1, sizeof(field.reserve1));

    // Order ref — auto-increment if not specified
    if (!String::IsNullOrEmpty(req->OrderRef)) {
        StringHelper::CopyToBuffer(req->OrderRef, field.OrderRef, sizeof(field.OrderRef));
    } else {
        auto ref = (_requestId + 1).ToString();
        StringHelper::CopyToBuffer(ref, field.OrderRef, sizeof(field.OrderRef));
    }

    // Direction
    field.Direction = (req->Direction == Direction::Buy)
        ? THOST_FTDC_D_Buy : THOST_FTDC_D_Sell;

    // Offset
    field.CombOffsetFlag[0] = static_cast<char>(req->Offset);

    // Price
    field.OrderPriceType = (req->PriceType == OrderPriceType::AnyPrice)
        ? THOST_FTDC_OPT_AnyPrice : THOST_FTDC_OPT_LimitPrice;
    field.LimitPrice = req->Price;
    field.VolumeTotalOriginal = req->Volume;

    // Standard order conditions
    field.TimeCondition      = THOST_FTDC_TC_GFD;        // Good For Day
    field.VolumeCondition    = THOST_FTDC_VC_AV;         // Any Volume
    field.ContingentCondition = THOST_FTDC_CC_Immediately;
    field.CombHedgeFlag[0]   = THOST_FTDC_HF_Speculation;

    int reqId = ++_requestId;
    _api->ReqOrderInsert(&field, reqId);
    return reqId;
}

int TraderApi::CancelOrder(CancelRequest^ req)
{
    if (!_api) throw gcnew ObjectDisposedException("TraderApi");

    CThostFtdcInputOrderActionField field;
    memset(&field, 0, sizeof(field));

    StringHelper::CopyToBuffer(_brokerId, field.BrokerID, sizeof(field.BrokerID));
    StringHelper::CopyToBuffer(_userId,   field.InvestorID, sizeof(field.InvestorID));
    StringHelper::CopyToBuffer(req->InstrumentID, field.reserve1, sizeof(field.reserve1));

    if (!String::IsNullOrEmpty(req->OrderRef))
        StringHelper::CopyToBuffer(req->OrderRef, field.OrderRef, sizeof(field.OrderRef));
    if (!String::IsNullOrEmpty(req->OrderSysID))
        StringHelper::CopyToBuffer(req->OrderSysID, field.OrderSysID, sizeof(field.OrderSysID));
    if (!String::IsNullOrEmpty(req->ExchangeID))
        StringHelper::CopyToBuffer(req->ExchangeID, field.ExchangeID, sizeof(field.ExchangeID));

    field.ActionFlag = THOST_FTDC_AF_Delete;

    int reqId = ++_requestId;
    _api->ReqOrderAction(&field, reqId);
    return reqId;
}

// ==================== Query ====================

int TraderApi::QueryPosition()
{
    if (!_api) throw gcnew ObjectDisposedException("TraderApi");
    CThostFtdcQryInvestorPositionField req;
    memset(&req, 0, sizeof(req));
    StringHelper::CopyToBuffer(_brokerId, req.BrokerID, sizeof(req.BrokerID));
    StringHelper::CopyToBuffer(_userId,   req.InvestorID, sizeof(req.InvestorID));
    return _api->ReqQryInvestorPosition(&req, ++_requestId);
}

int TraderApi::QueryAccount()
{
    if (!_api) throw gcnew ObjectDisposedException("TraderApi");
    CThostFtdcQryTradingAccountField req;
    memset(&req, 0, sizeof(req));
    StringHelper::CopyToBuffer(_brokerId, req.BrokerID, sizeof(req.BrokerID));
    StringHelper::CopyToBuffer(_userId,   req.InvestorID, sizeof(req.InvestorID));
    return _api->ReqQryTradingAccount(&req, ++_requestId);
}

int TraderApi::ConfirmSettlement()
{
    if (!_api) throw gcnew ObjectDisposedException("TraderApi");
    CThostFtdcSettlementInfoConfirmField req;
    memset(&req, 0, sizeof(req));
    StringHelper::CopyToBuffer(_brokerId, req.BrokerID, sizeof(req.BrokerID));
    StringHelper::CopyToBuffer(_userId,   req.InvestorID, sizeof(req.InvestorID));
    return _api->ReqSettlementInfoConfirm(&req, ++_requestId);
}

// ==================== Event Raisers (called from TraderSpi on CTP thread) ====================

void TraderApi::RaiseFrontConnected()
{
    _connected = true;
    OnFrontConnected();
}

void TraderApi::RaiseFrontDisconnected(int reason)
{
    _connected = false;
    OnFrontDisconnected(reason);
}

void TraderApi::RaiseHeartBeatWarning(int timeLapse) { OnHeartBeatWarning(timeLapse); }
void TraderApi::RaiseAuth(CtpError^ err)              { OnAuth(err); }
void TraderApi::RaiseLogin(CtpError^ err, LoginInfo^ info) { OnLogin(err, info); }
void TraderApi::RaiseLogout(CtpError^ err)            { OnLogout(err); }
void TraderApi::RaiseOrderInsertRsp(CtpError^ err, int requestId) { OnOrderInsertRsp(err, requestId); }
void TraderApi::RaiseOrderActionRsp(CtpError^ err, int requestId) { OnOrderActionRsp(err, requestId); }
void TraderApi::RaiseOrder(Order^ order)              { OnOrder(order); }
void TraderApi::RaiseTrade(Trade^ trade)              { OnTrade(trade); }
void TraderApi::RaisePosition(PosDetail^ pos, bool isLast) { OnPosition(pos, isLast); }
void TraderApi::RaisePositionQueryDone(CtpError^ err) { OnPositionQueryDone(err); }
void TraderApi::RaiseAccount(AccountInfo^ acc, CtpError^ err, bool isLast) { OnAccount(acc, err, isLast); }
void TraderApi::RaiseSettlementConfirm(CtpError^ err) { OnSettlementConfirm(err); }
void TraderApi::RaiseError(CtpError^ err, int requestId) { OnError(err, requestId); }

} // namespace CTP
