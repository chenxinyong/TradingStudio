#pragma once

namespace CTP
{
    /// Market data tick — managed DTO, complete 5-level depth
    public ref class Tick
    {
    public:
        System::String^ InstrumentID;       // eg: cu2607
        System::String^ ExchangeID;         // eg: SHFE
        System::String^ TradingDay;         // YYYYMMDD
        System::String^ UpdateTime;         // HH:MM:SS
        int             UpdateMillisec;

        double LastPrice;
        double PreSettlementPrice;
        double PreClosePrice;
        double PreOpenInterest;
        double OpenPrice;
        double HighestPrice;
        double LowestPrice;
        int    Volume;
        double Turnover;
        double OpenInterest;
        double ClosePrice;
        double SettlementPrice;
        double UpperLimitPrice;
        double LowerLimitPrice;

        // Depth L1-L5
        double BidPrice1;  int BidVolume1;
        double AskPrice1;  int AskVolume1;
        double BidPrice2;  int BidVolume2;
        double AskPrice2;  int AskVolume2;
        double BidPrice3;  int BidVolume3;
        double AskPrice3;  int AskVolume3;
        double BidPrice4;  int BidVolume4;
        double AskPrice4;  int AskVolume4;
        double BidPrice5;  int BidVolume5;
        double AskPrice5;  int AskVolume5;

        double AveragePrice;

        // Computed timestamps
        long long ExchangeTimestamp;   // Unix ms from TradingDay+UpdateTime+Millisec
        long long LocalTimestamp;      // UTC receive time
    };
}
