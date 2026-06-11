#pragma once

namespace CTP
{
    public enum class Direction
    {
        Buy  = '0',   // THOST_FTDC_D_Buy
        Sell = '1'    // THOST_FTDC_D_Sell
    };

    public enum class OffsetFlag
    {
        Open        = '0',   // THOST_FTDC_OF_Open
        Close       = '1',   // THOST_FTDC_OF_Close
        CloseToday  = '3'    // THOST_FTDC_OF_CloseToday
    };

    public enum class OrderPriceType
    {
        AnyPrice    = '1',   // THOST_FTDC_OPT_AnyPrice (market)
        LimitPrice  = '2'    // THOST_FTDC_OPT_LimitPrice
    };

    /// Order insert request DTO
    public ref class OrderRequest
    {
    public:
        System::String^  InstrumentID;
        double           Price;
        int              Volume;
        Direction        Direction;
        OffsetFlag       Offset;
        OrderPriceType   PriceType;
        System::String^  OrderRef;
    };

    /// Order cancel request DTO
    public ref class CancelRequest
    {
    public:
        System::String^ InstrumentID;
        System::String^ OrderRef;
        System::String^ OrderSysID;
        System::String^ ExchangeID;
    };
}
