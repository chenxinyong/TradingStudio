namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 中枢构建器 — spec §4.2-§4.3。
/// 连续三笔重叠即构成中枢，相邻重叠的中枢自动合并。
/// </summary>
public static class ZhongshuBuilder
{
    /// <summary>
    /// 从笔构建中枢 — spec §4.2。
    /// 连续三笔 bi[i], bi[i+1], bi[i+2]:
    ///   ZG = min(bi[i].High, bi[i+1].High, bi[i+2].High)
    ///   ZD = max(bi[i].Low,  bi[i+1].Low,  bi[i+2].Low)
    ///   if ZG > ZD + ZS_MIN_OVERLAP → 中枢成立
    /// </summary>
    public static List<Zhongshu> Build(
        List<Bi> bis,
        double minOverlap = ChanLunConfig.ZsMinOverlap)
    {
        if (bis.Count < 3)
            return [];

        var zsList = new List<Zhongshu>();
        int i = 0;

        while (i < bis.Count - 2)
        {
            var b1 = bis[i];
            var b2 = bis[i + 1];
            var b3 = bis[i + 2];

            double zg = Min(b1.High, b2.High, b3.High);
            double zd = Max(b1.Low, b2.Low, b3.Low);

            if (zg > zd + minOverlap)
            {
                var zs = new Zhongshu(
                    Zg: zg,
                    Zd: zd,
                    Zz: (zg + zd) / 2,
                    StartBiIdx: i,
                    EndBiIdx: i + 2,
                    BiCount: 3,
                    Level: 1,
                    DtStart: b1.DtStart,
                    DtEnd: b3.DtEnd
                );

                // 重叠合并
                if (zsList.Count > 0 && ZsOverlap(zsList[^1], zs))
                    zsList[^1] = MergeZhongshus(zsList[^1], zs);
                else
                    zsList.Add(zs);

                i++;
            }
            else
            {
                i++;
            }
        }

        return zsList;
    }

    /// <summary>检查两个中枢是否有重叠 — spec §4.2</summary>
    private static bool ZsOverlap(Zhongshu a, Zhongshu b) =>
        a.Zg > b.Zd && b.Zg > a.Zd;

    /// <summary>合并两个重叠中枢 — spec §4.2</summary>
    private static Zhongshu MergeZhongshus(Zhongshu a, Zhongshu b) =>
        new(
            Zg: Math.Max(a.Zg, b.Zg),
            Zd: Math.Min(a.Zd, b.Zd),
            Zz: (Math.Max(a.Zg, b.Zg) + Math.Min(a.Zd, b.Zd)) / 2,
            StartBiIdx: a.StartBiIdx,
            EndBiIdx: b.EndBiIdx,
            BiCount: a.BiCount + b.BiCount - 2,
            Level: a.Level,
            DtStart: a.DtStart,
            DtEnd: b.DtEnd
        );

    /// <summary>
    /// 走势分类 — spec §4.3。
    /// 返回: "UP" | "DOWN" | "CONSOLIDATION" | "UNCLASSIFIED"
    /// </summary>
    public static string ClassifyTrend(List<Zhongshu> zsList)
    {
        if (zsList.Count == 0)
            return "UNCLASSIFIED";
        if (zsList.Count == 1)
            return "CONSOLIDATION";

        string? direction = null;

        for (int i = 0; i < zsList.Count - 1; i++)
        {
            var zs1 = zsList[i];
            var zs2 = zsList[i + 1];

            if (zs2.Zd > zs1.Zg)  // 中枢上移
            {
                if (direction == "DOWN") return "CONSOLIDATION";
                direction = "UP";
            }
            else if (zs2.Zg < zs1.Zd)  // 中枢下移
            {
                if (direction == "UP") return "CONSOLIDATION";
                direction = "DOWN";
            }
            else
            {
                return "CONSOLIDATION";
            }
        }

        return direction ?? "CONSOLIDATION";
    }

    // Helpers — 三参数 Math.Min/Max
    private static double Min(double a, double b, double c) =>
        Math.Min(a, Math.Min(b, c));
    private static double Max(double a, double b, double c) =>
        Math.Max(a, Math.Max(b, c));
}
