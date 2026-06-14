namespace TradingStudio.Core.Indicators;

/// <summary>
/// MACD 指标 — 趋势跟踪动量指标。
/// DIF  = EMA(fast) - EMA(slow)
/// DEA  = EMA(signal) of DIF
/// HIST = 2 × (DIF - DEA)
/// </summary>
public class MacdIndicator : IIndicator
{
    private readonly int _fastPeriod;
    private readonly int _slowPeriod;
    private readonly int _signalPeriod;
    private readonly EmaIndicator _fastEma;
    private readonly EmaIndicator _slowEma;
    private readonly EmaIndicator _signalEma;
    private readonly List<double> _difValues = new();
    private readonly List<double> _deaValues = new();
    private readonly List<double> _histValues = new();

    public string Name => "MACD";
    public string Tag => $"fast={_fastPeriod},slow={_slowPeriod},signal={_signalPeriod}";
    public bool IsReady => _signalEma.IsReady;
    public double CurrentValue => IsReady ? _histValues[^1] : double.NaN;
    public int WarmupPeriod => _slowPeriod + _signalPeriod;
    public IReadOnlyList<double> Values => _histValues;

    // MACD 特有属性 — 供图表叠加使用
    public IReadOnlyList<double> Dif => _difValues;
    public IReadOnlyList<double> Dea => _deaValues;
    public IReadOnlyList<double> Hist => _histValues;
    public double CurrentDif => _difValues.Count > 0 ? _difValues[^1] : double.NaN;
    public double CurrentDea => _deaValues.Count > 0 ? _deaValues[^1] : double.NaN;
    public double CurrentHist => _histValues.Count > 0 ? _histValues[^1] : double.NaN;

    public MacdIndicator(int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
    {
        _fastPeriod = fastPeriod;
        _slowPeriod = slowPeriod;
        _signalPeriod = signalPeriod;
        _fastEma = new EmaIndicator(fastPeriod);
        _slowEma = new EmaIndicator(slowPeriod);
        _signalEma = new EmaIndicator(signalPeriod);
    }

    public void Update(Models.Bar bar)
    {
        _fastEma.Update(bar);
        _slowEma.Update(bar);

        bool bothReady = _fastEma.IsReady && _slowEma.IsReady;
        double dif = bothReady ? _fastEma.CurrentValue - _slowEma.CurrentValue : 0;
        _difValues.Add(dif);

        if (bothReady)
        {
            // 直接喂浮点数，不再构造虚拟 Bar
            _signalEma.Update(dif);

            if (_signalEma.IsReady)
            {
                double dea = _signalEma.CurrentValue;
                double hist = 2 * (dif - dea);
                _deaValues.Add(dea);
                _histValues.Add(hist);
            }
            else
            {
                _deaValues.Add(double.NaN);
                _histValues.Add(double.NaN);
            }
        }
        else
        {
            _deaValues.Add(double.NaN);
            _histValues.Add(double.NaN);
        }
    }

    public void Reset()
    {
        _fastEma.Reset();
        _slowEma.Reset();
        _signalEma.Reset();
        _difValues.Clear();
        _deaValues.Clear();
        _histValues.Clear();
    }
}
