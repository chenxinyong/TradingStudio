namespace TradingStudio.Core.Indicators;

/// <summary>
/// 指数移动平均线 — 最近价格权重更高。
/// α = 2/(period+1)，EMA = α × Price + (1-α) × EMA_prev
/// </summary>
public class EmaIndicator : IIndicator
{
    private readonly int _period;
    private readonly double _alpha;
    private readonly List<double> _values = new();
    private double _ema;
    private int _count;

    public string Name => "EMA";
    public string Tag => $"period={_period}";
    public bool IsReady => _count >= _period;
    public double CurrentValue => IsReady ? _ema : double.NaN;
    public int WarmupPeriod => _period;
    public IReadOnlyList<double> Values => _values;

    public EmaIndicator(int period = 26)
    {
        if (period < 2) throw new ArgumentException("Period must be >= 2", nameof(period));
        _period = period;
        _alpha = 2.0 / (period + 1);
    }

    /// <summary>从 Bar 收盘价更新</summary>
    public void Update(Models.Bar bar)
    {
        UpdateCore(bar.CloseDouble);
    }

    /// <summary>从任意数值更新（供复合指标如 MACD 使用）</summary>
    public void Update(double value)
    {
        UpdateCore(value);
    }

    private void UpdateCore(double price)
    {
        _count++;

        if (_count == 1)
            _ema = price;
        else
            _ema += _alpha * (price - _ema);

        _values.Add(_count >= _period ? _ema : double.NaN);
    }

    public void Reset()
    {
        _values.Clear();
        _ema = 0;
        _count = 0;
    }
}
