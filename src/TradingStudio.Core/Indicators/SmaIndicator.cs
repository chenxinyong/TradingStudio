namespace TradingStudio.Core.Indicators;

/// <summary>
/// 简单移动平均线 — 最近 N 根 Bar 收盘价的算术平均。
/// </summary>
public class SmaIndicator : IIndicator
{
    private readonly int _period;
    private readonly Queue<double> _buffer = new();
    private readonly List<double> _values = new();
    private double _sum;

    public string Name => "SMA";
    public string Tag => $"period={_period}";
    public bool IsReady => _buffer.Count >= _period;
    public double CurrentValue => IsReady ? _sum / _period : double.NaN;
    public int WarmupPeriod => _period;
    public IReadOnlyList<double> Values => _values;

    public SmaIndicator(int period = 20)
    {
        if (period < 2) throw new ArgumentException("Period must be >= 2", nameof(period));
        _period = period;
    }

    public void Update(Models.Bar bar)
    {
        var price = bar.CloseDouble;
        _buffer.Enqueue(price);
        _sum += price;

        if (_buffer.Count > _period)
            _sum -= _buffer.Dequeue();

        _values.Add(IsReady ? _sum / _period : double.NaN);
    }

    public void Reset()
    {
        _buffer.Clear();
        _values.Clear();
        _sum = 0;
    }
}
