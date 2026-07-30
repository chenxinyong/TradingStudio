using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradingStudio.ToolBox.BarExportTool;

/// <summary>
/// Bar CSV 导出工具 — 将 DuckDB 中任意 Bar 表导出为 CSV，用于外部交叉验证或数据分析。
///
/// 用法:
///   ToolBox bar-export --instrument ag2612 --table bars_5min [--from 2026-01-01] [--to 2026-07-01] [--output ag2612_5min.csv]
///
/// 输出列:
///   bar_time, trading_day, open, high, low, close, volume, turnover, open_interest, tick_count
/// 价格默认以小数输出（÷10^7），可通过 --price-scale raw 输出原始 BIGINT 值。
/// </summary>
public class BarExportTool : IToolCommand
{
    public string Name => "bar-export";
    public string? Alias => "be";
    public string Description => "导出 Bar 数据为 CSV（用于交叉验证或外部分析）";

    // Price scale from TickRecord
    private const long PriceScale = 10_000_000;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // No DI services needed — we use DuckDB directly
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        // Parse arguments
        var dbPath = ResolveDbPath("data/bars_history.duckdb");
        var instrument = "";
        var table = "bars_1min";
        var from = "";
        var to = "";
        var output = "";
        var priceScale = "decimal"; // decimal | raw

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" or "-d" when i + 1 < args.Length:
                    dbPath = ResolveDbPath(args[++i]);
                    break;
                case "--instrument" or "-i" when i + 1 < args.Length:
                    instrument = args[++i];
                    break;
                case "--table" or "-t" when i + 1 < args.Length:
                    table = args[++i];
                    break;
                case "--from" or "-f" when i + 1 < args.Length:
                    from = args[++i];
                    break;
                case "--to" when i + 1 < args.Length:
                    to = args[++i];
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--price-scale" or "-ps" when i + 1 < args.Length:
                    priceScale = args[++i].ToLowerInvariant();
                    break;
            }
        }

        // Validate
        if (string.IsNullOrEmpty(instrument))
        {
            Console.Error.WriteLine("Error: --instrument is required");
            PrintUsage();
            return 1;
        }

        // Validate table name (prevent SQL injection)
        var validTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bars_1min", "bars_5min", "bars_15min", "bars_day", "bars_week", "ticks_recent"
        };
        if (!validTables.Contains(table))
        {
            Console.Error.WriteLine($"Error: Unknown table '{table}'. Valid: {string.Join(", ", validTables.OrderBy(x => x))}");
            return 1;
        }

        // Validate price scale
        if (priceScale is not ("decimal" or "raw"))
        {
            Console.Error.WriteLine("Error: --price-scale must be 'decimal' or 'raw'");
            return 1;
        }

        // Default output path
        if (string.IsNullOrEmpty(output))
        {
            var safeInst = instrument.Replace('%', '_').Replace('*', '_');
            output = $"{safeInst}_{table}.csv";
        }
        if (!Path.IsPathRooted(output))
            output = Path.GetFullPath(output);

        // Verify DB exists
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Error: Database not found: {dbPath}");
            return 1;
        }

        // Build query
        var useProductMode = !instrument.Any(char.IsDigit); // e.g. "ag" matches ag2608, ag2612...
        var whereClauses = new List<string>();
        var parameters = new Dictionary<string, object>();

        if (useProductMode)
        {
            whereClauses.Add($"instrument_id ILIKE '{instrument}%'");
        }
        else
        {
            whereClauses.Add($"instrument_id ILIKE '{instrument}'");
        }

        if (!string.IsNullOrEmpty(from))
        {
            whereClauses.Add($"bar_time >= '{from}'");
        }

        if (!string.IsNullOrEmpty(to))
        {
            // If only date (no time), include the full day
            var toValue = to.Length <= 10 ? $"{to} 23:59:59" : to;
            whereClauses.Add($"bar_time <= '{toValue}'");
        }

        var whereClause = string.Join(" AND ", whereClauses);

        // ──── Export ────
        Console.WriteLine($"Exporting: {instrument} from {table}");
        Console.WriteLine($"  DB:   {dbPath}");
        Console.WriteLine($"  Mode: {(useProductMode ? "product (LIKE)" : "exact match")}");
        Console.WriteLine($"  Price: {priceScale}");

        using var duck = new DuckDBConnection($"Data Source={dbPath}");
        await duck.OpenAsync(ct);

        // Count first
        long totalCount = 0;
        using (var countCmd = duck.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {whereClause}";
            totalCount = (long)(await countCmd.ExecuteScalarAsync(ct))!;
        }

        if (totalCount == 0)
        {
            Console.WriteLine("  ⚠️  No bars found. Check instrument/table/date range.");
            return 0;
        }

        Console.WriteLine($"  Bars: {totalCount:N0}");

        // Write CSV
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir!);

        var rawMode = priceScale == "raw";
        using var queryCmd = duck.CreateCommand();
        queryCmd.CommandText = $@"
            SELECT instrument_id, bar_time, trading_day,
                   open, high, low, close, volume, turnover, open_interest, tick_count
            FROM {table}
            WHERE {whereClause}
            ORDER BY instrument_id, bar_time";

        var sb = new StringBuilder();
        // Header
        sb.AppendLine("instrument_id,bar_time,trading_day,open,high,low,close,volume,turnover,open_interest,tick_count");

        int written = 0;
        using var reader = (DuckDBDataReader)await queryCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var instId = reader.GetString(0);
            var barTime = ReadDateTime(reader, 1);
            var tradingDay = ReadDateOnly(reader, 2);

            if (rawMode)
            {
                sb.AppendLine($"{instId},{barTime:yyyy-MM-dd HH:mm:ss},{tradingDay:yyyy-MM-dd}," +
                              $"{reader.GetInt64(3)},{reader.GetInt64(4)},{reader.GetInt64(5)},{reader.GetInt64(6)}," +
                              $"{reader.GetInt64(7)},{reader.GetDouble(8)},{reader.GetDouble(9)},{reader.GetInt64(10)}");
            }
            else
            {
                sb.AppendLine($"{instId},{barTime:yyyy-MM-dd HH:mm:ss},{tradingDay:yyyy-MM-dd}," +
                              $"{(reader.GetInt64(3) / (double)PriceScale):F7},{(reader.GetInt64(4) / (double)PriceScale):F7}," +
                              $"{(reader.GetInt64(5) / (double)PriceScale):F7},{(reader.GetInt64(6) / (double)PriceScale):F7}," +
                              $"{reader.GetInt64(7)},{reader.GetDouble(8)},{reader.GetDouble(9)},{reader.GetInt64(10)}");
            }

            written++;

            // Progress every 100k bars
            if (written % 100_000 == 0)
                Console.Write($"\r  Writing... {written:N0} / {totalCount:N0}");
        }

        if (written >= 100_000)
            Console.WriteLine(); // newline after progress

        await File.WriteAllTextAsync(output, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);

        Console.WriteLine($"  Exported: {written:N0} rows → {output}");
        return 0;
    }

    // ──────────── helpers ────────────

    /// <summary>
    /// 读取 bar_time 列 — 兼容 VARCHAR (BuildPeriodsService 生成的表) 和 TIMESTAMP (bars_1min/bars_day)。
    /// </summary>
    private static DateTime ReadDateTime(DuckDBDataReader reader, int ordinal)
    {
        var type = reader.GetFieldType(ordinal);
        if (type == typeof(string))
            return DateTime.Parse(reader.GetString(ordinal));
        return reader.GetDateTime(ordinal);
    }

    /// <summary>
    /// 读取 trading_day 列 — 兼容 VARCHAR 和 DATE。
    /// </summary>
    private static DateOnly ReadDateOnly(DuckDBDataReader reader, int ordinal)
    {
        var type = reader.GetFieldType(ordinal);
        if (type == typeof(string))
            return DateOnly.Parse(reader.GetString(ordinal));
        return DateOnly.FromDateTime(reader.GetDateTime(ordinal));
    }

    /// <summary>向上查找 repo root 解决相对路径</summary>
    private static string ResolveDbPath(string dbPath)
    {
        if (Path.IsPathRooted(dbPath) && File.Exists(dbPath)) return dbPath;
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, dbPath));
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        return Path.GetFullPath(dbPath);
    }

    private void PrintUsage()
    {
        Console.WriteLine("ToolBox bar-export — 导出 Bar 数据为 CSV");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ToolBox bar-export --instrument <id> --table <table> [options]");
        Console.WriteLine();
        Console.WriteLine("参数:");
        Console.WriteLine("  --instrument, -i   合约代码 (如 ag2612) 或品种 (如 ag，匹配所有合约)");
        Console.WriteLine("  --table, -t        Bar 表名 (bars_1min/bars_5min/bars_15min/bars_day/bars_week)");
        Console.WriteLine("  --db, -d           DuckDB 路径 (默认 data/bars_history.duckdb)");
        Console.WriteLine("  --from, -f         起始时间 (可选, yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss)");
        Console.WriteLine("  --to               结束时间 (可选)");
        Console.WriteLine("  --output, -o       输出 CSV 路径 (默认 {instrument}_{table}.csv)");
        Console.WriteLine("  --price-scale,-ps  价格格式: decimal (÷10^7, 默认) 或 raw (BIGINT 原值)");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  ToolBox bar-export -i ag2612 -t bars_5min -f 2026-01-01 -o ag2612_5min.csv");
        Console.WriteLine("  ToolBox bar-export -i rb000 -t bars_day");
        Console.WriteLine("  ToolBox bar-export -i ag -t bars_1min -f 2026-06-15 --to 2026-06-20");
        Console.WriteLine();
        Console.WriteLine("输出列:");
        Console.WriteLine("  instrument_id, bar_time, trading_day, open, high, low, close,");
        Console.WriteLine("  volume, turnover, open_interest, tick_count");
    }
}
