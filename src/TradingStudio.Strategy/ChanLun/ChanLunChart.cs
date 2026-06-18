using System.Text.Json;

namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 缠论可视化 — 直接生成 Plotly JSON → HTML (零依赖 Plotly.NET 的 F# API)。
/// 输出与 Python Plotly 完全相同的交互式图表。
/// </summary>
public static class ChanLunChart
{
    /// <summary>
    /// 生成缠论分析 HTML 图表 (单面板: K线+分型+笔+中枢)。
    /// </summary>
    public static string SaveHtml(
        ChanLunResult result,
        string instrument,
        string outputPath,
        string? title = null,
        int maxBis = 80,
        int maxZhongshus = 15)
    {
        var bars = result.StdBars;
        int n = bars.Count;
        var times = bars.Select(b => b.Dt).ToList();

        // 时间→索引映射
        int T2I(DateTime dt)
        {
            for (int i = 0; i < n; i++) if (times[i] >= dt) return i;
            return n - 1;
        }

        // 日期标签
        var tickVals = new List<int>();
        var tickTexts = new List<string>();
        string? prevDay = null;
        for (int i = 0; i < n; i++)
        {
            var dk = times[i].ToString("MM/dd");
            if (dk != prevDay) { tickVals.Add(i); tickTexts.Add(dk); prevDay = dk; }
        }

        var traces = new List<object>();

        // ── 1. K线 Candlestick ──
        traces.Add(new
        {
            type = "candlestick",
            name = "K线",
            x = Enumerable.Range(0, n).Cast<object>().ToArray(),
            open = bars.Select(b => (object)b.Open).ToArray(),
            high = bars.Select(b => (object)b.High).ToArray(),
            low = bars.Select(b => (object)b.Low).ToArray(),
            close = bars.Select(b => (object)b.Close).ToArray(),
            increasing = new { line = new { color = "red" } },
            decreasing = new { line = new { color = "green" } },
            hovertext = bars.Select((b, i) =>
                $"{times[i]:yyyy-MM-dd HH:mm}<br>O:{b.Open:F1} H:{b.High:F1} L:{b.Low:F1} C:{b.Close:F1}").ToArray(),
            hoverinfo = "text",
        });

        // ── 2. 顶分型 ──
        var topFx = result.Fractals.Where(f => f.Type == FractalType.Top).ToList();
        if (topFx.Count > 0)
        {
            traces.Add(new
            {
                type = "scatter",
                mode = "markers",
                name = $"顶分型({topFx.Count})",
                x = topFx.Select(f => (object)T2I(f.Dt)).ToArray(),
                y = topFx.Select(f => (object)f.Price).ToArray(),
                marker = new { symbol = "triangle-down", size = 8, color = "red", line = new { width = 1, color = "darkred" } },
                hovertext = topFx.Select(f => $"{f.Dt:MM/dd HH:mm} {f.Price:F1}").ToArray(),
                hoverinfo = "text",
            });
        }

        // ── 3. 底分型 ──
        var botFx = result.Fractals.Where(f => f.Type == FractalType.Bottom).ToList();
        if (botFx.Count > 0)
        {
            traces.Add(new
            {
                type = "scatter",
                mode = "markers",
                name = $"底分型({botFx.Count})",
                x = botFx.Select(f => (object)T2I(f.Dt)).ToArray(),
                y = botFx.Select(f => (object)f.Price).ToArray(),
                marker = new { symbol = "triangle-up", size = 8, color = "green", line = new { width = 1, color = "darkgreen" } },
                hovertext = botFx.Select(f => $"{f.Dt:MM/dd HH:mm} {f.Price:F1}").ToArray(),
                hoverinfo = "text",
            });
        }

        // ── 4. 笔 ──
        foreach (var bi in result.Bis.Take(maxBis))
        {
            var color = bi.Type == Direction.Up ? "blue" : "orange";
            traces.Add(new
            {
                type = "scatter",
                mode = "lines+markers",
                name = "",
                showlegend = false,
                x = new object[] { T2I(bi.DtStart), T2I(bi.DtEnd) },
                y = new object[] { bi.StartFx.Price, bi.EndFx.Price },
                line = new { color, width = 2 },
                marker = new { size = 4, color },
                hovertext = $"power={bi.Power:F1} len={bi.BarCount}K {(bi.Type == Direction.Up ? "UP" : "DN")}",
                hoverinfo = "text",
            });
        }

        // ── 5. 中枢 ──
        foreach (var (zs, zi) in result.Zhongshus.Take(maxZhongshus).Select((z, i) => (z, i)))
        {
            if (zs.DtStart == null || zs.DtEnd == null) continue;
            double x0 = T2I(zs.DtStart.Value);
            double x1 = T2I(zs.DtEnd.Value);
            traces.Add(new
            {
                type = "scatter",
                mode = "lines",
                name = zi == 0 ? "中枢" : "",
                showlegend = zi == 0,
                x = new object[] { x0, x1, x1, x0, x0 },
                y = new object[] { zs.Zd, zs.Zd, zs.Zg, zs.Zg, zs.Zd },
                fill = "toself",
                fillcolor = "rgba(128,0,128,0.06)",
                line = new { color = "purple", width = 1, dash = "dot" },
                hovertext = $"[{zs.Zd:F1}, {zs.Zg:F1}] zz={zs.Zz:F1}",
                hoverinfo = "text",
            });
        }

        // ── Layout ──
        var upCount = result.Bis.Count(b => b.Type == Direction.Up);
        var dnCount = result.Bis.Count(b => b.Type == Direction.Down);
        var chartTitle = title ?? $"{instrument} 缠论分析 (MIN_BI_LEN=5)";

        var layout = new
        {
            title = new
            {
                text = $"{chartTitle}<br><sup>{result.RawCount}根→标准{result.StdCount}根→分型{result.FractalCount}→笔{result.BiCount}(UP={upCount} DN={dnCount})→中枢{result.ZhongshuCount}→走势:{result.Trend}</sup>",
                font = new { size = 14 },
            },
            xaxis = new
            {
                title = "日期",
                tickmode = "array",
                tickvals = tickVals.Cast<object>().ToArray(),
                ticktext = tickTexts.ToArray(),
                showgrid = true,
                rangeslider = new { visible = true, thickness = 0.05 },
            },
            yaxis = new { title = "价格" },
            height = 750,
            hovermode = "x unified",
            template = "plotly_white",
            margin = new { l = 60, r = 30, t = 80, b = 60 },
        };

        var chartData = new { data = traces, layout };

        var json = JsonSerializer.Serialize(chartData, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });

        var html = $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8"">
<script src=""https://cdn.plot.ly/plotly-2.35.2.min.js""></script>
</head><body>
<div id=""chart"" style=""width:100%;height:100vh;""></div>
<script>
var data = {json};
Plotly.newPlot('chart', data.data, data.layout, {{responsive: true, displaylogo: false}});
</script></body></html>";

        File.WriteAllText(outputPath, html);
        return outputPath;
    }

    /// <summary>
    /// 快捷方法: 分析并保存 HTML 图表。
    /// </summary>
    public static string AnalyzeAndSave(
        List<ChanLunBar> bars,
        string instrument,
        string outputPath,
        int minBiLen = 5)
    {
        var result = ChanLunAnalyzer.Analyze(bars, minBiLen: minBiLen);
        return SaveHtml(result, instrument, outputPath);
    }
}
