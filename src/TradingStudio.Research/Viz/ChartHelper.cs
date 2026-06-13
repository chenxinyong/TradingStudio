using ScottPlot;

namespace TradingStudio.Research.Viz;

/// <summary>
/// 图表工具 — ScottPlot 5 快速绘图。
/// 用法:
///   var bars = ctx.LoadBars("rb2605", ...);
///   bars.PlotOHLC().SavePng("rb2605.png", 1600, 900);
/// </summary>
public static class ChartHelper
{
    /// <summary>K线图</summary>
    public static Plot PlotOHLC(this BarSeries series, string? title = null, int maxBars = 200)
    {
        var plot = new Plot();
        title ??= $"{series.InstrumentId} ({series.StartTime:yyyy-MM-dd} ~ {series.EndTime:yyyy-MM-dd})";
        plot.Title(title);

        var ohlcs = series.Bars.Take(maxBars).Select(b => new OHLC(
            b.OpenDouble, b.HighDouble, b.LowDouble, b.CloseDouble
        )).ToList();
        plot.Add.Candlestick(ohlcs);

        return plot;
    }

    /// <summary>权益曲线</summary>
    public static Plot PlotEquityCurve(this double[] equity, string? title = null)
    {
        var plot = new Plot();
        plot.Title(title ?? "Equity Curve");
        var curve = plot.Add.Signal(equity);
        curve.LineWidth = 2;
        curve.Color = Colors.Green;
        return plot;
    }

    /// <summary>收益率直方图</summary>
    public static Plot PlotHistogram(this double[] returns, string? title = null, int bins = 50)
    {
        var plot = new Plot();
        plot.Title(title ?? "Returns Distribution");
        var valid = returns.Where(r => !double.IsNaN(r)).ToArray();
        var hist = ScottPlot.Statistics.Histogram.WithBinCount(bins, valid);
        // hist.Bins = bin edges (length = bins + 1)
        double[] binCenters = new double[hist.Bins.Length - 1];
        for (int i = 0; i < binCenters.Length; i++)
            binCenters[i] = (hist.Bins[i] + hist.Bins[i + 1]) / 2;
        plot.Add.Bars(binCenters, hist.Counts);
        return plot;
    }

    /// <summary>回撤曲线</summary>
    public static Plot PlotDrawdown(this double[] drawdownCurve, string? title = null)
    {
        var plot = new Plot();
        plot.Title(title ?? "Drawdown Curve");
        var fill = plot.Add.Signal(drawdownCurve);
        fill.LineWidth = 1;
        fill.Color = Colors.Red;
        return plot;
    }

    /// <summary>SMA 叠加 Close</summary>
    public static Plot PlotSMA(this BarSeries series, int period, int maxBars = 500)
    {
        var plot = new Plot();
        plot.Title($"{series.InstrumentId} SMA({period})");

        var closes = series.Closes.Take(maxBars).ToArray();
        var sma = series.SMA(period).Take(maxBars).ToArray();

        var price = plot.Add.Signal(closes);
        price.LegendText = "Close";
        price.LineWidth = 1;

        var ma = plot.Add.Signal(sma);
        ma.LegendText = $"SMA({period})";
        ma.LineWidth = 2;

        plot.ShowLegend();
        return plot;
    }

    /// <summary>双线对比</summary>
    public static Plot PlotDualSignal(this double[] fast, double[] slow,
        string fastLabel = "Fast", string slowLabel = "Slow", string? title = null)
    {
        var plot = new Plot();
        plot.Title(title ?? $"{fastLabel} vs {slowLabel}");
        var f = plot.Add.Signal(fast);
        f.LegendText = fastLabel;
        f.LineWidth = 1;
        var s = plot.Add.Signal(slow);
        s.LegendText = slowLabel;
        s.LineWidth = 1;
        plot.ShowLegend();
        return plot;
    }

    /// <summary>保存为 PNG</summary>
    public static string SavePng(this Plot plot, string filePath, int width = 1600, int height = 900)
    {
        plot.SavePng(filePath, width, height);
        return filePath;
    }
}
