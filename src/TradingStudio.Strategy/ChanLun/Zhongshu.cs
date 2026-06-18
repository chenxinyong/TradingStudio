namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 中枢 — spec §4.1。
/// 由连续三笔重叠区间构成，重叠中枢自动合并。
/// </summary>
public record Zhongshu(
    double Zg,          // 中枢上沿 = min(三笔高点最大值)
    double Zd,          // 中枢下沿 = max(三笔低点最小值)
    double Zz,          // 中枢中轨 = (Zg+Zd)/2
    int StartBiIdx,     // 第一笔在bi_list中的索引
    int EndBiIdx,       // 第三笔在bi_list中的索引
    int BiCount,        // 构成笔数（默认3，延伸后更大）
    int Level = 1,      // 中枢级别（1=笔中枢）
    DateTime? DtStart = null,
    DateTime? DtEnd = null
);
