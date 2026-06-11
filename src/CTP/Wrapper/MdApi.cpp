#include "MdApi.h"
#include "Native/MdSpi.h"
#include "StringHelper.h"
#include "DTO/Tick.h"
#include "DTO/LoginInfo.h"
#include <string>

using namespace System;
using namespace System::IO;

namespace CTP {

MdApi::MdApi()
{
    _requestId = 0;
    _connected = false;
    _disposed = false;

    // Create unique flow directory for this instance
    String^ flowDir = Path::Combine(
        Environment::CurrentDirectory,
        "ctp_flow\\md_" + Guid::NewGuid().ToString("N")->Substring(0, 8));
    Directory::CreateDirectory(flowDir);

    auto flowNative = StringHelper::ToNative(flowDir);
    _api = CThostFtdcMdApi::CreateFtdcMdApi(flowNative.c_str(), false, false, false);
}

MdApi::~MdApi()
{
    this->!MdApi();
}

MdApi::!MdApi()
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

void MdApi::Connect(String^ frontAddr)
{
    if (!_api) throw gcnew ObjectDisposedException("MdApi");

    auto nativeFront = StringHelper::ToNative(frontAddr);

    _spi = new MdSpi(this);
    _api->RegisterSpi(_spi);
    _api->RegisterFront(const_cast<char*>(nativeFront.c_str()));
    _api->Init();
}

void MdApi::Login(String^ brokerId, String^ userId, String^ password)
{
    if (!_api) throw gcnew ObjectDisposedException("MdApi");

    CThostFtdcReqUserLoginField req;
    memset(&req, 0, sizeof(req));

    StringHelper::CopyToBuffer(brokerId, req.BrokerID, sizeof(req.BrokerID));
    StringHelper::CopyToBuffer(userId,   req.UserID,   sizeof(req.UserID));
    StringHelper::CopyToBuffer(password, req.Password, sizeof(req.Password));

    _api->ReqUserLogin(&req, ++_requestId);
}

void MdApi::Logout()
{
    if (!_api) return;

    CThostFtdcUserLogoutField req;
    memset(&req, 0, sizeof(req));
    _api->ReqUserLogout(&req, ++_requestId);
}

// ==================== Subscribe ====================

int MdApi::Subscribe(array<String^>^ instrumentIds)
{
    if (!_api) throw gcnew ObjectDisposedException("MdApi");
    if (!instrumentIds || instrumentIds->Length == 0) return -1;

    // Marshal managed string[] → char*[] for CTP
    int count = instrumentIds->Length;
    std::string* natives = new std::string[count];
    char** pp = new char*[count];

    for (int i = 0; i < count; i++) {
        natives[i] = StringHelper::ToNative(instrumentIds[i]);
        pp[i] = const_cast<char*>(natives[i].c_str());
    }

    int ret = _api->SubscribeMarketData(pp, count);

    delete[] pp;
    delete[] natives;

    return ret;
}

int MdApi::Unsubscribe(array<String^>^ instrumentIds)
{
    if (!_api) throw gcnew ObjectDisposedException("MdApi");
    if (!instrumentIds || instrumentIds->Length == 0) return -1;

    int count = instrumentIds->Length;
    std::string* natives = new std::string[count];
    char** pp = new char*[count];

    for (int i = 0; i < count; i++) {
        natives[i] = StringHelper::ToNative(instrumentIds[i]);
        pp[i] = const_cast<char*>(natives[i].c_str());
    }

    int ret = _api->UnSubscribeMarketData(pp, count);

    delete[] pp;
    delete[] natives;

    return ret;
}

// ==================== Event Raisers (called from MdSpi on CTP thread) ====================

void MdApi::RaiseFrontConnected()          { _connected = true;  OnFrontConnected(); }
void MdApi::RaiseFrontDisconnected(int r)  { _connected = false; OnFrontDisconnected(r); }
void MdApi::RaiseHeartBeatWarning(int tl)  { OnHeartBeatWarning(tl); }
void MdApi::RaiseLogin(CtpError^ err, LoginInfo^ info)        { OnLogin(err, info); }
void MdApi::RaiseLogout(CtpError^ err)                        { OnLogout(err); }
void MdApi::RaiseSubscribeRsp(String^ id, CtpError^ e, bool l){ OnSubscribeRsp(id, e, l); }
void MdApi::RaiseUnsubscribeRsp(String^ id, CtpError^ e, bool l){ OnUnsubscribeRsp(id, e, l); }
void MdApi::RaiseTick(Tick^ tick)                             { OnTick(tick); }
void MdApi::RaiseError(CtpError^ err, int reqId)              { OnError(err, reqId); }

} // namespace CTP
