namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 分型识别器 — spec §2.2-§2.4。
/// 在标准K线(无包含)中识别顶底分型，并去除相邻同类型分型。
/// </summary>
public static class FractalDetector
{
    /// <summary>
    /// 识别顶底分型 — spec §2.2。
    /// 顶分型: b.High > a.High && b.High > c.High && b.Low > a.Low && b.Low > c.Low
    /// 底分型: b.Low < a.Low && b.Low < c.Low && b.High < a.High && b.High < c.High
    /// </summary>
    public static List<Fractal> Find(List<ChanLunBar> stdBars)
    {
        if (stdBars.Count < 3)
            return [];

        var fractals = new List<Fractal>();

        for (int i = 1; i < stdBars.Count - 1; i++)
        {
            var a = stdBars[i - 1];
            var b = stdBars[i];
            var c = stdBars[i + 1];

            // 顶分型
            if (b.High > a.High && b.High > c.High &&
                b.Low > a.Low && b.Low > c.Low)
            {
                fractals.Add(new Fractal(FractalType.Top, i, b.High, b.Dt));
            }
            // 底分型
            else if (b.Low < a.Low && b.Low < c.Low &&
                     b.High < a.High && b.High < c.High)
            {
                fractals.Add(new Fractal(FractalType.Bottom, i, b.Low, b.Dt));
            }
        }

        return fractals;
    }

    /// <summary>
    /// 分型去重 — spec §2.3。
    /// 相邻同类型分型 → 保留极值更极端者:
    ///   连续两个 TOP → 保留 Price 更高的
    ///   连续两个 BOTTOM → 保留 Price 更低的
    /// </summary>
    public static List<Fractal> Deduplicate(List<Fractal> fractals)
    {
        if (fractals.Count < 2)
            return fractals;

        var result = new List<Fractal> { fractals[0] };

        foreach (var f in fractals.Skip(1))
        {
            var last = result[^1];
            if (f.Type == last.Type)
            {
                if (f.Type == FractalType.Top && f.Price > last.Price)
                    result[^1] = f;
                else if (f.Type == FractalType.Bottom && f.Price < last.Price)
                    result[^1] = f;
            }
            else
            {
                result.Add(f);
            }
        }

        return result;
    }

    /// <summary>
    /// 完整分型识别流程: 识别 → 去重。
    /// </summary>
    public static List<Fractal> Detect(List<ChanLunBar> stdBars)
    {
        var raw = Find(stdBars);
        return Deduplicate(raw);
    }

    /// <summary>验证分型结果 — spec §2.4</summary>
    public static bool Validate(List<Fractal> fractals, List<ChanLunBar> stdBars)
    {
        if (fractals.Count < 2) return true;

        // 1. Top 和 Bottom 严格交替
        for (int i = 0; i < fractals.Count - 1; i++)
            if (fractals[i].Type == fractals[i + 1].Type)
                return false;

        // 2. Price 验证
        foreach (var f in fractals)
        {
            var bar = stdBars[f.Index];
            if (f.Type == FractalType.Top && Math.Abs(f.Price - bar.High) > 1e-9)
                return false;
            if (f.Type == FractalType.Bottom && Math.Abs(f.Price - bar.Low) > 1e-9)
                return false;
        }

        return true;
    }
}
