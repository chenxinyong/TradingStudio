#pragma once

namespace CTP
{
    /// Login response DTO
    public ref class LoginInfo
    {
    public:
        System::String^ TradingDay;
        System::String^ LoginTime;
        System::String^ BrokerID;
        System::String^ UserID;
        int    FrontID;
        int    SessionID;
        int    MaxOrderRef;
        System::String^ SHFETime;
        System::String^ DCETime;
        System::String^ CZCETime;
        System::String^ FFEXTime;
        System::String^ INETime;
    };

    /// CTP error info
    public ref class CtpError
    {
    public:
        int    ErrorID;
        System::String^ ErrorMsg;

        bool IsOK() { return ErrorID == 0; }
    };
}
