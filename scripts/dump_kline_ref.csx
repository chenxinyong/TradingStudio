#!/usr/bin/env dotnet-script
// 交叉核对脚本：从 DuckDB 导出样本品种的日 K 线数据。
// 用法: dotnet script scripts/dump_kline_ref.csx
#r "nuget: DuckDB.NET.Data.Full, 1.3.0"
using DuckDB.NET.Data;

const string DbPath = @"data/bars_history.duckdb";
const long S = 10_000_000;
string[] insts = ["rb000","CU000","MA000","SA000","IF000","TA000","ru000","ag000"];

using var db = new DuckDBConnection($"Data Source={DbPath}");
db.Open();

// ── 1. 清单: 各品种的数据量与日期范围 ──
Console.WriteLine("══════════════════════════════════════");
Console.WriteLine("  数据清单: 各品种日 Bar 行数与日期范围");
Console.WriteLine("══════════════════════════════════════");
Console.WriteLine($"{"品种",-8} {"Bar数",>8}  {"起始日",-12} {"截止日",-12}");
foreach (var inst in insts)
{
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM bars_day WHERE instrument_id='{inst}'";
    using var r = (DuckDBDataReader)cmd.ExecuteReader();
    if (r.Read() && !r.IsDBNull(0))
    {
        var cnt = r.GetInt64(0);
        var min = r.GetFieldType(1) == typeof(string) ? r.GetString(1)[..10] : r.GetDateTime(1).ToString("yyyy-MM-dd");
        var max = r.GetFieldType(2) == typeof(string) ? r.GetString(2)[..10] : r.GetDateTime(2).ToString("yyyy-MM-dd");
        Console.WriteLine($"{inst,-8} {cnt,8:N0}  {min,-12} {max,-12}");
    }
    else Console.WriteLine($"{inst,-8}    (无数据)");
}

// ── 2. 样本输出: rb000 的日 Bar (2024-06-10~20) ──
Console.WriteLine("\n══════════════════════════════════════════════");
Console.WriteLine("  交叉核对: rb000 日 K 线 (2024-06-10 ~ 06-20)");
Console.WriteLine("  请对照文华/博易同一时段,差异>1tick为异常");
Console.WriteLine("══════════════════════════════════════════════");
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = "SELECT bar_time, open, high, low, close, volume FROM bars_day WHERE instrument_id='rb000' AND bar_time>='2024-06-10' AND bar_time<='2024-06-20' ORDER BY bar_time";
    using var r = (DuckDBDataReader)cmd.ExecuteReader();
    Console.WriteLine($"{"Date",-12} {"Open",>7} {"High",>7} {"Low",>7} {"Close",>7} {"Vol",>8}");
    while (r.Read())
    {
        var dt = r.GetFieldType(0) == typeof(string) ? r.GetString(0)[..10] : r.GetDateTime(0).ToString("yyyy-MM-dd");
        var o = r.GetInt64(1)/(double)S; var h = r.GetInt64(2)/(double)S;
        var l = r.GetInt64(3)/(double)S; var c = r.GetInt64(4)/(double)S;
        Console.WriteLine($"{dt,-12} {o,7:F0} {h,7:F0} {l,7:F0} {c,7:F0} {r.GetInt64(5),8:N0}");
    }
}

// ── 3. 连续合约拼接质量: rb000 ──
Console.WriteLine("\n══════════════════════════════════════");
Console.WriteLine("  连续合约跳空: rb000 (换月 >2% 标记)");
Console.WriteLine("══════════════════════════════════════");
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = "SELECT bar_time, close, open FROM bars_day WHERE instrument_id='rb000' ORDER BY bar_time";
    using var r = (DuckDBDataReader)cmd.ExecuteReader();
    double? prevC = null; DateTime? prevT = null;
    while (r.Read())
    {
        var t = r.GetFieldType(0) == typeof(string)
            ? DateTime.Parse(r.GetString(0)) : r.GetDateTime(0);
        var open = r.GetInt64(2)/(double)S;
        var close = r.GetInt64(1)/(double)S;
        if (prevC.HasValue && prevC > 0 && open > 0)
        {
            var gap = Math.Abs(open - prevC.Value) / prevC.Value;
            if (gap > 0.02)
                Console.WriteLine($"  ⚡ {prevT:yyyy-MM-dd} close={prevC:F0} → {t:yyyy-MM-dd} open={open:F0}  gap={gap:P1}");
        }
        prevC = close; prevT = t;
    }
}

Console.WriteLine("\n→ 完成。核对 rb000 的样本数据后通知我，或直接在 issues 中记录差异。");
