using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Terminal.Services;
using Bar = TradingStudio.Core.Models.Bar;

namespace TradingStudio.Terminal.ViewModels;

/// <summary>
/// K线图表 ViewModel — 四窗格布局 (K线主图 / 成交量 / MACD / RSI)。
/// PlotModel 是纯数据对象，ViewModel 不持有 UI 控件引用。
/// </summary>
public partial class ChartViewModel : ObservableObject
{
    private readonly DataSimulator _simulator;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ChartViewModel> _logger;

    // === 技术指标引擎 ===
    private readonly SmaIndicator _ma5 = new(5);
    private readonly SmaIndicator _ma20 = new(20);
    private readonly SmaIndicator _ma60 = new(60);
    private readonly BollingerIndicator _boll = new(20, 2.0);
    private readonly MacdIndicator _macd = new(12, 26, 9);
    private readonly RsiIndicator _rsi = new(14);

    // === 图表序列（存引用以便增量更新） ===
    private CandleStickSeries _candleSeries = null!;
    private LineSeries _ma5Series = null!, _ma20Series = null!, _ma60Series = null!;
    private LineSeries _bollUpper = null!, _bollMiddle = null!, _bollLower = null!;
    private RectangleBarSeries _macdHistSeries = null!;
    private LineSeries _macdDifSeries = null!, _macdDeaSeries = null!;
    private LineSeries _rsiSeries = null!;

    // === X 轴同步 ===
    private readonly List<DateTimeAxis> _xAxes = new();
    private bool _isSyncing;

    // === PlotModel（纯数据对象，引用不变，不需要 [ObservableProperty]） ===
    public PlotModel KLineModel { get; private set; } = null!;
    public PlotModel VolumeModel { get; private set; } = null!;
    public PlotModel MacdModel { get; private set; } = null!;
    public PlotModel RsiModel { get; private set; } = null!;

    // === 可绑定状态属性 ===
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "就绪";

    // 指标开关
    [ObservableProperty] private bool _showMA = true;
    [ObservableProperty] private bool _showBoll = true;
    [ObservableProperty] private bool _showMACD = true;
    [ObservableProperty] private bool _showRSI = true;

    // === DuckDB 配置 ===
    private const string DefaultDbPath = @"C:\Works\ClaudeCode\TradingStudio\data\bars_history.duckdb";
    private const string DefaultProduct = "SA";
    private const string DefaultFreq = "15min";

    public ChartViewModel(ILogger<ChartViewModel> logger)
    {
        _logger = logger;
        _logger.LogDebug("ChartViewModel initializing...");

        _dispatcher = Application.Current?.Dispatcher
                      ?? Dispatcher.CurrentDispatcher;

        _simulator = new DataSimulator(startPrice: 5200);
        _simulator.BarUpdated += OnBarUpdated;

        InitializePlotModels();
        _logger.LogDebug("PlotModels initialized (4 panes)");

        // 尝试从 DuckDB 加载真实数据，失败则 fallback 到模拟器
        if (!TryLoadFromDuckDB(DefaultDbPath, DefaultProduct, DefaultFreq))
        {
            LoadHistoricalData();
            _logger.LogInformation("ChartViewModel ready (simulated): {bars} bars",
                _simulator.HistoryBars.Count);
        }
    }

    /// <summary>
    /// 从 DuckDB 加载成交量加权价格指数。
    /// 返回 true 表示成功加载，false 表示 fallback 到模拟数据。
    /// </summary>
    private bool TryLoadFromDuckDB(string dbPath, string product, string freq)
    {
        try
        {
            if (!System.IO.File.Exists(dbPath))
            {
                _logger.LogWarning("DuckDB not found: {path}, fallback to simulator", dbPath);
                return false;
            }

            using var conn = new DuckDBConnection($"Data Source={dbPath}");
            conn.Open();

            var table = $"bars_{product.ToLower()}_1min";
            string sql;

            // 按频率聚合
            if (freq == "day")
            {
                sql = $@"
                    SELECT trading_day::TIMESTAMP as dt,
                           FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                           MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                           SUM(volume) as volume
                    FROM {table} GROUP BY trading_day ORDER BY dt";
            }
            else if (int.TryParse(freq.Replace("min", ""), out int periodMin))
            {
                sql = $@"
                    SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                           INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / {periodMin}) * {periodMin}) MINUTE as dt,
                           FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                           MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                           SUM(volume) as volume
                    FROM {table} GROUP BY dt ORDER BY dt";
            }
            else
            {
                sql = $"SELECT bar_time::TIMESTAMP as dt, open/1e7 as open, high/1e7 as high, low/1e7 as low, close/1e7 as close, volume FROM {table} ORDER BY bar_time";
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();

            var bars = new List<Bar>();
            var dtIdx = reader.GetOrdinal("dt");
            var oIdx = reader.GetOrdinal("open");
            var hIdx = reader.GetOrdinal("high");
            var lIdx = reader.GetOrdinal("low");
            var cIdx = reader.GetOrdinal("close");
            var vIdx = reader.GetOrdinal("volume");

            while (reader.Read())
            {
                bars.Add(new Bar
                {
                    InstrumentId = product,
                    BarTime = reader.GetDateTime(dtIdx),
                    Open = (long)(reader.GetDouble(oIdx) * 1e7),
                    High = (long)(reader.GetDouble(hIdx) * 1e7),
                    Low = (long)(reader.GetDouble(lIdx) * 1e7),
                    Close = (long)(reader.GetDouble(cIdx) * 1e7),
                    Volume = reader.GetInt64(vIdx),
                });
            }

            if (bars.Count < 10)
            {
                _logger.LogWarning("DuckDB returned only {count} bars for {product}", bars.Count, product);
                return false;
            }

            // 喂入指标
            foreach (var bar in bars)
                FeedIndicators(bar);

            // 构建图表
            BuildCandleSeries(bars);
            BuildVolumeSeries(bars);
            RefreshAllPlotModels();

            // 更新 X 轴格式：长时间跨度用日期，短时间用时间
            var span = bars[^1].BarTime - bars[0].BarTime;
            var timeFormat = span.TotalDays > 3 ? "MM-dd" : "HH:mm";
            foreach (var ax in _xAxes)
                ax.StringFormat = timeFormat;

            StatusText = $"{product} {freq} | {bars.Count:N0} bars | {bars[0].BarTime:yyyy-MM-dd} ~ {bars[^1].BarTime:yyyy-MM-dd}";

            // 初始缩放到最后 200 根 Bar（用户可拖拽查看全量）
            var visible = Math.Min(200, bars.Count);
            double xMin = DateTimeAxis.ToDouble(bars[^visible].BarTime);
            double xMax = DateTimeAxis.ToDouble(bars[^1].BarTime);
            foreach (var ax in _xAxes)
                ax.Zoom(xMin, xMax);
            _logger.LogInformation("ChartViewModel ready (DuckDB): {product} {freq} {bars} bars, {start}~{end}",
                product, freq, bars.Count, bars[0].BarTime, bars[^1].BarTime);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DuckDB load failed for {product} {freq}, fallback to simulator", product, freq);
            return false;
        }
    }

    // ============================================================
    // 初始化四窗格 PlotModel
    // ============================================================

    private void InitializePlotModels()
    {
        // ── 窗格1: K线主图 ──
        var klineXAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm",
            IntervalType = DateTimeIntervalType.Minutes,
            MajorGridlineStyle = LineStyle.Solid,
            IsZoomEnabled = true,
            IsPanEnabled = true,
        };
        _xAxes.Add(klineXAxis);

        KLineModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        KLineModel.Axes.Add(klineXAxis);
        KLineModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right,
            Title = "价格",
            MajorGridlineStyle = LineStyle.Solid,
        });

        _candleSeries = new CandleStickSeries
        {
            IncreasingColor = OxyColors.Red,
            DecreasingColor = OxyColors.Green,
            CandleWidth = 4,
        };
        KLineModel.Series.Add(_candleSeries);

        _ma5Series = MakeLine(OxyColors.Gold, "MA5");
        _ma20Series = MakeLine(OxyColors.Magenta, "MA20");
        _ma60Series = MakeLine(OxyColors.Cyan, "MA60");
        KLineModel.Series.Add(_ma5Series);
        KLineModel.Series.Add(_ma20Series);
        KLineModel.Series.Add(_ma60Series);

        _bollUpper = MakeLine(OxyColor.FromRgb(100, 100, 255), "BOLL↑");
        _bollMiddle = MakeLine(OxyColor.FromRgb(150, 150, 255), "BOLL");
        _bollLower = MakeLine(OxyColor.FromRgb(100, 100, 255), "BOLL↓");
        KLineModel.Series.Add(_bollUpper);
        KLineModel.Series.Add(_bollMiddle);
        KLineModel.Series.Add(_bollLower);

#pragma warning disable CS0618 // AxisChanged 已过时但仍是当前最可靠的同步方式
        klineXAxis.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618

        // ── 窗格2: 成交量 ──
        var volXAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            IsAxisVisible = false,
            IsZoomEnabled = true,
            IsPanEnabled = true,
        };
        _xAxes.Add(volXAxis);

        VolumeModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        VolumeModel.Axes.Add(volXAxis);
        VolumeModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right,
            Title = "量",
            MajorGridlineStyle = LineStyle.Solid,
        });

        var volSeries = new RectangleBarSeries
        {
            FillColor = OxyColor.FromRgb(80, 160, 80),
            StrokeColor = OxyColors.Transparent,
            StrokeThickness = 0,
        };
        VolumeModel.Series.Add(volSeries);

#pragma warning disable CS0618
        volXAxis.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618

        // ── 窗格3: MACD ──
        var macdXAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            IsAxisVisible = false,
            IsZoomEnabled = true,
            IsPanEnabled = true,
        };
        _xAxes.Add(macdXAxis);

        MacdModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        MacdModel.Axes.Add(macdXAxis);
        MacdModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right,
            Title = "MACD",
        });

        _macdHistSeries = new RectangleBarSeries
        {
            FillColor = OxyColor.FromRgb(200, 60, 60),
            StrokeThickness = 0,
        };
        _macdDifSeries = new LineSeries { Color = OxyColors.White, StrokeThickness = 0.8, Title = "DIF" };
        _macdDeaSeries = new LineSeries { Color = OxyColors.Yellow, StrokeThickness = 0.8, Title = "DEA" };
        MacdModel.Series.Add(_macdHistSeries);
        MacdModel.Series.Add(_macdDifSeries);
        MacdModel.Series.Add(_macdDeaSeries);

#pragma warning disable CS0618
        macdXAxis.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618

        // ── 窗格4: RSI ──
        var rsiXAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm",
            IntervalType = DateTimeIntervalType.Minutes,
            MajorGridlineStyle = LineStyle.Solid,
            IsZoomEnabled = true,
            IsPanEnabled = true,
        };
        _xAxes.Add(rsiXAxis);

        RsiModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        RsiModel.Axes.Add(rsiXAxis);
        RsiModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right,
            Title = "RSI",
            Minimum = 0,
            Maximum = 100,
        });

        _rsiSeries = new LineSeries { Color = OxyColors.Cyan, StrokeThickness = 1.2, Title = "RSI" };
        RsiModel.Series.Add(_rsiSeries);

        // RSI 参考线
        RsiModel.Annotations.Add(new LineAnnotation
        {
            Y = 70, Type = LineAnnotationType.Horizontal,
            Color = OxyColor.FromRgb(255, 80, 80),
            LineStyle = LineStyle.Dash, StrokeThickness = 0.5,
        });
        RsiModel.Annotations.Add(new LineAnnotation
        {
            Y = 30, Type = LineAnnotationType.Horizontal,
            Color = OxyColor.FromRgb(80, 255, 80),
            LineStyle = LineStyle.Dash, StrokeThickness = 0.5,
        });

#pragma warning disable CS0618
        rsiXAxis.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618
    }

    // ============================================================
    // 加载历史数据
    // ============================================================

    private void LoadHistoricalData()
    {
        var bars = _simulator.GenerateHistory(500);

        foreach (var bar in bars)
            FeedIndicators(bar);

        BuildCandleSeries(bars);
        BuildVolumeSeries(bars);
        RefreshAllPlotModels();
    }

    private void FeedIndicators(Bar bar)
    {
        _ma5.Update(bar);
        _ma20.Update(bar);
        _ma60.Update(bar);
        _boll.Update(bar);
        _macd.Update(bar);
        _rsi.Update(bar);
    }

    // ============================================================
    // 构建序列数据
    // ============================================================

    private void BuildCandleSeries(List<Bar> bars)
    {
        var items = new List<HighLowItem>(bars.Count);
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            items.Add(new HighLowItem(
                DateTimeAxis.ToDouble(b.BarTime),
                b.HighDouble, b.LowDouble, b.OpenDouble, b.CloseDouble));
        }
        _candleSeries.ItemsSource = items;

        BuildLinePoints(_ma5Series, _ma5.Values, bars);
        BuildLinePoints(_ma20Series, _ma20.Values, bars);
        BuildLinePoints(_ma60Series, _ma60.Values, bars);
        BuildLinePoints(_bollUpper, _boll.Upper, bars);
        BuildLinePoints(_bollMiddle, _boll.Middle, bars);
        BuildLinePoints(_bollLower, _boll.Lower, bars);
    }

    private void BuildVolumeSeries(List<Bar> bars)
    {
        var volSeries = (RectangleBarSeries)VolumeModel.Series[0];
        volSeries.ItemsSource = bars.Select(b =>
        {
            double x = DateTimeAxis.ToDouble(b.BarTime);
            bool up = b.Close >= b.Open;
            return new RectangleBarItem(x - 0.35, 0, x + 0.35, b.Volume)
            {
                Color = up ? OxyColor.FromRgb(200, 80, 80) : OxyColor.FromRgb(80, 200, 80),
            };
        }).ToList();
    }

    // ============================================================
    // 实时 Bar 更新
    // ============================================================

    private void OnBarUpdated(Bar bar)
    {
        _dispatcher.InvokeAsync(() =>
        {
            FeedIndicators(bar);
            double x = DateTimeAxis.ToDouble(bar.BarTime);

            // K线
            var candleItems = _candleSeries.ItemsSource.Cast<HighLowItem>().ToList();
            candleItems.Add(new HighLowItem(x, bar.HighDouble, bar.LowDouble, bar.OpenDouble, bar.CloseDouble));
            _candleSeries.ItemsSource = candleItems;

            // MA
            TryAppendPoint(_ma5Series, x, _ma5.CurrentValue);
            TryAppendPoint(_ma20Series, x, _ma20.CurrentValue);
            TryAppendPoint(_ma60Series, x, _ma60.CurrentValue);

            // BOLL
            if (_boll.IsReady)
            {
                TryAppendPoint(_bollUpper, x, _boll.Upper[^1]);
                TryAppendPoint(_bollMiddle, x, _boll.Middle[^1]);
                TryAppendPoint(_bollLower, x, _boll.Lower[^1]);
            }

            // 成交量
            bool up = bar.Close >= bar.Open;
            var volSeries = (RectangleBarSeries)VolumeModel.Series[0];
            var volItems = volSeries.ItemsSource.Cast<RectangleBarItem>().ToList();
            volItems.Add(new RectangleBarItem(x - 0.35, 0, x + 0.35, bar.Volume)
            {
                Color = up ? OxyColor.FromRgb(200, 80, 80) : OxyColor.FromRgb(80, 200, 80),
            });
            volSeries.ItemsSource = volItems;

            // MACD
            if (_macd.IsReady)
            {
                _macdDifSeries.Points.Add(new DataPoint(x, _macd.CurrentDif));
                _macdDeaSeries.Points.Add(new DataPoint(x, _macd.CurrentDea));
                double h = _macd.CurrentHist;
                var histItems = _macdHistSeries.ItemsSource.Cast<RectangleBarItem>().ToList();
                histItems.Add(new RectangleBarItem(x - 0.35, 0, x + 0.35, h)
                {
                    Color = h >= 0 ? OxyColor.FromRgb(200, 80, 80) : OxyColor.FromRgb(80, 200, 80),
                });
                _macdHistSeries.ItemsSource = histItems;
            }

            // RSI
            if (_rsi.IsReady)
                _rsiSeries.Points.Add(new DataPoint(x, _rsi.CurrentValue));

            KeepWindowScrolling(100);
            RefreshAllPlotModels();
            StatusText = $"实时 | {bar.BarTime:HH:mm:ss} | C={bar.CloseDouble:F1} | V={bar.Volume}";

            // 每 60 根 Bar 打一次日志
            var count = _simulator.HistoryBars.Count;
            if (count % 60 == 0)
            {
                _logger.LogDebug("Bar #{idx}: {time} C={close:F2} V={vol}",
                    count, bar.BarTime, bar.CloseDouble, bar.Volume);
            }
        });
    }

    private void KeepWindowScrolling(int visibleCount)
    {
        var bars = _simulator.HistoryBars;
        if (bars.Count <= visibleCount) return;

        double xMin = DateTimeAxis.ToDouble(bars[^visibleCount].BarTime);
        double xMax = DateTimeAxis.ToDouble(bars[^1].BarTime);

        foreach (var ax in _xAxes)
            ax.Zoom(xMin, xMax);
    }

    private void RefreshAllPlotModels()
    {
        KLineModel.InvalidatePlot(true);
        VolumeModel.InvalidatePlot(true);
        MacdModel.InvalidatePlot(true);
        RsiModel.InvalidatePlot(true);
    }

    // ============================================================
    // X 轴同步
    // ============================================================

    private void OnPaneAxisChanged(object? sender, AxisChangedEventArgs e)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        var src = (DateTimeAxis)sender!;
        foreach (var ax in _xAxes)
        {
            if (ax == src) continue;
            ax.Zoom(src.ActualMinimum, src.ActualMaximum);
        }
        _isSyncing = false;
    }

    // ============================================================
    // 命令
    // ============================================================

    [RelayCommand]
    private void ToggleRealtime()
    {
        if (IsRunning)
        {
            _simulator.StopRealtime();
            IsRunning = false;
            StatusText = "已暂停";
            _logger.LogInformation("Realtime stopped. Total bars: {count}", _simulator.HistoryBars.Count);
        }
        else
        {
            _simulator.StartRealtime();
            IsRunning = true;
            StatusText = "实时运行中...";
            _logger.LogInformation("Realtime started");
        }
    }

    [RelayCommand]
    private void ResetView()
    {
        foreach (var ax in _xAxes)
            ax.Reset();
        foreach (var m in new[] { KLineModel, VolumeModel, MacdModel, RsiModel })
            m.ResetAllAxes();

        RefreshAllPlotModels();
        StatusText = "视图已重置";
    }

    // ── 指标开关 ──

    partial void OnShowMAChanged(bool value)
    {
        _ma5Series.IsVisible = value;
        _ma20Series.IsVisible = value;
        _ma60Series.IsVisible = value;
        KLineModel.InvalidatePlot(true);
    }

    partial void OnShowBollChanged(bool value)
    {
        _bollUpper.IsVisible = value;
        _bollMiddle.IsVisible = value;
        _bollLower.IsVisible = value;
        KLineModel.InvalidatePlot(true);
    }

    partial void OnShowMACDChanged(bool value)
    {
        _macdHistSeries.IsVisible = value;
        _macdDifSeries.IsVisible = value;
        _macdDeaSeries.IsVisible = value;
        MacdModel.InvalidatePlot(true);
    }

    partial void OnShowRSIChanged(bool value)
    {
        _rsiSeries.IsVisible = value;
        RsiModel.InvalidatePlot(true);
    }

    // ============================================================
    // 工具方法
    // ============================================================

    private static LineSeries MakeLine(OxyColor color, string title) =>
        new() { Color = color, StrokeThickness = 1.0, Title = title };

    private static void BuildLinePoints(LineSeries series, IReadOnlyList<double> values, List<Bar> bars)
    {
        series.Points.Clear();
        int count = Math.Min(values.Count, bars.Count);
        for (int i = 0; i < count; i++)
        {
            if (double.IsNaN(values[i])) continue;
            series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(bars[i].BarTime), values[i]));
        }
    }

    private static void TryAppendPoint(LineSeries series, double x, double value)
    {
        if (!double.IsNaN(value))
            series.Points.Add(new DataPoint(x, value));
    }
}
