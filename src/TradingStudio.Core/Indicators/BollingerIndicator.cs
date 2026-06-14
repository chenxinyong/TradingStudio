namespace TradingStudio.Core.Indicators;

/// <summary>
/// 布林带 — 中轨 SMA(N)，上轨 MA + K×σ，下轨 MA - K×σ
/// </summary>
public class BollingerIndicator : IIndicator
{
    private readonly int _period;
    private readonly double _multiplier;
    private readonly Queue<double> _buffer = new();
    private readonly List<double> _upperValues = new();
    private readonly List<double> _middleValues = new();
    private readonly List<double> _lowerValues = new();

    public string Name => "BOLL";
    public string Tag => $"period={_period},k={_multiplier}";
    public bool IsReady => _buffer.Count >= _period;
    public double CurrentValue => IsReady ? _middleValues[^1] : double.NaN;
    public int WarmupPeriod => _period;
    public IReadOnlyList<double> Values => _middleValues;

    // BOLL 特有属性 — 供图表叠加使用
    public IReadOnlyList<double> Upper => _upperValues;
    public IReadOnlyList<double> Middle => _middleValues;
    public IReadOnlyList<double> Lower => _lowerValues;

    public BollingerIndicator(int period = 20, double multiplier = 2.0)
    {
        if (period < 2) throw new ArgumentException("Period must be >= 2", nameof(period));
        _period = period;
        _multiplier = multiplier;
    }

    public void Update(Models.Bar bar)
    {
        var price = bar.CloseDouble;
        _buffer.Enqueue(price);
        if (_buffer.Count > _period)
            _buffer.Dequeue();

        if (_buffer.Count < _period)
        {
            _upperValues.Add(double.NaN);
            _middleValues.Add(double.NaN);
            _lowerValues.Add(double.NaN);
            return;
        }

        double mean = _buffer.Average();
        double sumSq = _buffer.Sum(x => (x - mean) * (x - mean));
        double std = Math.Sqrt(sumSq / _period);

        _middleValues.Add(mean);
        _upperValues.Add(mean + _multiplier * std);
        _lowerValues.Add(mean - _multiplier * std);
    }

    public void Reset()
    {
        _buffer.Clear();
        _upperValues.Clear();
        _middleValues.Clear();
        _lowerValues.Clear();
    }
}
