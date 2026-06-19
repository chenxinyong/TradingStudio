using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.ContinuousTool;

/// <summary>
/// 加权连续合约构建引擎。
///
/// 算法（决策确认 2026-06-18）：
///   1. 每日选取一个品种全部活跃合约
///   2. 权重 = normalize(0.5×volume + 0.5×avg_oi)，最大5合约，最小5%
///   3. 换月检测：次主力连续3天成交量 > 当前主力 → 触发
///   4. 比率法回溯调整（ratio back-adjust）
///
/// 输出：data/continuous/{品种}_continuous.duckdb
///   - continuous_day: 加权日线 OHLCV
///   - rollover_log: 换月记录
/// </summary>
public class ContinuousService
{
    private readonly ILogger<ContinuousService> _log;

    // DB代码映射
    private static readonly Dictionary<string, string> CodeMap = new()
    {
        ["m2"] = "m", ["p2"] = "p", ["v2"] = "v", ["i2"] = "i", ["y2"] = "y",
        ["c2"] = "c", ["l2"] = "l", ["a2"] = "a", ["b2"] = "b", ["j2"] = "j",
    };

    public ContinuousService(ILogger<ContinuousService> log)
    {
        _log = log;
    }

    /// <summary>
    /// 为指定品种构建加权连续合约。
    /// </summary>
    public async Task<ContinuousResult> BuildAsync(
        string dbPath, string varietyCode, string outputDir, CancellationToken ct = default)
    {
        var result = new ContinuousResult { VarietyCode = varietyCode };
        var outputPath = Path.Combine(outputDir, $"{varietyCode}_continuous.duckdb");

        // 删除旧输出
        if (File.Exists(outputPath)) File.Delete(outputPath);

        using var duck = new DuckDBConnection($"Data Source={outputPath}");
        await duck.OpenAsync(ct);

        using var srcDuck = new DuckDBConnection($"Data Source={dbPath};access_mode=read_only");
        await srcDuck.OpenAsync(ct);

        // 1. 加载每日合约分布
        var dailyContracts = await LoadDailyContractsAsync(srcDuck, varietyCode, ct);
        if (dailyContracts.Count == 0)
        {
            _log.LogWarning("No data for {Variety}", varietyCode);
            return result;
        }

        _log.LogInformation("{Variety}: {Days} trading days, {Contracts} contracts",
            varietyCode, dailyContracts.Count, dailyContracts.SelectMany(d => d.Contracts).Select(c => c.InstrumentId).Distinct().Count());

        // 2. 计算每日权重
        var weightedDays = ComputeWeights(dailyContracts);
        result.WeightedDays = weightedDays;

        // 3. 换月检测
        var rollovers = DetectRollovers(weightedDays);
        result.Rollovers = rollovers;
        _log.LogInformation("{Variety}: {Count} rollover events detected", varietyCode, rollovers.Count);

        // 4. 比率法回溯调整
        var adjusted = ApplyBackAdjust(weightedDays, rollovers);
        result.AdjustedDays = adjusted;

        // 5. 生成 1min 加权 Bar
        Set1MinOutput(duck);
        _log.LogInformation("{Variety}: building 1min continuous...", varietyCode);
        var weighted1Min = await Build1MinBarsAsync(srcDuck, weightedDays, adjusted, ct);
        result.Weighted1MinCount = weighted1Min;
        Flush1MinBuffer();
        _1minAppender?.Close();
        _1minAppender = null;
        // 1min 索引
        using (var idxCmd = duck.CreateCommand())
        {
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_cont1m_time ON continuous_1min(trading_day, bar_time)";
            idxCmd.ExecuteNonQuery();
        }

        // 6. 从 1min 合成多周期 Bar
        var multiPeriodMinutes = new[] { 5, 15 };
        foreach (var period in multiPeriodMinutes)
        {
            _log.LogInformation("{Variety}: building {Period}min...", varietyCode, period);
            await BuildMultiPeriodAsync(duck, period, ct);
        }

        // 7. 写入 DuckDB（day + rollover）
        await WriteOutputAsync(duck, adjusted, rollovers, ct);

        // 8. 周线（从 continuous_day 聚合，必须在 WriteOutput 之后）
        _log.LogInformation("{Variety}: building weekly...", varietyCode);
        await BuildWeeklyAsync(duck, ct);
        result.OutputPath = outputPath;
        result.OutputSizeBytes = new FileInfo(outputPath).Length;

        _log.LogInformation("{Variety}: done → {Path} ({Size:F1} MB)",
            varietyCode, outputPath, result.OutputSizeBytes / 1024.0 / 1024.0);

        return result;
    }

    // ─── Step 1: Load daily contracts ───

    private async Task<List<DailySnapshot>> LoadDailyContractsAsync(
        DuckDBConnection srcDuck, string varietyCode, CancellationToken ct)
    {
        using var cmd = srcDuck.CreateCommand();
        var pattern = $"{varietyCode}%";
        cmd.CommandText = $@"
            SELECT
                trading_day,
                instrument_id,
                CAST(SUM(volume) AS DOUBLE) AS total_vol,
                CAST(AVG(open_interest) AS DOUBLE) AS avg_oi,
                CAST(MIN(open) AS DOUBLE) AS open_p,
                CAST(MAX(high) AS DOUBLE) AS high_p,
                CAST(MIN(low) AS DOUBLE) AS low_p,
                CAST(AVG(close) AS DOUBLE) AS close_p,
                CAST(SUM(turnover) AS DOUBLE) AS turnover
            FROM bars_day
            WHERE instrument_id ILIKE '{pattern}'
            GROUP BY trading_day, instrument_id
            ORDER BY trading_day, instrument_id";

        var days = new Dictionary<string, DailySnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var day = reader.GetString(0);
            if (!days.TryGetValue(day, out var snap))
                days[day] = snap = new DailySnapshot { TradingDay = day };

            snap.Contracts.Add(new ContractDay
            {
                InstrumentId = reader.GetString(1),
                Volume = reader.GetDouble(2),
                AvgOpenInterest = reader.GetDouble(3),
                Open = reader.GetDouble(4),
                High = reader.GetDouble(5),
                Low = reader.GetDouble(6),
                Close = reader.GetDouble(7),
                Turnover = reader.GetDouble(8),
            });
        }

        return days.Values.OrderBy(d => d.TradingDay).ToList();
    }

    // ─── Step 2: Compute weights ───

    private List<WeightedDay> ComputeWeights(List<DailySnapshot> snapshots)
    {
        var result = new List<WeightedDay>();
        foreach (var snap in snapshots)
        {
            if (snap.Contracts.Count == 0) continue;

            var contracts = snap.Contracts.OrderByDescending(c => c.Volume).Take(5).ToList();
            double totalScore = contracts.Sum(c => 0.5 * c.Volume + 0.5 * c.AvgOpenInterest);
            if (totalScore <= 0) continue;

            var weighted = new WeightedDay { TradingDay = snap.TradingDay };

            foreach (var c in contracts)
            {
                double rawWeight = (0.5 * c.Volume + 0.5 * c.AvgOpenInterest) / totalScore;
                if (rawWeight < 0.05) continue; // 最小 5%

                weighted.Contracts.Add(new WeightedContract
                {
                    InstrumentId = c.InstrumentId,
                    Weight = rawWeight,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume,
                    AvgOpenInterest = c.AvgOpenInterest,
                });
            }

            if (weighted.Contracts.Count == 0) continue;

            // 归一化（去除低于5%的合约后重新归一）
            double sumW = weighted.Contracts.Sum(c => c.Weight);
            foreach (var c in weighted.Contracts) c.Weight /= sumW;

            // 加权 OHLCV
            weighted.Open = weighted.Contracts.Sum(c => c.Open * c.Weight);
            weighted.High = weighted.Contracts.Sum(c => c.High * c.Weight);
            weighted.Low = weighted.Contracts.Sum(c => c.Low * c.Weight);
            weighted.Close = weighted.Contracts.Sum(c => c.Close * c.Weight);
            weighted.Volume = weighted.Contracts.Sum(c => c.Volume * c.Weight);
            weighted.DominantContract = contracts[0].InstrumentId;

            result.Add(weighted);
        }

        return result;
    }

    // ─── Step 3: Detect rollovers ───

    private List<RolloverEvent> DetectRollovers(List<WeightedDay> days)
    {
        var rollovers = new List<RolloverEvent>();
        if (days.Count < 5) return rollovers;

        string? currentDominant = null;
        int crossoverCount = 0;
        string? challengingContract = null;
        WeightedDay? rolloverDay = null;

        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var top2 = day.Contracts.OrderByDescending(c => c.Volume).Take(2).ToList();
            if (top2.Count < 2) continue;

            var first = top2[0].InstrumentId;
            var second = top2[1].InstrumentId;

            if (currentDominant == null)
            {
                currentDominant = first;
                continue;
            }

            // 检测交叉：次主力成交量 > 当前主力
            if (first != currentDominant)
            {
                if (challengingContract == first)
                    crossoverCount++;
                else
                {
                    challengingContract = first;
                    crossoverCount = 1;
                }

                if (crossoverCount >= 3 && first != currentDominant)
                {
                    // 确认换月
                    var oldContract = currentDominant;
                    var newContract = first;

                    // 计算换月价差
                    var oldClose = top2.FirstOrDefault(c => c.InstrumentId == oldContract)?.Close ?? 0;
                    var newClose = top2.FirstOrDefault(c => c.InstrumentId == first)?.Close ?? 0;
                    var ratio = oldClose > 0 ? newClose / oldClose : 1.0;

                    rollovers.Add(new RolloverEvent
                    {
                        RolloverDate = day.TradingDay,
                        OldDominant = oldContract,
                        NewDominant = newContract,
                        PriceRatio = ratio,
                        OldClose = oldClose,
                        NewClose = newClose,
                    });

                    currentDominant = newContract;
                    challengingContract = null;
                    crossoverCount = 0;
                    rolloverDay = day;
                }
            }
            else
            {
                // 主力不变，重置
                challengingContract = null;
                crossoverCount = 0;
            }
        }

        return rollovers;
    }

    // ─── Step 4: Ratio back-adjust ───

    private List<AdjustedDay> ApplyBackAdjust(List<WeightedDay> days, List<RolloverEvent> rollovers)
    {
        if (rollovers.Count == 0)
        {
            return days.Select(d => new AdjustedDay
            {
                TradingDay = d.TradingDay,
                Open = d.Open, High = d.High, Low = d.Low, Close = d.Close,
                Volume = d.Volume, DominantContract = d.DominantContract,
                AdjustmentFactor = 1.0, RawClose = d.Close
            }).ToList();
        }

        // 从前往后：每个换月日之前的数据乘以后续所有ratio的乘积
        var result = new List<AdjustedDay>();
        var sortedRollovers = rollovers.OrderBy(r => r.RolloverDate).ToList();
        var reverseRollovers = rollovers.OrderByDescending(r => r.RolloverDate).ToList();

        // 构建累积因子：在某日期之前的数据需要乘以该日期之后所有ratio的乘积
        foreach (var day in days)
        {
            // 计算该日期的累积调整因子
            double factor = 1.0;
            foreach (var ro in sortedRollovers)
            {
                if (string.Compare(day.TradingDay, ro.RolloverDate) < 0)
                    factor *= ro.PriceRatio;
            }

            result.Add(new AdjustedDay
            {
                TradingDay = day.TradingDay,
                Open = day.Open * factor,
                High = day.High * factor,
                Low = day.Low * factor,
                Close = day.Close * factor,
                Volume = day.Volume,
                DominantContract = day.DominantContract,
                AdjustmentFactor = factor,
                RawClose = day.Close,
            });
        }

        return result;
    }

    // ─── Step 5: Build 1min weighted bars ───

    private async Task<long> Build1MinBarsAsync(
        DuckDBConnection srcDuck,
        List<WeightedDay> weightedDays,
        List<AdjustedDay> adjusted,
        CancellationToken ct)
    {
        // 构建每天调整因子和合约权重映射
        var adjMap = adjusted.ToDictionary(a => a.TradingDay, a => a.AdjustmentFactor);
        var weightMap = new Dictionary<string, Dictionary<string, double>>(); // day → contract → weight
        foreach (var wd in weightedDays)
        {
            var cw = new Dictionary<string, double>();
            foreach (var c in wd.Contracts)
                cw[c.InstrumentId] = c.Weight;
            weightMap[wd.TradingDay] = cw;
        }

        long total1Min = 0;
        var batchSize = 60; // 一次处理 60 个交易日
        var allDays = weightMap.Keys.OrderBy(d => d).ToList();

        for (int batchStart = 0; batchStart < allDays.Count; batchStart += batchSize)
        {
            var batchDays = allDays.Skip(batchStart).Take(batchSize).ToList();
            var dayList = string.Join("','", batchDays);
            var allContracts = batchDays.SelectMany(d => weightMap[d].Keys).Distinct().ToList();

            if (allContracts.Count == 0) continue;

            var contractList = string.Join("','", allContracts);

            using var cmd = srcDuck.CreateCommand();
            cmd.CommandText = $@"
                SELECT trading_day, bar_time, instrument_id,
                       open, high, low, close, volume, turnover, open_interest
                FROM bars_1min
                WHERE instrument_id IN ('{contractList}')
                  AND trading_day IN ('{dayList}')
                ORDER BY trading_day, bar_time, instrument_id";

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            string currentDay = "", currentBarTime = "";
            double wOpen = 0, wHigh = 0, wLow = 0, wClose = 0, wVolume = 0;

            while (await reader.ReadAsync(ct))
            {
                var day = reader.GetString(0);
                var barTime = reader.GetString(1);
                var instId = reader.GetString(2);
                var open = reader.GetDouble(3);
                var high = reader.GetDouble(4);
                var low = reader.GetDouble(5);
                var close = reader.GetDouble(6);
                var volume = reader.GetDouble(7);

                if (!weightMap.TryGetValue(day, out var cw) || !cw.TryGetValue(instId, out var w))
                    continue;

                var rowKey = $"{day}|{barTime}";
                if (rowKey != $"{currentDay}|{currentBarTime}")
                {
                    if (currentBarTime != "" && wVolume > 0)
                    {
                        var adj = adjMap.GetValueOrDefault(currentDay, 1.0);
                        await Write1MinRow(currentDay, currentBarTime,
                            wOpen * adj, wHigh * adj, wLow * adj, wClose * adj, wVolume, adj);
                        total1Min++;
                    }
                    currentDay = day;
                    currentBarTime = barTime;
                    // 第一个合约的加权值
                    wOpen = open * w; wHigh = high * w; wLow = low * w; wClose = close * w; wVolume = volume * w;
                }
                else
                {
                    // 加权合成：多个合约的 OHLC 按权重融合
                    wOpen += open * w;
                    wHigh += high * w;
                    wLow += low * w;
                    wClose += close * w;
                    wVolume += volume * w;
                }
            }
            if (currentBarTime != "" && wVolume > 0)
            {
                var adj = adjMap.GetValueOrDefault(currentDay, 1.0);
                await Write1MinRow(currentDay, currentBarTime,
                    wOpen * adj, wHigh * adj, wLow * adj, wClose * adj, wVolume, adj);
                total1Min++;
            }

            await reader.DisposeAsync();
        }

        Flush1MinBuffer();
        return total1Min;
    }

    // 1min 行缓冲（避免逐行写 DuckDB）
    private readonly List<(string, string, double, double, double, double, double, double)> _1minBuffer = new();

    private async Task Write1MinRow(string day, string minute,
        double open, double high, double low, double close, double volume, double adj)
    {
        _1minBuffer.Add((day, minute, open, high, low, close, volume, adj));
        if (_1minBuffer.Count >= 50000)
            Flush1MinBuffer();
    }

    private DuckDBConnection? _1minDuck;
    private DuckDBAppender? _1minAppender;

    private void Set1MinOutput(DuckDBConnection duck)
    {
        _1minDuck = duck;
        using var cmd = duck.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE continuous_1min (
                trading_day VARCHAR,
                bar_time VARCHAR,
                open DOUBLE,
                high DOUBLE,
                low DOUBLE,
                close DOUBLE,
                volume DOUBLE,
                adjustment_factor DOUBLE
            )";
        cmd.ExecuteNonQuery();
        _1minAppender = duck.CreateAppender("continuous_1min");
    }

    private void Flush1MinBuffer()
    {
        if (_1minBuffer.Count == 0 || _1minAppender == null) return;
        foreach (var (day, minute, open, high, low, close, volume, adj) in _1minBuffer)
        {
            var row = _1minAppender.CreateRow();
            row.AppendValue(day);
            row.AppendValue(minute);
            row.AppendValue(open);
            row.AppendValue(high);
            row.AppendValue(low);
            row.AppendValue(close);
            row.AppendValue(volume);
            row.AppendValue(adj);
            row.EndRow();
        }
        _1minBuffer.Clear();
    }

    // ─── Step 6: Build multi-period bars from 1min ───

    private async Task BuildMultiPeriodAsync(DuckDBConnection duck, int minutes, CancellationToken ct)
    {
        var table = $"continuous_{minutes}min";
        using var cmd = duck.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE {table} AS
            WITH rounded AS (
                SELECT
                    trading_day,
                    bar_time,
                    DATE_TRUNC('hour', CAST(bar_time AS TIMESTAMP))
                        + INTERVAL ({minutes} * (EXTRACT(MINUTE FROM CAST(bar_time AS TIMESTAMP)) // {minutes})) MINUTE
                        AS bar_period,
                    open, high, low, close, volume
                FROM continuous_1min
            )
            SELECT
                trading_day,
                bar_period::VARCHAR AS bar_time,
                FIRST(open ORDER BY bar_time) AS open,
                MAX(high) AS high,
                MIN(low) AS low,
                LAST(close ORDER BY bar_time) AS close,
                SUM(volume) AS volume,
                COUNT(*) AS tick_count
            FROM rounded
            GROUP BY trading_day, bar_period
            ORDER BY bar_period
        ";
        await cmd.ExecuteNonQueryAsync(ct);

        // 索引
        using var idxCmd = duck.CreateCommand();
        idxCmd.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{table}_time ON {table}(trading_day, bar_time)";
        await idxCmd.ExecuteNonQueryAsync(ct);

        var cnt = await CountAsync(duck, table, ct);
        _log.LogInformation("  {Table}: {Count:N0} rows", table, cnt);
    }

    private static async Task<long> CountAsync(DuckDBConnection duck, string table, CancellationToken ct)
    {
        using var cmd = duck.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : Convert.ToInt64(result);
    }

    // ─── Step 6b: Build weekly bars from daily ───

    private async Task BuildWeeklyAsync(DuckDBConnection duck, CancellationToken ct)
    {
        using var cmd = duck.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE continuous_week AS
            WITH week_assign AS (
                SELECT
                    trading_day,
                    -- ISO week: Monday start
                    CAST(trading_day AS DATE) - INTERVAL (EXTRACT(DOW FROM CAST(trading_day AS DATE)) - 1) DAY AS week_start,
                    open, high, low, close, volume
                FROM continuous_day
            )
            SELECT
                week_start::VARCHAR AS week_start,
                FIRST(trading_day ORDER BY trading_day) AS first_day,
                LAST(trading_day ORDER BY trading_day) AS last_day,
                COUNT(*) AS day_count,
                -- 周 Open = 周一 Open
                FIRST(open ORDER BY trading_day) AS open,
                MAX(high) AS high,
                MIN(low) AS low,
                -- 周 Close = 周五 Close
                LAST(close ORDER BY trading_day) AS close,
                SUM(volume) AS volume
            FROM week_assign
            GROUP BY week_start
            ORDER BY week_start
        ";
        await cmd.ExecuteNonQueryAsync(ct);

        using var idxCmd = duck.CreateCommand();
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_cont_week ON continuous_week(week_start)";
        await idxCmd.ExecuteNonQueryAsync(ct);

        var cnt = await CountAsync(duck, "continuous_week", ct);
        _log.LogInformation("  continuous_week: {Count:N0} rows", cnt);
    }

    // ─── Step 7: Write DuckDB output ───

    private async Task WriteOutputAsync(
        DuckDBConnection duck,
        List<AdjustedDay> adjusted,
        List<RolloverEvent> rollovers,
        CancellationToken ct)
    {
        // Create continuous_day table
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE continuous_day (
                    trading_day VARCHAR,
                    open DOUBLE,
                    high DOUBLE,
                    low DOUBLE,
                    close DOUBLE,
                    volume DOUBLE,
                    dominant_contract VARCHAR,
                    adjustment_factor DOUBLE,
                    raw_close DOUBLE
                )";
            cmd.ExecuteNonQuery();
        }

        // Batch insert
        using var appender = duck.CreateAppender("continuous_day");
        foreach (var d in adjusted)
        {
            var row = appender.CreateRow();
            row.AppendValue(d.TradingDay);
            row.AppendValue(d.Open);
            row.AppendValue(d.High);
            row.AppendValue(d.Low);
            row.AppendValue(d.Close);
            row.AppendValue(d.Volume);
            row.AppendValue(d.DominantContract);
            row.AppendValue(d.AdjustmentFactor);
            row.AppendValue(d.RawClose);
            row.EndRow();
        }
        appender.Close();

        // Create index
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX idx_cont_day ON continuous_day(trading_day)";
            cmd.ExecuteNonQuery();
        }

        // Create rollover_log table
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE rollover_log (
                    rollover_date VARCHAR,
                    old_dominant VARCHAR,
                    new_dominant VARCHAR,
                    price_ratio DOUBLE,
                    old_close DOUBLE,
                    new_close DOUBLE
                )";
            cmd.ExecuteNonQuery();
        }

        if (rollovers.Count > 0)
        {
            using var rollAppender = duck.CreateAppender("rollover_log");
            foreach (var r in rollovers)
            {
                var row = rollAppender.CreateRow();
                row.AppendValue(r.RolloverDate);
                row.AppendValue(r.OldDominant);
                row.AppendValue(r.NewDominant);
                row.AppendValue(r.PriceRatio);
                row.AppendValue(r.OldClose);
                row.AppendValue(r.NewClose);
                row.EndRow();
            }
            rollAppender.Close();
        }
    }
}

// ────────────────────────────────────────
// Data models
// ────────────────────────────────────────

public class DailySnapshot
{
    public string TradingDay { get; set; } = "";
    public List<ContractDay> Contracts { get; set; } = new();
}

public class ContractDay
{
    public string InstrumentId { get; set; } = "";
    public double Volume { get; set; }
    public double AvgOpenInterest { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Turnover { get; set; }
}

public class WeightedDay
{
    public string TradingDay { get; set; } = "";
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
    public string DominantContract { get; set; } = "";
    public List<WeightedContract> Contracts { get; set; } = new();
}

public class WeightedContract
{
    public string InstrumentId { get; set; } = "";
    public double Weight { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
    public double AvgOpenInterest { get; set; }
}

public class RolloverEvent
{
    public string RolloverDate { get; set; } = "";
    public string OldDominant { get; set; } = "";
    public string NewDominant { get; set; } = "";
    public double PriceRatio { get; set; }
    public double OldClose { get; set; }
    public double NewClose { get; set; }
}

public class AdjustedDay
{
    public string TradingDay { get; set; } = "";
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
    public string DominantContract { get; set; } = "";
    public double AdjustmentFactor { get; set; }
    public double RawClose { get; set; }
}

public class ContinuousResult
{
    public string VarietyCode { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public long OutputSizeBytes { get; set; }
    public long Weighted1MinCount { get; set; }
    public List<WeightedDay> WeightedDays { get; set; } = new();
    public List<RolloverEvent> Rollovers { get; set; } = new();
    public List<AdjustedDay> AdjustedDays { get; set; } = new();
}
