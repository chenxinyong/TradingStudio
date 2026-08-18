// Save as kline_report.cs — compile with:
//   dotnet-script scripts/kline_report.cs
// or use the existing TradingStudio.Data project.
//
// Quick DuckDB inventory script.

using DuckDB.NET.Data;
const string DB = "data/bars_history.duckdb";
const long S = 10_000_000;

using var db = new DuckDBConnection($"Data Source={DB}");
db.Open();

// Tables
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema='main' ORDER BY table_name";
    using var r = (DuckDBDataReader)cmd.ExecuteReader();
    Console.WriteLine("=== DuckDB Tables ===");
    while (r.Read()) Console.WriteLine($"  {r.GetString(0)}");
}

// bars_day instruments with >0 count
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = "SELECT instrument_id, COUNT(*) AS cnt, MIN(bar_time), MAX(bar_time) FROM bars_day WHERE instrument_id ILIKE '%000' GROUP BY instrument_id ORDER BY instrument_id";
    using var r = (DuckDBDataReader)cmd.ExecuteReader();
    Console.WriteLine("\n=== bars_day continuous contracts (xxx000) ===");
    Console.WriteLine($"{"Inst",-8} {"Count",>8}  {"Min Date",-12} {"Max Date",-12}");
    while (r.Read())
    {
        var min = r.GetString(2)[..10]; var max = r.GetString(3)[..10];
        Console.WriteLine($"{r.GetString(0),-8} {r.GetInt64(1),8:N0}  {min,-12} {max,-12}");
    }
}
