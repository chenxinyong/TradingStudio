namespace ChanAnalysis;

/// <summary>缠论核心算法：包含处理 → 分型 → 笔 → 中枢 → 背驰。</summary>
public static class ChanEngine
{
    /// <summary>包含关系处理（合并有包含关系的相邻K线）。</summary>
    public static List<Bar> Merge(List<Bar> bars)
    {
        if (bars.Count < 2) return new List<Bar>(bars);

        var result = new List<Bar> { bars[0] };
        int? direction = null; // 1 = 向上, -1 = 向下

        for (int i = 1; i < bars.Count; i++)
        {
            var prev = result[^1];
            var curr = bars[i];

            // 向上：当前高点更高且低点更高 → 无包含，方向向上
            if (curr.High > prev.High && curr.Low > prev.Low)
            {
                direction = 1;
                result.Add(curr);
            }
            // 向下：当前高点更低且低点更低 → 无包含，方向向下
            else if (curr.High < prev.High && curr.Low < prev.Low)
            {
                direction = -1;
                result.Add(curr);
            }
            // 包含关系：合并
            else
            {
                Bar merged;
                if (direction == 1 || (direction is null && curr.High >= prev.High))
                {
                    // 向上合并：取高点的高者、低点的高者
                    merged = new Bar(curr.Date, prev.Open,
                        Math.Max(prev.High, curr.High), Math.Max(prev.Low, curr.Low), curr.Close);
                }
                else
                {
                    // 向下合并：取高点的低者、低点的低者
                    merged = new Bar(curr.Date, prev.Open,
                        Math.Min(prev.High, curr.High), Math.Min(prev.Low, curr.Low), curr.Close);
                }
                result[^1] = merged;
            }
        }
        return result;
    }

    /// <summary>判断第 i 根是否为分型。</summary>
    private static bool HasFractal(IReadOnlyList<Bar> bars, int i, FractalType type)
    {
        if (i < 1 || i >= bars.Count - 1) return false;
        var (a, b, c) = (bars[i - 1], bars[i], bars[i + 1]);
        return type == FractalType.Top
            ? b.High > a.High && b.High > c.High
            : b.Low < a.Low && b.Low < c.Low;
    }

    /// <summary>
    /// 完整分析：返回合并后的K线、笔、中枢、顶分型、底分型。
    /// </summary>
    public static AnalysisResult Analyze(List<Bar> bars, int minGap = 3)
    {
        var merged = Merge(bars);
        var tops = new List<int>();
        var bottoms = new List<int>();

        for (int i = 1; i < merged.Count - 1; i++)
        {
            if (HasFractal(merged, i, FractalType.Top)) tops.Add(i);
            if (HasFractal(merged, i, FractalType.Bottom)) bottoms.Add(i);
        }

        // 合并分型序列（按索引排序）
        var fractals = tops.Select(i => (i, FractalType.Top))
            .Concat(bottoms.Select(i => (i, FractalType.Bottom)))
            .OrderBy(f => f.Item1).ThenBy(f => f.Item2 == FractalType.Top ? 1 : 0)
            .ToList();

        // 划分笔
        var bis = new List<Bi>();
        (int Index, FractalType Type)? pending = null;

        foreach (var (idx, type) in fractals)
        {
            if (pending is null)
            {
                pending = (idx, type);
                continue;
            }
            if (pending.Value.Type == type)
            {
                // 同向分型：保留更极端的
                if (type == FractalType.Top)
                {
                    if (merged[idx].High > merged[pending.Value.Index].High)
                        pending = (idx, type);
                }
                else
                {
                    if (merged[idx].Low < merged[pending.Value.Index].Low)
                        pending = (idx, type);
                }
                continue;
            }
            // 反向分型：达到最小间隔才形成一笔，并更新 pending 为新笔起点；
            // 未达到则丢弃这个"太近"的分型，pending 保持不变（与 Python 语义一致）
            if (idx - pending.Value.Index >= minGap)
            {
                bis.Add(new Bi(pending.Value.Index, idx, pending.Value.Type, type));
                pending = (idx, type);
            }
        }

        // 识别中枢：三段连续笔重叠
        var zhongshus = new List<Zhongshu>();
        for (int i = 0; i < bis.Count - 2; i++)
        {
            var (b0, b1, b2) = (bis[i], bis[i + 1], bis[i + 2]);

            // 每笔的价格区间
            var (lo0, hi0) = BiRange(merged, b0);
            var (lo1, hi1) = BiRange(merged, b1);
            var (lo2, hi2) = BiRange(merged, b2);

            double lower = Math.Max(lo0, Math.Max(lo1, lo2));  // 三笔低点的最大值
            double upper = Math.Min(hi0, Math.Min(hi1, hi2));  // 三笔高点的最小值

            if (upper > lower) // 有重叠
                zhongshus.Add(new Zhongshu(lower, upper,
                    merged[bis[i].StartIndex].Date, merged[bis[i + 2].EndIndex].Date));
        }

        return new AnalysisResult(merged, bis, zhongshus, tops, bottoms);
    }

    /// <summary>一笔的价格区间（低点、高点）。</summary>
    private static (double Low, double High) BiRange(IReadOnlyList<Bar> bars, Bi bi)
    {
        var s = bars[bi.StartIndex];
        var e = bars[bi.EndIndex];
        double low = Math.Min(s.Low, e.Low);
        double high = Math.Max(s.High, e.High);
        return (low, high);
    }

    /// <summary>判断价格相对中枢的位置。</summary>
    public static Location Locate(double price, AnalysisResult result)
    {
        if (result.Zhongshus.Count == 0) return Location.NoZhongshu;
        var zs = result.Zhongshus[^1];
        if (price > zs.Upper) return Location.SanMaiAbove;
        if (price < zs.Lower) return Location.SanMaiBelow;
        return Location.ZhenDang;
    }

    /// <summary>背驰判断：比较最后一笔与同向前一笔的幅度。</summary>
    public static DivergenceResult CheckDivergence(AnalysisResult result, double threshold = 0.7)
    {
        var bis = result.Bis;
        if (bis.Count < 3) return new DivergenceResult(false, "无", 0, 0, 0);

        var last = bis[^1];
        // 找同向的上一笔
        Bi? previous = null;
        for (int i = bis.Count - 2; i >= 0; i--)
        {
            if (bis[i].StartType == last.StartType && bis[i].EndType == last.EndType)
            {
                previous = bis[i];
                break;
            }
        }
        if (previous is null) return new DivergenceResult(false, "无", 0, 0, 0);

        var bars = result.MergedBars;
        double currAmp = Math.Abs(bars[last.EndIndex].Close - bars[last.StartIndex].Close);
        double prevAmp = Math.Abs(bars[previous.Value.EndIndex].Close - bars[previous.Value.StartIndex].Close);
        double ratio = currAmp / prevAmp;

        // 底背驰：下跌笔幅度缩小（last.EndType == Bottom）
        // 顶背驰：上涨笔幅度缩小（last.EndType == Top）
        bool divergent = ratio < threshold;
        string kind = last.EndType == FractalType.Bottom ? "底背驰" : "顶背驰";

        return new DivergenceResult(divergent, kind, currAmp, prevAmp, ratio);
    }
}
