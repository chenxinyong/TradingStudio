using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.AnalyzeTool;

/// <summary>
/// 品种分析引擎 — 基于 DuckDB 的品种排名、合约周期分类、研究报告生成。
/// 查询模式：DuckDB 原生 SQL，利用复合索引 (instrument_id, bar_time)。
/// </summary>
public class AnalyzeService
{
    private readonly ILogger<AnalyzeService> _log;

    // DB 代码 → 标准代码映射（DCE suffix-2 修正）
    private static readonly Dictionary<string, string> CodeMap = new()
    {
        ["m2"] = "m", ["p2"] = "p", ["v2"] = "v", ["i2"] = "i", ["y2"] = "y",
        ["c2"] = "c", ["l2"] = "l", ["a2"] = "a", ["b2"] = "b", ["j2"] = "j",
        ["CS"] = "cs", ["BB"] = "bb", ["FB"] = "fb", ["JD"] = "jd",
        ["PP"] = "pp", ["EG"] = "eg", ["EB"] = "eb", ["PG"] = "pg", ["LH"] = "lh",
        ["RR"] = "rr", ["JM"] = "jm",
    };

    public AnalyzeService(ILogger<AnalyzeService> log)
    {
        _log = log;
    }

    /// <summary>获取 2-char 品种代码的标准形式</summary>
    public static string NormalizeCode(string rawCode)
        => CodeMap.TryGetValue(rawCode, out var mapped) ? mapped : rawCode.ToLowerInvariant();

    // ────────────────────────────────────────
    // 排名
    // ────────────────────────────────────────

    public async Task<List<VarietyRank>> RankAsync(string dbPath, RankDimension dimension, int topN = 50, CancellationToken ct = default)
    {
        using var duck = new DuckDBConnection($"Data Source={dbPath}");
        await duck.OpenAsync(ct);

        var metric = dimension switch
        {
            RankDimension.Volume => "SUM(volume)",
            RankDimension.Turnover => "SUM(turnover)",
            RankDimension.OpenInterest => "AVG(open_interest)",
            RankDimension.BarCount => "COUNT(*)",
            RankDimension.TradingDays => "COUNT(DISTINCT trading_day)",
            _ => "SUM(volume)"
        };
        var orderDir = "DESC";

        using var cmd = duck.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                SUBSTR(instrument_id, 1, 2) AS variety,
                COUNT(*)::BIGINT AS bar_count,
                CAST(SUM(volume) AS DOUBLE) AS total_vol,
                CAST(SUM(turnover) AS DOUBLE) AS total_turnover,
                CAST(AVG(open_interest) AS DOUBLE) AS avg_oi,
                CAST(COUNT(DISTINCT trading_day) AS INTEGER) AS trading_days,
                CAST(COUNT(DISTINCT instrument_id) AS INTEGER) AS contract_count,
                MIN(trading_day) AS first_date,
                MAX(trading_day) AS last_date
            FROM bars_1min
            GROUP BY variety
            ORDER BY {metric} {orderDir}
            LIMIT {topN}";

        var results = new List<VarietyRank>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        int rank = 0;
        while (await reader.ReadAsync(ct))
        {
            rank++;
            results.Add(new VarietyRank
            {
                Rank = rank,
                Code = NormalizeCode(reader.GetString(0)),
                RawCode = reader.GetString(0),
                BarCount = reader.GetInt64(1),
                TotalVolume = reader.GetDouble(2),
                TotalTurnover = reader.GetDouble(3),
                AvgOpenInterest = reader.GetDouble(4),
                TradingDays = reader.GetInt32(5),
                ContractCount = reader.GetInt32(6),
                FirstDate = reader.GetString(7),
                LastDate = reader.GetString(8),
            });
        }
        return results;
    }

    // ────────────────────────────────────────
    // 合约周期分类
    // ────────────────────────────────────────

    public async Task<List<ContractCycleResult>> ClassifyAsync(string dbPath, string? year = null, CancellationToken ct = default)
    {
        using var duck = new DuckDBConnection($"Data Source={dbPath}");
        await duck.OpenAsync(ct);

        // 获取所有品种
        using var cmdV = duck.CreateCommand();
        cmdV.CommandText = "SELECT DISTINCT SUBSTR(instrument_id,1,2) AS variety FROM bars_1min ORDER BY variety";
        var varieties = new List<string>();
        await using (var r = await cmdV.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) varieties.Add(r.GetString(0));

        var results = new List<ContractCycleResult>();
        foreach (var variety in varieties)
        {
            var yearFilter = year != null ? $"AND trading_day BETWEEN '{year}-01-01' AND '{year}-12-31'" : "";

            using var cmd = duck.CreateCommand();
            var pattern = $"{variety}%";
            cmd.CommandText = $@"
                SELECT
                    instrument_id,
                    CAST(SUM(volume) AS DOUBLE) AS vol,
                    CAST(COUNT(DISTINCT trading_day) AS INTEGER) AS days,
                    MIN(trading_day) as first_day,
                    MAX(trading_day) as last_day,
                    CAST(SUM(turnover) AS DOUBLE) as turnover,
                    CAST(AVG(open_interest) AS DOUBLE) as avg_oi
                FROM bars_1min
                WHERE instrument_id LIKE '{pattern}' {yearFilter}
                GROUP BY instrument_id
                ORDER BY vol DESC";

            var contracts = new List<ContractInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                contracts.Add(new ContractInfo
                {
                    InstrumentId = reader.GetString(0),
                    Volume = reader.GetDouble(1),
                    TradingDays = reader.GetInt32(2),
                    FirstDay = reader.GetString(3),
                    LastDay = reader.GetString(4),
                    Turnover = reader.GetDouble(5),
                    AvgOpenInterest = reader.GetDouble(6),
                });
            }

            if (contracts.Count == 0) continue;

            var totalVol = contracts.Sum(c => c.Volume);
            var top3Share = contracts.Take(3).Sum(c => c.Volume) / totalVol;
            var top1Share = contracts[0].Volume / totalVol;

            // 月度分布熵（衡量合约活跃是否均匀分布在多个月份）
            var allMonths = new HashSet<string>();
            foreach (var c in contracts)
            {
                // Extract months from trading day range
                if (c.FirstDay.Length >= 7 && c.LastDay.Length >= 7)
                {
                    var start = DateTime.Parse(c.FirstDay[..7] + "-01");
                    var end = DateTime.Parse(c.LastDay[..7] + "-01");
                    for (var m = start; m <= end; m = m.AddMonths(1))
                        if (c.TradingDays > 5) // 只计数有实质交易的月份
                            allMonths.Add(m.ToString("yyyy-MM"));
                }
            }

            var cycleType = ClassifyCycle(top3Share, top1Share, contracts.Count, allMonths.Count);
            results.Add(new ContractCycleResult
            {
                Code = NormalizeCode(variety),
                RawCode = variety,
                ContractCount = contracts.Count,
                TotalVolume = totalVol,
                Top1Share = top1Share,
                Top3Share = top3Share,
                Top1Contract = contracts[0].InstrumentId,
                Top2Contract = contracts.Count > 1 ? contracts[1].InstrumentId : "",
                Top3Contract = contracts.Count > 2 ? contracts[2].InstrumentId : "",
                ActiveMonths = allMonths.Count,
                CycleType = cycleType,
                Contracts = contracts,
            });
        }

        return results.OrderByDescending(r => r.TotalVolume).ToList();
    }

    private static string ClassifyCycle(double top3Share, double top1Share, int contractCount, int activeMonths)
    {
        if (contractCount <= 3) return "少合约(新品种)";
        if (top1Share > 0.65) return "单合约主导";
        if (top3Share > 0.70) return "三主力轮换";
        if (activeMonths >= 8) return "多月活跃";
        if (top3Share > 0.50) return "偏主力集中";
        return "多月分散";
    }

    /// <summary>
    /// 计算合约周期分类的详细依据（用于报告）
    /// </summary>
    public string ClassifyDetail(ContractCycleResult r)
    {
        return r.CycleType switch
        {
            "三主力轮换" => $"Top3合约占比 {r.Top3Share:P0}（{r.Top1Contract}/{r.Top2Contract}/{r.Top3Contract}），"
                           + $"共 {r.ContractCount} 个合约，{r.ActiveMonths} 个活跃月份。主力月：1/5/9 典型年度轮换。",
            "多月活跃" => $"Top3合约仅占 {r.Top3Share:P0}，共 {r.ContractCount} 个合约、{r.ActiveMonths} 个月份有实质交易。"
                         + "流动性分散在全年多个月份。",
            "单合约主导" => $"{r.Top1Contract} 独占 {r.Top1Share:P0} 成交量。",
            "少合约(新品种)" => $"全年仅 {r.ContractCount} 个合约有交易，多为新上市或低流动性品种。",
            _ => $"Top3={r.Top3Share:P0}, 合约数={r.ContractCount}, 活跃月={r.ActiveMonths}",
        };
    }
}

// ────────────────────────────────────────
// 数据模型
// ────────────────────────────────────────

public enum RankDimension { Volume, Turnover, OpenInterest, BarCount, TradingDays }

public class VarietyRank
{
    public int Rank { get; set; }
    public string Code { get; set; } = "";
    public string RawCode { get; set; } = "";
    public long BarCount { get; set; }
    public double TotalVolume { get; set; }
    public double TotalTurnover { get; set; }
    public double AvgOpenInterest { get; set; }
    public int TradingDays { get; set; }
    public int ContractCount { get; set; }
    public string FirstDate { get; set; } = "";
    public string LastDate { get; set; } = "";
}

public class ContractCycleResult
{
    public string Code { get; set; } = "";
    public string RawCode { get; set; } = "";
    public int ContractCount { get; set; }
    public double TotalVolume { get; set; }
    public double Top1Share { get; set; }
    public double Top3Share { get; set; }
    public string Top1Contract { get; set; } = "";
    public string Top2Contract { get; set; } = "";
    public string Top3Contract { get; set; } = "";
    public int ActiveMonths { get; set; }
    public string CycleType { get; set; } = "";
    public List<ContractInfo> Contracts { get; set; } = new();
}

public class ContractInfo
{
    public string InstrumentId { get; set; } = "";
    public double Volume { get; set; }
    public int TradingDays { get; set; }
    public string FirstDay { get; set; } = "";
    public string LastDay { get; set; } = "";
    public double Turnover { get; set; }
    public double AvgOpenInterest { get; set; }
}
