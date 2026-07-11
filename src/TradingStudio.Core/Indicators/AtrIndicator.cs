using TradingStudio.Core.Models;

namespace TradingStudio.Core.Indicators;

/// <summary>
/// ATR (Average True Range) 指标 — 所有策略共享，消除6个重复实现。
/// 使用 Wilder 平滑：前 N 根 TR 的简单平均作种子，其后 ATR = (PrevATR×(N-1) + TR) / N。
/// 与文华/TradingView 等主流平台一致。
/// </summary>
public class AtrIndicator : IIndicator
{
    private readonly int _period;
    private double _prevClose = double.NaN;
    private double _seedSum;   // 前 N 根 TR 累积（种子）
    private double _atr;
    private int _count;        // 已处理 TR 数

    public string Name => "ATR";
    public string Tag { get; }
    public bool IsReady => _count >= _period;
    public double CurrentValue => IsReady ? _atr : 0;
    public int WarmupPeriod => _period;
    public IReadOnlyList<double> Values { get; private set; } = Array.Empty<double>();

    public AtrIndicator(int period = 14, string? tag = null)
    {
        _period = period;
        Tag = tag ?? period.ToString();
    }

    /// <summary>计算单根Bar的True Range</summary>
    public static double TrueRange(Bar bar, double prevClose)
    {
        if (double.IsNaN(prevClose)) return bar.HighDouble - bar.LowDouble;
        return Math.Max(bar.HighDouble - bar.LowDouble,
            Math.Max(Math.Abs(bar.HighDouble - prevClose),
                     Math.Abs(bar.LowDouble - prevClose)));
    }

    public void Reset() { _seedSum = 0; _atr = 0; _prevClose = double.NaN; _count = 0; }

    public void Update(Bar bar)
    {
        var tr = TrueRange(bar, _prevClose);
        _prevClose = bar.CloseDouble;
        _count++;

        if (_count < _period)
            _seedSum += tr;                                 // 累积种子
        else if (_count == _period)
            _atr = (_seedSum + tr) / _period;               // 种子 = 前 N 根 TR 的简单平均
        else
            _atr = (_atr * (_period - 1) + tr) / _period;   // Wilder 平滑
    }
}
