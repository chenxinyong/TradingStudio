using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.ToolBox.ChanLunTool;

/// <summary>
/// 缠论 CSV 分析工具 — 从 CSV 文件加载K线数据，运行完整缠论分析管线。
/// 复用现有 ChanLunAnalyzer + DivergenceDetector + ChanLunChart。
///
/// 用法:
///   ToolBox chanlun-csv scripts/data/soda_ash_weekly.csv
///   ToolBox clcsv scripts/data/soda_ash_weekly.csv --bi-len 4 --chart
///   ToolBox clcsv data.csv --output json
///
/// CSV 格式:
///   Date,Open,High,Low,Close,Volume
///   2019-12-06,1580.0,1585.0,1546.0,1561.0,318030
/// </summary>
public class ChanLunCsvTool : IToolCommand
{
    public string Name => "chanlun-csv";
    public string? Alias => "clcsv";
    public string Description => "缠论分析(CSV数据源)——从CSV加载K线，输出分型/笔/中枢/背驰/走势分类";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // No extra services needed
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        var csvPath = args[0];
        var biLen = ChanLunConfig.MinBiLen;
        var outputFmt = "text";
        var generateChart = false;
        string? chartPath = null;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "--bi-len" or "-b" && i + 1 < args.Length)
                biLen = int.Parse(args[++i]);
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length)
                outputFmt = args[++i];
            else if (args[i] is "--chart" or "-c")
            {
                generateChart = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    chartPath = args[++i];
            }
        }

        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"CSV file not found: {csvPath}");
            return 1;
        }

        var log = sp.GetRequiredService<ILogger<ChanLunCsvTool>>();
        log.LogInformation("ChanLun CSV: {Path}, biLen={BiLen}", csvPath, biLen);

        // ── Step 1: Read CSV → List<ChanLunBar> ──
        var rawBars = LoadBarsFromCsv(csvPath);
        if (rawBars.Count == 0)
        {
            Console.Error.WriteLine($"No bars loaded from {csvPath}");
            return 1;
        }
        log.LogInformation("Loaded {Count} bars from CSV", rawBars.Count);

        // ── Step 2: Run ChanLun analysis ──
        var result = ChanLunAnalyzer.Analyze(rawBars, minBiLen: biLen);

        // ── Step 3: Detect divergence ──
        var divergences = DivergenceDetector.Detect(result.Bis);

        // ── Step 4: Generate report ──
        var symbol = Path.GetFileNameWithoutExtension(csvPath);
        var tf = DetectTimeframe(rawBars);

        switch (outputFmt)
        {
            case "json":
                await OutputJsonAsync(symbol, tf, result, divergences, ct);
                break;
            default:
                PrintReport(symbol, tf, result, divergences, rawBars);
                break;
        }

        // ── Step 5: Optional HTML chart ──
        if (generateChart)
        {
            chartPath ??= Path.ChangeExtension(csvPath, ".html");
            ChanLunChart.SaveHtml(result, symbol, chartPath,
                title: $"{symbol} — 缠论分析 ({tf})");
            Console.WriteLine($"Chart saved: {chartPath}");
        }

        return 0;
    }

    // ═══════════════════════════════════════════
    // CSV Loading
    // ═══════════════════════════════════════════

    private static List<ChanLunBar> LoadBarsFromCsv(string path)
    {
        var bars = new List<ChanLunBar>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 2) return bars;

        // Detect delimiter: comma or tab
        var header = lines[0];
        var sep = header.Contains('\t') ? '\t' : ',';

        // Parse header to find column indices
        var cols = header.Split(sep).Select(c => c.Trim().ToLowerInvariant()).ToList();
        int idxDate = cols.IndexOf("date");
        int idxOpen = cols.IndexOf("open");
        int idxHigh = cols.IndexOf("high");
        int idxLow = cols.IndexOf("low");
        int idxClose = cols.IndexOf("close");
        int idxVol = cols.IndexOf("volume");

        if (idxDate < 0 || idxOpen < 0 || idxHigh < 0 || idxLow < 0 || idxClose < 0)
        {
            Console.Error.WriteLine("CSV must contain columns: Date, Open, High, Low, Close");
            return bars;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(sep);
            if (parts.Length < 5) continue;

            if (!DateTime.TryParse(parts[idxDate], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                continue;
            if (!double.TryParse(parts[idxOpen], NumberStyles.Any, CultureInfo.InvariantCulture, out var open))
                continue;
            if (!double.TryParse(parts[idxHigh], NumberStyles.Any, CultureInfo.InvariantCulture, out var high))
                continue;
            if (!double.TryParse(parts[idxLow], NumberStyles.Any, CultureInfo.InvariantCulture, out var low))
                continue;
            if (!double.TryParse(parts[idxClose], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                continue;

            long vol = 0;
            if (idxVol >= 0 && idxVol < parts.Length)
                long.TryParse(parts[idxVol], NumberStyles.Any, CultureInfo.InvariantCulture, out vol);

            bars.Add(new ChanLunBar
            {
                Dt = dt,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = vol,
            });
        }

        return bars;
    }

    /// <summary>Detect approximate timeframe from bar spacing.</summary>
    private static string DetectTimeframe(List<ChanLunBar> bars)
    {
        if (bars.Count < 3) return "?";
        var avgDays = (bars[^1].Dt - bars[0].Dt).TotalDays / (bars.Count - 1);
        return avgDays switch
        {
            <= 1.0 / 24 + 0.01 => "1h",
            <= 1.0 / 6 + 0.01 => "4h",
            <= 1.1 => "1d",
            <= 8 => "1w",
            _ => $"{avgDays:F0}d"
        };
    }

    // ═══════════════════════════════════════════
    // Text Report (enhanced Python-style output)
    // ═══════════════════════════════════════════

    private static void PrintReport(string symbol, string tf, ChanLunResult r,
        List<DivergenceSignal> divs, List<ChanLunBar> rawBars)
    {
        var priceFmt = rawBars.Max(b => b.High) > 100 ? "F0" : "F2";

        Console.WriteLine();
        Console.WriteLine($"══════ 缠论分析报告 (CSV) ══════");
        Console.WriteLine($"文件: {symbol}  周期: ~{tf}  K线: {r.RawCount}(原始) → {r.StdCount}(标准)");
        Console.WriteLine($"分型: {r.FractalCount} (顶:{r.Fractals.Count(f => f.Type == FractalType.Top)} "
            + $"底:{r.Fractals.Count(f => f.Type == FractalType.Bottom)})");
        Console.WriteLine($"笔: {r.BiCount} (上升:{r.Bis.Count(b => b.Type == Direction.Up)} "
            + $"下降:{r.Bis.Count(b => b.Type == Direction.Down)})");
        Console.WriteLine($"中枢: {r.ZhongshuCount}  走势: {r.Trend}");
        Console.WriteLine();

        // ── 价格概况 ──
        Console.WriteLine("── 价格概况 ──");
        Console.WriteLine($"  区间: {rawBars.Min(b => b.Low).ToString(priceFmt)} ~ {rawBars.Max(b => b.High).ToString(priceFmt)}");
        Console.WriteLine($"  首根: {rawBars[0].Dt:yyyy-MM-dd}  O={rawBars[0].Open.ToString(priceFmt)}");
        Console.WriteLine($"  末根: {rawBars[^1].Dt:yyyy-MM-dd}  C={rawBars[^1].Close.ToString(priceFmt)}");
        Console.WriteLine();

        // ── 中枢 ──
        if (r.Zhongshus.Count > 0)
        {
            Console.WriteLine($"── 中枢（共 {r.ZhongshuCount}）──");
            Console.WriteLine($"{"#",-4} {"时间区间",-24} {"价格区间",-16} {"宽度",-8} {"笔数"}");
            Console.WriteLine(new string('─', 70));
            for (int i = 0; i < r.Zhongshus.Count; i++)
            {
                var zs = r.Zhongshus[i];
                var zsBars = r.StdBars;
                var sd = zsBars.Count > 0 ? zsBars[0].Dt : DateTime.MinValue;
                var ed = zsBars.Count > 0 ? zsBars[^1].Dt : DateTime.MinValue;
                // Map bi indices to bar dates
                var biStart = zs.StartBiIdx < r.Bis.Count ? r.Bis[zs.StartBiIdx] : null;
                var biEnd = zs.EndBiIdx < r.Bis.Count ? r.Bis[zs.EndBiIdx] : null;
                var sdStr = biStart?.DtStart.ToString("yyyy-MM-dd") ?? "?";
                var edStr = biEnd?.DtEnd.ToString("yyyy-MM-dd") ?? "?";
                var pctWidth = 100.0 * (zs.Zg - zs.Zd) / zs.Zd;
                var biCount = zs.EndBiIdx - zs.StartBiIdx + 1;
                Console.WriteLine($"  Z{i + 1,-2} {sdStr} ~ {edStr}  "
                    + $"{zs.Zd.ToString(priceFmt)}-{zs.Zg.ToString(priceFmt)}  "
                    + $"{pctWidth,5:F1}%  {biCount}");
            }
            Console.WriteLine();
        }

        // ── 背驰信号 ──
        if (divs.Count > 0)
        {
            Console.WriteLine($"── 背驰信号（共 {divs.Count}）──");
            foreach (var d in divs)
            {
                var isBuy = d.CurrBi.Type == Direction.Down; // 底背驰=买
                var signalType = isBuy ? "底背驰 (1买)" : "顶背驰 (1卖)";
                Console.WriteLine($"  {d.LevelLabel} {signalType}");
                Console.WriteLine($"    前笔: {d.PrevBi.DtStart:yyyy-MM-dd}→{d.PrevBi.DtEnd:yyyy-MM-dd} "
                    + $"{d.PrevBi.ChangePct:+.00}% (力度:{d.PrevBi.Power:F0})");
                Console.WriteLine($"    后笔: {d.CurrBi.DtStart:yyyy-MM-dd}→{d.CurrBi.DtEnd:yyyy-MM-dd} "
                    + $"{d.CurrBi.ChangePct:+.00}% (力度:{d.CurrBi.Power:F0})");
                Console.WriteLine($"    满足: 价格{(d.PriceDivergence ? "✓" : "✗")} "
                    + $"力度{(d.PowerDivergence ? "✓" : "✗")} "
                    + $"速度{(d.SpeedDivergence ? "✓" : "✗")} "
                    + $"幅度{(d.RangeDivergence ? "✓" : "✗")} "
                    + $"(得分:{d.Score}/4)");
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("── 背驰 ──");
            Console.WriteLine("  无背驰信号");
            Console.WriteLine();
        }

        // ── 笔详情 ──
        if (r.Bis.Count > 0)
        {
            Console.WriteLine($"── 笔详情（共 {r.BiCount}）──");
            Console.WriteLine($"{"#",-4} {"方向",-6} {"起点",-12} {"终点",-12} {"涨跌幅",-8} {"K线数",-6}");
            Console.WriteLine(new string('─', 55));
            var showBis = r.Bis.TakeLast(20).ToList();
            var startIdx = r.Bis.Count - showBis.Count;
            for (int i = 0; i < showBis.Count; i++)
            {
                var bi = showBis[i];
                var dir = bi.Type == Direction.Up ? "↑" : "↓";
                Console.WriteLine($"  {startIdx + i + 1,-3} {dir,-5} "
                    + $"{bi.StartFx.Dt:yyyy-MM-dd}  {bi.EndFx.Dt:yyyy-MM-dd}  "
                    + $"{bi.ChangePct,7:F2}%  {bi.BarCount,-5}");
            }
            if (r.Bis.Count > 20)
                Console.WriteLine($"  ... 仅显示最近 20 笔");
            Console.WriteLine();
        }

        // ── 当前状态研判 ──
        Console.WriteLine("── 当前状态研判 ──");
        if (r.Bis.Count > 0)
        {
            var lastBi = r.Bis[^1];
            var dirStr = lastBi.Type == Direction.Up ? "上升" : "下降";
            Console.WriteLine($"  最后一笔: {dirStr}笔 ({lastBi.DtStart:yyyy-MM-dd} → {lastBi.DtEnd:yyyy-MM-dd})");
            Console.WriteLine($"            {lastBi.StartFx.Price.ToString(priceFmt)} → "
                + $"{lastBi.EndFx.Price.ToString(priceFmt)} "
                + $"({lastBi.ChangePct:+.00}%)");
        }
        var lastBar = rawBars[^1];
        Console.WriteLine($"  最新收盘: {lastBar.Dt:yyyy-MM-dd} = {lastBar.Close.ToString(priceFmt)}");

        if (r.Zhongshus.Count > 0)
        {
            var lastZs = r.Zhongshus[^1];
            if (lastBar.Close > lastZs.Zg)
            {
                var abovePct = 100 * (lastBar.Close / lastZs.Zg - 1);
                Console.WriteLine($"  位于 Z{r.ZhongshuCount} 上方 +{abovePct:F1}% "
                    + $"(中枢: {lastZs.Zd.ToString(priceFmt)}-{lastZs.Zg.ToString(priceFmt)})");
                Console.WriteLine($"  → 中枢上方运行, 关注三买机会 (回踩不破 ZG={lastZs.Zg.ToString(priceFmt)})");
            }
            else if (lastBar.Close < lastZs.Zd)
            {
                var belowPct = 100 * (1 - lastBar.Close / lastZs.Zd);
                Console.WriteLine($"  位于 Z{r.ZhongshuCount} 下方 -{belowPct:F1}% "
                    + $"(中枢: {lastZs.Zd.ToString(priceFmt)}-{lastZs.Zg.ToString(priceFmt)})");
                Console.WriteLine($"  → 中枢下方运行, 关注底背驰买点");
            }
            else
            {
                Console.WriteLine($"  位于 Z{r.ZhongshuCount} 内部 "
                    + $"(中枢: {lastZs.Zd.ToString(priceFmt)}-{lastZs.Zg.ToString(priceFmt)})");
                Console.WriteLine($"  → 中枢震荡, 等待方向选择");
            }
        }

        // ── 背驰总结 ──
        if (divs.Count > 0)
        {
            var lastDiv = divs[^1];
            var isBuy = lastDiv.CurrBi.Type == Direction.Down;
            var signalLabel = isBuy ? "底背驰(买点)" : "顶背驰(卖点)";
            Console.WriteLine($"  最近背驰: {lastDiv.LevelLabel} {signalLabel} "
                + $"@ {lastDiv.CurrBi.DtEnd:yyyy-MM-dd} (得分:{lastDiv.Score}/4)");
        }

        Console.WriteLine();
    }

    // ═══════════════════════════════════════════
    // JSON output
    // ═══════════════════════════════════════════

    private static async Task OutputJsonAsync(
        string symbol, string tf, ChanLunResult r, List<DivergenceSignal> divs, CancellationToken ct)
    {
        var output = new
        {
            symbol,
            timeframe = tf,
            bars = r.RawCount,
            fractals = r.FractalCount,
            bi_count = r.BiCount,
            zhongshu_count = r.ZhongshuCount,
            trend = r.Trend,
            last_bi_type = r.Bis.Count > 0 ? (r.Bis[^1].Type == Direction.Up ? "UP" : "DOWN") : null,
            bis = r.Bis.Select(b => new
            {
                type = b.Type == Direction.Up ? "UP" : "DOWN",
                start = b.DtStart.ToString("yyyy-MM-dd"),
                end = b.DtEnd.ToString("yyyy-MM-dd"),
                start_price = b.StartFx.Price,
                end_price = b.EndFx.Price,
                change_pct = Math.Round(b.ChangePct, 2),
                bar_count = b.BarCount,
                low = b.Low,
                high = b.High,
            }),
            zhongshus = r.Zhongshus.Select(z => new
            {
                zg = z.Zg,
                zd = z.Zd,
                zz = z.Zz,
                range = z.Zg - z.Zd,
            }),
            divergences = divs.Select(d => new
            {
                level = d.LevelLabel,
                type = d.DirectionLabel,
                score = d.Score,
                prev_change_pct = Math.Round(d.PrevBi.ChangePct, 2),
                curr_change_pct = Math.Round(d.CurrBi.ChangePct, 2),
                prev_speed = d.PrevSpeed,
                curr_speed = d.CurrSpeed,
                price_div = d.PriceDivergence,
                power_div = d.PowerDivergence,
                speed_div = d.SpeedDivergence,
                range_div = d.RangeDivergence,
            }),
        };

        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await Console.Out.WriteLineAsync(json);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ToolBox chanlun-csv — 缠论 CSV 分析工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ToolBox chanlun-csv <csv-path> [--bi-len 5] [--chart [path]] [--output json]");
        Console.WriteLine("  ToolBox clcsv <csv-path> [-b 4] [-c]");
        Console.WriteLine();
        Console.WriteLine("参数:");
        Console.WriteLine("  csv-path    CSV 文件路径 (列: Date,Open,High,Low,Close,Volume)");
        Console.WriteLine("  --bi-len,-b 最小笔长度 (默认 5, 周线建议 4-5)");
        Console.WriteLine("  --chart,-c  生成 HTML 交互图表 (默认输出到 csv 同目录)");
        Console.WriteLine("  --output,-o 输出格式: text (默认) | json");
        Console.WriteLine();
        Console.WriteLine("CSV 示例格式:");
        Console.WriteLine("  Date,Open,High,Low,Close,Volume");
        Console.WriteLine("  2019-12-06,1580.0,1585.0,1546.0,1561.0,318030");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  ToolBox clcsv scripts/data/soda_ash_weekly.csv");
        Console.WriteLine("  ToolBox clcsv data/silver_weekly.csv --bi-len 4 --chart");
        Console.WriteLine("  ToolBox clcsv data.csv --output json");
    }
}
