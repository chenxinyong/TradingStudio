namespace TradingStudio.Core.Indicators;

/// <summary>
/// 相对强弱指标 — 衡量价格变动的速度和幅度。
/// RSI = 100 - 100/(1 + RS),  RS = avg_gain(N) / avg_loss(N)
/// 使用 Wilder's smoothing。
/// </summary>
public class RsiIndicator : IIndicator
{
    private readonly int _period;
    private readonly List<double> _values = new();
    private double _prevClose = double.NaN;
    private double _avgGain;
    private double _avgLoss;
    private int _count;

    public string Name => "RSI";
    public string Tag => $"period={_period}";
    public bool IsReady => _count > _period;
    public double CurrentValue => IsReady ? _values[^1] : double.NaN;
    public int WarmupPeriod => _period + 1;
    public IReadOnlyList<double> Values => _values;

    public RsiIndicator(int period = 14)
    {
        if (period < 2) throw new ArgumentException("Period must be >= 2", nameof(period));
        _period = period;
    }

    public void Update(Models.Bar bar)
    {
        var close = bar.CloseDouble;

        if (double.IsNaN(_prevClose))
        {
            _prevClose = close;
            _values.Add(double.NaN);
            return;
        }

        double change = close - _prevClose;
        double gain = change > 0 ? change : 0;
        double loss = change < 0 ? -change : 0;

        _count++;

        if (_count <= _period)
            _avgGain = (_avgGain * (_count - 1) + gain) / _count;
        else
            _avgGain = (_avgGain * (_period - 1) + gain) / _period;

        if (_count <= _period)
            _avgLoss = (_avgLoss * (_count - 1) + loss) / _count;
        else
            _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;

        double rsi = _avgLoss == 0 ? 100 : 100 - 100 / (1 + _avgGain / _avgLoss);
        _values.Add(_count >= _period ? rsi : double.NaN);
        _prevClose = close;
    }

    public void Reset()
    {
        _values.Clear();
        _prevClose = double.NaN;
        _avgGain = 0;
        _avgLoss = 0;
        _count = 0;
    }
}
