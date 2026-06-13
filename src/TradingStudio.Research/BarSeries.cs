using TradingStudio.Core.Models;

namespace TradingStudio.Research;

/// <summary>
/// Bar 序列 — 带分析扩展方法的 Bar 集合。
/// </summary>
public class BarSeries
{
    public string InstrumentId { get; }
    public IReadOnlyList<Bar> Bars { get; }

    public int Count => Bars.Count;
    public DateTime StartTime => Bars.Count > 0 ? Bars[0].BarTime : default;
    public DateTime EndTime => Bars.Count > 0 ? Bars[^1].BarTime : default;

    public BarSeries(string instrumentId, IReadOnlyList<Bar> bars)
    {
        InstrumentId = instrumentId;
        Bars = bars;
    }

    // ═══ 数据提取 ═══

    public double[] Closes => Bars.Select(b => b.CloseDouble).ToArray();
    public double[] Opens => Bars.Select(b => b.OpenDouble).ToArray();
    public double[] Highs => Bars.Select(b => b.HighDouble).ToArray();
    public double[] Lows => Bars.Select(b => b.LowDouble).ToArray();
    public double[] Volumes => Bars.Select(b => (double)b.Volume).ToArray();

    // ═══ 指标计算 ═══

    public double[] SMA(int period)
    {
        var closes = Closes;
        var result = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
        {
            if (i < period - 1) { result[i] = double.NaN; continue; }
            double sum = 0;
            for (int j = i - period + 1; j <= i; j++) sum += closes[j];
            result[i] = sum / period;
        }
        return result;
    }

    public double[] LogReturns()
    {
        var closes = Closes;
        var result = new double[closes.Length];
        result[0] = double.NaN;
        for (int i = 1; i < closes.Length; i++)
            result[i] = Math.Log(closes[i] / closes[i - 1]);
        return result;
    }

    public double[] Returns()
    {
        var closes = Closes;
        var result = new double[closes.Length];
        result[0] = double.NaN;
        for (int i = 1; i < closes.Length; i++)
            result[i] = (closes[i] - closes[i - 1]) / closes[i - 1];
        return result;
    }

    /// <summary>按日期切分</summary>
    public IEnumerable<BarSeries> SplitByDay()
    {
        return Bars.GroupBy(b => DateOnly.FromDateTime(b.BarTime.Date))
                   .Select(g => new BarSeries(InstrumentId, g.ToList()));
    }
}
