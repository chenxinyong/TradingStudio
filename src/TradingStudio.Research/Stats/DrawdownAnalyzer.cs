namespace TradingStudio.Research.Stats;

/// <summary>回撤分析结果</summary>
public class DrawdownAnalyzer
{
    public double MaxDrawdown { get; }
    public int MaxDrawdownStartIndex { get; }
    public int MaxDrawdownEndIndex { get; }
    public int MaxDrawdownDuration { get; }  // 根数
    public double AvgDrawdown { get; }
    public double[] DrawdownCurve { get; }

    public DrawdownAnalyzer(double[] equityCurve)
    {
        if (equityCurve.Length < 2)
        {
            DrawdownCurve = [];
            return;
        }

        DrawdownCurve = new double[equityCurve.Length];
        double peak = equityCurve[0];
        MaxDrawdown = 0;
        int ddStart = 0;

        for (int i = 0; i < equityCurve.Length; i++)
        {
            if (equityCurve[i] > peak)
            {
                peak = equityCurve[i];
                ddStart = i;
            }
            double dd = peak > 0 ? (equityCurve[i] - peak) / peak : 0;
            DrawdownCurve[i] = dd;

            if (dd < MaxDrawdown)
            {
                MaxDrawdown = dd;
                MaxDrawdownStartIndex = ddStart;
                MaxDrawdownEndIndex = i;
            }
        }

        MaxDrawdownDuration = MaxDrawdownEndIndex - MaxDrawdownStartIndex;
        AvgDrawdown = DrawdownCurve.Where(d => d < 0).DefaultIfEmpty(0).Average();
    }

    public void Describe()
    {
        Console.WriteLine($"Max Drawdown:    {MaxDrawdown:P2}");
        Console.WriteLine($"Avg Drawdown:    {AvgDrawdown:P2}");
        Console.WriteLine($"DD Duration:     {MaxDrawdownDuration:N0} bars");
    }
}
