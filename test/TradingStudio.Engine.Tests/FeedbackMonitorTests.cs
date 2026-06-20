using TradingStudio.Core.Engine;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine.Tests;

public class FeedbackMonitorTests
{
    private static OrderEvent Fill(decimal price = 3500, decimal slippage = 0.5m,
        OrderEventType type = OrderEventType.Filled)
        => new() { FillPrice = price, Slippage = slippage, Type = type, StrategyId = "s1" };

    private static Trade Win(double pnl = 1000) => new() { PnL = (decimal)pnl };
    private static Trade Loss(double pnl = -500) => new() { PnL = (decimal)pnl };

    [Fact] public void RecordFill_CountsFilled()
    { var fm = new FeedbackMonitor(); fm.RecordFill(Fill(), "s1"); fm.RecordFill(Fill(), "s1"); Assert.Equal(2, fm.GetStats("s1").TotalFills); }

    [Fact] public void RecordFill_AccumulatesSlippage()
    { var fm = new FeedbackMonitor(); fm.RecordFill(Fill(slippage: 0.5m), "s1"); fm.RecordFill(Fill(slippage: 1.0m), "s1"); Assert.Equal(1.5, fm.GetStats("s1").TotalSlippage); }

    [Fact] public void RecordFill_CountsRejects()
    { var fm = new FeedbackMonitor(); fm.RecordFill(Fill(type: OrderEventType.Rejected), "s1"); var stats = fm.GetStats("s1"); Assert.Equal(1, stats.TotalRejects); Assert.Equal(0, stats.TotalFills); }

    [Fact] public void RecordTrade_CountsTrades()
    { var fm = new FeedbackMonitor(); fm.RecordTrade(Win(), "s1"); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Win(500), "s1"); Assert.Equal(3, fm.GetStats("s1").TotalTrades); }

    [Fact] public void RecordTrade_AccumulatesPnl()
    { var fm = new FeedbackMonitor(); fm.RecordTrade(Win(1000), "s1"); fm.RecordTrade(Loss(-500), "s1"); Assert.Equal(500, fm.GetStats("s1").TotalPnl); }

    [Fact] public void RecordTrade_TracksWinLossCount()
    { var fm = new FeedbackMonitor(); fm.RecordTrade(Win(), "s1"); fm.RecordTrade(Win(), "s1"); fm.RecordTrade(Loss(), "s1"); var s = fm.GetStats("s1"); Assert.Equal(2, s.Wins); Assert.Equal(1, s.Losses); }

    [Fact] public void RecordTrade_TracksMaxConsecutiveLosses()
    { var fm = new FeedbackMonitor(); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Win(), "s1"); fm.RecordTrade(Loss(), "s1"); Assert.Equal(2, fm.GetStats("s1").MaxConsecutiveLosses); }

    [Fact] public void RecordTrade_WinRate()
    { var fm = new FeedbackMonitor(); fm.RecordTrade(Win(), "s1"); fm.RecordTrade(Win(), "s1"); fm.RecordTrade(Loss(), "s1"); Assert.Equal(2.0/3.0, fm.GetStats("s1").WinRate); }

    [Fact] public void MultipleStrategies_IndependentStats()
    { var fm = new FeedbackMonitor(); fm.RecordTrade(Win(1000), "s1"); fm.RecordTrade(Loss(-500), "s2"); Assert.Equal(1, fm.GetStats("s1").TotalTrades); Assert.Equal(1000, fm.GetStats("s1").TotalPnl); Assert.Equal(1, fm.GetStats("s2").TotalTrades); Assert.Equal(-500, fm.GetStats("s2").TotalPnl); }

    [Fact] public void CheckAlerts_ConsecutiveLossesExceeded()
    { var fm = new FeedbackMonitor(new FeedbackThresholds { MaxConsecutiveLosses = 3 }); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Loss(), "s1"); Assert.NotEmpty(fm.CheckAlerts()); }

    [Fact] public void CheckAlerts_BelowThreshold()
    { var fm = new FeedbackMonitor(new FeedbackThresholds { MaxConsecutiveLosses = 5 }); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Loss(), "s1"); Assert.Empty(fm.CheckAlerts()); }

    [Fact] public void ToSummary_NoActivity_ReturnsZeros()
    { var fm = new FeedbackMonitor(); var s = fm.ToSummary(); Assert.Equal(0, s.TotalSlippage); Assert.Equal(0, s.AlertCount); }

    [Fact] public void ToSummary_AggregatesStats()
    { var fm = new FeedbackMonitor(); fm.RecordFill(Fill(slippage: 0.5m), "s1"); fm.RecordFill(Fill(slippage: 1.0m), "s2"); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Loss(), "s1"); fm.RecordTrade(Loss(), "s1"); var s = fm.ToSummary(); Assert.Equal(1.5, s.TotalSlippage); Assert.Equal(3, s.MaxConsecutiveLosses); }
}
