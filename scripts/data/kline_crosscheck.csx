using DuckDB.NET.Data;

const string DbPath = @"..\..\..\data\bars_history.duckdb";
const long S = 10_000_000;
string[] insts = ["rb000", "CU000", "MA000", "SA000", "TA000", "ru000", "ag000"];

using var db = new DuckDBConnection($"Data Source={DbPath}");
db.Open();

foreach (var inst in insts)
{
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM bars_day WHERE instrument_id='{inst}'";
    using var r = (DuckDBDataReader)cmd.ExecuteReader();
    if (r.Read() && !r.IsDBNull(0))
        Console.WriteLine($"{inst,-8} {r.GetInt64(0),8:N0} bars  {r.GetString(1)[..10]} ~ {r.GetString(2)[..10]}");
    else Console.WriteLine($"{inst,-8} (no data)");
}

Console.WriteLine("\n--- rb000 日Bar (2024-06-10~20) 供人工核对 ---");
Console.WriteLine("Date        |   Open |   High |    Low |  Close |     Vol");
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = "SELECT bar_time, open, high, low, close, volume FROM bars_day WHERE instrument_id='rb000' AND bar_time>='2024-06-10' AND bar_time<='2024-06-20' ORDER BY bar_time";
    using var r = (DuckDBDataReader)cmd.ExecuteReader();
    while (r.Read())
    {
        var dt = r.GetString(0)[..10];
        Console.WriteLine($"{dt} | {r.GetInt64(1)/(double)S,6:F0} | {r.GetInt64(2)/(double)S,6:F0} | {r.GetInt64(3)/(double)S,6:F0} | {r.GetInt64(4)/(double)S,6:F0} | {r.GetInt64(5),8:N0}");
    }
}
