using System.Globalization;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.ToolBox.ChanLunTool;

/// <summary>
/// 缠论分析工具 — 从 DuckDB 加载K线数据，运行完整缠论分析管线，
/// 输出分型/笔/中枢/背驰/走势分类报告。
///
/// 用法:
///   ToolBox chanlun ag000          [--tf 1d] [--db path] [--output json|md]
///   ToolBox chanlun ag2608 --tf 1h [--db path] [--bi-len 5]
///
/// 支持的周期 (--tf):
///   1min, 5min, 15min, 30min, 1h, 2h, 4h, 1d, 1w
/// </summary>
public class ChanLunAnalyzeTool : IToolCommand
{
    public string Name => "chanlun";
    public string? Alias => "cl";
    public string Description => "缠论分析——分型/笔/中枢/背驰/走势分类";

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

        var symbol = args[0];
        var tf = "1d";
        var dbPath = ResolveDbPath("data/bars_history.duckdb");
        var outputFmt = "text";
        var biLen = 5;
        var limit = 500;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "--tf" or "-t" && i + 1 < args.Length) tf = args[++i];
            else if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = ResolveDbPath(args[++i]);
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length) outputFmt = args[++i];
            else if (args[i] is "--bi-len" or "-b" && i + 1 < args.Length) biLen = int.Parse(args[++i]);
            else if (args[i] is "--limit" or "-l" && i + 1 < args.Length) limit = int.Parse(args[++i]);
        }

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Database not found: {dbPath}");
            return 1;
        }

        var log = sp.GetRequiredService<ILogger<ChanLunAnalyzeTool>>();
        log.LogInformation("ChanLun analyze: {Symbol} {Tf}, db={Db}", symbol, tf, dbPath);

        // ── Step 1: Load bars from DuckDB ──
        var table = TableName(tf);
        var rawBars = await LoadBarsAsync(dbPath, table, symbol, limit, ct);
        if (rawBars.Count == 0)
        {
            Console.Error.WriteLine($"No bars found for {symbol} in {table}");
            return 1;
        }

        log.LogInformation("Loaded {Count} bars from {Table}", rawBars.Count, table);

        // ── Step 2: Run ChanLun analysis ──
        var result = ChanLunAnalyzer.Analyze(rawBars, minBiLen: biLen);

        // ── Step 3: Detect divergence ──
        var divergences = DivergenceDetector.Detect(result.Bis);

        // ── Step 4: Generate report ──
        switch (outputFmt)
        {
            case "json":
                await OutputJsonAsync(symbol, tf, result, divergences, ct);
                break;
            default:
                PrintReport(symbol, tf, result, divergences);
                break;
        }

        return 0;
    }

    // ═══════════════════════════════════════════
    // Bar loading
    // ═══════════════════════════════════════════

    private static async Task<List<ChanLunBar>> LoadBarsAsync(
        string dbPath, string table, string symbol, int limit, CancellationToken ct)
    {
        var bars = new List<ChanLunBar>();

        await using var conn = new DuckDBConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT bar_time, open, high, low, close, volume
            FROM {table}
            WHERE instrument_id = $symbol
            ORDER BY bar_time
            LIMIT $limit
        ";
        cmd.Parameters.Add(new DuckDBParameter("symbol", symbol));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            bars.Add(new ChanLunBar
            {
                Dt = ReadBarTime(reader, 0),
                Open = reader.GetInt64(1) / 10_000_000.0,
                High = reader.GetInt64(2) / 10_000_000.0,
                Low = reader.GetInt64(3) / 10_000_000.0,
                Close = reader.GetInt64(4) / 10_000_000.0,
                Volume = reader.GetInt64(5),
            });
        }

        return bars;
    }

    /// <summary>Map human timeframe to DuckDB table name.</summary>
    private static string TableName(string tf) => tf.ToLowerInvariant() switch
    {
        "1min" => "bars_1min",
        "5min" => "bars_5min",
        "15min" => "bars_15min",
        "30min" => "bars_30min",
        "1h" or "60min" => "bars_hour",
        "2h" or "120min" => "bars_2hour",
        "4h" or "240min" => "bars_4hour",
        "1d" or "day" => "bars_day",
        "1w" or "week" => "bars_week",
        _ => "bars_day",
    };

    // ═══════════════════════════════════════════
    // Text report
    // ═══════════════════════════════════════════

    private static void PrintReport(string symbol, string tf, ChanLunResult r, List<DivergenceSignal> divs)
    {
        var priceFmt = "F2";

        Console.WriteLine();
        Console.WriteLine($"══ 缠论分析报告 ══");
        Console.WriteLine($"品种: {symbol,-8}  周期: {tf,-4}  K线: {r.RawCount}(原始) → {r.StdCount}(标准)");
        Console.WriteLine($"分型: {r.FractalCount}  笔: {r.BiCount}  中枢: {r.ZhongshuCount}  走势: {r.Trend}");
        Console.WriteLine();

        // ── 分型 ──
        if (r.Fractals.Count > 0)
        {
            Console.WriteLine("── 分型 ──");
            foreach (var f in r.Fractals.TakeLast(12))
            {
                var tag = f.Type == FractalType.Top ? "顶" : "底";
                Console.WriteLine($"  [{tag}] {f.Dt:yyyy-MM-dd}  {f.Price.ToString(priceFmt)}");
            }
            if (r.Fractals.Count > 12)
                Console.WriteLine($"  ... 共 {r.Fractals.Count} 个分型，仅显示最近 12 个");
            Console.WriteLine();
        }

        // ── 笔 ──
        if (r.Bis.Count > 0)
        {
            Console.WriteLine($"── 笔（共 {r.BiCount}）──");
            Console.WriteLine($"{"#",-4} {"方向",-6} {"起点",-12} {"终点",-12} {"涨跌幅",-8} {"力度",-8} {"K线数",-6} {"速度/天"}");
            Console.WriteLine(new string('─', 80));
            foreach (var bi in r.Bis.TakeLast(16))
            {
                var dir = bi.Type == Direction.Up ? "↑" : "↓";
                var days = Math.Max(1, (int)(bi.DtEnd - bi.DtStart).TotalDays);
                var speed = bi.ChangePct / days;
                Console.WriteLine($"  {dir,-5} {bi.StartFx.Dt:yyyy-MM-dd}  {bi.EndFx.Dt:yyyy-MM-dd}  {bi.ChangePct,7:F2}%  {bi.Power,7:F0}   {bi.BarCount,-5}  {speed,7:F4}%/d");
            }
            if (r.Bis.Count > 16)
                Console.WriteLine($"  ... 仅显示最近 16 笔");
            Console.WriteLine();
        }

        // ── 中枢 ──
        if (r.Zhongshus.Count > 0)
        {
            Console.WriteLine($"── 中枢（共 {r.ZhongshuCount}）──");
            Console.WriteLine($"{"#",-4} {"上沿",-10} {"下沿",-10} {"中轨",-10} {"区间",-10} {"笔范围"}");
            Console.WriteLine(new string('─', 70));
            for (int i = 0; i < r.Zhongshus.Count; i++)
            {
                var zs = r.Zhongshus[i];
                var range = zs.Zg - zs.Zd;
                Console.WriteLine($"  ZS{i + 1,-2} {zs.Zg.ToString(priceFmt),-10} {zs.Zd.ToString(priceFmt),-10} {zs.Zz.ToString(priceFmt),-10} {range.ToString(priceFmt),-10} 笔{zs.StartBiIdx}-{zs.EndBiIdx}");
            }
            Console.WriteLine();
        }

        // ── 背驰 ──
        if (divs.Count > 0)
        {
            Console.WriteLine($"── 背驰信号（共 {divs.Count}）──");
            foreach (var d in divs.TakeLast(4))
            {
                Console.WriteLine($"  {d.LevelLabel} | {d.DirectionLabel}");
                Console.WriteLine($"    前笔: {d.PrevBi.ChangePct:F2}% (速度 {d.PrevSpeed:F4}%/d)  {d.PrevBi.DtStart:yyyy-MM-dd}→{d.PrevBi.DtEnd:yyyy-MM-dd}");
                Console.WriteLine($"    后笔: {d.CurrBi.ChangePct:F2}% (速度 {d.CurrSpeed:F4}%/d)  {d.CurrBi.DtStart:yyyy-MM-dd}→{d.CurrBi.DtEnd:yyyy-MM-dd}");
                Console.WriteLine($"    满足: 价格{(d.PriceDivergence ? "✓" : "✗")} 力度{(d.PowerDivergence ? "✓" : "✗")} 速度{(d.SpeedDivergence ? "✓" : "✗")} 幅度{(d.RangeDivergence ? "✓" : "✗")}  (得分:{d.Score}/4)");
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("── 背驰 ──");
            Console.WriteLine("  无背驰信号（未出现满足 ≥2/4 条件的同向笔对）");
            Console.WriteLine();
        }

        // ── 仓位建议 ──
        Console.WriteLine("── 走势分类 ──");
        Console.WriteLine($"  方向: {r.Trend}");
        var lastBiType = r.Bis.Count > 0 ? (r.Bis[^1].Type == Direction.Up ? "上涨" : "下跌") : "—";
        Console.WriteLine($"  最后一笔: {lastBiType}");
        if (divs.Any(d => d.Level >= DivergenceLevel.Clear))
        {
            var lastDiv = divs.Last(d => d.Level >= DivergenceLevel.Clear);
            Console.WriteLine($"  ⚠ {lastDiv.LevelLabel}: {lastDiv.DirectionLabel}");
            Console.WriteLine($"  → 关注趋势反转可能");
        }
        Console.WriteLine();
    }

    // ═══════════════════════════════════════════
    // JSON output (for Mind module / scriptability)
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

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

    /// <summary>读取 bar_time 列，兼容 TIMESTAMP 和 VARCHAR 两种存储类型</summary>
    private static DateTime ReadBarTime(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.GetFieldType(ordinal) == typeof(string))
            return DateTime.Parse(reader.GetString(ordinal));
        return reader.GetDateTime(ordinal);
    }

    private static string ResolveDbPath(string dbPath)
    {
        if (Path.IsPathRooted(dbPath)) return dbPath;
        if (File.Exists(dbPath)) return Path.GetFullPath(dbPath);

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, dbPath));
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        return Path.GetFullPath(dbPath);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ToolBox chanlun — 缠论分析工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ToolBox chanlun <symbol> [--tf 1d|1w|4h|1h|15min|5min] [--db <path>] [--output json] [--bi-len 5] [--limit 500]");
        Console.WriteLine();
        Console.WriteLine("参数:");
        Console.WriteLine("  symbol     品种代码，如 ag000（连续）、ag2608（主力合约）、601600（股票）");
        Console.WriteLine("  --tf,-t    周期: 1min/5min/15min/30min/1h/2h/4h/1d/1w  (默认 1d)");
        Console.WriteLine("  --db,-d    DuckDB 路径 (默认 data/bars_history.duckdb)");
        Console.WriteLine("  --output,-o 输出格式: text (默认) | json (供 Mind 模块调用)");
        Console.WriteLine("  --bi-len,-b 最小笔长度 (默认 5)");
        Console.WriteLine("  --limit,-l  最大K线数 (默认 500)");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  ToolBox chanlun ag000 --tf 1d");
        Console.WriteLine("  ToolBox chanlun ag000 --tf 1d --output json");
        Console.WriteLine("  ToolBox chanlun ag2608 --tf 1h --bi-len 5");
        Console.WriteLine("  ToolBox chanlun 601600 --tf 1w");
    }
}
