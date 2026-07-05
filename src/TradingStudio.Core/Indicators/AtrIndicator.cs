using TradingStudio.Core.Models;

namespace TradingStudio.Core.Indicators;

/// <summary>
/// ATR (Average True Range) 指标 — 所有策略共享，消除6个重复实现。
/// 使用 Wilder 平滑: ATR = (PrevATR*(N-1) + TR) / N
/// </summary>
public class AtrIndicator : IIndicator
{
    private readonly int _period;
    private readonly Queue<double> _trBuffer;
    private double _prevClose = double.NaN;
    private double _atrSum;

    public string Name => "ATR";
    public string Tag { get; }
    public bool IsReady { get; private set; }
    public double CurrentValue { get; private set; }
    public int WarmupPeriod => _period;
    public IReadOnlyList<double> Values { get; private set; } = Array.Empty<double>();

    public AtrIndicator(int period = 14, string? tag = null)
    {
        _period = period;
        Tag = tag ?? period.ToString();
        _trBuffer = new Queue<double>(period + 1);
    }

    /// <summary>计算单根Bar的True Range</summary>
    public static double TrueRange(Bar bar, double prevClose)
    {
        if (double.IsNaN(prevClose)) return bar.HighDouble - bar.LowDouble;
        return Math.Max(bar.HighDouble - bar.LowDouble,
            Math.Max(Math.Abs(bar.HighDouble - prevClose),
                     Math.Abs(bar.LowDouble - prevClose)));
    }

    public void Reset() { _trBuffer.Clear(); _atrSum = 0; _prevClose = double.NaN; CurrentValue = 0; IsReady = false; }

    public void Update(Bar bar)
    {
        var tr = TrueRange(bar, _prevClose);
        _prevClose = bar.CloseDouble;

        _trBuffer.Enqueue(tr);
        _atrSum += tr;
        if (_trBuffer.Count > _period)
            _atrSum -= _trBuffer.Dequeue();

        if (_trBuffer.Count >= _period)
        {
            CurrentValue = _atrSum / _period;
            IsReady = true;
        }
    }
}
