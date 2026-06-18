namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 缠论分析完整结果。
/// </summary>
public record ChanLunResult(
    List<ChanLunBar> RawBars,
    List<ChanLunBar> StdBars,       // 无包含标准K线
    List<Fractal> Fractals,
    List<Bi> Bis,
    List<Zhongshu> Zhongshus,
    string Trend                    // "UP" | "DOWN" | "CONSOLIDATION" | "UNCLASSIFIED"
)
{
    public int RawCount => RawBars.Count;
    public int StdCount => StdBars.Count;
    public int FractalCount => Fractals.Count;
    public int BiCount => Bis.Count;
    public int ZhongshuCount => Zhongshus.Count;
}
