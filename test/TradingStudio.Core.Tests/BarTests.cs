using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

public class BarTests
{
    [Fact]
    public void OpenDouble_ConvertsUsingPriceScale()
    {
        var b = new Bar { Open = 35_630_000_000L };
        Assert.Equal(3563.0, b.OpenDouble, 6);
    }

    [Fact]
    public void HighLow_OrderIsCorrect()
    {
        var b = new Bar { High = 36_000_000_000L, Low = 35_000_000_000L };
        Assert.True(b.HighDouble > b.LowDouble);
    }

    [Fact]
    public void TickCount_DefaultsToZero()
    {
        Assert.Equal(0, new Bar().TickCount);
    }

    [Fact]
    public void BarTime_StoredInUtc()
    {
        var dt = new DateTime(2026, 6, 23, 9, 30, 0, DateTimeKind.Utc);
        var b = new Bar { BarTime = dt };
        Assert.Equal(DateTimeKind.Utc, b.BarTime.Kind);
        Assert.Equal(9, b.BarTime.Hour);
    }
}
