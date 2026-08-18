namespace ChanAnalysis;

/// <summary>单根K线（日/周/月通用）。</summary>
public readonly record struct Bar(
    DateTime Date,
    double Open,
    double High,
    double Low,
    double Close,
    long OpenInterest = 0);

/// <summary>分型类型：顶 / 底。</summary>
public enum FractalType { Top, Bottom }

/// <summary>一笔：从某个分型到下一个反向分型。</summary>
public readonly record struct Bi(
    int StartIndex,
    int EndIndex,
    FractalType StartType,
    FractalType EndType);

/// <summary>中枢：三段连续笔重叠形成的价格区间。</summary>
public readonly record struct Zhongshu(
    double Lower,       // 中枢下沿 = 三笔低点的最大值 (Python 里的 zh)
    double Upper,       // 中枢上沿 = 三笔高点的最小值 (Python 里的 zd)
    DateTime StartDate,
    DateTime EndDate);

/// <summary>价格相对中枢的位置。</summary>
public enum Location
{
    NoZhongshu,   // 无中枢
    SanMai,       // 三买：价格在中枢上沿之上
    SanMaiArea,   // 三买区：离开中枢后未回补
    SanMaiAbove,  // 中枢上方（通用标注 ABV）
    ZhenDang,     // 震荡：价格在中枢内部
    SanMaiBelow   // 中枢下方（通用标注 BLW）
}

/// <summary>某个级别（周/日等）的完整缠论分析结果。</summary>
public sealed record AnalysisResult(
    IReadOnlyList<Bar> MergedBars,
    IReadOnlyList<Bi> Bis,
    IReadOnlyList<Zhongshu> Zhongshus,
    IReadOnlyList<int> TopFractals,
    IReadOnlyList<int> BottomFractals);

/// <summary>背驰判定结果。</summary>
public sealed record DivergenceResult(
    bool IsDivergent,
    string Kind,        // "底背驰" / "顶背驰" / "无"
    double CurrentAmplitude,
    double PreviousAmplitude,
    double Ratio);      // 当前幅度 / 前幅度
