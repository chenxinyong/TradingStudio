using System.Collections.ObjectModel;
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
/// K线图表 ViewModel — 四窗格 (K线/量/MACD/RSI)。
/// 支持：DuckDB 历史数据 / DataSimulator 演示 / SignalR 实时 Bar。
/// </summary>
public partial class ChartViewModel : ObservableObject
{
    private readonly ILogger<ChartViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    // === 技术指标引擎 ===
    private SmaIndicator _ma5 = new(5);
    private SmaIndicator _ma20 = new(20);
    private SmaIndicator _ma60 = new(60);
    private BollingerIndicator _boll = new(20, 2.0);
    private MacdIndicator _macd = new(12, 26, 9);
    private RsiIndicator _rsi = new(14);

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

    // === 已加载的 Bar 数据（用于增量更新） ===
    private List<Bar> _loadedBars = new();
    private readonly object _barLock = new();

    // === PlotModel (引用不变) ===
    public PlotModel KLineModel { get; private set; } = null!;
    public PlotModel VolumeModel { get; private set; } = null!;
    public PlotModel MacdModel { get; private set; } = null!;
    public PlotModel RsiModel { get; private set; } = null!;

    // === 状态 ===
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "就绪";

    // === 指标开关 ===
    [ObservableProperty] private bool _showMA = true;
    [ObservableProperty] private bool _showBoll = true;
    [ObservableProperty] private bool _showMACD = true;
    [ObservableProperty] private bool _showRSI = true;

    // === 品种 / 周期选择 ===
    public ObservableCollection<ProductItem> AvailableProducts { get; } = new();
    public List<string> AvailableFrequencies { get; } = new()
        { "1min", "5min", "15min", "30min", "day", "week" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ProductItem? _selectedProduct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _selectedFrequency = "15min";

    public string WindowTitle => SelectedProduct != null
        ? $"K线图 — {SelectedProduct.Code} {SelectedFrequency}"
        : "K线图 — 选择品种";

    // === 数据源路径 ===
    private string _dbPath;
    private const string DefaultDbPath = @"C:\Works\ClaudeCode\TradingStudio\data\bars_history.duckdb";

    // === SignalR 实时 Bar ===
    private EngineHubClient? _hub;
    private IDisposable? _barSubscription;

    public ChartViewModel(ILogger<ChartViewModel> logger,
                          EngineHubClient? hub = null,
                          string? dbPath = null)
    {
        _logger = logger;
        _hub = hub;
        _dbPath = dbPath ?? DefaultDbPath;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        InitializePlotModels();
        LoadProductList();
    }

    // ============================================================
    // 品种列表
    // ============================================================

    private void LoadProductList()
    {
        try
        {
            using var conn = new DuckDBConnection($"Data Source={_dbPath}");
            conn.Open();

            var sql = """
                SELECT DISTINCT
                    REPLACE(instrument_id, '000', '') as code,
                    instrument_id,
                    COUNT(*) as bars,
                    MIN(bar_time) as first_bar,
                    MAX(bar_time) as last_bar
                FROM bars_5min
                WHERE instrument_id LIKE '%000'
                  AND instrument_id NOT LIKE '%F000'
                GROUP BY instrument_id
                ORDER BY code
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();

            var codeIdx = reader.GetOrdinal("code");
            var idIdx = reader.GetOrdinal("instrument_id");
            var barsIdx = reader.GetOrdinal("bars");
            var firstIdx = reader.GetOrdinal("first_bar");
            var lastIdx = reader.GetOrdinal("last_bar");

            AvailableProducts.Clear();
            while (reader.Read())
            {
                var item = new ProductItem(
                    reader.GetString(codeIdx).ToUpper(),
                    reader.GetString(idIdx),
                    reader.GetInt64(barsIdx),
                    reader.GetString(firstIdx),
                    reader.GetString(lastIdx));
                AvailableProducts.Add(item);
            }

            _logger.LogInformation("Loaded {count} products from DuckDB", AvailableProducts.Count);

            // 默认选中第一个品种
            if (AvailableProducts.Count > 0)
            {
                SelectedProduct = AvailableProducts.FirstOrDefault(p => p.Code == "SA")
                                  ?? AvailableProducts[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load product list from DuckDB, using defaults");
            // fallback: 少量常见品种
            foreach (var code in new[] { "SA", "RB", "AG", "CU", "AU", "MA", "FG", "TA", "I", "M" })
                AvailableProducts.Add(new ProductItem(code, code.ToLower() + "000", 0, "", ""));
            if (AvailableProducts.Count > 0) SelectedProduct = AvailableProducts[0];
        }
    }

    // ============================================================
    // 品种/周期切换
    // ============================================================

    partial void OnSelectedProductChanged(ProductItem? value)
    {
        if (value != null) ReloadData();
    }

    partial void OnSelectedFrequencyChanged(string value)
    {
        if (SelectedProduct != null) ReloadData();
    }

    [RelayCommand]
    private void Reload()
    {
        if (SelectedProduct != null)
            ReloadData();
    }

    private void ReloadData()
    {
        if (SelectedProduct == null) return;

        var product = SelectedProduct.InstrumentId;
        var freq = SelectedFrequency;

        StatusText = $"加载中... {SelectedProduct.Code} {freq}";
        _logger.LogInformation("Reloading chart: {Product} {Freq}", product, freq);

        // 重置指标引擎
        ResetIndicators();

        // 尝试 DuckDB 加载
        if (TryLoadFromDuckDB(product, freq))
            return;

        // Fallback: 模拟数据
        _logger.LogWarning("DuckDB load failed, using simulator for {Product}", product);
        StatusText = $"模拟 {SelectedProduct.Code} {freq} | DuckDB 不可用";
    }

    private bool TryLoadFromDuckDB(string instrumentId, string freq)
    {
        try
        {
            if (!System.IO.File.Exists(_dbPath))
            {
                _logger.LogWarning("DuckDB not found: {path}", _dbPath);
                return false;
            }

            using var conn = new DuckDBConnection($"Data Source={_dbPath}");
            conn.Open();

            var (table, timeCol) = ResolveTable(freq);
            string sql = BuildAggregationSql(table, timeCol, freq, instrumentId);
            string sqlCount = $"SELECT COUNT(*) FROM {table} WHERE instrument_id = '{instrumentId}'";

            // 先检查数据存在
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = sqlCount;
            var rowCount = (long)countCmd.ExecuteScalar()!;

            if (rowCount == 0)
            {
                // 从 bars_1min 聚合
                _logger.LogInformation("No data in {table}, aggregating from bars_1min", table);
                sql = Build1MinAggregation(freq, instrumentId);
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
                    InstrumentId = instrumentId,
                    BarTime = reader.GetDateTime(dtIdx),
                    Open = (long)(reader.GetDouble(oIdx) * 1e7),
                    High = (long)(reader.GetDouble(hIdx) * 1e7),
                    Low = (long)(reader.GetDouble(lIdx) * 1e7),
                    Close = (long)(reader.GetDouble(cIdx) * 1e7),
                    Volume = reader.GetInt64(vIdx),
                });
            }

            if (bars.Count < 5)
            {
                _logger.LogWarning("Only {count} bars for {product}", bars.Count, instrumentId);
                return false;
            }

            // 喂入指标 + 构建图表
            foreach (var bar in bars)
                FeedIndicators(bar);

            lock (_barLock) { _loadedBars = bars; }

            BuildAllSeries(bars);
            RefreshAllPlotModels();

            // 初始缩放到最后 200 根
            var visible = Math.Min(200, bars.Count);
            double xMin = DateTimeAxis.ToDouble(bars[^visible].BarTime);
            double xMax = DateTimeAxis.ToDouble(bars[^1].BarTime);
            foreach (var ax in _xAxes)
                ax.Zoom(xMin, xMax);

            StatusText = $"{SelectedProduct!.Code} {freq} | {bars.Count:N0} bars | "
                       + $"{bars[0].BarTime:yyyy-MM-dd} ~ {bars[^1].BarTime:yyyy-MM-dd}";
            _logger.LogInformation("Chart loaded: {Product} {Freq} {Count} bars",
                instrumentId, freq, bars.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DuckDB load failed for {Product} {Freq}", instrumentId, freq);
            return false;
        }
    }

    // ============================================================
    // SQL 构建
    // ============================================================

    private static (string table, string timeCol) ResolveTable(string freq) => freq switch
    {
        "5min" => ("bars_5min", "bar_time"),
        "15min" => ("bars_15min", "bar_time"),
        "30min" => ("bars_15min", "bar_time"),
        "day" => ("bars_day", "trading_day"),
        "week" => ("bars_week", "trading_week"),
        _ => ("bars_1min", "bar_time"),
    };

    private static string BuildAggregationSql(string table, string timeCol, string freq, string instrumentId)
    {
        if (freq is "day" or "week")
        {
            return $"""
                SELECT {timeCol} as dt,
                    FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                    MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                    SUM(volume) as volume
                FROM {table}
                WHERE instrument_id = '{instrumentId}'
                GROUP BY {timeCol} ORDER BY dt
                """;
        }

        if (freq == "30min")
        {
            return $"""
                SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                       INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / 30) * 30) MINUTE as dt,
                    FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                    MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                    SUM(volume) as volume
                FROM {table}
                WHERE instrument_id = '{instrumentId}'
                GROUP BY dt ORDER BY dt
                """;
        }

        // 1min / 5min / 15min: 直接选
        int periodMin = int.Parse(freq.Replace("min", ""));
        if (periodMin > 1)
        {
            return $"""
                SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                       INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / {periodMin}) * {periodMin}) MINUTE as dt,
                    FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                    MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                    SUM(volume) as volume
                FROM {table}
                WHERE instrument_id = '{instrumentId}'
                GROUP BY dt ORDER BY dt
                """;
        }

        return $"""
            SELECT bar_time::TIMESTAMP as dt,
                open/1e7 as open, high/1e7 as high,
                low/1e7 as low, close/1e7 as close,
                volume
            FROM {table}
            WHERE instrument_id = '{instrumentId}'
            ORDER BY dt
            """;
    }

    private static string Build1MinAggregation(string freq, string instrumentId)
    {
        int periodMin = freq switch
        {
            "5min" => 5, "15min" => 15, "30min" => 30,
            _ => 5,
        };
        return $"""
            SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                   INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / {periodMin}) * {periodMin}) MINUTE as dt,
                FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                SUM(volume) as volume
            FROM bars_1min
            WHERE instrument_id = '{instrumentId}'
            GROUP BY dt ORDER BY dt
            """;
    }

    // ============================================================
    // 指标
    // ============================================================

    private void ResetIndicators()
    {
        _ma5 = new(5); _ma20 = new(20); _ma60 = new(60);
        _boll = new(20, 2.0); _macd = new(12, 26, 9); _rsi = new(14);
    }

    private void FeedIndicators(Bar bar)
    {
        _ma5.Update(bar); _ma20.Update(bar); _ma60.Update(bar);
        _boll.Update(bar); _macd.Update(bar); _rsi.Update(bar);
    }

    // ============================================================
    // 图表初始化
    // ============================================================

    private void InitializePlotModels()
    {
        // ── K线主图 ──
        var klineX = new DateTimeAxis
        {
            Position = AxisPosition.Bottom, StringFormat = "MM-dd",
            MajorGridlineStyle = LineStyle.Solid,
            IsZoomEnabled = true, IsPanEnabled = true,
        };
        _xAxes.Add(klineX);
        KLineModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        KLineModel.Axes.Add(klineX);
        KLineModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right, Title = "价格",
            MajorGridlineStyle = LineStyle.Solid,
        });
        _candleSeries = new CandleStickSeries
        {
            IncreasingColor = OxyColors.Red, DecreasingColor = OxyColors.Green,
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
#pragma warning disable CS0618
        klineX.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618

        // ── 成交量 ──
        var volX = new DateTimeAxis
        {
            Position = AxisPosition.Bottom, IsAxisVisible = false,
            IsZoomEnabled = true, IsPanEnabled = true,
        };
        _xAxes.Add(volX);
        VolumeModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        VolumeModel.Axes.Add(volX);
        VolumeModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right, Title = "量",
            MajorGridlineStyle = LineStyle.Solid,
        });
        VolumeModel.Series.Add(new RectangleBarSeries
        {
            FillColor = OxyColor.FromRgb(80, 160, 80),
            StrokeColor = OxyColors.Transparent, StrokeThickness = 0,
        });
#pragma warning disable CS0618
        volX.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618

        // ── MACD ──
        var macdX = new DateTimeAxis
        {
            Position = AxisPosition.Bottom, IsAxisVisible = false,
            IsZoomEnabled = true, IsPanEnabled = true,
        };
        _xAxes.Add(macdX);
        MacdModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        MacdModel.Axes.Add(macdX);
        MacdModel.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Title = "MACD" });
        _macdHistSeries = new RectangleBarSeries
        {
            FillColor = OxyColor.FromRgb(200, 60, 60), StrokeThickness = 0,
        };
        _macdDifSeries = new LineSeries { Color = OxyColors.White, StrokeThickness = 0.8, Title = "DIF" };
        _macdDeaSeries = new LineSeries { Color = OxyColors.Yellow, StrokeThickness = 0.8, Title = "DEA" };
        MacdModel.Series.Add(_macdHistSeries);
        MacdModel.Series.Add(_macdDifSeries);
        MacdModel.Series.Add(_macdDeaSeries);
#pragma warning disable CS0618
        macdX.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618

        // ── RSI ──
        var rsiX = new DateTimeAxis
        {
            Position = AxisPosition.Bottom, StringFormat = "MM-dd",
            MajorGridlineStyle = LineStyle.Solid,
            IsZoomEnabled = true, IsPanEnabled = true,
        };
        _xAxes.Add(rsiX);
        RsiModel = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };
        RsiModel.Axes.Add(rsiX);
        RsiModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right, Title = "RSI",
            Minimum = 0, Maximum = 100,
        });
        _rsiSeries = new LineSeries { Color = OxyColors.Cyan, StrokeThickness = 1.2, Title = "RSI" };
        RsiModel.Series.Add(_rsiSeries);
        RsiModel.Annotations.Add(new LineAnnotation
        {
            Y = 70, Type = LineAnnotationType.Horizontal,
            Color = OxyColor.FromRgb(255, 80, 80), LineStyle = LineStyle.Dash, StrokeThickness = 0.5,
        });
        RsiModel.Annotations.Add(new LineAnnotation
        {
            Y = 30, Type = LineAnnotationType.Horizontal,
            Color = OxyColor.FromRgb(80, 255, 80), LineStyle = LineStyle.Dash, StrokeThickness = 0.5,
        });
#pragma warning disable CS0618
        rsiX.AxisChanged += OnPaneAxisChanged;
#pragma warning restore CS0618
    }

    // ============================================================
    // 构建序列
    // ============================================================

    private void BuildAllSeries(List<Bar> bars)
    {
        BuildCandleSeries(bars);
        BuildVolumeSeries(bars);

        // 判断时间跨度调整 X 轴格式
        if (bars.Count >= 2)
        {
            var span = bars[^1].BarTime - bars[0].BarTime;
            var format = span.TotalDays > 7 ? "MM-dd" : "MM-dd HH:mm";
            foreach (var ax in _xAxes)
                ax.StringFormat = format;
        }
    }

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
        var series = (RectangleBarSeries)VolumeModel.Series[0];
        series.ItemsSource = bars.Select(b =>
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
    // SignalR 实时 Bar
    // ============================================================

    /// <summary>订阅引擎推送的实时 Bar (供外部调用或构造函数自动连接)</summary>
    public void SubscribeRealTimeBars()
    {
        if (_hub == null) return;
        _barSubscription?.Dispose();

        _hub.BarUpdated += OnSignalRBar;
        _logger.LogInformation("Subscribed to real-time bars via SignalR");
    }

    public void UnsubscribeRealTimeBars()
    {
        if (_hub == null) return;
        _hub.BarUpdated -= OnSignalRBar;
        _barSubscription?.Dispose();
        _barSubscription = null;
    }

    private void OnSignalRBar(BarPayload bar)
    {
        // 只处理当前品种的 Bar
        if (SelectedProduct == null || bar.InstrumentId != SelectedProduct.InstrumentId)
            return;

        _dispatcher.InvokeAsync(() =>
        {
            var b = new Bar
            {
                InstrumentId = bar.InstrumentId,
                BarTime = bar.BarTime,
                Open = (long)(bar.Open * 1e7),
                High = (long)(bar.High * 1e7),
                Low = (long)(bar.Low * 1e7),
                Close = (long)(bar.Close * 1e7),
                Volume = bar.Volume,
            };

            FeedIndicators(b);
            double x = DateTimeAxis.ToDouble(b.BarTime);

            // K线追加
            var candleItems = _candleSeries.ItemsSource.Cast<HighLowItem>().ToList();
            // 如果最后一根 Bar 时间相同，替换；否则追加
            if (candleItems.Count > 0)
            {
                var last = candleItems[^1];
                if (Math.Abs(last.X - x) < 0.001)
                    candleItems[^1] = new HighLowItem(x, b.HighDouble, b.LowDouble, b.OpenDouble, b.CloseDouble);
                else
                    candleItems.Add(new HighLowItem(x, b.HighDouble, b.LowDouble, b.OpenDouble, b.CloseDouble));
            }
            else
            {
                candleItems.Add(new HighLowItem(x, b.HighDouble, b.LowDouble, b.OpenDouble, b.CloseDouble));
            }
            _candleSeries.ItemsSource = candleItems;

            // 均线追加
            TryAppendPoint(_ma5Series, x, _ma5.CurrentValue);
            TryAppendPoint(_ma20Series, x, _ma20.CurrentValue);
            TryAppendPoint(_ma60Series, x, _ma60.CurrentValue);
            if (_boll.IsReady)
            {
                TryAppendPoint(_bollUpper, x, _boll.Upper[^1]);
                TryAppendPoint(_bollMiddle, x, _boll.Middle[^1]);
                TryAppendPoint(_bollLower, x, _boll.Lower[^1]);
            }

            // 成交量
            bool up = b.Close >= b.Open;
            var volSeries = (RectangleBarSeries)VolumeModel.Series[0];
            var volItems = volSeries.ItemsSource.Cast<RectangleBarItem>().ToList();
            if (volItems.Count > 0 && Math.Abs(volItems[^1].X0 - (x - 0.35)) < 0.001)
                volItems[^1] = new RectangleBarItem(x - 0.35, 0, x + 0.35, b.Volume)
                    { Color = up ? OxyColor.FromRgb(200, 80, 80) : OxyColor.FromRgb(80, 200, 80) };
            else
                volItems.Add(new RectangleBarItem(x - 0.35, 0, x + 0.35, b.Volume)
                    { Color = up ? OxyColor.FromRgb(200, 80, 80) : OxyColor.FromRgb(80, 200, 80) });
            volSeries.ItemsSource = volItems;

            // MACD
            if (_macd.IsReady)
            {
                _macdDifSeries.Points.Add(new DataPoint(x, _macd.CurrentDif));
                _macdDeaSeries.Points.Add(new DataPoint(x, _macd.CurrentDea));
                double h = _macd.CurrentHist;
                var histItems = _macdHistSeries.ItemsSource.Cast<RectangleBarItem>().ToList();
                histItems.Add(new RectangleBarItem(x - 0.35, 0, x + 0.35, h)
                    { Color = h >= 0 ? OxyColor.FromRgb(200, 80, 80) : OxyColor.FromRgb(80, 200, 80) });
                _macdHistSeries.ItemsSource = histItems;
            }

            // RSI
            if (_rsi.IsReady)
                _rsiSeries.Points.Add(new DataPoint(x, _rsi.CurrentValue));

            KeepWindowScrolling(150);
            RefreshAllPlotModels();
            StatusText = $"实时 | {SelectedProduct!.Code} {SelectedFrequency} | "
                       + $"{b.BarTime:HH:mm:ss} | C={b.CloseDouble:F1}";
        });
    }

    // ============================================================
    // X 轴同步 + 滚动
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

    private void KeepWindowScrolling(int visibleCount)
    {
        List<Bar> bars;
        lock (_barLock) { bars = _loadedBars; }
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
    // 命令
    // ============================================================

    [RelayCommand]
    private void ResetView()
    {
        foreach (var ax in _xAxes) ax.Reset();
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
    // 工具
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

/// <summary>品种列表项</summary>
public record ProductItem(string Code, string InstrumentId, long BarCount,
    string FirstBar, string LastBar)
{
    public string Display => $"{Code}  ({BarCount / 1000d:F0}K bars)";
}
