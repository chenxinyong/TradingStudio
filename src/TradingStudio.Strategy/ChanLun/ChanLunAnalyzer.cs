namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 缠论分析器 — 完整管线入口。
/// 一键运行: 包含处理 → 分型识别 → 笔识别 → 中枢识别 → 走势分类。
///
/// 用法:
///   var bars = BarAdapter.FromCoreBars(coreBars);
///   var result = ChanLunAnalyzer.Analyze(bars, minBiLen: 5);
///   Console.WriteLine($"{result.BiCount}笔, {result.ZhongshuCount}中枢, 走势={result.Trend}");
/// </summary>
public static class ChanLunAnalyzer
{
    /// <summary>
    /// 完整缠论分析管线。
    /// </summary>
    /// <param name="rawBars">原始K线列表（按时间升序）</param>
    /// <param name="minBiLen">最小笔长度，默认5（15min最优）</param>
    /// <param name="maxBiNum">最大笔数量</param>
    /// <param name="minZsOverlap">中枢最小重叠</param>
    /// <returns>完整分析结果</returns>
    public static ChanLunResult Analyze(
        List<ChanLunBar> rawBars,
        int minBiLen = ChanLunConfig.MinBiLen,
        int maxBiNum = ChanLunConfig.MaxBiNum,
        double minZsOverlap = ChanLunConfig.ZsMinOverlap)
    {
        // Step 1: 包含处理
        var stdBars = InclusionProcessor.Process(rawBars);

        // Step 2: 分型识别 (含去重)
        var fractals = FractalDetector.Detect(stdBars);

        // Step 3: 笔识别
        var bis = BiBuilder.Build(fractals, stdBars, minBiLen, maxBiNum);

        // Step 4: 中枢识别
        var zhongshus = ZhongshuBuilder.Build(bis, minZsOverlap);

        // Step 5: 走势分类
        var trend = ZhongshuBuilder.ClassifyTrend(zhongshus);

        return new ChanLunResult(rawBars, stdBars, fractals, bis, zhongshus, trend);
    }

    /// <summary>
    /// 验证分析结果的内部一致性。
    /// </summary>
    public static (bool inclusion, bool fractal, bool bi) Validate(ChanLunResult result, int minBiLen = ChanLunConfig.MinBiLen)
    {
        return (
            InclusionProcessor.Validate(result.RawBars, result.StdBars),
            FractalDetector.Validate(result.Fractals, result.StdBars),
            BiBuilder.Validate(result.Bis, minBiLen)
        );
    }
}
