using Microsoft.Data.Sqlite;
using TradingStudio.Core.Models;

namespace TradingStudio.Research;

/// <summary>
/// Bar 读取器 — 直接从 SQLite 读 Bar 数据。
/// BarStore 只负责写入，读取用此类。
/// </summary>
public class BarReader
{
    private readonly string _dbPath;

    public BarReader(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>加载指定品种和周期的 Bar</summary>
    public IReadOnlyList<Bar> Load(string instrumentId, string tableName,
        DateTime start, DateTime end)
    {
        var bars = new List<Bar>();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT instrument_id, trading_day, bar_time,
                   open, high, low, close, volume, turnover, open_interest, tick_count
            FROM {tableName}
            WHERE instrument_id = @inst
              AND bar_time >= @start
              AND bar_time <= @end
            ORDER BY bar_time";
        cmd.Parameters.AddWithValue("@inst", instrumentId);
        cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd HH:mm:ss"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bars.Add(new Bar
            {
                InstrumentId = reader.GetString(0),
                TradingDay = DateOnly.Parse(reader.GetString(1)),
                BarTime = DateTime.Parse(reader.GetString(2)),
                Open = reader.GetInt64(3),
                High = reader.GetInt64(4),
                Low = reader.GetInt64(5),
                Close = reader.GetInt64(6),
                Volume = reader.GetInt32(7),
                Turnover = reader.GetDouble(8),
                OpenInterest = reader.GetDouble(9),
                TickCount = reader.GetInt32(10)
            });
        }
        return bars;
    }

    /// <summary>列出所有品种</summary>
    public IReadOnlyList<string> GetInstruments(string tableName = "bars_1min")
    {
        var instruments = new List<string>();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT instrument_id FROM {tableName} ORDER BY instrument_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            instruments.Add(reader.GetString(0));
        return instruments;
    }

    /// <summary>品种可交易日期范围</summary>
    public (DateTime min, DateTime max) GetDateRange(string instrumentId,
        string tableName = "bars_1min")
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT MIN(bar_time), MAX(bar_time)
            FROM {tableName}
            WHERE instrument_id = @inst";
        cmd.Parameters.AddWithValue("@inst", instrumentId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0))
            return (DateTime.Parse(reader.GetString(0)), DateTime.Parse(reader.GetString(1)));
        return (DateTime.MinValue, DateTime.MaxValue);
    }
}
