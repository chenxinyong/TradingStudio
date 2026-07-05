using DuckDB.NET.Data;
using TradingStudio.Core.Models;
using TradingStudio.Core.Storage;

namespace TradingStudio.Data.Storage;

/// <summary>
/// 连续合约 Bar 存储 — 从 per-product 连续 DuckDB 文件读取，支持多品种。
///
/// 目录: data/continuous/v_continuous.duckdb, rb_continuous.duckdb, ...
/// 表映射: bars_1min → continuous_1min, bars_day → continuous_day
/// 换月: ratio back-adjust 已在构建时消除价差
///
/// 只读。不实现写入方法。
/// </summary>
public class ContinuousBarStore : IBarStore
{
    private readonly string _baseDir;

    public long WrittenCount => 0;

    /// <param name="baseDir">连续合约文件目录 (如 "data/continuous")</param>
    public ContinuousBarStore(string baseDir)
    {
        _baseDir = Path.GetFullPath(baseDir);
        if (!Directory.Exists(_baseDir))
            throw new DirectoryNotFoundException($"连续合约目录不存在: {_baseDir}");
    }

    // ═══ 写入 (不支持) ═══
    public ValueTask WriteAsync(Bar bar) => throw new NotSupportedException();
    public ValueTask WriteBatchAsync(IEnumerable<Bar> bars, CancellationToken ct = default) => throw new NotSupportedException();
    public Task BuildMultiPeriodTableAsync(int periodMinutes, CancellationToken ct = default) => Task.CompletedTask;

    // ═══ 查询 ═══

    public async Task<IReadOnlyList<Bar>> QueryBarsAsync(
        string instrumentId, DateTime start, DateTime end,
        string table = "bars_1min", CancellationToken ct = default)
    {
        var code = instrumentId.ToLowerInvariant();
        var dbPath = GetDbPath(code);
        if (!File.Exists(dbPath)) return Array.Empty<Bar>();

        var continuousTable = MapTableName(table);
        var bars = new List<Bar>();

        using var conn = OpenConnection(dbPath);
        using var cmd = conn.CreateCommand();
        // tick_count 列在某些连续表中不存在 (如 continuous_1min 有 adjustment_factor)
        var hasTickCount = TableHasColumn(conn, continuousTable, "tick_count");
        var tickCol = hasTickCount ? "COALESCE(tick_count, 0)" : "0";

        cmd.CommandText = $@"
            SELECT trading_day, bar_time, open, high, low, close, volume, {tickCol} as tc
            FROM {continuousTable}
            WHERE bar_time >= $start AND bar_time <= $end
            ORDER BY bar_time";
        cmd.Parameters.Add(new DuckDBParameter("start", start.ToString("yyyy-MM-dd HH:mm:ss")));
        cmd.Parameters.Add(new DuckDBParameter("end", end.ToString("yyyy-MM-dd HH:mm:ss")));

        using var reader = (DuckDBDataReader)await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var barTime = DateTime.Parse(reader.GetString(1));
            bars.Add(new Bar
            {
                InstrumentId = code,
                TradingDay = DateOnly.Parse(reader.GetString(0)),
                BarTime = barTime,
                Open = DoubleToPrice(reader.GetDouble(2)),
                High = DoubleToPrice(reader.GetDouble(3)),
                Low = DoubleToPrice(reader.GetDouble(4)),
                Close = DoubleToPrice(reader.GetDouble(5)),
                Volume = (long)reader.GetDouble(6),
                Turnover = 0,
                OpenInterest = 0,
                TickCount = reader.IsDBNull(7) ? 0 : Math.Max(0, (int)reader.GetInt64(7)),
            });
        }
        return bars;
    }

    public Task<IReadOnlyList<string>> QueryInstrumentsAsync(
        string table = "bars_1min", CancellationToken ct = default)
    {
        // 扫描目录发现所有可用品种
        var products = new List<string>();
        if (Directory.Exists(_baseDir))
        {
            foreach (var f in Directory.GetFiles(_baseDir, "*_continuous.duckdb"))
            {
                var name = Path.GetFileNameWithoutExtension(f);    // "v_continuous"
                var code = name.Replace("_continuous", "");        // "v"
                products.Add(code);
            }
        }
        return Task.FromResult<IReadOnlyList<string>>(products);
    }

    // ═══ 内部 ═══

    private string GetDbPath(string productCode)
        => Path.Combine(_baseDir, $"{productCode.ToLowerInvariant()}_continuous.duckdb");

    private static DuckDBConnection OpenConnection(string path)
    {
        var conn = new DuckDBConnection($"Data Source={path}");
        conn.Open();
        return conn;
    }

    private static string MapTableName(string table) => table switch
    {
        "bars_day" => "continuous_day",
        "bars_1min" => "continuous_1min",
        "bars_5min" => "continuous_5min",
        "bars_15min" => "continuous_15min",
        _ => table.StartsWith("bars_") && table.EndsWith("min")
            ? "continuous_" + table["bars_".Length..]
            : table.Replace("bars_", "continuous_")
    };

    /// <summary>连续文件存储 DOUBLE 但值即 ×10⁷ 整数，直接转 long</summary>
    private static long DoubleToPrice(double value)
        => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static bool TableHasColumn(DuckDBConnection conn, string table, string column)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {column} FROM {table} LIMIT 0";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ContinuousBarStore] Write failed: {ex.Message}"); return false; }
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
