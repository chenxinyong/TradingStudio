#pragma once

namespace CTP
{
    /// Position detail DTO
    public ref class PosDetail
    {
    public:
        System::String^ InstrumentID;
        System::String^ BrokerID;
        System::String^ InvestorID;
        wchar_t         Direction;          // '0'=Long, '1'=Short
        wchar_t         HedgeFlag;
        int             Position;
        int             PositionToday;      // today's open
        int             PositionYesterday;  // yesterday's open
        int             FrozenPosition;     // frozen (pending close)
        double          OpenCost;
        double          PositionCost;
        double          UseMargin;
        double          Commission;
        double          CloseProfit;
        double          PositionProfit;
        double          PreSettlementPrice;
        double          SettlementPrice;
        System::String^ TradingDay;
    };

    /// Account info DTO
    public ref class AccountInfo
    {
    public:
        System::String^ BrokerID;
        System::String^ AccountID;
        double          Balance;            // static equity
        double          Available;          // available funds
        double          CurrMargin;         // current margin
        double          CloseProfit;        // close profit
        double          PositionProfit;     // position profit
        double          Commission;         // commission
        double          FrozenMargin;       // frozen margin
        double          FrozenCommission;   // frozen commission
        double          WithdrawQuota;      // withdrawable
        double          Reserve;            // reserve
        System::String^ TradingDay;
    };
}
