namespace TradingStudio.Core.Factors;

/// <summary>
/// 因子基类 — 提供标准化、滚动统计、信号映射等通用逻辑。
///
/// 所有因子继承此类，只需实现 Compute() 方法（计算原始因子值），
/// 标准化和信号映射由基类自动完成。
///
/// 标准化窗口:
///   - 滚动窗口跟踪最近 N 个原始值的分布
///   - 新值到达 → 更新滚动统计 → 计算 z-score 和百分位
///   - 窗口越长，标准化越稳定，但对市场变化越不敏感
/// </summary>
public abstract class FactorBase : IFactor
{
    protected readonly FactorConfig Config;

    // 滚动统计窗口
    private readonly Queue<double> _valueWindow;
    private readonly List<double> _sortedSnapshot; // 定期排序的快照，用于百分位计算
    private int _sortCounter;
    private double _valueSum;
    private double _valueSumSq;
    private int _warmupCount;

    protected FactorBase(FactorConfig config)
    {
        Config = config;
        _valueWindow = new Queue<double>(config.WindowSize);
        _sortedSnapshot = new List<double>(config.WindowSize);
    }

    public string Name => Config.Name;
    public string Category => Config.Category;
    public bool IsInitialized { get; private set; }
    public bool IsReady => _valueWindow.Count >= Config.MinWarmupBars;
    public FactorValue LastValue { get; private set; }

    /// <summary>
    /// 用历史数据预热因子。
    /// 策略初始化时调用，传入足够的历史价格让因子建立滚动统计基线。
    /// </summary>
    public void Initialize(IReadOnlyList<double> warmupPrices)
    {
        if (warmupPrices.Count < 2)
            throw new ArgumentException($"Factor '{Name}' needs at least 2 warmup bars, got {warmupPrices.Count}");

        // 前几个 bar 用于 Compute 的内部状态初始化（如 Momentum factor 的 lookback）
        // 后面的 bar 用于滚动统计
        foreach (var price in warmupPrices)
        {
            var rawValue = Compute(price, double.NaN, double.NaN, double.NaN);
            if (!double.IsNaN(rawValue))
                UpdateStatistics(rawValue, DateTime.MinValue);
        }

        IsInitialized = true;
    }

    /// <summary>
    /// 每个 Bar 调用一次。
    /// 1. 调用子类的 Compute() 计算原始因子值
    /// 2. 更新滚动统计
    /// 3. 生成标准化的 FactorValue
    /// </summary>
    public FactorValue Update(double price, double high = double.NaN, double low = double.NaN, double volume = double.NaN)
    {
        var rawValue = Compute(price, high, low, volume);

        if (double.IsNaN(rawValue) || double.IsInfinity(rawValue))
        {
            LastValue = new FactorValue
            {
                FactorName = Name,
                Timestamp = DateTime.UtcNow,
                RawValue = rawValue,
                IsValid = false,
            };
            return LastValue;
        }

        var now = DateTime.UtcNow;
        UpdateStatistics(rawValue, now);

        var zScore = ComputeZScore(rawValue);
        var percentile = ComputePercentile(rawValue);
        var signal = ComputeSignal(percentile);

        LastValue = new FactorValue
        {
            FactorName = Name,
            Timestamp = now,
            RawValue = rawValue,
            ZScore = zScore,
            Percentile = percentile,
            SignalStrength = signal,
            IsValid = IsReady,
        };

        return LastValue;
    }

    public virtual void Reset()
    {
        _valueWindow.Clear();
        _sortedSnapshot.Clear();
        _valueSum = 0;
        _valueSumSq = 0;
        _warmupCount = 0;
        IsInitialized = false;
    }

    // ═══════════════════════════════════════════
    // 子类必须实现
    // ═══════════════════════════════════════════

    /// <summary>
    /// 计算原始因子值（未标准化）。
    /// 子类只需实现这个方法，标准化和信号映射由 FactorBase 处理。
    /// </summary>
    /// <returns>原始因子值，NaN 表示数据不足</returns>
    protected abstract double Compute(double price, double high, double low, double volume);

    // ═══════════════════════════════════════════
    // 标准化逻辑
    // ═══════════════════════════════════════════

    private void UpdateStatistics(double rawValue, DateTime timestamp)
    {
        // 滚动窗口管理
        _valueWindow.Enqueue(rawValue);
        _valueSum += rawValue;
        _valueSumSq += rawValue * rawValue;

        if (_valueWindow.Count > Config.WindowSize)
        {
            var old = _valueWindow.Dequeue();
            _valueSum -= old;
            _valueSumSq -= old * old;
        }

        // 定期重建排序快照（每 50 次更新或窗口变化 >10% 时）
        _sortCounter++;
        if (_sortCounter >= 50 || _valueWindow.Count - _sortedSnapshot.Count > Config.WindowSize / 10)
        {
            _sortedSnapshot.Clear();
            _sortedSnapshot.AddRange(_valueWindow);
            _sortedSnapshot.Sort();
            _sortCounter = 0;
        }

        // 预热计数
        if (!IsReady) _warmupCount++;
    }

    private double ComputeZScore(double rawValue)
    {
        if (_valueWindow.Count < Config.MinWarmupBars) return 0;

        var mean = _valueSum / _valueWindow.Count;
        var variance = (_valueSumSq / _valueWindow.Count) - (mean * mean);
        var std = Math.Sqrt(Math.Max(variance, 1e-10));

        // 截断极端 z-score（防止单个异常值污染后续统计）
        var z = std > 0 ? (rawValue - mean) / std : 0;
        return Math.Clamp(z, -Config.ZScoreCap, Config.ZScoreCap);
    }

    private double ComputePercentile(double rawValue)
    {
        if (_sortedSnapshot.Count < Config.MinWarmupBars) return 0.5;

        // 二分查找当前值在排序快照中的位置
        var idx = _sortedSnapshot.BinarySearch(rawValue);
        if (idx < 0) idx = ~idx; // 插入位置 → 排名
        return Math.Clamp((double)idx / _sortedSnapshot.Count, 0, 1);
    }

    /// <summary>
    /// 从百分位映射到信号强度 [-1, +1]。
    ///
    /// 逻辑（Chan 的方法）:
    ///   Top 20% → 做多信号，强度按比例映射到 (0, +1]
    ///   Bottom 20% → 做空信号，强度映射到 [-1, 0)
    ///   中间 60% → 中性，信号 = 0
    ///
    /// 这是为了减少噪声——只交易因子最明确的品种。
    /// </summary>
    private double ComputeSignal(double percentile)
    {
        return percentile switch
        {
            >= 0.80 => (percentile - 0.80) / 0.20,   // (0, +1]
            <= 0.20 => -(1.0 - percentile / 0.20),   // [-1, 0)
            _ => 0,                                     // 中性
        };
    }
}

/// <summary>因子配置</summary>
public class FactorConfig
{
    /// <summary>因子唯一名称</summary>
    public string Name { get; init; } = "Unnamed";

    /// <summary>因子类别</summary>
    public string Category { get; init; } = "General";

    /// <summary>滚动标准化窗口大小（Bar 数）。默认 252 (≈1年日线)</summary>
    public int WindowSize { get; init; } = 252;

    /// <summary>最少预热 Bar 数（窗口的 1/4，至少 10）</summary>
    public int MinWarmupBars => Math.Max(10, WindowSize / 4);

    /// <summary>Z-Score 截断上限（默认 ±4σ，超出的截断）</summary>
    public double ZScoreCap { get; init; } = 4.0;

    /// <summary>信号阈值：百分位超过此值产生做多信号</summary>
    public double LongThreshold { get; init; } = 0.80;

    /// <summary>信号阈值：百分位低于此值产生做空信号</summary>
    public double ShortThreshold { get; init; } = 0.20;
}
