using System.Text;

namespace ChanAnalysis;

/// <summary>把分析结果格式化为终端文本或 Markdown。</summary>
public static class ReportGenerator
{
    /// <summary>
    /// 生成 Markdown 格式（用于追加到每日日志）。
    /// weeklyMinGap：指数=4，期货=2；includeDaily：期货含日线，指数仅周线。
    /// </summary>
    public static string BuildMarkdown(string symbol, string name, List<Bar> daily,
        int weeklyMinGap = 2, bool includeDaily = true)
    {
        var sb = new StringBuilder();
        var weeklyBars = TimeFrame.ToWeekly(daily);
        var w = ChanEngine.Analyze(weeklyBars, weeklyMinGap);

        var last = daily[^1];
        sb.AppendLine($"#### {name} {symbol}");
        sb.AppendLine();
        sb.AppendLine($"- 数据：{daily[0].Date:yyyy-MM-dd} ~ {last.Date:yyyy-MM-dd}（{weeklyBars.Count}根周K）");
        sb.AppendLine($"- 现价：**{last.Close:F0}**");

        AppendMarkdownTimeFrame(sb, "周线", weeklyBars, w, last.Close, weeklyMinGap);

        if (includeDaily)
        {
            var d = ChanEngine.Analyze(daily, 2);
            AppendMarkdownTimeFrame(sb, "日线", daily, d, last.Close, 2);
        }
        return sb.ToString();
    }

    private static void AppendMarkdownTimeFrame(StringBuilder sb, string label,
        List<Bar> bars, AnalysisResult result, double price, int minGap)
    {
        sb.AppendLine();
        sb.AppendLine($"**{label}**：{bars.Count}->{result.MergedBars.Count}根，笔{result.Bis.Count}，中枢{result.Zhongshus.Count}");
        sb.AppendLine();

        // 最近几笔
        sb.AppendLine("| 笔 | 方向 | 区间 | 幅度 |");
        sb.AppendLine("|----|------|------|------|");
        int total = result.Bis.Count;
        foreach (var (bi, idx) in result.Bis.Select((b, i) => (b, i)).TakeLast(4))
        {
            var s = result.MergedBars[bi.StartIndex];
            var e = result.MergedBars[bi.EndIndex];
            string dir = bi.StartType == FractalType.Top ? "↓跌" : "↑涨";
            double sp = bi.StartType == FractalType.Top ? s.High : s.Low;
            double ep = bi.EndType == FractalType.Bottom ? e.Low : e.High;
            double pct = (ep - sp) / sp * 100;
            sb.AppendLine($"| Bi{idx + 1}/{total} | {dir} | {s.Date:MM-dd}({sp:F0}) → {e.Date:MM-dd}({ep:F0}) | {pct:+0.0;-0.0}% |");
        }

        // 中枢（最近 2 个）
        if (result.Zhongshus.Count > 0)
        {
            var zs = result.Zhongshus[^1];
            sb.AppendLine();
            sb.AppendLine($"- 最近中枢：**[{zs.Lower:F0} ~ {zs.Upper:F0}]**");

            string locText = price > zs.Upper ? "三买区（中枢上方）" :
                             price < zs.Lower ? $"三卖区（中枢下方 {zs.Lower - price:F0}）" :
                             "中枢震荡";
            sb.AppendLine($"- 走势形态：**{locText}**");
        }

        // 背驰
        var div = ChanEngine.CheckDivergence(result);
        if (div.IsDivergent)
            sb.AppendLine($"- 背驰：**潜在{div.Kind}**（{div.CurrentAmplitude:F0} &lt; {div.PreviousAmplitude:F0}，-{100 - div.Ratio * 100:F0}%）");
        else if (div.Kind != "无")
            sb.AppendLine($"- 背驰：暂未（{div.CurrentAmplitude:F0} vs {div.PreviousAmplitude:F0}）");

        // 最近分型
        if (result.BottomFractals.Count > 0)
        {
            var b = result.MergedBars[result.BottomFractals[^1]];
            sb.AppendLine($"- 最近底分型：{b.Date:MM-dd} L={b.Low:F0}");
        }
        if (result.TopFractals.Count > 0)
        {
            var t = result.MergedBars[result.TopFractals[^1]];
            sb.AppendLine($"- 最近顶分型：{t.Date:MM-dd} H={t.High:F0}");
        }
    }


    /// <summary>生成完整报告。</summary>
    public static string BuildReport(string symbol, string name, List<Bar> daily)
    {
        var sb = new StringBuilder();
        var weeklyBars = TimeFrame.ToWeekly(daily);
        var monthlyBars = TimeFrame.ToMonthly(daily);

        var m = ChanEngine.Analyze(monthlyBars, 2);
        var w = ChanEngine.Analyze(weeklyBars, 2);
        var d = ChanEngine.Analyze(daily, 2);

        var last = daily[^1];
        double high = daily.Max(k => k.High);
        double low = daily.Min(k => k.Low);

        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"  {symbol} {name}  周线+月线+日线 缠论分析");
        sb.AppendLine($"  数据: {daily[0].Date:yyyy-MM-dd} ~ {last.Date:yyyy-MM-dd} " +
                      $"(月{monthlyBars.Count}根 周{weeklyBars.Count}根 日{daily.Count}根)");
        sb.AppendLine($"  历史区间: {low:F0} ~ {high:F0}");
        sb.AppendLine($"  今日: O{last.Open:F0} H{last.High:F0} L{last.Low:F0} C{last.Close:F0}  持仓{last.OpenInterest}");
        sb.Append("  最近5日收盘: ");
        foreach (var k in daily.TakeLast(5))
            sb.Append($"{k.Date:MM-dd}({k.Close:F0}) ");
        sb.AppendLine();

        // ---- 月线 ----
        AppendTimeFrame(sb, "月线", monthlyBars, m, last.Close, 2);

        // ---- 周线 ----
        AppendTimeFrame(sb, "周线", weeklyBars, w, last.Close, 5);

        // ---- 日线 ----
        AppendTimeFrame(sb, "日线", daily, d, last.Close, 5);

        // ---- 联立 ----
        var wLoc = ChanEngine.Locate(last.Close, w);
        var dLoc = ChanEngine.Locate(last.Close, d);
        sb.AppendLine();
        sb.AppendLine($"[联立] 周:{LocText(wLoc)} | 日:{LocText(dLoc)}");

        // ---- 均线 ----
        double ma5 = daily.TakeLast(5).Average(k => k.Close);
        double ma20 = daily.TakeLast(20).Average(k => k.Close);
        sb.AppendLine($"[均线] MA5={ma5:F0} MA20={ma20:F0}  当前{(last.Close > ma20 ? "在MA20上方" : "在MA20下方")}");

        // ---- 近期区间 ----
        var recent = daily.TakeLast(20).ToList();
        double rHigh = recent.Max(k => k.High);
        double rLow = recent.Min(k => k.Low);
        sb.AppendLine($"[近期] 20日: {rLow:F0}~{rHigh:F0}  当前{last.Close:F0}  " +
                      $"距低+{(last.Close - rLow) / rLow * 100:F1}%  距高-{(rHigh - last.Close) / last.Close * 100:F1}%");

        long oiDiff = daily.Count >= 2 ? last.OpenInterest - daily[^2].OpenInterest : 0;
        sb.AppendLine($"[持仓] {last.OpenInterest} ({oiDiff:+0;-0;0})");

        return sb.ToString();
    }

    private static void AppendTimeFrame(StringBuilder sb, string label,
        List<Bar> bars, AnalysisResult result, double price, int recentBis)
    {
        sb.AppendLine();
        sb.AppendLine($"[{label}] {bars.Count}->{result.MergedBars.Count}根  " +
                      $"笔:{result.Bis.Count}  中枢:{result.Zhongshus.Count}");

        // 最近几笔
        foreach (var bi in result.Bis.TakeLast(recentBis))
        {
            var sb2 = result.MergedBars[bi.StartIndex];
            var eb = result.MergedBars[bi.EndIndex];
            string dir = bi.StartType == FractalType.Top ? "down" : "up  ";
            double sp = bi.StartType == FractalType.Top ? sb2.High : sb2.Low;
            double ep = bi.EndType == FractalType.Bottom ? eb.Low : eb.High;
            double pct = (ep - sp) / sp * 100;
            sb.AppendLine($"  {dir} {sb2.Date:MM-dd}({sp:F0}) -> {eb.Date:MM-dd}({ep:F0})  {pct:+0.0;-0.0;0.0}%");
        }

        // 中枢（只显示最近 3 个，日线中枢过多时避免刷屏）
        foreach (var (zs, i) in result.Zhongshus.TakeLast(3).Select((z, i) => (z, i)))
        {
            int n = result.Zhongshus.Count - Math.Min(result.Zhongshus.Count, 3) + i + 1;
            string fl = zs.Lower <= price && price <= zs.Upper ? "IN" : (price > zs.Upper ? "ABV" : "BLW");
            sb.AppendLine($"  ZS{n}: [{zs.Lower:F0} ~ {zs.Upper:F0}]  {zs.StartDate:MM-dd}~{zs.EndDate:MM-dd}  {fl}");
        }
        if (result.Zhongshus.Count > 3)
            sb.AppendLine($"  ... 共 {result.Zhongshus.Count} 个中枢，仅显示最近 3 个");

        // 背驰
        var div = ChanEngine.CheckDivergence(result);
        if (div.IsDivergent)
            sb.AppendLine($"  ** 潜在{div.Kind} ** {div.CurrentAmplitude:F0} < {div.PreviousAmplitude:F0} " +
                          $"(-{100 - div.Ratio * 100:F0}%)");
        else if (div.Kind != "无")
            sb.AppendLine($"  暂未背驰 ({div.CurrentAmplitude:F0} vs {div.PreviousAmplitude:F0})");

        // 最近分型
        if (result.BottomFractals.Count > 0)
        {
            var b = result.MergedBars[result.BottomFractals[^1]];
            sb.AppendLine($"  最近底分型: {b.Date:MM-dd} L={b.Low:F0}");
        }
        if (result.TopFractals.Count > 0)
        {
            var t = result.MergedBars[result.TopFractals[^1]];
            sb.AppendLine($"  最近顶分型: {t.Date:MM-dd} H={t.High:F0}");
        }
    }

    private static string LocText(Location loc) => loc switch
    {
        Location.SanMaiAbove => "三买",
        Location.SanMaiBelow => "三卖",
        Location.ZhenDang => "震荡",
        _ => "无中枢"
    };
}
