namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 包含处理器 — spec §1.1-§1.5。
/// 将原始K线中的包含关系合并为标准K线。
/// 方向判定: 基于前两根已确认非包含K线的关系。
/// </summary>
public static class InclusionProcessor
{
    /// <summary>检查两根K线是否存在包含关系 — spec §1.1</summary>
    public static bool HasInclusion(ChanLunBar a, ChanLunBar b) =>
        (a.High >= b.High && a.Low <= b.Low) ||
        (b.High >= a.High && b.Low <= a.Low);

    /// <summary>
    /// 包含处理主函数 — spec §1.4。
    /// 将所有具有包含关系的相邻K线合并为标准K线。
    /// </summary>
    public static List<ChanLunBar> Process(List<ChanLunBar> rawBars)
    {
        if (rawBars.Count < 2)
            return new List<ChanLunBar>(rawBars);

        Direction? direction = null;
        var result = new List<ChanLunBar>();
        ChanLunBar? pending = null;

        foreach (var bar in rawBars)
        {
            if (pending == null)
            {
                pending = bar;
                continue;
            }

            if (!HasInclusion(pending, bar))
            {
                // 无包含 → pending确认，bar变新的pending
                if (result.Count >= 1)
                {
                    var prev = result[^1];
                    direction = bar.High > prev.High ? Direction.Up
                               : bar.High < prev.High ? Direction.Down
                               : direction;
                }
                result.Add(pending);
                pending = bar;
            }
            else
            {
                // 有包含 → 合并到pending
                if (result.Count >= 1)
                {
                    var prev = result[^1];
                    direction = pending.High > prev.High ? Direction.Up
                               : pending.High < prev.High ? Direction.Down
                               : direction;
                }
                var d = direction ?? Direction.Up;

                pending = d == Direction.Up
                    ? new ChanLunBar
                    {
                        Dt = bar.Dt,
                        Open = pending.Open,
                        Close = bar.Close,
                        High = Math.Max(pending.High, bar.High),
                        Low = Math.Max(pending.Low, bar.Low),
                        Volume = pending.Volume + bar.Volume,
                    }
                    : new ChanLunBar
                    {
                        Dt = bar.Dt,
                        Open = pending.Open,
                        Close = bar.Close,
                        High = Math.Min(pending.High, bar.High),
                        Low = Math.Min(pending.Low, bar.Low),
                        Volume = pending.Volume + bar.Volume,
                    };
            }
        }

        if (pending != null)
            result.Add(pending);

        return result;
    }

    /// <summary>验证包含处理结果 — spec §1.5</summary>
    public static bool Validate(List<ChanLunBar> original, List<ChanLunBar> processed)
    {
        // 1. result.Count <= rawBars.Count
        if (processed.Count > original.Count) return false;
        // 2. 任意相邻两根标准K线无包含
        for (int i = 0; i < processed.Count - 1; i++)
            if (HasInclusion(processed[i], processed[i + 1])) return false;
        // 3. 时间单调递增
        for (int i = 0; i < processed.Count - 1; i++)
            if (processed[i].Dt >= processed[i + 1].Dt) return false;
        return true;
    }
}
