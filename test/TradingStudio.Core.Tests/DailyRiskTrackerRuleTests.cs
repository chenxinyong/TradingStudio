using TradingStudio.Core.Engine;
using TradingStudio.Core.Risk;

namespace TradingStudio.Core.Tests;

/// <summary>
/// DailyRiskTracker IRiskRule 接入测试 — 守护日/月/连亏熔断在风控链中生效。
/// 修复前：DailyRiskTracker 为死代码，无人实例化、无人调用、风控只靠 MaxDrawdownRule。
/// </summary>
public class DailyRiskTrackerRuleTests
{
    private static DailyRiskTracker Tracker(double dailyLimit = 0.05, double monthlyLimit = 0.15,
        double maxDrawdown = 0.25, int consecutiveDays = 5) => new(new()
    {
        DailyLossLimit = dailyLimit, MonthlyLossLimit = monthlyLimit,
        MaxDrawdownLimit = maxDrawdown, MaxConsecutiveLossDays = consecutiveDays,
    });

    // ═══ CheckPreOrder: 熔断时拒单 ═══

    [Fact]
    public void NormalState_PreOrderPasses()
    {
        var t = Tracker();
        var result = t.CheckPreOrder(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy, Quantity = 1 },
            new MockPortfolio { Equity = 100_000 });
        Assert.True(result.Passed);
    }

    [Fact]
    public void DailyLimitHit_PreOrderRejected()
    {
        var t = Tracker(dailyLimit: 0.01); // 1% daily limit
        t.RecordPnl(-2000, 100_000); // -2% → 超过 1%
        var result = t.CheckPreOrder(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy, Quantity = 1 },
            new MockPortfolio { Equity = 98_000 });
        Assert.False(result.Passed);
        Assert.Contains("DailyLimitHit", result.Reason);
    }

    [Fact]
    public void MaxDrawdownHit_PreOrderRejected()
    {
        var t = Tracker(maxDrawdown: 0.10); // 10%
        t.RecordPnl(-15000, 100_000); // -15% → exceed 10%
        var result = t.CheckPreOrder(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy, Quantity = 1 },
            new MockPortfolio { Equity = 85_000 });
        Assert.False(result.Passed);
    }

    // ═══ RecordPnl: 公开方法供引擎调用 ═══

    [Fact]
    public void RecordPnl_NormalTrade_NoEvent()
    {
        var t = Tracker();
        Assert.Null(t.RecordPnl(-100, 100_000)); // -0.1% within 5% limit
        Assert.Equal(RiskState.Normal, t.State);
    }

    [Fact]
    public void RecordPnl_ExceedsDailyLimit_FiresEvent()
    {
        var t = Tracker(dailyLimit: 0.02);
        var evt = t.RecordPnl(-3000, 100_000); // -3% > 2%
        Assert.NotNull(evt);
        Assert.Equal(RiskEventType.DailyLossLimit, evt!.Type);
        Assert.Equal(RiskState.DailyLimitHit, t.State);
    }

    // ═══ 集成: RiskController 链中生效 ═══

    // ═══ RecordTrade 间接测试 (内部闭环) ═══

    [Fact]
    public void ConsecutiveLossDays_Triggers()
    {
        var t = Tracker(consecutiveDays: 2);
        // 连续2天亏损
        t.CloseDay(100_000, 99_000); // -1%
        Assert.Equal(RiskState.Normal, t.State);
        t.CloseDay(99_000, 98_000);  // -1%
        Assert.Equal(RiskState.ConsecutiveLosses, t.State);
    }

    // ═══ Mock ═══
    private class MockPortfolio : IPortfolioState
    {
        public decimal Cash => 0;
        public decimal Equity { get; init; } = 100_000;
        public decimal MarginUsed => 0;
        public decimal StartingCapital => 100_000;
        public decimal PeakEquity => 100_000;
        public decimal TodayPnL => 0;
        public decimal TotalPnL => 0;
        public PositionSnapshot? GetPosition(string strategyId, string instrumentId) => null;
        public IReadOnlyList<PositionSnapshot> AllPositions => [];
        public IReadOnlyList<Order> ActiveOrders => [];
        public IReadOnlyList<Trade> TradeHistory => [];
        public IReadOnlyList<SubPortfolioState> SubPortfolios => [];
    }
}
