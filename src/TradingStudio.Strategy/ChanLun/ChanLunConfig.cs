namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 缠论参数配置 — spec §0.2。
/// 所有可调参数集中管理，运行时不可变。
/// </summary>
public static class ChanLunConfig
{
    /// <summary>最小笔长度（包含K线数），默认7</summary>
    public const int MinBiLen = 5; // 15min最优参数

    /// <summary>最大笔数量</summary>
    public const int MaxBiNum = 500;

    /// <summary>中枢最小重叠（价格单位）</summary>
    public const double ZsMinOverlap = 1.0;

    /// <summary>背驰力度比阈值（C段力度/A段力度）</summary>
    public const double DivergenceRatio = 0.5;
}

/// <summary>方向</summary>
public enum Direction { Up, Down }

/// <summary>分型类型</summary>
public enum FractalType { Top, Bottom }
