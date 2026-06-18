namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 笔构建器 — spec §3.2-§3.4。
/// 从分型序列构建笔，允许非相邻分型连接（向右扫描跳过不满足条件的分型）。
/// 后处理合并相邻同向笔。
/// </summary>
public static class BiBuilder
{
    /// <summary>
    /// 从分型构建笔 — spec §3.2。
    /// 笔成立条件:
    ///   1. start_fx 和 end_fx 类型不同
    ///   2. end_idx - start_idx + 1 >= MIN_BI_LEN
    ///   3. Up笔: end_fx.Price > start_fx.Price, Down笔: start_fx.Price > end_fx.Price
    /// </summary>
    public static List<Bi> Build(
        List<Fractal> fractals,
        List<ChanLunBar> stdBars,
        int minBiLen = ChanLunConfig.MinBiLen,
        int maxBiNum = ChanLunConfig.MaxBiNum)
    {
        if (fractals.Count < 2)
            return [];

        var bis = new List<Bi>();
        int i = 0;

        while (i < fractals.Count - 1)
        {
            var start = fractals[i];
            bool found = false;

            // 向右扫描，寻找第一个有效终点
            int maxJ = Math.Min(i + 1 + maxBiNum, fractals.Count);
            for (int j = i + 1; j < maxJ; j++)
            {
                var end = fractals[j];

                // 1) 类型必须不同
                if (start.Type == end.Type)
                    continue;

                // 2) K线数检查
                int klineCount = end.Index - start.Index + 1;
                if (klineCount < minBiLen)
                    continue;

                // 3) 方向 + 价格检查
                Direction biType;
                if (start.Type == FractalType.Bottom && end.Type == FractalType.Top)
                {
                    biType = Direction.Up;
                    if (end.Price <= start.Price)
                        continue;
                }
                else
                {
                    biType = Direction.Down;
                    if (start.Price <= end.Price)
                        continue;
                }

                // 计算笔的极值区间
                var segBars = stdBars.GetRange(start.Index, klineCount);
                double biHigh = segBars.Max(b => b.High);
                double biLow = segBars.Min(b => b.Low);

                bis.Add(new Bi(
                    Type: biType,
                    StartFx: start,
                    EndFx: end,
                    StartIdx: start.Index,
                    EndIdx: end.Index,
                    BarCount: klineCount,
                    Low: biLow,
                    High: biHigh,
                    DtStart: start.Dt,
                    DtEnd: end.Dt
                ));

                i = j;  // 跳到终点分型继续
                found = true;
                break;
            }

            if (!found)
                i++;  // 无有效终点，跳过当前分型
        }

        // 后处理: 合并相邻同向笔
        return MergeSameDirectionBis(bis, stdBars);
    }

    /// <summary>合并相邻同向笔（跳过中间分型可能导致同向相邻笔）</summary>
    private static List<Bi> MergeSameDirectionBis(List<Bi> bis, List<ChanLunBar> stdBars)
    {
        if (bis.Count < 2)
            return bis;

        var merged = new List<Bi> { bis[0] };

        foreach (var bi in bis.Skip(1))
        {
            var last = merged[^1];
            if (bi.Type == last.Type)
            {
                // 合并: 取更宽的范围
                int newBarCount = bi.EndIdx - last.StartIdx + 1;
                var seg = stdBars.GetRange(last.StartIdx, newBarCount);

                merged[^1] = new Bi(
                    Type: last.Type,
                    StartFx: last.StartFx,
                    EndFx: bi.EndFx,
                    StartIdx: last.StartIdx,
                    EndIdx: bi.EndIdx,
                    BarCount: newBarCount,
                    Low: seg.Min(b => b.Low),
                    High: seg.Max(b => b.High),
                    DtStart: last.DtStart,
                    DtEnd: bi.DtEnd
                );
            }
            else
            {
                merged.Add(bi);
            }
        }

        return merged;
    }

    /// <summary>验证笔结果 — spec §3.4</summary>
    public static bool Validate(List<Bi> bis, int minBiLen = ChanLunConfig.MinBiLen)
    {
        if (bis.Count < 2) return true;

        for (int i = 0; i < bis.Count; i++)
        {
            var bi = bis[i];
            // 1. 相邻笔方向相反
            if (i > 0 && bis[i].Type == bis[i - 1].Type)
                return false;
            // 2. BarCount >= MinBiLen
            if (bi.BarCount < minBiLen)
                return false;
            // 3. 价格约束
            if (bi.Type == Direction.Up)
            {
                if (!(bi.High >= bi.Low && bi.EndFx.Price > bi.StartFx.Price))
                    return false;
            }
            else
            {
                if (!(bi.High >= bi.Low && bi.StartFx.Price > bi.EndFx.Price))
                    return false;
            }
        }

        return true;
    }
}
