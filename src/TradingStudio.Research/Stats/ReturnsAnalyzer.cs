namespace TradingStudio.Research.Stats;

/// <summary>收益率分析结果</summary>
public class ReturnsAnalyzer
{
    public double[] Returns { get; }
    public double Mean { get; }
    public double StdDev { get; }
    public double Skewness { get; }
    public double Kurtosis { get; }
    public double Min { get; }
    public double Max { get; }
    public double SharpeRatio { get; }
    public int Count { get; }

    /// <summary>正收益率占比</summary>
    public double PositiveRatio { get; }

    /// <summary>VaR (95%)</summary>
    public double VaR95 { get; }

    /// <summary>CVaR (Expected Shortfall, 95%)</summary>
    public double CVaR95 { get; }

    public ReturnsAnalyzer(double[] returns, double riskFreeRate = 0.02)
    {
        Returns = returns;
        var valid = returns.Where(r => !double.IsNaN(r)).ToArray();
        Count = valid.Length;
        if (Count < 2) return;

        Mean = valid.Average();
        StdDev = Math.Sqrt(valid.Average(r => (r - Mean) * (r - Mean)));
        Min = valid.Min();
        Max = valid.Max();
        PositiveRatio = (double)valid.Count(r => r > 0) / Count;

        // Skewness
        var m3 = valid.Average(r => Math.Pow(r - Mean, 3));
        Skewness = StdDev > 1e-10 ? m3 / Math.Pow(StdDev, 3) : 0;

        // Kurtosis (excess)
        var m4 = valid.Average(r => Math.Pow(r - Mean, 4));
        Kurtosis = StdDev > 1e-10 ? (m4 / Math.Pow(StdDev, 4)) - 3 : 0;

        // Sharpe (annualized, assuming daily returns)
        SharpeRatio = StdDev > 1e-10 ? (Mean - riskFreeRate / 252) / StdDev * Math.Sqrt(252) : 0;

        // VaR 95%
        var sorted = valid.OrderBy(r => r).ToArray();
        int varIndex = (int)(Count * 0.05);
        VaR95 = sorted[Math.Min(varIndex, Count - 1)];

        // CVaR 95%
        CVaR95 = sorted.Take(Math.Max(1, varIndex)).Average();
    }

    public void Describe()
    {
        Console.WriteLine($"Count:      {Count:N0}");
        Console.WriteLine($"Mean:       {Mean * 10000:F2} bps");
        Console.WriteLine($"StdDev:     {StdDev * 10000:F2} bps");
        Console.WriteLine($"Skewness:   {Skewness:F3}");
        Console.WriteLine($"Kurtosis:   {Kurtosis:F3}");
        Console.WriteLine($"Min/Max:    {Min * 100:F2}% / {Max * 100:F2}%");
        Console.WriteLine($"Win Ratio:  {PositiveRatio:P1}");
        Console.WriteLine($"Sharpe:     {SharpeRatio:F2}");
        Console.WriteLine($"VaR 95%:    {VaR95 * 100:F2}%");
        Console.WriteLine($"CVaR 95%:   {CVaR95 * 100:F2}%");
    }
}

/// <summary>BarSeries 扩展方法</summary>
public static class BarSeriesStatsExtensions
{
    public static ReturnsAnalyzer AnalyzeReturns(this Research.BarSeries series)
    {
        return new ReturnsAnalyzer(series.Returns());
    }

    public static double[] CumulativePnL(this double[] returns)
    {
        var result = new double[returns.Length];
        double cum = 1.0;
        for (int i = 0; i < returns.Length; i++)
        {
            if (!double.IsNaN(returns[i]))
                cum *= (1 + returns[i]);
            result[i] = cum - 1;
        }
        return result;
    }
}
