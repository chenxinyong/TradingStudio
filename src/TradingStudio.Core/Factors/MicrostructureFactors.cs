using TradingStudio.Core.Factors;

namespace TradingStudio.Core.Factors;

/// <summary>
/// 日内动量因子 — IntradayMom = (Close_30min - Open) / Open。
///
/// 每根Bar调用 Update(price, timestamp, isNewDay)。
/// - isNewDay=true: 记录 Open (price = 开盘价)
/// - 累计约30分钟后: 记录 Close_30min (price = 当前价)
/// - 此后每根Bar: 持续输出当前日的 IntradayMom 值
///
/// Python验证: IC_IR=+0.96, OOS IC_IR=+1.05, 截面Sharpe=1.79(同日O2C)
/// </summary>
public class IntradayMomFactor : IFactor
{
    private readonly int _warmupMinutes = 30;  // 开盘后等待的分钟数
    private readonly Queue<double> _valueWindow;
    private readonly List<double> _sortedSnapshot;
    private double _valueSum, _valueSumSq;
    private int _sortCounter;
    private readonly int _windowSize;

    private double _dayOpen = double.NaN;
    private double _close30Min = double.NaN;
    private int _barsInDay;
    private DateTime _lastBarTime;
    private bool _dayMomComputed;
    private bool _initialized;

    public string Name => "IntradayMom";
    public string Category => "Microstructure";
    public bool IsInitialized => _initialized;
    public bool IsReady => _valueWindow.Count >= _windowSize / 4;
    public FactorValue LastValue { get; private set; }

    /// <summary>标准接口: Update(price, high, low, volume)。high=开盘价(新日首Bar), low=Bar周期分钟</summary>
    public FactorValue Update(double price, double high = double.NaN, double low = double.NaN, double volume = double.NaN)
    {
        // high用作isNewDay标记 (>0表示新日), low用作barPeriodMinutes
        bool newDay = high > 0;
        int periodMin = !double.IsNaN(low) && low > 0 ? (int)low : 1;
        return Update(price, DateTime.UtcNow, newDay, periodMin);
    }

    public IntradayMomFactor(int windowSize = 252)
    {
        _windowSize = windowSize;
        _valueWindow = new Queue<double>(windowSize);
        _sortedSnapshot = new List<double>(windowSize);
    }

    /// <summary>
    /// 每根 Bar 调用一次。
    /// </summary>
    /// <param name="price">当前价格 (close)</param>
    /// <param name="barTime">Bar时间</param>
    /// <param name="isNewDay">是否为新交易日第一根Bar</param>
    /// <param name="barPeriodMinutes">Bar周期(分钟), 用于判断是否到达30分钟</param>
    public FactorValue Update(double price, DateTime barTime, bool isNewDay, int barPeriodMinutes = 1)
    {
        if (isNewDay)
        {
            _dayOpen = price;
            _barsInDay = 0;
            _dayMomComputed = false;
            _close30Min = double.NaN;
        }

        _barsInDay++;
        _lastBarTime = barTime;

        // 检查是否到达~30分钟: bar_index * period >= warmup_minutes
        if (!_dayMomComputed && _barsInDay * barPeriodMinutes >= _warmupMinutes)
        {
            _close30Min = price;
            _dayMomComputed = true;
        }

        double rawValue;
        if (!double.IsNaN(_dayOpen) && !double.IsNaN(_close30Min) && Math.Abs(_dayOpen) > 1e-10)
        {
            rawValue = (_close30Min - _dayOpen) / _dayOpen;
        }
        else
        {
            rawValue = double.NaN;
        }

        // Same-day: return same value for all bars after computation
        // Different day: NaN until next day's 30min mark

        if (!double.IsNaN(rawValue) && !double.IsInfinity(rawValue))
        {
            UpdateStatistics(rawValue);
            var zScore = ComputeZScore(rawValue);
            var percentile = ComputePercentile(rawValue);
            var signal = ComputeSignal(percentile);

            LastValue = new FactorValue
            {
                FactorName = Name, Timestamp = barTime,
                RawValue = rawValue, ZScore = zScore,
                Percentile = percentile, SignalStrength = signal, IsValid = IsReady,
            };
        }
        else
        {
            LastValue = new FactorValue { FactorName = Name, Timestamp = barTime, IsValid = false };
        }

        return LastValue;
    }

    public void Initialize(IReadOnlyList<double> warmupPrices)
    {
        foreach (var p in warmupPrices)
        {
            if (!double.IsNaN(p) && !double.IsInfinity(p))
                UpdateStatistics(p);
        }
        _initialized = true;
    }

    public void Reset()
    {
        _valueWindow.Clear(); _sortedSnapshot.Clear();
        _valueSum = _valueSumSq = 0;
        _dayOpen = _close30Min = double.NaN;
        _barsInDay = 0; _dayMomComputed = false;
        _initialized = false;
    }

    // ── Statistics (mirrors FactorBase) ──

    private void UpdateStatistics(double value)
    {
        _valueWindow.Enqueue(value); _valueSum += value; _valueSumSq += value * value;
        if (_valueWindow.Count > _windowSize) { var old = _valueWindow.Dequeue(); _valueSum -= old; _valueSumSq -= old * old; }
        _sortCounter++;
        if (_sortCounter >= 50 || _valueWindow.Count - _sortedSnapshot.Count > _windowSize / 10)
        {
            _sortedSnapshot.Clear(); _sortedSnapshot.AddRange(_valueWindow); _sortedSnapshot.Sort(); _sortCounter = 0;
        }
    }

    private double ComputeZScore(double raw)
    {
        if (_valueWindow.Count < _windowSize / 4) return 0;
        var mean = _valueSum / _valueWindow.Count;
        var variance = (_valueSumSq / _valueWindow.Count) - (mean * mean);
        var std = Math.Sqrt(Math.Max(variance, 1e-10));
        var z = std > 0 ? (raw - mean) / std : 0;
        return Math.Clamp(z, -4.0, 4.0);
    }

    private double ComputePercentile(double raw)
    {
        if (_sortedSnapshot.Count < _windowSize / 4) return 0.5;
        var idx = _sortedSnapshot.BinarySearch(raw);
        if (idx < 0) idx = ~idx;
        return Math.Clamp((double)idx / _sortedSnapshot.Count, 0, 1);
    }

    private static double ComputeSignal(double percentile) => percentile switch
    {
        >= 0.80 => (percentile - 0.80) / 0.20,
        <= 0.20 => -(1.0 - percentile / 0.20),
        _ => 0,
    };
}

/// <summary>
/// VWAP偏离因子 — VWAP_Dev = (Close - VWAP) / VWAP。
///
/// 每根Bar调用 Update(price, volume, isNewDay)。
/// - 日内累积: cumPV += price × volume, cumVol += volume
/// - VWAP = cumPV / cumVol (日内实时)
/// - 每根Bar输出: (price - VWAP) / VWAP
///
/// Python验证: IC_IR=-2.08, OOS IC_IR=-2.49, 均值回归信号
/// </summary>
public class VwapDevFactor : IFactor
{
    private readonly int _windowSize;
    private readonly Queue<double> _valueWindow;
    private readonly List<double> _sortedSnapshot;
    private double _valueSum, _valueSumSq;
    private int _sortCounter;

    private double _cumPV, _cumVol;
    private int _barsInDay;
    private bool _initialized;

    public string Name => "VWAP_Dev";
    public string Category => "Microstructure";
    public bool IsInitialized => _initialized;
    public bool IsReady => _valueWindow.Count >= _windowSize / 4;
    public FactorValue LastValue { get; private set; }

    /// <summary>标准接口: Update(price, high, low, volume). high>0表示新日, volume=成交量</summary>
    public FactorValue Update(double price, double high = double.NaN, double low = double.NaN, double volume = double.NaN)
    {
        bool newDay = high > 0;
        double vol = !double.IsNaN(volume) ? volume : 1;
        return Update(price, vol, DateTime.UtcNow, newDay);
    }

    public VwapDevFactor(int windowSize = 252)
    {
        _windowSize = windowSize;
        _valueWindow = new Queue<double>(windowSize);
        _sortedSnapshot = new List<double>(windowSize);
    }

    /// <summary>
    /// 每根 Bar 调用一次。
    /// </summary>
    /// <param name="price">当前Bar收盘价</param>
    /// <param name="volume">当前Bar成交量 (手)</param>
    /// <param name="isNewDay">是否为新交易日</param>
    public FactorValue Update(double price, double volume, DateTime barTime, bool isNewDay)
    {
        if (isNewDay)
        {
            _cumPV = 0;
            _cumVol = 0;
            _barsInDay = 0;
        }

        _barsInDay++;
        _cumPV += price * volume;
        _cumVol += volume;

        double rawValue = double.NaN;
        if (_cumVol > 0)
        {
            var vwap = _cumPV / _cumVol;
            if (Math.Abs(vwap) > 1e-10)
                rawValue = (price - vwap) / vwap;
        }

        if (!double.IsNaN(rawValue) && !double.IsInfinity(rawValue))
        {
            UpdateStatistics(rawValue);
            var zScore = ComputeZScore(rawValue);
            var percentile = ComputePercentile(rawValue);

            LastValue = new FactorValue
            {
                FactorName = Name, Timestamp = barTime,
                RawValue = rawValue, ZScore = zScore,
                Percentile = percentile, SignalStrength = ComputeSignal(percentile),
                IsValid = IsReady,
            };
        }
        else
        {
            LastValue = new FactorValue { FactorName = Name, Timestamp = barTime, IsValid = false };
        }

        return LastValue;
    }

    public void Initialize(IReadOnlyList<double> warmupPrices)
    {
        foreach (var p in warmupPrices)
            if (!double.IsNaN(p) && !double.IsInfinity(p))
                UpdateStatistics(p);
    }

    public void Reset()
    {
        _valueWindow.Clear(); _sortedSnapshot.Clear();
        _valueSum = _valueSumSq = 0;
        _cumPV = _cumVol = 0; _barsInDay = 0;
    }

    private void UpdateStatistics(double value)
    {
        _valueWindow.Enqueue(value); _valueSum += value; _valueSumSq += value * value;
        if (_valueWindow.Count > _windowSize) { var old = _valueWindow.Dequeue(); _valueSum -= old; _valueSumSq -= old * old; }
        _sortCounter++;
        if (_sortCounter >= 50 || _valueWindow.Count - _sortedSnapshot.Count > _windowSize / 10)
        {
            _sortedSnapshot.Clear(); _sortedSnapshot.AddRange(_valueWindow); _sortedSnapshot.Sort(); _sortCounter = 0;
        }
    }

    private double ComputeZScore(double raw)
    {
        if (_valueWindow.Count < _windowSize / 4) return 0;
        var mean = _valueSum / _valueWindow.Count;
        var variance = (_valueSumSq / _valueWindow.Count) - (mean * mean);
        var std = Math.Sqrt(Math.Max(variance, 1e-10));
        var z = std > 0 ? (raw - mean) / std : 0;
        return Math.Clamp(z, -4.0, 4.0);
    }

    private double ComputePercentile(double raw)
    {
        if (_sortedSnapshot.Count < _windowSize / 4) return 0.5;
        var idx = _sortedSnapshot.BinarySearch(raw);
        if (idx < 0) idx = ~idx;
        return Math.Clamp((double)idx / _sortedSnapshot.Count, 0, 1);
    }

    private static double ComputeSignal(double percentile) => percentile switch
    {
        >= 0.80 => (percentile - 0.80) / 0.20,
        <= 0.20 => -(1.0 - percentile / 0.20),
        _ => 0,
    };
}
