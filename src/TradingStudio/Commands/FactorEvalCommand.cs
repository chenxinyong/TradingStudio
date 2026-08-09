using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using TradingStudio.Core.Analysis;

namespace TradingStudio.Commands;

/// <summary>
/// C# 原生因子IC评估 — 通过 DuckDB 直接读取 Parquet / DuckDB 表, 运行 Rank IC + Quantile 分析。
/// 替代 Python factor_ic.py / factor_quantile.py, 统一技术栈, 消除 Python→CSV 中间层。
///
/// 支持两种数据格式:
///   格式A (通用FwdRet): FwdRet_1d 列作为所有因子的共享前向收益 (Python factor_compute.py 输出)
///   格式B (逐因子FwdRet): FwdRet_IntradayMom 等列, 每个因子自己的前向收益
///
/// 用法:
///   dotnet run -- factor-eval --panel factors_v1.parquet [--factors IntradayMom,VWAP_Dev]
///   dotnet run -- factor-eval --db bars_history.duckdb --table factor_panel_v1 [--factors IntradayMom]
///   dotnet run -- factor-eval --db bars_history.duckdb --table factor_panel_v1 --is-end 2023-12-31 --output report.md
/// </summary>
public class FactorEvalCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var panelPath = "";
        var dbPath = "";
        var tableName = "";
        var factorFilter = new List<string>();
        var isEnd = DateTime.Parse("2023-12-31");
        var outputPath = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--panel" or "-p" && i + 1 < args.Length) panelPath = args[++i];
            else if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] is "--table" or "-t" && i + 1 < args.Length) tableName = args[++i];
            else if (args[i] is "--factors" or "-f" && i + 1 < args.Length)
                factorFilter.AddRange(args[++i].Split(','));
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

        // ── 1. 加载数据 → factor → [per-day snapshots with instrument-level keys] ──
        Dictionary<string, List<FactorSnapshot>> factorPanel;
        if (isDuckDbMode)
        {
            if (!File.Exists(dbPath)) { Console.Error.WriteLine($"DB not found: {dbPath}"); return 1; }
            Console.WriteLine($"  DB: {dbPath}  Table: {tableName}");
            factorPanel = await LoadFromDuckDB(dbPath, tableName, factorFilter);
        }
        else
        {
            if (!File.Exists(panelPath)) { Console.Error.WriteLine($"Panel not found: {panelPath}"); return 1; }
            Console.WriteLine($"  Panel: {panelPath}");
            factorPanel = await LoadFromParquetDuckDB(panelPath, factorFilter);
        }

        if (factorPanel.Count == 0)
        {
            Console.Error.WriteLine("No data loaded. Make sure the source has instrument_id/trading_day columns.");
            return 1;
        }

        var totalVals = factorPanel.Sum(kv => kv.Value.Sum(s => s.FactorValues.Count));
        var totalDays = factorPanel.Values.FirstOrDefault()?.Count ?? 0;
        Console.WriteLine($"  Loaded: {factorPanel.Count} factors, {totalDays} days, {totalVals:N0} values");

        // ── 2. IS/OOS 分割 ──
        var isPanel = new Dictionary<string, List<FactorSnapshot>>();
        var oosPanel = new Dictionary<string, List<FactorSnapshot>>();
        foreach (var (fName, snaps) in factorPanel)
        {
            isPanel[fName] = snaps.Where(s => s.Timestamp <= isEnd).OrderBy(s => s.Timestamp).ToList();
            oosPanel[fName] = snaps.Where(s => s.Timestamp > isEnd).OrderBy(s => s.Timestamp).ToList();
        }

        var evaluator = new FactorEvaluator();
        var sb = new StringBuilder();
        var sourceLabel = isDuckDbMode
            ? $"{Path.GetFileName(dbPath)}::{tableName}"
            : Path.GetFileName(panelPath);

        sb.AppendLine($"# C# Factor IC Evaluation Report");
        sb.AppendLine($"> Source: {sourceLabel}");
        sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"> IS: ≤ {isEnd:yyyy-MM-dd}  |  OOS: > {isEnd:yyyy-MM-dd}");
        sb.AppendLine();

        // ── 3. 逐因子分析 ──
        foreach (var (factorName, allSnaps) in factorPanel.OrderBy(kv => kv.Key))
        {
            var isSnaps = isPanel.GetValueOrDefault(factorName, new());
            var oosSnaps = oosPanel.GetValueOrDefault(factorName, new());

            if (isSnaps.Count < 20) continue;

            Console.WriteLine($"\n  ── {factorName} ──");

            // IS IC
            var isIc = evaluator.AnalyzeIC(isSnaps);
            Console.WriteLine($"    IS  Mean IC: {isIc.MeanIC,10:F4}  Std: {isIc.StdIC,8:F4}  IR: {isIc.ICIR,8:F4}  Hit: {isIc.PositiveRatio,8:P1}  [{isIc.Verdict}]");

            // OOS IC
            IcAnalysisResult oosIc = new() { IsInsufficientData = true };
            if (oosSnaps.Count >= 20)
            {
                oosIc = evaluator.AnalyzeIC(oosSnaps);
                Console.WriteLine($"    OOS Mean IC: {oosIc.MeanIC,10:F4}  Std: {oosIc.StdIC,8:F4}  IR: {oosIc.ICIR,8:F4}  Hit: {oosIc.PositiveRatio,8:P1}  [{oosIc.Verdict}]");
            }

            // 过拟合检测
            var overfit = DetectOverfit(isIc, oosIc);

            // Markdown 报告
            sb.AppendLine($"## {factorName}");
            sb.AppendLine();
            sb.AppendLine("### IC 分析");
            sb.AppendLine($"| 周期 | Mean IC | IC Std | IC IR | IC&gt;0% | t-stat | Half-Life | 结论 |");
            sb.AppendLine($"|------|---------|--------|-------|--------|--------|-----------|------|");
            sb.AppendLine($"| IS | {isIc.MeanIC:F4} | {isIc.StdIC:F4} | {isIc.ICIR:F4} | {isIc.PositiveRatio:P1} | {isIc.TStatistic:F2} | {isIc.HalfLife}期 | **{isIc.Verdict}** |");
            if (!oosIc.IsInsufficientData)
                sb.AppendLine($"| OOS | {oosIc.MeanIC:F4} | {oosIc.StdIC:F4} | {oosIc.ICIR:F4} | {oosIc.PositiveRatio:P1} | {oosIc.TStatistic:F2} | {oosIc.HalfLife}期 | **{oosIc.Verdict}** |");
            sb.AppendLine();

            // IC 衰减
            if (isIc.ICDecay.Count > 0)
            {
                sb.AppendLine("### IC 衰减 (IS)");
                sb.AppendLine($"| Lag | 1 | 2 | 3 | 5 | 10 | 20 |");
                sb.AppendLine($"|-----|---|---|---|---|---|----|");
                var decayVals = new[] { 1, 2, 3, 5, 10, 20 }
                    .Select(l => isIc.ICDecay.TryGetValue(l, out var v) ? $"{v:F4}" : "-");
                sb.AppendLine($"| IC | {string.Join(" | ", decayVals)} |");
                sb.AppendLine();
            }

            // 分位数
            var qtIs = evaluator.AnalyzeQuantiles(isSnaps, 5);
            if (!qtIs.IsInsufficientData)
            {
                sb.AppendLine("### 分位数收益 (IS)");
                sb.AppendLine($"| 分位 | 品种数 | 平均收益 | 年化收益 | Sharpe | 胜率 |");
                sb.AppendLine($"|------|--------|----------|----------|--------|------|");
                foreach (var q in qtIs.Quantiles)
                    sb.AppendLine($"| Q{q.Quantile} | {q.Count} | {q.AvgReturn*10000:F1}bps | {q.AnnualReturn:P2} | {q.Sharpe:F2} | {q.WinRate:P1} |");
                sb.AppendLine($"| **多空** | - | - | **{qtIs.LongShortAnnual:P2}** | - | - |");
                sb.AppendLine($"| 单调性 | {(qtIs.IsMonotonic ? "✅ 单调递增" : "⚠️ 非单调")} | | | | |");
                sb.AppendLine();
            }

            // 过拟合判断
            sb.AppendLine("### 稳定性评估");
            sb.AppendLine($"| 指标 | 值 | 判断 |");
            sb.AppendLine($"|------|-----|------|");
            sb.AppendLine($"| IS/OOS IC 符号一致 | {(overfit.SignMatch ? "✅" : "⚠️")} | |");
            sb.AppendLine($"| IS IC IR | {isIc.ICIR:F4} | {isIc.Verdict} |");
            sb.AppendLine($"| OOS IC IR | {(oosIc.IsInsufficientData ? "N/A" : $"{oosIc.ICIR:F4}")} | {(oosIc.IsInsufficientData ? "样本不足" : oosIc.Verdict.ToString())} |");
            sb.AppendLine($"| IS/OOS IR Ratio | {overfit.IrRatio:F2} | {(overfit.IrRatio < 0.5 ? "⚠️ 过拟合" : "✅ 稳定")} |");
            sb.AppendLine($"| **综合判定** | **{overfit.Verdict}** | |");
            sb.AppendLine();
        }

        // ── 4. 因子概览表 ──
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 因子概览");
        sb.AppendLine();
        sb.AppendLine($"| 因子 | IS IC Mean | IS IC IR | IS IC&gt;0% | OOS IC Mean | OOS IC IR | 判定 |");
        sb.AppendLine($"|------|-----------|----------|------------|-------------|-----------|------|");
        foreach (var (fName, _) in factorPanel.OrderBy(kv => kv.Key))
        {
            var isSnaps = isPanel.GetValueOrDefault(fName, new());
            var oosSnaps = oosPanel.GetValueOrDefault(fName, new());
            if (isSnaps.Count < 20) continue;
            var isIc = evaluator.AnalyzeIC(isSnaps);
            IcAnalysisResult oosIc = new() { IsInsufficientData = true };
            if (oosSnaps.Count >= 20) oosIc = evaluator.AnalyzeIC(oosSnaps);
            var ov = DetectOverfit(isIc, oosIc);
            var oosMean = oosIc.IsInsufficientData ? "N/A" : $"{oosIc.MeanIC:F4}";
            var oosIr = oosIc.IsInsufficientData ? "N/A" : $"{oosIc.ICIR:F4}";
            sb.AppendLine($"| {fName} | {isIc.MeanIC:F4} | {isIc.ICIR:F4} | {isIc.PositiveRatio:P0} | {oosMean} | {oosIr} | {ov.Verdict} |");
        }
        sb.AppendLine();

        // ── 5. 保存报告 ──
        if (string.IsNullOrEmpty(outputPath))
        {
            var baseName = isDuckDbMode ? tableName : Path.GetFileNameWithoutExtension(panelPath);
            outputPath = Path.Combine(
                Path.GetDirectoryName(panelPath) ?? ".",
                $"{baseName}.ic-report.md");
        }
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n  Report: {outputPath}");

        return 0;
    }

    // ═══════════════════════════════════════════
    // 数据加载
    // ═══════════════════════════════════════════

    private static async Task<Dictionary<string, List<FactorSnapshot>>> LoadFromParquetDuckDB(
        string parquetPath, List<string> filterFactors)
    {
        using var conn = new DuckDBConnection("Data Source=:memory:");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM '{parquetPath}'";
        using var reader = await cmd.ExecuteReaderAsync();
        return BuildPanel(reader, filterFactors);
    }

    private static async Task<Dictionary<string, List<FactorSnapshot>>> LoadFromDuckDB(
        string dbPath, string tableName, List<string> filterFactors)
    {
        using var conn = new DuckDBConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {tableName}";
        using var reader = await cmd.ExecuteReaderAsync();
        return BuildPanel(reader, filterFactors);
    }

    /// <summary>
    /// 从 DbDataReader 构建因子面板。
    /// 返回: factorName → List&lt;FactorSnapshot&gt; (每天一个快照, 键=instrument_id)
    /// </summary>
    private static Dictionary<string, List<FactorSnapshot>> BuildPanel(
        System.Data.Common.DbDataReader reader, List<string> filterFactors)
    {
        // 1. 发现列
        var factorCols = new List<(string Name, int Ordinal)>();
        var fwdCols = new List<(string BaseFactor, int Ordinal)>();
        int idxDate = -1, idxInst = -1;
        var factorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (name == "trading_day") idxDate = i;
            else if (name == "instrument_id") idxInst = i;
            else if (name is "bar_time" or "period") { /* skip */ }
            else if (name.StartsWith("FwdRet_"))
            {
                fwdCols.Add((name["FwdRet_".Length..], i));
            }
            else
            {
                if (filterFactors.Count == 0 || filterFactors.Contains(name))
                {
                    factorCols.Add((name, i));
                    factorNames.Add(name);
                }
            }
        }

        if (idxDate < 0 || idxInst < 0)
            return new();

        // 2. 前向收益策略
        bool hasUniversalFwdRet = fwdCols.Count > 0
            && fwdCols.All(f => !factorNames.Contains(f.BaseFactor));
        int universalFwdOrdinal = hasUniversalFwdRet ? fwdCols[0].Ordinal : -1;

        // 3. 第一遍: 按 (factor, day) → (inst → value), 按 day → (inst → fwdRet)
        //    intermediate: factorName → day → (inst → factorValue)
        var raw = new Dictionary<string, Dictionary<DateTime, Dictionary<string, double>>>();
        var fwdByDay = new Dictionary<DateTime, Dictionary<string, double>>();

        while (reader.Read())
        {
            var date = ReadDateTime(reader, idxDate);
            if (date == default) continue;
            var inst = reader.GetString(idxInst);
            if (string.IsNullOrEmpty(inst)) continue;

            // 读取所有因子值
            foreach (var (fName, fOrd) in factorCols)
            {
                var val = ReadDouble(reader, fOrd);
                if (!raw.TryGetValue(fName, out var byDay))
                {
                    byDay = new Dictionary<DateTime, Dictionary<string, double>>();
                    raw[fName] = byDay;
                }
                if (!byDay.TryGetValue(date, out var byInst))
                {
                    byInst = new Dictionary<string, double>();
                    byDay[date] = byInst;
                }
                byInst[inst] = val;
            }

            // 读取前向收益
            double fwdRet = 0;
            if (hasUniversalFwdRet)
            {
                fwdRet = ReadDouble(reader, universalFwdOrdinal);
            }

            if (!fwdByDay.TryGetValue(date, out var fwdInst))
            {
                fwdInst = new Dictionary<string, double>();
                fwdByDay[date] = fwdInst;
            }

            if (hasUniversalFwdRet)
                fwdInst[inst] = fwdRet;
            else
            {
                // 逐因子FwdRet: 后续在BuildSnapshot时按因子匹配
                // 这里先存所有FwdRet到临时字典
                foreach (var (baseFactor, fOrd) in fwdCols)
                {
                    fwdInst[inst + "|" + baseFactor] = ReadDouble(reader, fOrd);
                }
            }
        }

        // 4. 第二遍: 组装 FactorSnapshot (每个因子, 每天, inst→fv + inst→fwd)
        var result = new Dictionary<string, List<FactorSnapshot>>();

        foreach (var (fName, byDay) in raw)
        {
            var snapshots = new List<FactorSnapshot>();
            foreach (var (date, fvByInst) in byDay.OrderBy(kv => kv.Key))
            {
                var frByInst = new Dictionary<string, double>();

                if (hasUniversalFwdRet && fwdByDay.TryGetValue(date, out var uFwd))
                {
                    // 所有品种共用同一个 FwdRet_1d
                    foreach (var inst in fvByInst.Keys)
                        frByInst[inst] = uFwd.GetValueOrDefault(inst, 0);
                }
                else
                {
                    // 查找 FwdRet_{factorName} 的值
                    if (fwdByDay.TryGetValue(date, out var dFwd))
                    {
                        var fwdKey = "|" + fName; // key format: "ag000|IntradayMom"
                        foreach (var inst in fvByInst.Keys)
                        {
                            var lookupKey = inst + fwdKey;
                            frByInst[inst] = dFwd.GetValueOrDefault(lookupKey, 0);
                        }
                    }
                }

                snapshots.Add(new FactorSnapshot
                {
                    Timestamp = date,
                    FactorValues = new Dictionary<string, double>(fvByInst),
                    ForwardReturns = frByInst,
                });
            }

            result[fName] = snapshots;
        }

        return result;
    }

    // ═══════════════════════════════════════════
    // 过拟合检测
    // ═══════════════════════════════════════════

    private static (bool SignMatch, double IrRatio, string Verdict) DetectOverfit(
        IcAnalysisResult isIc, IcAnalysisResult oosIc)
    {
        var signMatch = isIc.MeanIC * (oosIc.IsInsufficientData ? isIc.MeanIC : oosIc.MeanIC) > 0;
        var irRatio = oosIc.IsInsufficientData ? 1.0
            : (isIc.ICIR != 0 ? Math.Abs(oosIc.ICIR / isIc.ICIR) : 0);
        var absOosIr = Math.Abs(oosIc.ICIR);
        var absIsIr = Math.Abs(isIc.ICIR);

        var verdict = (signMatch, irRatio, oosIc.IsInsufficientData) switch
        {
            (false, _, _) => "⚠️ IS/OOS 符号反转 — 强烈过拟合",
            (_, < 0.3, _) => "⚠️ 严重过拟合 (OOS IR 衰减 >70%)",
            (_, < 0.5, _) => "⚠️ 过拟合 (OOS IR 衰减 >50%)",
            (_, < 0.7, _) => "⚠️ 轻度衰减",
            (true, >= 0.7, false) => absOosIr >= 0.3 ? "✅ 稳健有效" : "✅ 稳定但偏弱",
            (true, _, true) => absIsIr >= 0.3 ? "⚠️ 无OOS数据, IS有效待验证" : "❌ IS无效",
            _ => "—"
        };

        return (signMatch, irRatio, verdict);
    }

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

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
