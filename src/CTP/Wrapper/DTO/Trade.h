#pragma once

namespace CTP
{
    /// Order status update DTO
    public ref class Order
    {
    public:
        System::String^ BrokerID;
        System::String^ InvestorID;
        System::String^ InstrumentID;
        System::String^ OrderRef;
        System::String^ OrderSysID;
        System::String^ ExchangeID;
        System::String^ OrderLocalID;
        wchar_t         Direction;
        wchar_t         OffsetFlag;
        double          LimitPrice;
        int             VolumeTotalOriginal;
        int             VolumeTraded;
        int             VolumeTotal;
        wchar_t         OrderStatus;
        wchar_t         OrderSubmitStatus;
        System::String^ StatusMsg;
        System::String^ InsertDate;
        System::String^ InsertTime;
        System::String^ UpdateTime;
        System::String^ TradingDay;
    };

    /// Trade fill DTO
    public ref class Trade
    {
    public:
        System::String^ BrokerID;
        System::String^ InvestorID;
        System::String^ InstrumentID;
        System::String^ OrderRef;
        System::String^ OrderSysID;
        System::String^ ExchangeID;
        System::String^ TradeID;
        wchar_t         Direction;
        wchar_t         OffsetFlag;
        double          Price;
        int             Volume;
        System::String^ TradeDate;
        System::String^ TradeTime;
        System::String^ TradingDay;
    };
}
