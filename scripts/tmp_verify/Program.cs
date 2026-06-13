using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : @"c:\Works\ClaudeCode\TradingStudio\dist\bars_history.db";
if (!File.Exists(dbPath)) { Console.WriteLine($"NOT FOUND: {dbPath}"); return 1; }

using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();
Console.WriteLine($"DB: {dbPath}  ({new FileInfo(dbPath).Length / 1024 / 1024:N0} MB)\n");

// Counts
using (var cmd = conn.CreateCommand())
{ cmd.CommandText = "SELECT COUNT(*) FROM bars_1min"; Console.WriteLine("1min Bars : {0:N0}", (long)cmd.ExecuteScalar()!); }
using (var cmd = conn.CreateCommand())
{ cmd.CommandText = "SELECT COUNT(*) FROM bars_day"; Console.WriteLine("Day Bars  : {0:N0}", (long)cmd.ExecuteScalar()!); }

// Date range
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT MIN(bar_time), MAX(bar_time) FROM bars_1min";
    using var r = cmd.ExecuteReader();
    if (r.Read()) Console.WriteLine("\nDate: {0} ~ {1}", r.GetString(0), r.GetString(1));
}

// Instruments & counts
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT instrument_id, COUNT(*) cnt, MIN(bar_time), MAX(bar_time) FROM bars_1min GROUP BY instrument_id ORDER BY instrument_id";
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\n{0,-10} {1,8}  {2,-20} {3,-20}", "Inst", "Bars", "From", "To");
    Console.WriteLine(new string('-', 65));
    while (r.Read())
        Console.WriteLine("{0,-10} {1,8:N0}  {2,-20} {3,-20}", r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3));
}

// Sample bars for first instrument
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT instrument_id FROM bars_1min GROUP BY instrument_id ORDER BY instrument_id LIMIT 1";
    var first = (string)cmd.ExecuteScalar()!;
    cmd.CommandText = string.Format("SELECT * FROM bars_1min WHERE instrument_id='{0}' ORDER BY bar_time LIMIT 5", first);
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\n--- Sample: {0} (first 5 bars) ---", first);
    while (r.Read())
    {
        var o = Convert.ToDouble((long)r["open"]) / 10_000_000;
        var h = Convert.ToDouble((long)r["high"]) / 10_000_000;
        var l = Convert.ToDouble((long)r["low"]) / 10_000_000;
        var c = Convert.ToDouble((long)r["close"]) / 10_000_000;
        Console.WriteLine("  {0}  O:{1,10:F2} H:{2,10:F2} L:{3,10:F2} C:{4,10:F2} V:{5,6} Ticks:{6,4}",
            r["bar_time"], o, h, l, c, r["volume"], r["tick_count"]);
    }
}

// Volume check
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM bars_1min WHERE volume = 0 AND bar_time > '2020-01-02 09:05:00'";
    var zeroVol = (long)cmd.ExecuteScalar()!;
    Console.WriteLine("\nZero-volume bars after 09:05: {0:N0}", zeroVol);
}

// Trading hours coverage
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT SUBSTR(bar_time, 12, 5) as time_slot, COUNT(*) cnt FROM bars_1min GROUP BY time_slot ORDER BY cnt DESC LIMIT 10";
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\n--- Top 10 Time Slots ---");
    while (r.Read()) Console.WriteLine("  {0} : {1:N0} bars", r.GetString(0), r.GetInt64(1));
}

// Day bars check
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT instrument_id, trading_day, open, high, low, close, volume, tick_count FROM bars_day ORDER BY trading_day, instrument_id LIMIT 5";
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\n--- Day Bar Sample ---");
    while (r.Read())
    {
        var o = Convert.ToDouble((long)r["open"]) / 10_000_000;
        var c = Convert.ToDouble((long)r["close"]) / 10_000_000;
        Console.WriteLine("  {0}  {1}  O:{2:F2} C:{3:F2} V:{4,8} Ticks:{5,4}",
            r["instrument_id"], r["trading_day"], o, c, r["volume"], r["tick_count"]);
    }
}

Console.WriteLine("\nDone.");
conn.Close();
return 0;
