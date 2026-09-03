namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 买卖点信号类型 — spec §6.1。
/// B=买点(做多), S=卖点(做空), 1/2/3 对应缠论三类买卖点。
/// </summary>
public enum ChanSignalType { B1, B2, B3, S1, S2, S3 }

/// <summary>
/// 趋势背驰信号（MACD 面积法）— spec §5.3。
/// 仅在趋势（UP/DOWN）中产生：比较进入中枢的 A 段与离开中枢的 C 段的 MACD 面积。
/// 注意：与 DivergenceDetector.DivergenceSignal（4维度笔比较）是两套背驰口径，
/// 本类才是买卖点 B1/S1 的前置信号。
/// </summary>
public record MacdDivergence(
    string Type,        // "BOTTOM_DIVERGENCE" | "TOP_DIVERGENCE"
    int ZsIndex,        // 第二个中枢在 zsList 中的索引
    double AArea,       // A 段 MACD 面积（绿=下跌动能 / 红=上涨动能）
    double CArea,       // C 段 MACD 面积
    double Ratio,       // C/A 面积比
    Bi ABi,             // A 段笔（进入第一个中枢前）
    Bi CBi,             // C 段笔（离开第二个中枢）
    DateTime Dt
);

/// <summary>
/// 缠论买卖点信号 — spec §6.1。
/// </summary>
public record ChanSignal(
    ChanSignalType Type,
    DateTime Dt,        // 信号时间（确认笔起点，静态分析/画图用）
    DateTime ConfirmEnd,// 确认笔完成时间 = 最早可交易时间（无前视；策略层据此触发）
    double Price,       // 理想入场价 = 确认笔起点（分型价格，回测不可直接成交）
    double StopLoss,    // 止损价
    int Confidence,     // 1-3
    Zhongshu Zs,
    string Description
);

/// <summary>
/// 买卖点生成器 — spec §5.2/§5.3/§6/§7。
/// 背驰（MACD 面积法）→ 一买/一卖 → 二买/二卖 → 三买/三卖 → 双级别联立。
/// 纯函数，无状态；策略层（ChanLunStrategy）负责调用与仓位管理。
/// </summary>
public static class SignalGenerator
{
    // ════════════════════════════════════════════════════════════
    // §5.2 MACD 面积
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 计算指定标准K线区间的 MACD 红柱面积与绿柱面积。
    /// </summary>
    /// <param name="bars">标准K线（stdBars，无包含）</param>
    /// <param name="startIdx">起始索引（含）</param>
    /// <param name="endIdx">结束索引（含）</param>
    public static (double RedArea, double GreenArea) MacdArea(
        List<ChanLunBar> bars, int startIdx, int endIdx)
    {
        if (bars.Count == 0 || startIdx < 0 || endIdx >= bars.Count || endIdx < startIdx)
            return (0, 0);

        int count = endIdx - startIdx + 1;
        var closes = new double[count];
        for (int i = 0; i < count; i++)
            closes[i] = bars[startIdx + i].Close;

        var ema12 = Ema(closes, 12);
        var ema26 = Ema(closes, 26);

        var dif = new double[count];
        for (int i = 0; i < count; i++)
            dif[i] = ema12[i] - ema26[i];

        var dea = Ema(dif, 9);

        double redArea = 0, greenArea = 0;
        for (int i = 0; i < count; i++)
        {
            double macdBar = 2 * (dif[i] - dea[i]); // (DIF-DEA)*2
            if (macdBar > 0) redArea += macdBar;
            else greenArea += Math.Abs(macdBar);
        }
        return (redArea, greenArea);
    }

    private static double[] Ema(double[] data, int period)
    {
        double k = 2.0 / (period + 1);
        var result = new double[data.Length];
        if (data.Length == 0) return result;
        result[0] = data[0];
        for (int i = 1; i < data.Length; i++)
            result[i] = data[i] * k + result[i - 1] * (1 - k);
        return result;
    }

    // ════════════════════════════════════════════════════════════
    // §5.3 趋势背驰检测
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 趋势背驰检测（仅趋势中）。
    /// 上涨趋势顶背驰: C段MACD红柱面积 &lt; A段红柱面积 × DivergenceRatio
    /// 下跌趋势底背驰: C段MACD绿柱面积 &lt; A段绿柱面积 × DivergenceRatio
    /// </summary>
    public static List<MacdDivergence> CheckDivergence(
        List<Bi> bis, List<Zhongshu> zsList, List<ChanLunBar> stdBars)
    {
        var trend = ZhongshuBuilder.ClassifyTrend(zsList);
        if (trend is not ("UP" or "DOWN"))
            return [];

        var signals = new List<MacdDivergence>();
        bool isDown = trend == "DOWN";

        for (int i = 0; i < zsList.Count - 1; i++)
        {
            var zs1 = zsList[i];
            var zs2 = zsList[i + 1];

            // A段：进入第一个中枢前的那一笔
            Bi? aBi = zs1.StartBiIdx > 0 ? bis[zs1.StartBiIdx - 1] : null;
            // C段：离开第二个中枢、方向与趋势一致的那一笔
            var cDir = isDown ? Direction.Down : Direction.Up;
            var cBi = FindExitBi(bis, zs2, cDir);
            if (aBi == null || cBi == null) continue;

            var (redA, greenA) = MacdArea(stdBars, aBi.StartIdx, aBi.EndIdx);
            var (redC, greenC) = MacdArea(stdBars, cBi.StartIdx, cBi.EndIdx);

            if (isDown && greenC > 0 && greenC < greenA * ChanLunConfig.DivergenceRatio)
            {
                signals.Add(new MacdDivergence("BOTTOM_DIVERGENCE", i + 1,
                    greenA, greenC, greenA > 0 ? greenC / greenA : 1.0,
                    aBi, cBi, cBi.DtEnd));
            }
            else if (!isDown && redC > 0 && redC < redA * ChanLunConfig.DivergenceRatio)
            {
                signals.Add(new MacdDivergence("TOP_DIVERGENCE", i + 1,
                    redA, redC, redA > 0 ? redC / redA : 1.0,
                    aBi, cBi, cBi.DtEnd));
            }
        }
        return signals;
    }

    /// <summary>从中枢结束后的笔序列中，找到第一笔指定方向的笔。</summary>
    private static Bi? FindExitBi(List<Bi> bis, Zhongshu zs, Direction dir)
    {
        for (int i = zs.EndBiIdx + 1; i < bis.Count; i++)
            if (bis[i].Type == dir) return bis[i];
        return null;
    }

    // ════════════════════════════════════════════════════════════
    // §6.2 一买 / 一卖（趋势背驰）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 一买 B1：趋势底背驰成立 + C段之后出现反向向上笔确认。
    /// 入场 = 确认笔起点（底分型价格），止损 = C段最低点 × 0.995。
    /// </summary>
    public static ChanSignal? SignalB1(
        MacdDivergence div, List<Bi> bis, List<Zhongshu> zsList)
    {
        var cIdx = bis.IndexOf(div.CBi);
        if (cIdx < 0 || cIdx + 1 >= bis.Count) return null;

        var confirm = bis[cIdx + 1];
        if (confirm.Type != Direction.Up) return null;

        return new ChanSignal(ChanSignalType.B1, confirm.DtStart, confirm.DtEnd,
            confirm.StartFx.Price,
            div.CBi.Low * 0.995,
            zsList.Count >= 2 ? 2 : 1,
            zsList[div.ZsIndex],
            $"一买: {div.ZsIndex + 1}中枢底背驰, C/A={div.Ratio:F2}");
    }

    /// <summary>
    /// 一卖 S1：趋势顶背驰成立 + C段之后出现反向向下笔确认。
    /// 入场 = 确认笔起点（顶分型价格），止损 = C段最高点 × 1.005。
    /// </summary>
    public static ChanSignal? SignalS1(
        MacdDivergence div, List<Bi> bis, List<Zhongshu> zsList)
    {
        var cIdx = bis.IndexOf(div.CBi);
        if (cIdx < 0 || cIdx + 1 >= bis.Count) return null;

        var confirm = bis[cIdx + 1];
        if (confirm.Type != Direction.Down) return null;

        return new ChanSignal(ChanSignalType.S1, confirm.DtStart, confirm.DtEnd,
            confirm.StartFx.Price,
            div.CBi.High * 1.005,
            zsList.Count >= 2 ? 2 : 1,
            zsList[div.ZsIndex],
            $"一卖: {div.ZsIndex + 1}中枢顶背驰, C/A={div.Ratio:F2}");
    }

    // ════════════════════════════════════════════════════════════
    // §6.3 二买 / 二卖（回抽确认）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 二买 B2：B1 后回抽不创新低 + 反向向上笔确认。
    /// 止损 = B1低点 × 0.995。
    /// </summary>
    public static ChanSignal? SignalB2(
        List<Bi> bis, List<Zhongshu> zsList, MacdDivergence b1Div)
    {
        if (b1Div == null) return null;
        var b1Idx = bis.IndexOf(b1Div.CBi);
        if (b1Idx < 0 || b1Idx + 3 >= bis.Count) return null;

        var upBi = bis[b1Idx + 1];       // B1 确认笔
        var pullback = bis[b1Idx + 2];   // 回抽笔
        if (upBi.Type != Direction.Up || pullback.Type != Direction.Down)
            return null;
        if (pullback.Low <= b1Div.CBi.Low)
            return null;                 // 创新低 → 不是二买

        var confirm = bis[b1Idx + 3];
        if (confirm.Type != Direction.Up) return null;

        var zs = zsList[b1Div.ZsIndex];
        string strength = pullback.Low > zs.Zg ? "强"
                        : pullback.Low >= zs.Zd ? "标准" : "弱";

        return new ChanSignal(ChanSignalType.B2, confirm.DtStart, confirm.DtEnd,
            confirm.StartFx.Price,
            b1Div.CBi.Low * 0.995,
            strength == "强" ? 3 : 2, zs,
            $"二买({strength}): 回抽不破B1低点");
    }

    /// <summary>
    /// 二卖 S2：S1 后反弹不创新高 + 反向向下笔确认。
    /// 止损 = S1高点 × 1.005。
    /// </summary>
    public static ChanSignal? SignalS2(
        List<Bi> bis, List<Zhongshu> zsList, MacdDivergence s1Div)
    {
        if (s1Div == null) return null;
        var s1Idx = bis.IndexOf(s1Div.CBi);
        if (s1Idx < 0 || s1Idx + 3 >= bis.Count) return null;

        var downBi = bis[s1Idx + 1];     // S1 确认笔
        var rally = bis[s1Idx + 2];      // 反弹笔
        if (downBi.Type != Direction.Down || rally.Type != Direction.Up)
            return null;
        if (rally.High >= s1Div.CBi.High)
            return null;                 // 创新高 → 不是二卖

        var confirm = bis[s1Idx + 3];
        if (confirm.Type != Direction.Down) return null;

        var zs = zsList[s1Div.ZsIndex];
        string strength = rally.High < zs.Zd ? "强"
                        : rally.High <= zs.Zg ? "标准" : "弱";

        return new ChanSignal(ChanSignalType.S2, confirm.DtStart, confirm.DtEnd,
            confirm.StartFx.Price,
            s1Div.CBi.High * 1.005,
            strength == "强" ? 3 : 2, zs,
            $"二卖({strength}): 反弹不破S1高点");
    }

    // ════════════════════════════════════════════════════════════
    // §6.4 三买 / 三卖（中枢突破回抽确认）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 三买 B3：向上离开中枢后回抽不入中枢（回抽低点 > ZG）+ 向上笔确认。
    /// 止损 = ZG × 0.998。
    /// </summary>
    public static List<ChanSignal> SignalB3(List<Bi> bis, List<Zhongshu> zsList)
    {
        var signals = new List<ChanSignal>();
        foreach (var zs in zsList)
        {
            var exitBi = FindExitBi(bis, zs, Direction.Up);
            if (exitBi == null || exitBi.Low <= zs.Zg) continue;

            int exitIdx = bis.IndexOf(exitBi);
            if (exitIdx < 0 || exitIdx + 2 >= bis.Count) continue;

            var pullback = bis[exitIdx + 1];
            if (pullback.Type != Direction.Down || pullback.Low <= zs.Zg) continue;

            var confirm = bis[exitIdx + 2];
            if (confirm.Type != Direction.Up) continue;

            double dist = (pullback.Low - zs.Zg) / zs.Zg * 10000;
            signals.Add(new ChanSignal(ChanSignalType.B3, confirm.DtStart, confirm.DtEnd,
                confirm.StartFx.Price, zs.Zg * 0.998,
                dist > 50 ? 3 : 2, zs,
                $"三买: 回抽距ZG {dist:F0}bp"));
        }
        return signals;
    }

    /// <summary>
    /// 三卖 S3：向下离开中枢后反弹不入中枢（反弹高点 < ZD）+ 向下笔确认。
    /// 止损 = ZD × 1.002。
    /// </summary>
    public static List<ChanSignal> SignalS3(List<Bi> bis, List<Zhongshu> zsList)
    {
        var signals = new List<ChanSignal>();
        foreach (var zs in zsList)
        {
            var exitBi = FindExitBi(bis, zs, Direction.Down);
            if (exitBi == null || exitBi.High >= zs.Zd) continue;

            int exitIdx = bis.IndexOf(exitBi);
            if (exitIdx < 0 || exitIdx + 2 >= bis.Count) continue;

            var rally = bis[exitIdx + 1];
            if (rally.Type != Direction.Up || rally.High >= zs.Zd) continue;

            var confirm = bis[exitIdx + 2];
            if (confirm.Type != Direction.Down) continue;

            double dist = (zs.Zd - rally.High) / zs.Zd * 10000;
            signals.Add(new ChanSignal(ChanSignalType.S3, confirm.DtStart, confirm.DtEnd,
                confirm.StartFx.Price, zs.Zd * 1.002,
                dist > 50 ? 3 : 2, zs,
                $"三卖: 反弹距ZD {dist:F0}bp"));
        }
        return signals;
    }

    // ════════════════════════════════════════════════════════════
    // §7.3 双级别联立
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 双级别联立：大级别方向明确 + 小级别出现背驰买卖点。
    /// 大级别 DOWN + 小级别底背驰 → 一买；大级别 UP + 小级别顶背驰 → 一卖。
    /// 联立信号置信度统一升为 3。
    /// </summary>
    public static List<ChanSignal> DualLevelSignal(
        List<ChanLunBar> barsBig, List<ChanLunBar> barsSmall)
    {
        var big = ChanLunAnalyzer.Analyze(barsBig);
        if (big.Trend is not ("UP" or "DOWN"))
            return [];

        var sm = ChanLunAnalyzer.Analyze(barsSmall);
        var div = CheckDivergence(sm.Bis, sm.Zhongshus, sm.StdBars);

        var signals = new List<ChanSignal>();
        foreach (var d in div)
        {
            ChanSignal? s = null;
            if (big.Trend == "DOWN" && d.Type == "BOTTOM_DIVERGENCE")
                s = SignalB1(d, sm.Bis, sm.Zhongshus);
            else if (big.Trend == "UP" && d.Type == "TOP_DIVERGENCE")
                s = SignalS1(d, sm.Bis, sm.Zhongshus);

            if (s != null)
                signals.Add(s with { Confidence = 3 });
        }
        return signals;
    }
}
