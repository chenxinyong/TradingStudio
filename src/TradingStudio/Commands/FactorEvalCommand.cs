using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using TradingStudio.Core.Analysis;

namespace TradingStudio.Commands;

/// <summary>
/// C# 原生因子IC评估 — 通过 DuckDB 直接读取 Parquet / DuckDB 表, 运行 Rank IC + Quantile 分析。
/// 替代 Python factor_ic.py / factor_quantile.py, 统一技术栈, 消除 Python→CSV 中间层。
///
/// 用法:
///   dotnet run -- factor-eval --panel factors_v1.parquet [--factors IntradayMom,VWAP_Dev]
///   dotnet run -- factor-eval --db bars_history.duckdb --table factor_panel_v1 [--factors IntradayMom]
/// </summary>
public class FactorEvalCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var panelPath = "";
        var dbPath = "";
        var tableName = "";
        var factors = new List<string>();
        var isEnd = DateTime.Parse("2023-12-31");
        var outputPath = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--panel" or "-p" && i + 1 < args.Length) panelPath = args[++i];
            else if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] is "--table" or "-t" && i + 1 < args.Length) tableName = args[++i];
            else if (args[i] is "--factors" or "-f" && i + 1 < args.Length)
                factors.AddRange(args[++i].Split(','));
            else if (args[i] is "--is-end" && i + 1 < args.Length) isEnd = DateTime.Parse(args[++i]);
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length) outputPath = args[++i];
        }

        var isDuckDbMode = !string.IsNullOrEmpty(dbPath) && !string.IsNullOrEmpty(tableName);
        var isParquetMode = !string.IsNullOrEmpty(panelPath);

        if (!isDuckDbMode && !isParquetMode)
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  TradingStudio factor-eval --panel <factors.parquet> [--factors f1,f2]");
            Console.Error.WriteLine("  TradingStudio factor-eval --db <path.duckdb> --table <name> [--factors f1,f2]");
            return 1;
        }

        Console.WriteLine($"═══ C# Factor IC Evaluation ═══");

        List<FactorSnapshot> snapshots;
        if (isDuckDbMode)
        {
            if (!File.Exists(dbPath)) { Console.Error.WriteLine($"DB not found: {dbPath}"); return 1; }
            Console.WriteLine($"  DB: {dbPath}  Table: {tableName}");
            snapshots = await LoadFromDuckDB(dbPath, tableName, factors);
        }
        else
        {
            if (!File.Exists(panelPath)) { Console.Error.WriteLine($"Panel not found: {panelPath}"); return 1; }
            Console.WriteLine($"  Panel: {panelPath}");
            snapshots = await LoadFromParquetDuckDB(panelPath, factors);
        }

        if (snapshots.Count == 0)
        {
            Console.Error.WriteLine("No data loaded. Make sure the source has instrument_id/trading_day columns.");
            return 1;
        }

        Console.WriteLine($"  Loaded: {snapshots.Sum(s => s.FactorValues.Count):N0} factor values across {snapshots.Count} days");

        var evaluator = new FactorEvaluator();

        // Get factor names from first snapshot
        var factorNames = snapshots.First().FactorValues.Keys.ToList();
        if (factors.Count > 0) factorNames = factorNames.Where(f => factors.Contains(f)).ToList();

        Console.WriteLine($"  Factors: {string.Join(", ", factorNames)}");
        Console.WriteLine();

        var sb = new StringBuilder();
        sb.AppendLine($"# C# Factor IC Evaluation Report");
        sb.AppendLine($"> Source: {(isDuckDbMode ? $"{Path.GetFileName(dbPath)}::{tableName}" : Path.GetFileName(panelPath))}");
        sb.AppendLine();

        foreach (var factorName in factorNames)
        {
            // Build per-factor snapshots
            var factorSnaps = snapshots.Select(s =>
            {
                var fv = new Dictionary<string, double>();
                var fr = new Dictionary<string, double>();
                if (s.FactorValues.TryGetValue(factorName, out var val))
                    fv["_"] = val;
                if (s.ForwardReturns.TryGetValue(factorName, out var ret))
                    fr["_"] = ret;
                return new FactorSnapshot { Timestamp = s.Timestamp, FactorValues = fv, ForwardReturns = fr };
            }).Where(s => s.FactorValues.Count > 0 && s.ForwardReturns.Count > 0).ToList();

            if (factorSnaps.Count < 20) continue;

            // IC Analysis
            var icResult = evaluator.AnalyzeIC(factorSnaps);
            Console.WriteLine($"  {factorName}:");
            Console.WriteLine($"    Mean IC:   {icResult.MeanIC,10:F4}");
            Console.WriteLine($"    IC Std:    {icResult.StdIC,10:F4}");
            Console.WriteLine($"    IC IR:     {icResult.ICIR,10:F4}");
            Console.WriteLine($"    t-stat:    {icResult.TStatistic,10:F2}");
            Console.WriteLine($"    Pos Ratio: {icResult.PositiveRatio,10:P1}");
            Console.WriteLine($"    Verdict:   {icResult.Verdict}");

            sb.AppendLine($"## {factorName}");
            sb.AppendLine($"| 指标 | 值 |");
            sb.AppendLine($"|------|-----|");
            sb.AppendLine($"| Mean IC | {icResult.MeanIC:F4} |");
            sb.AppendLine($"| IC Std | {icResult.StdIC:F4} |");
            sb.AppendLine($"| IC IR | {icResult.ICIR:F4} |");
            sb.AppendLine($"| t-stat | {icResult.TStatistic:F2} |");
            sb.AppendLine($"| IC>0% | {icResult.PositiveRatio:P1} |");
            sb.AppendLine($"| Half-Life | {icResult.HalfLife}期 |");
            sb.AppendLine($"| 结论 | **{icResult.Verdict}** |");
            sb.AppendLine();

            // Quantile Analysis
            var qtResult = evaluator.AnalyzeQuantiles(factorSnaps, 5);
            sb.AppendLine("### 分位数收益");
            sb.AppendLine($"| 分位 | 品种数 | 平均收益 | 年化收益 | Sharpe | 胜率 |");
            sb.AppendLine($"|------|--------|----------|----------|--------|------|");
            foreach (var q in qtResult.Quantiles)
                sb.AppendLine($"| Q{q.Quantile+1} | {q.Count} | {q.AvgReturn*10000:F1}bps | {q.AnnualReturn:P2} | {q.Sharpe:F2} | {q.WinRate:P1} |");
            sb.AppendLine($"| **多空** | - | - | **{qtResult.LongShortSpread:P2}** | - | - |");
            sb.AppendLine($"| 单调性 | {(qtResult.IsMonotonic ? "✅" : "⚠️")} | | | | |");
            sb.AppendLine();

            Console.WriteLine();
        }

        // Save report
        if (string.IsNullOrEmpty(outputPath))
            outputPath = Path.ChangeExtension(panelPath, ".ic-report.md");
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"  Report: {outputPath}");

        return 0;
    }

    /// <summary>
    /// 通过 DuckDB 直接读取 Parquet 文件 → FactorSnapshot 列表。
    /// 替代 Python pandas.read_parquet → to_csv 中间层, 零依赖, 纯 C#。
    /// </summary>
    private static async Task<List<FactorSnapshot>> LoadFromParquetDuckDB(string parquetPath, List<string> filterFactors)
    {
        // DuckDB 原生支持 Parquet: SELECT * FROM 'path/to/file.parquet'
        using var conn = new DuckDBConnection("Data Source=:memory:");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM '{parquetPath}'";
        using var reader = await cmd.ExecuteReaderAsync();

        return BuildSnapshots(reader, filterFactors);
    }

    /// <summary>
    /// 从 DuckDB 表加载因子面板 → FactorSnapshot 列表。
    /// </summary>
    private static async Task<List<FactorSnapshot>> LoadFromDuckDB(string dbPath, string tableName, List<string> filterFactors)
    {
        using var conn = new DuckDBConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {tableName}";
        using var reader = await cmd.ExecuteReaderAsync();

        return BuildSnapshots(reader, filterFactors);
    }

    /// <summary>
    /// 从 DbDataReader 构建 FactorSnapshot 列表。
    /// 自动发现因子列 (排除 instrument_id / trading_day / bar_time / period / FwdRet_* 等元数据列)。
    /// 按 trading_day 分组, 生成 (品种→因子值, 品种→前向收益) 快照。
    /// </summary>
    private static List<FactorSnapshot> BuildSnapshots(System.Data.Common.DbDataReader reader, List<string> filterFactors)
    {
        // 发现列
        var schema = new List<(string Name, int Ordinal, bool IsFwdRet)>();
        int idxDate = -1, idxInst = -1;
        var fwdCols = new List<(string BaseFactor, int Ordinal)>();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (name == "trading_day") idxDate = i;
            else if (name == "instrument_id") idxInst = i;
            else if (name is "bar_time" or "period") { /* skip metadata */ }
            else if (name.StartsWith("FwdRet_"))
            {
                var baseFactor = name["FwdRet_".Length..];
                fwdCols.Add((baseFactor, i));
            }
            else
            {
                if (filterFactors.Count == 0 || filterFactors.Contains(name))
                    schema.Add((name, i, false));
            }
        }

        if (idxDate < 0 || idxInst < 0)
            return new();

        // 按天分组
        var dayData = new Dictionary<DateTime, (Dictionary<string, double> fv, Dictionary<string, double> fr)>();

        while (reader.Read())
        {
            var date = ReadDateTime(reader, idxDate);
            if (date == default) continue;
            var inst = reader.GetString(idxInst);
            if (string.IsNullOrEmpty(inst)) continue;

            var fv = new Dictionary<string, double>();
            var fr = new Dictionary<string, double>();

            foreach (var (name, ord, _) in schema)
            {
                var val = ReadDouble(reader, ord);
                fv[name] = val;
            }

            foreach (var (baseFactor, ord) in fwdCols)
            {
                var val = ReadDouble(reader, ord);
                fr[baseFactor] = val;  // FwdRet_IntradayMom → key="IntradayMom"
            }

            if (!dayData.TryGetValue(date, out var dd))
            {
                dd = (new Dictionary<string, double>(), new Dictionary<string, double>());
                dayData[date] = dd;
            }

            foreach (var (name, val) in fv)
            {
                dd.fv[name + "_" + inst] = val;
                dd.fr[name + "_" + inst] = fr.GetValueOrDefault(name, 0);
            }
        }

        return dayData.Select(kv => new FactorSnapshot
        {
            Timestamp = kv.Key,
            FactorValues = kv.Value.fv,
            ForwardReturns = kv.Value.fr,
        }).OrderBy(s => s.Timestamp).ToList();
    }

    private static DateTime ReadDateTime(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return default;
        try
        {
            if (reader.GetFieldType(ordinal) == typeof(string))
                return DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
            return reader.GetDateTime(ordinal);
        }
        catch { return default; }
    }

    private static double ReadDouble(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return 0;
        try { return reader.GetDouble(ordinal); }
        catch { return 0; }
    }
}
