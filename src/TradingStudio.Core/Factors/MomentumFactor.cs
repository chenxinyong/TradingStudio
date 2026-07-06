namespace TradingStudio.Core.Factors;

/// <summary>
/// 时间序列动量因子 — Ernie Chan Ch7.1。
///
/// 定义: M(t) = (Price(t) - Price(t-Lookback)) / Price(t-Lookback)
///       = 过去 Lookback 期的收益率
///
/// Chan 的要点:
///   - 时间序列动量（只看自己过去收益）≠ 横截面动量（和其他品种比排名）
///   - 多个 Lookback 可以组合: 1M/3M/6M/12M 动量等权或择优
///   - 标准化: z-score = (raw - 滚动均值) / 滚动标准差
///   - 标准化后的值可以直接用作仓位权重
///
/// 实现: 在线更新（每个 Bar 调用一次 Update），不需要存完整历史。
/// </summary>
public class MomentumFactor
{
    private readonly string _name;
    private readonly int _lookback;
    private readonly int _normPeriod; // 标准化用的滚动窗口
    private readonly Queue<double> _prices;
    private readonly Queue<double> _rawValues;
    private double _rawSum;
    private double _rawSumSq;

    public MomentumFactor(string name, int lookback = 20, int normPeriod = 60)
    {
        _name = name;
        _lookback = lookback;
        _normPeriod = normPeriod;
        _prices = new Queue<double>(lookback + 1);
        _rawValues = new Queue<double>(normPeriod);
    }

    public string Name => _name;
    public int Lookback => _lookback;

    /// <summary>原始动量值（收益率，未标准化）</summary>
    public double RawValue { get; private set; } = double.NaN;

    /// <summary>标准化动量值（z-score）</summary>
    public double ZScore { get; private set; } = double.NaN;

    /// <summary>收益率百分位（0-1，基于滚动窗口）</summary>
    public double Percentile { get; private set; } = double.NaN;

    /// <summary>是否有足够数据计算</summary>
    public bool IsReady => _prices.Count > _lookback && _rawValues.Count >= Math.Max(10, _normPeriod / 3);

    /// <summary>
    /// 每根 Bar 调用一次，更新动量因子值。
    /// </summary>
    public void Update(double price)
    {
        if (price <= 0) return;

        _prices.Enqueue(price);
        if (_prices.Count > _lookback + 1)
            _prices.Dequeue();

        // ── 原始动量 = 收益率 ──
        if (_prices.Count > _lookback)
        {
            var prices = _prices.ToArray();
            var prevPrice = prices[0]; // Lookback 期之前的价格
            RawValue = (price - prevPrice) / prevPrice;

            // ── 更新滚动统计 ──
            _rawValues.Enqueue(RawValue);
            _rawSum += RawValue;
            _rawSumSq += RawValue * RawValue;
            if (_rawValues.Count > _normPeriod)
            {
                var old = _rawValues.Dequeue();
                _rawSum -= old;
                _rawSumSq -= old * old;
            }

            // ── 计算 z-score ──
            if (_rawValues.Count >= Math.Max(10, _normPeriod / 3))
            {
                var mean = _rawSum / _rawValues.Count;
                var variance = (_rawSumSq / _rawValues.Count) - (mean * mean);
                var std = Math.Sqrt(Math.Max(variance, 1e-10));
                ZScore = std > 0 ? (RawValue - mean) / std : 0;

                // ── 计算百分位 ──
                var sorted = _rawValues.OrderBy(v => v).ToList();
                var rank = sorted.BinarySearch(RawValue);
                if (rank < 0) rank = ~rank;
                Percentile = (double)rank / sorted.Count;
            }
        }
    }

    /// <summary>重置（切换品种时使用）</summary>
    public void Reset()
    {
        _prices.Clear();
        _rawValues.Clear();
        _rawSum = 0;
        _rawSumSq = 0;
        RawValue = double.NaN;
        ZScore = double.NaN;
        Percentile = double.NaN;
    }
}

/// <summary>
/// 横截面动量排名器 — Chan Ch7.1。
///
/// 在同一时刻对所有品种的动量值做排名，生成 0-1 信号：
///   Top 20% → 做多 (信号 > 0)
///   Bottom 20% → 做空 (信号 < 0)
///   中间 60% → 不交易 (信号 ≈ 0)
/// </summary>
public class CrossSectionalMomentum
{
    private readonly double _longThreshold;  // 做多的百分位阈值
    private readonly double _shortThreshold; // 做空的百分位阈值

    public CrossSectionalMomentum(double longThreshold = 0.80, double shortThreshold = 0.20)
    {
        _longThreshold = longThreshold;
        _shortThreshold = shortThreshold;
    }

    /// <summary>
    /// 对所有品种的原始动量做排名，生成信号。
    /// </summary>
    /// <param name="momenta">品种→原始动量值</param>
    /// <returns>品种→信号强度 [-1, 1]</returns>
    public Dictionary<string, double> GenerateSignals(Dictionary<string, double> momenta)
    {
        var signals = new Dictionary<string, double>();
        if (momenta.Count < 3) return signals;

        // 按动量值排序
        var sorted = momenta.OrderBy(kv => kv.Value).ToList();
        var n = sorted.Count;

        for (int i = 0; i < n; i++)
        {
            var percentile = (double)i / (n - 1); // 0 = 最弱, 1 = 最强

            double signal;
            if (percentile >= _longThreshold)
            {
                // 最强 → 做多，信号 = 映射到 (0, 1]
                signal = (percentile - _longThreshold) / (1.0 - _longThreshold);
            }
            else if (percentile <= _shortThreshold)
            {
                // 最弱 → 做空，信号 = 映射到 [-1, 0)
                signal = -(1.0 - percentile / _shortThreshold);
            }
            else
            {
                // 中间 → 不交易
                signal = 0;
            }

            signals[sorted[i].Key] = Math.Clamp(signal, -1, 1);
        }

        return signals;
    }
}
