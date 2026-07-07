namespace TradingStudio.Core.Factors;

/// <summary>
/// 时间序列动量因子 — Ernie Chan Ch7.1。
/// M(t) = (P(t) - P(t-Lookback)) / P(t-Lookback)
/// </summary>
public class TimeSeriesMomentum : FactorBase
{
    private readonly int _lookback;
    private readonly Queue<double> _prices;

    public TimeSeriesMomentum(int lookback = 20, int windowSize = 252)
        : base(new FactorConfig
        {
            Name = $"Mom{lookback}",
            Category = "Momentum",
            WindowSize = windowSize,
        })
    {
        _lookback = lookback;
        _prices = new Queue<double>(lookback + 1);
    }

    public int Lookback => _lookback;

    protected override double Compute(double price, double high, double low, double volume)
    {
        if (price <= 0) return double.NaN;

        _prices.Enqueue(price);
        if (_prices.Count > _lookback + 1)
            _prices.Dequeue();

        if (_prices.Count <= _lookback) return double.NaN;

        var prices = _prices.ToArray();
        var prevPrice = prices[0];
        return (price - prevPrice) / prevPrice; // 收益率 = 原始动量
    }

    public override void Reset()
    {
        base.Reset();
        _prices.Clear();
    }
}

/// <summary>
/// 移动平均偏离度因子。
/// Deviation(t) = (P(t) - SMA(t)) / SMA(t)
/// 正值 = 价格在均线之上（趋势偏多），负值 = 价格在均线之下（趋势偏空）
/// </summary>
public class SmaDeviation : FactorBase
{
    private readonly int _period;
    private readonly Queue<double> _prices;
    private double _sum;

    public SmaDeviation(int period = 20, int windowSize = 252)
        : base(new FactorConfig
        {
            Name = $"SmaDev{period}",
            Category = "Trend",
            WindowSize = windowSize,
        })
    {
        _period = period;
        _prices = new Queue<double>(period);
    }

    protected override double Compute(double price, double high, double low, double volume)
    {
        _prices.Enqueue(price);
        _sum += price;
        if (_prices.Count > _period)
            _sum -= _prices.Dequeue();
        if (_prices.Count < _period) return double.NaN;

        var sma = _sum / _period;
        return (price - sma) / sma; // 偏离度
    }

    public override void Reset()
    {
        base.Reset();
        _prices.Clear();
        _sum = 0;
    }
}

/// <summary>
/// 波动率因子（历史波动率）。
/// Vol(t) = StdDev(returns, period) * sqrt(periodsPerYear)
/// 高波动率 = 风险高但机会也可能大；低波动率 = 横盘等待突破
/// </summary>
public class HistoricalVolatility : FactorBase
{
    private readonly int _period;
    private readonly Queue<double> _returns;
    private double _prevPrice = double.NaN;

    public HistoricalVolatility(int period = 20, int windowSize = 252)
        : base(new FactorConfig
        {
            Name = $"HV{period}",
            Category = "Volatility",
            WindowSize = windowSize,
        })
    {
        _period = period;
        _returns = new Queue<double>(period);
    }

    protected override double Compute(double price, double high, double low, double volume)
    {
        if (double.IsNaN(_prevPrice))
        {
            _prevPrice = price;
            return double.NaN;
        }

        var ret = (price - _prevPrice) / _prevPrice;
        _prevPrice = price;

        _returns.Enqueue(ret);
        if (_returns.Count > _period)
            _returns.Dequeue();
        if (_returns.Count < _period) return double.NaN;

        var mean = _returns.Average();
        var variance = _returns.Average(r => Math.Pow(r - mean, 2));
        var dailyVol = Math.Sqrt(variance);

        // 年化波动率 (252 交易日)
        return dailyVol * Math.Sqrt(252);
    }

    public override void Reset()
    {
        base.Reset();
        _returns.Clear();
        _prevPrice = double.NaN;
    }
}

/// <summary>
/// 成交量比率因子。
/// VolRatio(t) = Volume(t) / SMA(Volume, period)
/// > 1.0 = 放量（市场关注度高），< 1.0 = 缩量（冷清）
/// </summary>
public class VolumeRatio : FactorBase
{
    private readonly int _period;
    private readonly Queue<double> _volumes;
    private double _volSum;

    public VolumeRatio(int period = 20, int windowSize = 252)
        : base(new FactorConfig
        {
            Name = $"VolRatio{period}",
            Category = "Sentiment",
            WindowSize = windowSize,
        })
    {
        _period = period;
        _volumes = new Queue<double>(period);
    }

    protected override double Compute(double price, double high, double low, double volume)
    {
        if (volume <= 0) return double.NaN;

        _volumes.Enqueue(volume);
        _volSum += volume;
        if (_volumes.Count > _period)
            _volSum -= _volumes.Dequeue();
        if (_volumes.Count < _period) return double.NaN;

        var avgVol = _volSum / _period;
        return avgVol > 0 ? volume / avgVol : 1.0;
    }

    public override void Reset()
    {
        base.Reset();
        _volumes.Clear();
        _volSum = 0;
    }
}
