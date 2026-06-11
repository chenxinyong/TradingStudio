#pragma once

// === Native CTP headers — must be in unmanaged context ===
#pragma managed(push, off)
#include "ThostFtdcTraderApi.h"
#pragma managed(pop)

// === Managed forward declarations ===
class TraderSpi;

namespace CTP
{
    ref class CtpError;
    ref class LoginInfo;
    ref class Order;
    ref class Trade;
    ref class PosDetail;
    ref class AccountInfo;
    ref class OrderRequest;
    ref class CancelRequest;

    // === Delegate types (public for C# visibility) ===
    public delegate void ConnectedHandler();
    public delegate void DisconnectedHandler(int reason);
    public delegate void HeartBeatHandler(int timeLapse);
    public delegate void LoginHandler(CtpError^ error, LoginInfo^ info);
    public delegate void LogoutHandler(CtpError^ error);
    public delegate void AuthHandler(CtpError^ error);
    public delegate void OrderInsertRspHandler(CtpError^ error, int requestId);
    public delegate void OrderActionRspHandler(CtpError^ error, int requestId);
    public delegate void OrderHandler(Order^ order);
    public delegate void TradeHandler(Trade^ trade);
    public delegate void PositionHandler(PosDetail^ position, bool isLast);
    public delegate void PositionQueryDoneHandler(CtpError^ error);
    public delegate void AccountHandler(AccountInfo^ account, CtpError^ error, bool isLast);
    public delegate void SettlementConfirmHandler(CtpError^ error);
    public delegate void ErrorHandler(CtpError^ error, int requestId);

    /// Managed Trader API — C# direct reference
    public ref class TraderApi
    {
    public:
        TraderApi();
        ~TraderApi();
        !TraderApi();

        void Connect(System::String^ frontAddr);
        void Login(System::String^ brokerId, System::String^ userId, System::String^ password);
        void Logout();
        bool IsConnected();

        int InsertOrder(OrderRequest^ req);
        int CancelOrder(CancelRequest^ req);

        int QueryPosition();
        int QueryAccount();
        int ConfirmSettlement();

        event ConnectedHandler^           OnFrontConnected;
        event DisconnectedHandler^        OnFrontDisconnected;
        event HeartBeatHandler^           OnHeartBeatWarning;
        event LoginHandler^               OnLogin;
        event LogoutHandler^              OnLogout;
        event AuthHandler^                OnAuth;
        event OrderInsertRspHandler^      OnOrderInsertRsp;
        event OrderActionRspHandler^      OnOrderActionRsp;
        event OrderHandler^               OnOrder;
        event TradeHandler^               OnTrade;
        event PositionHandler^            OnPosition;
        event PositionQueryDoneHandler^   OnPositionQueryDone;
        event AccountHandler^             OnAccount;
        event SettlementConfirmHandler^   OnSettlementConfirm;
        event ErrorHandler^               OnError;

    internal:
        void RaiseFrontConnected();
        void RaiseFrontDisconnected(int reason);
        void RaiseHeartBeatWarning(int timeLapse);
        void RaiseAuth(CtpError^ err);
        void RaiseLogin(CtpError^ err, LoginInfo^ info);
        void RaiseLogout(CtpError^ err);
        void RaiseOrderInsertRsp(CtpError^ err, int requestId);
        void RaiseOrderActionRsp(CtpError^ err, int requestId);
        void RaiseOrder(Order^ order);
        void RaiseTrade(Trade^ trade);
        void RaisePosition(PosDetail^ pos, bool isLast);
        void RaisePositionQueryDone(CtpError^ err);
        void RaiseAccount(AccountInfo^ acc, CtpError^ err, bool isLast);
        void RaiseSettlementConfirm(CtpError^ err);
        void RaiseError(CtpError^ err, int requestId);

    private:
        CThostFtdcTraderApi* _api;
        TraderSpi*           _spi;
        int                  _requestId;
        System::String^      _brokerId;
        System::String^      _userId;
        bool                 _connected;
        bool                 _disposed;
    };
}
