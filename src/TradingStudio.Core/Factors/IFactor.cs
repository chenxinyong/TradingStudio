namespace TradingStudio.Core.Factors;

/// <summary>
/// 因子接口 — 量化因子框架的核心抽象。
///
/// 因子 vs 指标的区别（Chan → Gray 的思维升级）:
///   指标: SMA(233) = 3456.78  → "这个数字本身没有交易含义"
///   因子: Momentum(20) = +0.85σ → "比过去60天85%的时间更强" → 可以做多
///
/// 因子必须输出三种标准化值:
///   RawValue  — 原始计算值（如 2.3% 收益率）
///   ZScore    — z-score 标准化（偏离滚动均值的标准差数）→ 跨品种可比
///   Percentile — 百分位（0=最弱, 1=最强）→ 排序选品种
/// </summary>
public interface IFactor
{
    /// <summary>因子名称（唯一标识）</summary>
    string Name { get; }

    /// <summary>因子类别 (Momentum, Trend, Volatility, Value, Sentiment)</summary>
    string Category { get; }

    /// <summary>是否已初始化</summary>
    bool IsInitialized { get; }

    /// <summary>预热是否完成（有足够数据产生可靠信号）</summary>
    bool IsReady { get; }

    /// <summary>最近一次计算的因子值</summary>
    FactorValue LastValue { get; }

    /// <summary>
    /// 初始化因子。传入初始价格序列用于预热。
    /// </summary>
    void Initialize(IReadOnlyList<double> warmupPrices);

    /// <summary>
    /// 每个 Bar 调用一次，更新因子值。
    /// </summary>
    /// <param name="price">当前收盘价</param>
    /// <param name="high">最高价（可选，波动率类因子用）</param>
    /// <param name="low">最低价（可选）</param>
    /// <param name="volume">成交量（可选，流量类因子用）</param>
    FactorValue Update(double price, double high = double.NaN, double low = double.NaN, double volume = double.NaN);

    /// <summary>重置因子状态（切换品种时调用）</summary>
    void Reset();
}

/// <summary>
/// 因子的标准化输出值。
/// 每个因子必须同时输出三种形式，满足不同的使用场景。
/// </summary>
public readonly struct FactorValue
{
    /// <summary>因子名称</summary>
    public string FactorName { get; init; }

    /// <summary>计算时间</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>原始值（未标准化，保留量纲）</summary>
    public double RawValue { get; init; }

    /// <summary>
    /// Z-Score 标准化值。
    /// = (RawValue - 滚动均值) / 滚动标准差
    /// 范围通常在 [-3, +3]，跨品种、跨时间可比。
    /// </summary>
    public double ZScore { get; init; }

    /// <summary>
    /// 百分位排名 [0, 1]。
    /// 0 = 滚动窗口中最弱，1 = 最强。
    /// 横截面排名时直接用这个值排序。
    /// </summary>
    public double Percentile { get; init; }

    /// <summary>
    /// 信号强度 [-1, +1]。
    /// 从 Percentile 映射而来：
    ///   Percentile > 0.8 → +信号（做多倾向）
    ///   Percentile < 0.2 → -信号（做空倾向）
    ///   中间 → 0（中性，不交易）
    /// </summary>
    public double SignalStrength { get; init; }

    /// <summary>是否有效（预热完成、无异常值）</summary>
    public bool IsValid { get; init; }

    public override string ToString() =>
        $"{FactorName}: raw={RawValue:F4} z={ZScore:+0.00;-0.00} pct={Percentile:P0} sig={SignalStrength:+0.00;-0.00}";
}

/// <summary>因子标准化模式</summary>
public enum NormalizationMode
{
    /// <summary>Z-Score: (x - μ) / σ，适合对称分布</summary>
    ZScore,

    /// <summary>Min-Max: (x - min) / (max - min)，适合有界分布</summary>
    MinMax,

    /// <summary>Rank: 百分位排名，适合非正态分布（推荐大多数金融因子使用）</summary>
    Rank,

    /// <summary>不标准化，返回原始值</summary>
    None,
}
