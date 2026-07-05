using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;

namespace TradingStudio.Terminal.ViewModels;

/// <summary>
/// K线回放 ViewModel — 加载历史Bar, 逐根播放, 叠加指标。
/// </summary>
public partial class ReplayViewModel : ObservableObject
{
    private readonly ILogger<ReplayViewModel> _log;
    private readonly DispatcherTimer _timer;
    private List<Bar> _allBars = [];
    private int _position;
    private readonly Dispatcher _dispatcher;

    // 指标
    private SmaIndicator _ma5 = new(5), _ma20 = new(20);
    private BollingerIndicator _boll = new(20, 2.0);

    // Chart series
    private CandleStickSeries _candleSeries = null!;
    private LineSeries _ma5Series = null!, _ma20Series = null!;
    private LineSeries _bollUpper = null!, _bollMid = null!, _bollLower = null!;
    private LineSeries _equitySeries = null!;

    public PlotModel KLineModel { get; } = new() { PlotAreaBorderThickness = new OxyThickness(0) };
    public PlotModel EquityModel { get; } = new() { PlotAreaBorderThickness = new OxyThickness(0) };

    // ── 可绑定状态 ──
    [ObservableProperty] private string _instrumentId = "rb000";
    [ObservableProperty] private string _startDate = "2024-01-01";
    [ObservableProperty] private string _endDate = "2024-06-30";
    [ObservableProperty] private int _speed = 3; // bars per tick
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private int _totalBars;
    [ObservableProperty] private string _currentBarInfo = "";

    public ObservableCollection<int> SpeedOptions { get; } = new() { 1, 3, 10, 30, 100 };

    private const string DefaultDbPath = "data/bars_history.duckdb";

    public ReplayViewModel(ILogger<ReplayViewModel> log)
    {
        _log = log;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += OnTimerTick;
        InitializeCharts();
    }

    private void InitializeCharts()
    {
        var xAxis = new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "MM-dd HH:mm" };
        KLineModel.Axes.Add(xAxis);
        KLineModel.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Title = "价格" });
        _candleSeries = new CandleStickSeries { IncreasingColor = OxyColors.Red, DecreasingColor = OxyColors.Green, CandleWidth = 3 };
        KLineModel.Series.Add(_candleSeries);
        _ma5Series = new LineSeries { Color = OxyColors.Gold, StrokeThickness = 0.8 };
        _ma20Series = new LineSeries { Color = OxyColors.Magenta, StrokeThickness = 0.8 };
        _bollUpper = new LineSeries { Color = OxyColor.FromRgb(100, 100, 255), StrokeThickness = 0.5 };
        _bollMid = new LineSeries { Color = OxyColor.FromRgb(150, 150, 255), StrokeThickness = 0.5 };
        _bollLower = new LineSeries { Color = OxyColor.FromRgb(100, 100, 255), StrokeThickness = 0.5 };
        KLineModel.Series.Add(_ma5Series);
        KLineModel.Series.Add(_ma20Series);
        KLineModel.Series.Add(_bollUpper);
        KLineModel.Series.Add(_bollMid);
        KLineModel.Series.Add(_bollLower);

        EquityModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "权益" });
        EquityModel.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "MM-dd" });
        _equitySeries = new LineSeries { Color = OxyColors.LimeGreen, StrokeThickness = 1.2 };
        EquityModel.Series.Add(_equitySeries);
    }

    [RelayCommand]
    private void Load()
    {
        try
        {
            StatusText = $"加载 {InstrumentId} {StartDate}~{EndDate}...";
            var dbPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", DefaultDbPath);
            if (!File.Exists(dbPath)) dbPath = DefaultDbPath;

            using var conn = new DuckDBConnection($"Data Source={dbPath}");
            conn.Open();

            var table = "bars_5min";
            var sql = $"""
                SELECT bar_time::TIMESTAMP as dt, open/1e7 as o, high/1e7 as h, low/1e7 as l, close/1e7 as c, volume as v
                FROM {table} WHERE instrument_id = '{InstrumentId}'
                AND bar_time >= '{StartDate}' AND bar_time <= '{EndDate} 23:59:59'
                ORDER BY dt
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();

            _allBars = new List<Bar>();
            while (reader.Read())
            {
                _allBars.Add(new Bar
                {
                    InstrumentId = InstrumentId, BarTime = reader.GetDateTime(0),
                    Open = (long)(reader.GetDouble(1) * 1e7), High = (long)(reader.GetDouble(2) * 1e7),
                    Low = (long)(reader.GetDouble(3) * 1e7), Close = (long)(reader.GetDouble(4) * 1e7),
                    Volume = reader.GetInt64(5),
                });
            }

            TotalBars = _allBars.Count;
            StatusText = $"已加载 {TotalBars:N0} bars | 点击 ▶ 开始回放";
            _position = 0; Progress = 0;
            _ma5 = new(5); _ma20 = new(20); _boll = new(20, 2.0);
            _candleSeries.ItemsSource = new List<HighLowItem>();
            KLineModel.InvalidatePlot(true);
        }
        catch (Exception ex) { StatusText = $"加载失败: {ex.Message}"; }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (IsPlaying) { _timer.Stop(); IsPlaying = false; StatusText = "已暂停"; }
        else { _timer.Start(); IsPlaying = true; StatusText = "回放中..."; }
    }

    [RelayCommand]
    private void Step()
    {
        if (_position < _allBars.Count)
            FeedBars(Speed);
    }

    [RelayCommand]
    private void Reset()
    {
        _timer.Stop(); IsPlaying = false;
        _position = 0; Progress = 0;
        _ma5 = new(5); _ma20 = new(20); _boll = new(20, 2.0);
        _candleSeries.ItemsSource = new List<HighLowItem>();
        _ma5Series.Points.Clear(); _ma20Series.Points.Clear();
        _bollUpper.Points.Clear(); _bollMid.Points.Clear(); _bollLower.Points.Clear();
        _equitySeries.Points.Clear();
        KLineModel.InvalidatePlot(true); EquityModel.InvalidatePlot(true);
        StatusText = $"已重置 | {TotalBars:N0} bars 就绪";
    }

    private void OnTimerTick(object? sender, EventArgs e) => FeedBars(Speed);

    private void FeedBars(int count)
    {
        var end = Math.Min(_position + count, _allBars.Count);
        for (int i = _position; i < end; i++)
        {
            var bar = _allBars[i];
            _ma5.Update(bar); _ma20.Update(bar); _boll.Update(bar);

            // K线追加
            var items = _candleSeries.ItemsSource as List<HighLowItem> ?? new();
            items.Add(new HighLowItem(DateTimeAxis.ToDouble(bar.BarTime), bar.HighDouble, bar.LowDouble, bar.OpenDouble, bar.CloseDouble));
            _candleSeries.ItemsSource = items;

            // 均线
            if (!double.IsNaN(_ma5.CurrentValue)) _ma5Series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(bar.BarTime), _ma5.CurrentValue));
            if (!double.IsNaN(_ma20.CurrentValue)) _ma20Series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(bar.BarTime), _ma20.CurrentValue));
            if (_boll.IsReady)
            {
                _bollUpper.Points.Add(new DataPoint(DateTimeAxis.ToDouble(bar.BarTime), _boll.Upper[^1]));
                _bollMid.Points.Add(new DataPoint(DateTimeAxis.ToDouble(bar.BarTime), _boll.Middle[^1]));
                _bollLower.Points.Add(new DataPoint(DateTimeAxis.ToDouble(bar.BarTime), _boll.Lower[^1]));
            }
        }

        _position = end;
        Progress = (int)(_position * 100.0 / _allBars.Count);
        if (_allBars.Count > 0)
        {
            var cur = _allBars[_position - 1];
            CurrentBarInfo = $"{cur.BarTime:yyyy-MM-dd HH:mm} | O={cur.OpenDouble:F1} H={cur.HighDouble:F1} L={cur.LowDouble:F1} C={cur.CloseDouble:F1} V={cur.Volume}";
        }
        StatusText = $"回放中: {_position:N0}/{TotalBars:N0} ({Progress}%) 速度:{Speed}x";

        KLineModel.InvalidatePlot(true);
        if (_position >= _allBars.Count) { _timer.Stop(); IsPlaying = false; StatusText = $"回放完成 | {TotalBars:N0} bars"; }
    }
}
