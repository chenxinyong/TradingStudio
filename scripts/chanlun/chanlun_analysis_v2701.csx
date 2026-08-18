#!/usr/bin/env dotnet-script
/// <summary>
/// V2701 缠论日线+周线分析脚本
/// 用法: dotnet script scripts/chanlun_analysis_v2701.csx
/// </summary>

#r "nuget: DuckDB.NET.Data.Full, 1.2.0"

using DuckDB.NET.Data;
using System.Text;

// ═══════════ 配置 ═══════════
string dbPath = @"C:\Works\Datas\bars_history.duckdb";
string symbol = "v2701";

// ═══════════ Step 1: 加载日线数据 ═══════════
Console.WriteLine("═══ V2701 日线数据加载 ═══");

var dayBars = new List<(DateTime Dt, double O, double H, double L, double C, long V)>();
using (var conn = new DuckDBConnection($"Data Source={dbPath}"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT bar_time, open/1e7, high/1e7, low/1e7, close/1e7, volume
        FROM bars_day WHERE instrument_id = 'v2701'
        ORDER BY bar_time";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        DateTime dt = reader.GetFieldType(0) == typeof(string)
            ? DateTime.Parse(reader.GetString(0))
            : reader.GetDateTime(0);
        dayBars.Add((dt, reader.GetDouble(1), reader.GetDouble(2),
                        reader.GetDouble(3), reader.GetDouble(4), reader.GetInt64(5)));
    }
}
Console.WriteLine($"日线: {dayBars.Count} 根 [{dayBars[0].Dt:yyyy-MM-dd} → {dayBars[^1].Dt:yyyy-MM-dd}]");
Console.WriteLine($"价格范围: {dayBars.Min(b => b.L):F0} ~ {dayBars.Max(b => b.H):F0}");
Console.WriteLine($"最新: O={dayBars[^1].O:F0} H={dayBars[^1].H:F0} L={dayBars[^1].L:F0} C={dayBars[^1].C:F0}");

// ═══════════ Step 2: 合成周线 ═══════════
Console.WriteLine("\n═══ V2701 周线合成 (从日线聚合) ═══");

var weekBars = dayBars
    .GroupBy(b => {
        // ISO week: Monday = start of week
        var d = b.Dt;
        int diff = (7 + ((int)d.DayOfWeek - 1)) % 7;
        return d.AddDays(-diff).Date;
    })
    .OrderBy(g => g.Key)
    .Select(g => (
        Dt: g.Key,
        O: g.First().O,
        H: g.Max(b => b.H),
        L: g.Min(b => b.L),
        C: g.Last().C,
        V: g.Sum(b => b.V)
    ))
    .ToList();

Console.WriteLine($"周线: {weekBars.Count} 根 [{weekBars[0].Dt:yyyy-MM-dd} → {weekBars[^1].Dt:yyyy-MM-dd}]");

// ═══════════ Step 3: 缠论分析函数 ═══════════
// 简化版缠论分析 (包含处理→分型→笔)
static List<(string Type, DateTime Start, DateTime End, double StartP, double EndP, double Low, double High, int BarCount, double ChangePct)>
    AnalyzeBis(List<(DateTime Dt, double O, double H, double L, double C, long V)> bars, int minBiLen = 5)
{
    // 包含处理
    var std = new List<(DateTime Dt, double H, double L, double C)>();
    int dir = 0; // 0=未知, 1=向上, -1=向下

    for (int i = 0; i < bars.Count; i++)
    {
        var b = bars[i];
        if (std.Count == 0) { std.Add((b.Dt, b.H, b.L, b.C)); continue; }

        var prev = bars[i - 1];
        // 判断方向
        if (b.H > prev.H && b.L > prev.L) dir = 1;
        else if (b.L < prev.L && b.H < prev.H) dir = -1;

        var last = std[^1];
        // 检查包含
        if ((b.H >= last.H && b.L <= last.L) || (b.H <= last.H && b.L >= last.L))
        {
            // 有包含关系，合并
            if (dir == 1) // 向上: 取高高、取高低
                std[^1] = (last.Dt, Math.Max(b.H, last.H), Math.Max(b.L, last.L), b.C);
            else if (dir == -1) // 向下: 取低高、取低低
                std[^1] = (last.Dt, Math.Min(b.H, last.H), Math.Min(b.L, last.L), b.C);
            else // 未知方向，按趋势延续
                std[^1] = (last.Dt, Math.Max(b.H, last.H), Math.Min(b.L, last.L), b.C);
        }
        else
        {
            std.Add((b.Dt, b.H, b.L, b.C));
        }
    }

    // 分型识别
    var fractals = new List<(string Type, int Idx, double Price, DateTime Dt)>();
    for (int i = 1; i < std.Count - 1; i++)
    {
        var (_, h0, l0, _) = std[i - 1];
        var (dt1, h1, l1, _) = std[i];
        var (_, h2, l2, _) = std[i + 1];

        if (h1 > h0 && h1 > h2 && l1 > l0 && l1 > l2)
            fractals.Add(("Top", i, h1, dt1));
        else if (l1 < l0 && l1 < l2 && h1 < h0 && h1 < h2)
            fractals.Add(("Bottom", i, l1, dt1));
    }

    // 去重
    var deduped = new List<(string Type, int Idx, double Price, DateTime Dt)>();
    foreach (var f in fractals)
    {
        if (deduped.Count > 0 && deduped[^1].Type == f.Type)
        {
            if (f.Type == "Top" && f.Price > deduped[^1].Price)
                deduped[^1] = f;
            else if (f.Type == "Bottom" && f.Price < deduped[^1].Price)
                deduped[^1] = f;
        }
        else
            deduped.Add(f);
    }

    // 笔构建
    var bis = new List<(string Type, DateTime Start, DateTime End, double StartP, double EndP, double Low, double High, int BarCount, double ChangePct)>();
    int fi = 0;
    while (fi < deduped.Count - 1)
    {
        var start = deduped[fi];
        // 向右扫描
        int ej = fi + 1;
        while (ej < deduped.Count)
        {
            var end = deduped[ej];
            if (end.Type == start.Type) { ej++; continue; }

            int barCount = end.Idx - start.Idx;
            if (barCount < minBiLen) { ej++; continue; }

            // 价格验证
            bool valid;
            if (start.Type == "Bottom")
                valid = end.Price > start.Price;
            else
                valid = end.Price < start.Price;

            if (!valid) { ej++; continue; }

            // 找笔内极值
            double low = double.MaxValue, high = double.MinValue;
            for (int k = start.Idx; k <= end.Idx; k++)
            {
                low = Math.Min(low, std[k].L);
                high = Math.Max(high, std[k].H);
            }

            double changePct = start.Type == "Bottom"
                ? (end.Price - start.Price) / start.Price * 100
                : (start.Price - end.Price) / start.Price * 100;

            bis.Add((start.Type == "Bottom" ? "UP" : "DOWN",
                     start.Dt, end.Dt, start.Price, end.Price,
                     low, high, barCount, changePct));
            fi = ej;
            break;
        }
        if (ej >= deduped.Count) break;
        if (fi < ej) fi = ej; else fi++;
    }

    return bis;
}

// ═══════════ Step 4: 分析 ═══════════
Console.WriteLine("\n═══ V2701 日线缠论分析 ═══");
var dayBis = AnalyzeBis(dayBars, minBiLen: 4); // 日线用较小的 minBiLen
PrintBis(dayBis);

Console.WriteLine("\n═══ V2701 周线缠论分析 ═══");
var weekBis = AnalyzeBis(weekBars, minBiLen: 3); // 周线K线较少
PrintBis(weekBis);

// ═══════════ Step 5: 关键价位 ═══════════
Console.WriteLine("\n═══ 关键价位分析 ═══");

if (dayBars.Count > 0)
{
    var recent = dayBars.TakeLast(30).ToList();
    Console.WriteLine($"近30日日线:");
    Console.WriteLine($"  最高: {recent.Max(b => b.H):F0}");
    Console.WriteLine($"  最低: {recent.Min(b => b.L):F0}");
    Console.WriteLine($"  最新收盘: {recent[^1].C:F0}");

    // 支撑阻力
    var allHighs = dayBars.Select(b => b.H).OrderDescending().Take(5).ToList();
    var allLows = dayBars.Select(b => b.L).Order().Take(5).ToList();
    Console.WriteLine($"  历史最高: {allHighs[0]:F0}  历史最低: {allLows[0]:F0}");
}

if (weekBars.Count > 0)
{
    Console.WriteLine($"周线:");
    Console.WriteLine($"  最高: {weekBars.Max(b => b.H):F0}");
    Console.WriteLine($"  最低: {weekBars.Min(b => b.L):F0}");
    Console.WriteLine($"  最新收盘: {weekBars[^1].C:F0}");
}

// ═══════════ Helpers ═══════════
static void PrintBis(List<(string Type, DateTime Start, DateTime End, double StartP, double EndP, double Low, double High, int BarCount, double ChangePct)> bis)
{
    if (bis.Count == 0) { Console.WriteLine("  无笔"); return; }

    Console.WriteLine($"  {"方向",-6} {"起点",-12} {"终点",-12} {"涨跌幅",-8} {"K线数",-6} {"笔内低",-8} {"笔内高",-8}");
    Console.WriteLine($"  {new string('─', 70)}");

    foreach (var bi in bis)
    {
        string arrow = bi.Type == "UP" ? "↑" : "↓";
        Console.WriteLine($"  {arrow,-5} {bi.Start:yyyy-MM-dd}  {bi.End:yyyy-MM-dd}  {bi.ChangePct,7:F2}%  {bi.BarCount,-5}  {bi.Low,8:F0}  {bi.High,8:F0}");
    }

    // 趋势判断
    string trend = "盘整";
    if (bis.Count >= 2)
    {
        var lastTwo = bis.TakeLast(2).ToList();
        if (lastTwo[0].Type == "UP" && lastTwo[1].Type == "UP" &&
            lastTwo[1].High > lastTwo[0].High)
            trend = "上涨趋势";
        else if (lastTwo[0].Type == "DOWN" && lastTwo[1].Type == "DOWN" &&
                 lastTwo[1].Low < lastTwo[0].Low)
            trend = "下跌趋势";
    }

    var lastBi = bis[^1];
    Console.WriteLine($"\n  笔数: {bis.Count}  走势: {trend}");
    Console.WriteLine($"  最后一笔: {lastBi.Type} {lastBi.Start:yyyy-MM-dd}→{lastBi.End:yyyy-MM-dd} {lastBi.ChangePct:F2}%");
}
