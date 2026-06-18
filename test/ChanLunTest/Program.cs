using TradingStudio.Strategy.ChanLun;

const string DbPath = @"C:\Works\ClaudeCode\TradingStudio\data\archive\bars_2024.db";

// === Test 1: Full analysis (ag2412, 1min -> 15min) ===
Console.WriteLine();
var result15 = ChanLunValidator.RunValidation(DbPath, "ag2412", minBiLen: 5);

// === Test 2: Parameter sensitivity ===
Console.WriteLine("\n" + new string('=', 60));
Console.WriteLine("Parameter Sensitivity (MIN_BI_LEN)");
Console.WriteLine(new string('=', 60));

var bars = ChanLunValidator.LoadBars(DbPath, "ag2412", periodMinutes: 15);

foreach (var minLen in new[] { 3, 5, 7, 9, 12 })
{
    var r = ChanLunAnalyzer.Analyze(bars, minBiLen: minLen);
    var (_, _, biOk) = ChanLunAnalyzer.Validate(r, minLen);
    Console.WriteLine($"  MIN_BI_LEN={minLen,2}: std={r.StdCount,5} fx={r.FractalCount,4} bi={r.BiCount,3} zs={r.ZhongshuCount,2} valid={biOk}");
}

// === Test 3: 1min vs 15min comparison ===
Console.WriteLine("\n" + new string('=', 60));
Console.WriteLine("1min vs 15min Comparison");
Console.WriteLine(new string('=', 60));

var bars1m = ChanLunValidator.LoadBars(DbPath, "ag2412", periodMinutes: 1, limit: 5000);
var r1m = ChanLunAnalyzer.Analyze(bars1m, minBiLen: 7);
var r15m = ChanLunAnalyzer.Analyze(bars, minBiLen: 5);

Console.WriteLine($"  {"Metric",-20} {"15min",10} {"1min",10}");
Console.WriteLine($"  {"Bars",-20} {r15m.RawCount,10} {r1m.RawCount,10}");
Console.WriteLine($"  {"Bis",-20} {r15m.BiCount,10} {r1m.BiCount,10}");
Console.WriteLine($"  {"ZS",-20} {r15m.ZhongshuCount,10} {r1m.ZhongshuCount,10}");
Console.WriteLine($"  {"Trend",-20} {r15m.Trend,10} {r1m.Trend,10}");

// Summary
Console.WriteLine("\n" + new string('=', 60));
var (iOk, fOk, bOk) = ChanLunAnalyzer.Validate(result15, 5);
Console.WriteLine($"C# Validation: Inclusion={(iOk ? "PASS" : "FAIL")}  Fractal={(fOk ? "PASS" : "FAIL")}  Bi={(bOk ? "PASS" : "FAIL")}");
Console.WriteLine(new string('=', 60));

// === Test 4: Generate HTML chart ===
Console.WriteLine("\n" + new string('=', 60));
Console.WriteLine("Generating HTML Chart...");
Console.WriteLine(new string('=', 60));

var bars15 = ChanLunValidator.LoadBars(DbPath, "ag2412", periodMinutes: 15, limit: 6000);
var resultChart = ChanLunAnalyzer.Analyze(bars15, minBiLen: 5);

var outputDir = @"C:\Works\ClaudeCode\TradingStudio\scripts\chanlun\output";
Directory.CreateDirectory(outputDir);
var htmlPath = ChanLunChart.AnalyzeAndSave(bars15, "ag2412",
    Path.Combine(outputDir, "chanlun_csharp.html"), minBiLen: 5);
Console.WriteLine($"Chart saved: {htmlPath}");
Console.WriteLine($"Size: {new FileInfo(htmlPath).Length / 1024} KB");
