using TradingStudio.Core.Models;

namespace TradingStudio.UI.Services;

/// <summary>
/// 价格数据模拟器 — 随机游走 + 均值回归，生成逼真的 OHLC 数据。
/// 历史 Bar 一次性生成，实时 Bar 通过 Timer 每秒推送。
/// </summary>
public class DataSimulator : IDisposable
{
    private readonly double _startPrice;
    private readonly double _volatility;    // 每步波动率 (相对值)
    private readonly double _meanReversion; // 均值回归强度 (0=纯随机游走)
    private readonly long _volumeBase;       // 基础成交量
    private readonly Random _rng = new(42);

    private readonly List<Bar> _historyBars = new();
    private double _currentPrice;
    private DateTime _currentTime;
    private int _barIndex;

    private readonly System.Timers.Timer _realtimeTimer;
    public IReadOnlyList<Bar> HistoryBars => _historyBars;
    public bool IsRealtimeRunning { get; private set; }

    /// <summary>实时 Bar 到达事件 (UI 线程无关，ViewModel 负责调度)</summary>
    public event Action<Bar>? BarUpdated;

    public DataSimulator(
        double startPrice = 5200,
        double volatility = 0.002,
        double meanReversion = 0.001,
        long volumeBase = 500)
    {
        _startPrice = startPrice;
        _volatility = volatility;
        _meanReversion = meanReversion;
        _volumeBase = volumeBase;
        _currentPrice = startPrice;
        _currentTime = DateTime.Today.AddHours(9).AddMinutes(1); // 09:01

        _realtimeTimer = new System.Timers.Timer(1000); // 1 秒 = 模拟 1 分钟 Bar
        _realtimeTimer.Elapsed += OnRealtimeTick;
        _realtimeTimer.AutoReset = true;
    }

    /// <summary>
    /// 生成历史 K 线数据
    /// </summary>
    public List<Bar> GenerateHistory(int barCount = 500)
    {
        _historyBars.Clear();

        for (int i = 0; i < barCount; i++)
        {
            var bar = GenerateBar();
            _historyBars.Add(bar);
            _barIndex++;
        }

        return _historyBars;
    }

    /// <summary>
    /// 启动实时模拟 — 每秒生成一根新 Bar
    /// </summary>
    public void StartRealtime()
    {
        if (IsRealtimeRunning) return;
        IsRealtimeRunning = true;
        _realtimeTimer.Start();
    }

    /// <summary>
    /// 暂停实时模拟
    /// </summary>
    public void StopRealtime()
    {
        IsRealtimeRunning = false;
        _realtimeTimer.Stop();
    }

    private void OnRealtimeTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var bar = GenerateBar();
        _historyBars.Add(bar);
        _barIndex++;
        BarUpdated?.Invoke(bar);
    }

    private Bar GenerateBar()
    {
        // 随机游走 + 均值回归
        double randomComponent = (_rng.NextDouble() - 0.5) * 2 * _volatility * _startPrice;
        double meanReversionComponent = (_startPrice - _currentPrice) * _meanReversion;
        double drift = randomComponent + meanReversionComponent;

        double open = _currentPrice;
        double close = open + drift;
        double high = Math.Max(open, close) + Math.Abs(drift) * _rng.NextDouble() * 0.8;
        double low = Math.Min(open, close) - Math.Abs(drift) * _rng.NextDouble() * 0.8;

        // 防止价格为负
        if (low < _startPrice * 0.5) low = _startPrice * 0.5;
        if (low > high) (low, high) = (high, low);

        long volume = _volumeBase + _rng.Next(-200, 800);
        if (volume < 1) volume = 1;

        var bar = new Bar
        {
            InstrumentId = "rb2608",
            TradingDay = DateOnly.FromDateTime(_currentTime),
            BarTime = _currentTime,
            Open = (long)(open * TickRecord.PriceScale),
            High = (long)(high * TickRecord.PriceScale),
            Low = (long)(low * TickRecord.PriceScale),
            Close = (long)(close * TickRecord.PriceScale),
            Volume = volume,
            Turnover = volume * (open + close) / 2,
            OpenInterest = 100000 + _rng.Next(-5000, 5000),
            TickCount = _rng.Next(10, 50),
        };

        _currentPrice = close;
        _currentTime = _currentTime.AddMinutes(1);

        return bar;
    }

    public void Dispose()
    {
        _realtimeTimer.Stop();
        _realtimeTimer.Dispose();
    }
}
