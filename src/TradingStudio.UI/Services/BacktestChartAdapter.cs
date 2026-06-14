using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using TradingStudio.Engine;
using TradingStudio.Engine.Statistics;

namespace TradingStudio.UI.Services;

/// <summary>
/// EngineReport → OxyPlot 图表数据转换。
/// 回测结果到 UI 的唯一桥接层——切换图表库只需改此类。
/// </summary>
public static class BacktestChartAdapter
{
    /// <summary>构建权益曲线 + 回撤双轴图</summary>
    public static PlotModel CreateEquityModel(PerformanceReport report)
    {
        var model = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };

        // 权益轴
        var equityAxis = new LinearAxis
        {
            Position = AxisPosition.Left, Title = "权益",
            MajorGridlineStyle = LineStyle.Solid,
        };
        model.Axes.Add(equityAxis);

        // 回撤轴（右轴，百分比）
        var drawdownAxis = new LinearAxis
        {
            Position = AxisPosition.Right, Title = "回撤",
            StringFormat = "P0", Minimum = -1, Maximum = 0,
            MajorGridlineStyle = LineStyle.Dot,
        };
        model.Axes.Add(drawdownAxis);

        // 时间轴
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "MM-dd", MajorGridlineStyle = LineStyle.Solid,
        });

        // 权益曲线
        var equitySeries = new LineSeries
        {
            Title = "权益", Color = OxyColors.Cyan, StrokeThickness = 1.5,
            YAxisKey = equityAxis.Key,
        };
        foreach (var (time, equity) in report.EquityCurve)
            equitySeries.Points.Add(new DataPoint(DateTimeAxis.ToDouble(time.DateTime), (double)equity));
        model.Series.Add(equitySeries);

        // 回撤曲线
        var peak = report.StartingCapital;
        var ddSeries = new LineSeries
        {
            Title = "回撤", Color = OxyColor.FromRgb(255, 80, 80), StrokeThickness = 0.8,
            YAxisKey = drawdownAxis.Key,
        };
        foreach (var (time, equity) in report.EquityCurve)
        {
            if (equity > peak) peak = equity;
            var dd = peak > 0 ? (double)((equity - peak) / peak) : 0;
            ddSeries.Points.Add(new DataPoint(DateTimeAxis.ToDouble(time.DateTime), dd));
        }
        model.Series.Add(ddSeries);

        return model;
    }

    /// <summary>K 线图 + 交易买卖标记</summary>
    public static PlotModel CreateTradeChartModel(PerformanceReport report,
        IReadOnlyList<Core.Models.Bar>? bars = null)
    {
        var model = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };

        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "MM-dd", MajorGridlineStyle = LineStyle.Solid,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right, Title = "价格",
            MajorGridlineStyle = LineStyle.Solid,
        });

        // K 线（如有 Bar 数据）
        if (bars != null && bars.Count > 0)
        {
            var candleSeries = new CandleStickSeries
            {
                IncreasingColor = OxyColors.Red, DecreasingColor = OxyColors.Green,
                CandleWidth = 3,
            };
            var items = bars.Select(b => new HighLowItem(
                DateTimeAxis.ToDouble(b.BarTime), b.HighDouble, b.LowDouble,
                b.OpenDouble, b.CloseDouble)).ToList();
            candleSeries.ItemsSource = items;
            model.Series.Add(candleSeries);
        }

        // 买卖标记
        var buyArrow = new ArrowAnnotation
        {
            Color = OxyColors.Red, HeadLength = 6, HeadWidth = 4, StrokeThickness = 1.5,
        };
        var sellArrow = new ArrowAnnotation
        {
            Color = OxyColors.Green, HeadLength = 6, HeadWidth = 4, StrokeThickness = 1.5,
        };

        foreach (var trade in report.Trades)
        {
            var x = DateTimeAxis.ToDouble(trade.EntryTime);
            // Buy 标记
            model.Annotations.Add(new ArrowAnnotation
            {
                Color = OxyColors.Red, HeadLength = 4, HeadWidth = 3,
                StartPoint = new DataPoint(x, (double)trade.EntryPrice * 0.995),
                EndPoint = new DataPoint(x, (double)trade.EntryPrice),
            });
            // Sell 标记
            x = DateTimeAxis.ToDouble(trade.ExitTime);
            model.Annotations.Add(new ArrowAnnotation
            {
                Color = OxyColors.Green, HeadLength = 4, HeadWidth = 3,
                StartPoint = new DataPoint(x, (double)trade.ExitPrice * 1.005),
                EndPoint = new DataPoint(x, (double)trade.ExitPrice),
            });
        }

        return model;
    }

    /// <summary>绩效数据 → 扁平化对象（DataGrid 绑定）</summary>
    public static object ToFlatData(PerformanceReport report) => new
    {
        report.StrategyId,
        report.StartingCapital,
        report.FinalEquity,
        report.TotalNetProfit,
        report.MaxDrawdown,
        report.TotalTrades,
        report.WinRate,
        report.AverageWin,
        report.AverageLoss,
        report.TotalFees,
        report.TotalSlippage,
    };
}
