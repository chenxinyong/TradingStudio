#pragma once

// === Native CTP headers — must be in unmanaged context ===
#pragma managed(push, off)
#include "ThostFtdcMdApi.h"
#pragma managed(pop)

// === Managed forward declarations ===
class MdSpi;

namespace CTP
{
    ref class CtpError;
    ref class LoginInfo;
    ref class Quote;

    // === Delegate types (public for C# visibility) ===
    public delegate void MdConnectedHandler();
    public delegate void MdDisconnectedHandler(int reason);
    public delegate void MdHeartBeatHandler(int timeLapse);
    public delegate void MdLoginHandler(CtpError^ error, LoginInfo^ info);
    public delegate void MdLogoutHandler(CtpError^ error);
    public delegate void MdSubscribeRspHandler(System::String^ instrumentId, CtpError^ error, bool isLast);
    public delegate void MdUnsubscribeRspHandler(System::String^ instrumentId, CtpError^ error, bool isLast);
    public delegate void MdQuoteHandler(Quote^ tick);
    public delegate void MdErrorHandler(CtpError^ error, int requestId);

    /// Managed Market Data API — C# direct reference
    public ref class MdApi
    {
    public:
        MdApi();
        ~MdApi();
        !MdApi();

        void Connect(System::String^ frontAddr);
        void Login(System::String^ brokerId, System::String^ userId, System::String^ password);
        void Logout();

        int Subscribe(array<System::String^>^ instrumentIds);
        int Unsubscribe(array<System::String^>^ instrumentIds);

        event MdConnectedHandler^      OnFrontConnected;
        event MdDisconnectedHandler^   OnFrontDisconnected;
        event MdHeartBeatHandler^      OnHeartBeatWarning;
        event MdLoginHandler^          OnLogin;
        event MdLogoutHandler^         OnLogout;
        event MdSubscribeRspHandler^   OnSubscribeRsp;
        event MdUnsubscribeRspHandler^ OnUnsubscribeRsp;
        event MdQuoteHandler^           OnQuote;
        event MdErrorHandler^          OnError;

    internal:
        void RaiseFrontConnected();
        void RaiseFrontDisconnected(int reason);
        void RaiseHeartBeatWarning(int timeLapse);
        void RaiseLogin(CtpError^ err, LoginInfo^ info);
        void RaiseLogout(CtpError^ err);
        void RaiseSubscribeRsp(System::String^ instrumentId, CtpError^ err, bool isLast);
        void RaiseUnsubscribeRsp(System::String^ instrumentId, CtpError^ err, bool isLast);
        void RaiseQuote(Quote^ tick);
        void RaiseError(CtpError^ err, int requestId);

    private:
        CThostFtdcMdApi*  _api;
        MdSpi*            _spi;
        int               _requestId;
        bool              _connected;
        bool              _disposed;
    };
}
