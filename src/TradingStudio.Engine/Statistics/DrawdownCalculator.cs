namespace TradingStudio.Engine.Statistics;

/// <summary>回撤计算器</summary>
public static class DrawdownCalculator
{
    /// <summary>计算最大回撤（峰值到谷底的最大百分比跌幅）</summary>
    public static double CalculateMaxDrawdown(IReadOnlyList<(DateTimeOffset Time, decimal Equity)> equityCurve)
    {
        if (equityCurve.Count == 0) return 0;

        decimal peak = equityCurve[0].Equity;
        double maxDrawdown = 0;

        foreach (var (_, equity) in equityCurve)
        {
            if (equity > peak) peak = equity;
            var drawdown = (double)((peak - equity) / peak);
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        return maxDrawdown;
    }
}
