using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

public class PortfolioManagerTests
{
    [Fact]
    public void CreateSubPortfolio_AllocatesCorrectCapital()
    {
        var pm = new PortfolioManager(100000);
        pm.CreateSubPortfolio("s1", 30000);
        pm.CreateSubPortfolio("s2", 20000);

        Assert.Equal(30000m, pm.GetSubPortfolio("s1").AllocatedCapital);
        Assert.Equal(20000m, pm.GetSubPortfolio("s2").AllocatedCapital);
        Assert.Equal(100000m, pm.StartingCapital);
    }

    [Fact]
    public void SubPortfolios_ReturnsAllCreated()
    {
        var pm = new PortfolioManager(100000);
        pm.CreateSubPortfolio("a", 50000);
        pm.CreateSubPortfolio("b", 50000);

        Assert.Equal(2, pm.SubPortfolios.Count);
    }

    [Fact]
    public void Equity_Initial_EqualsCash()
    {
        var pm = new PortfolioManager(100000);
        Assert.Equal(100000m, pm.Equity);
        Assert.Equal(100000m, pm.Cash);
        Assert.Equal(0m, pm.MarginUsed);
    }

    [Fact]
    public void GetSubPortfolio_Missing_Throws()
    {
        var pm = new PortfolioManager(100000);
        Assert.Throws<InvalidOperationException>(() => pm.GetSubPortfolio("nonexistent"));
    }
}
