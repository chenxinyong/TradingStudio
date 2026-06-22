using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace TradingStudio.Data.Storage;

/// <summary>
/// 多周期聚合引擎 — 从 bars_1min 构建 5min/15min/day/week 表。
/// 仅处理连续合约（xxx000），增量模式仅处理新日期。
/// </summary>
public class BuildPeriodsService
{
    private readonly ILogger<BuildPeriodsService> _log;

    public BuildPeriodsService(ILogger<BuildPeriodsService> log) => _log = log;

    /// <summary>
    /// 构建/刷新所有多周期表（仅连续合约 xxx000）。
    /// </summary>
    /// <param name="dbPath">DuckDB 路径</param>
    /// <param name="fullRebuild">true=全量重建, false=仅处理新日期（默认）</param>
    public async Task<BuildPeriodsResult> RunAsync(string dbPath, bool fullRebuild = false, CancellationToken ct = default)
    {
        var result = new BuildPeriodsResult { DbPath = dbPath };

        using var conn = new DuckDBConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);

        // 确定需处理的日期范围
        string? dateFilter = null;
        if (!fullRebuild)
        {
            dateFilter = await GetLastProcessedDate(conn, "bars_5min", ct);
            _log.LogInformation("Incremental mode: from {Date}", dateFilter ?? "beginning");
        }

        // Step 0: 自动生成缺失的连续合约（从月份合约计算最活跃主力）
        var generated = await BuildContinuousContractsAsync(conn, dateFilter, ct);
        if (generated > 0)
            _log.LogInformation("Generated {Count:N0} continuous contract bars (xxx000) from individual contracts", generated);

        // 发现连续合约
        var contContracts = await DiscoverContinuousContracts(conn, ct);
        _log.LogInformation("Found {Count} continuous contracts (xxx000)", contContracts.Count);
        if (contContracts.Count == 0)
        {
            _log.LogWarning("No continuous contracts found — nothing to build");
            return result;
        }

        // 每个 period 依次构建
        var periods = new[] { 5, 15 };
        var extraTables = new[] { "bars_day", "bars_week" };

        foreach (var periodMin in periods)
        {
            if (ct.IsCancellationRequested) break;
            var dstTable = $"bars_{periodMin}min";
            try
            {
                var rows = await BuildMinutePeriod(conn, periodMin, contContracts, dstTable, dateFilter, ct);
                result.Tables[dstTable] = rows;
                _log.LogInformation("  {Table}: {Rows:N0} rows", dstTable, rows);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "  {Table} failed", dstTable);
                result.Errors.Add($"{dstTable}: {ex.Message}");
            }
        }

        // bars_day — aggregate from bars_1min for continuous contracts
        try
        {
            var dayRows = await BuildDayBars(conn, contContracts, dateFilter, ct);
            result.Tables["bars_day (cont)"] = dayRows;
            _log.LogInformation("  bars_day (cont): {Rows:N0} rows", dayRows);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "  bars_day (cont) failed");
            result.Errors.Add($"bars_day (cont): {ex.Message}");
        }

        // bars_week — aggregate from bars_day
        try
        {
            var weekRows = await BuildWeekBars(conn, contContracts, ct);
            result.Tables["bars_week"] = weekRows;
            _log.LogInformation("  bars_week: {Rows:N0} rows", weekRows);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "  bars_week failed");
            result.Errors.Add($"bars_week: {ex.Message}");
        }

        result.TotalRows = result.Tables.Values.Sum();
        _log.LogInformation("Build periods done: {Rows:N0} total rows across {Tables} tables",
            result.TotalRows, result.Tables.Count);

        return result;
    }

    private async Task<long> BuildMinutePeriod(
        DuckDBConnection conn, int periodMin, List<string> contContracts,
        string dstTable, string? dateFilter, CancellationToken ct)
    {
        // CREATE TABLE IF NOT EXISTS
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {dstTable} (
                    instrument_id VARCHAR NOT NULL,
                    trading_day   VARCHAR NOT NULL,
                    bar_time      VARCHAR NOT NULL,
                    open          BIGINT NOT NULL,
                    high          BIGINT NOT NULL,
                    low           BIGINT NOT NULL,
                    close         BIGINT NOT NULL,
                    volume        BIGINT NOT NULL,
                    turnover      DOUBLE NOT NULL,
                    open_interest DOUBLE NOT NULL,
                    tick_count    INTEGER DEFAULT 0,
                    PRIMARY KEY (instrument_id, bar_time)
                )";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Full rebuild: truncate; incremental: ON CONFLICT handles upsert below
        if (dateFilter == null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {dstTable}";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Build filter clause
        var dateClause = dateFilter != null ? $"AND bar_time >= '{dateFilter}'" : "";

        // Use DuckDB time_bucket for aggregation
        var instList = string.Join("', '", contContracts);
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = $@"
            INSERT INTO {dstTable} BY NAME
            SELECT
                instrument_id,
                MIN(trading_day) AS trading_day,
                CAST(time_bucket(INTERVAL '{periodMin} minutes', CAST(bar_time AS TIMESTAMP)) AS VARCHAR) AS bar_time,
                FIRST(open ORDER BY bar_time) AS open,
                MAX(high) AS high,
                MIN(low) AS low,
                LAST(close ORDER BY bar_time) AS close,
                SUM(volume) AS volume,
                SUM(turnover) AS turnover,
                LAST(open_interest ORDER BY bar_time) AS open_interest,
                SUM(tick_count) AS tick_count
            FROM bars_1min
            WHERE instrument_id IN ('{instList}')
                {dateClause}
            GROUP BY instrument_id, time_bucket(INTERVAL '{periodMin} minutes', CAST(bar_time AS TIMESTAMP))
            ON CONFLICT (instrument_id, bar_time) DO UPDATE SET
                open          = EXCLUDED.open,
                high          = EXCLUDED.high,
                low           = EXCLUDED.low,
                close         = EXCLUDED.close,
                volume        = EXCLUDED.volume,
                turnover      = EXCLUDED.turnover,
                open_interest = EXCLUDED.open_interest,
                tick_count    = EXCLUDED.tick_count,
                trading_day   = EXCLUDED.trading_day";
        return await cmd2.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> BuildDayBars(
        DuckDBConnection conn, List<string> contContracts,
        string? dateFilter, CancellationToken ct)
    {
        var dateClause = dateFilter != null ? $"AND bar_time >= '{dateFilter}'" : "";

        // bars_day lacks PRIMARY KEY (instrument_id, bar_time) — use DELETE+INSERT
        if (dateFilter != null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM bars_day WHERE instrument_id LIKE '%000' AND bar_time >= '{dateFilter}'";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM bars_day WHERE instrument_id LIKE '%000'";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var instList = string.Join("', '", contContracts);
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = $@"
            INSERT INTO bars_day
            SELECT
                instrument_id,
                MIN(trading_day) AS trading_day,
                CAST(CAST(bar_time AS DATE) AS VARCHAR) || ' 00:00:00' AS bar_time,
                FIRST(open ORDER BY bar_time) AS open,
                MAX(high) AS high,
                MIN(low) AS low,
                LAST(close ORDER BY bar_time) AS close,
                SUM(volume) AS volume,
                SUM(turnover) AS turnover,
                LAST(open_interest ORDER BY bar_time) AS open_interest,
                SUM(tick_count) AS tick_count
            FROM bars_1min
            WHERE instrument_id IN ('{instList}')
                {dateClause}
            GROUP BY instrument_id, CAST(bar_time AS DATE)";
        return await cmd2.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> BuildWeekBars(
        DuckDBConnection conn, List<string> contContracts, CancellationToken ct)
    {
        // Ensure table exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS bars_week (
                    instrument_id VARCHAR NOT NULL,
                    trading_day   VARCHAR NOT NULL,
                    bar_time      VARCHAR NOT NULL,
                    open          BIGINT NOT NULL,
                    high          BIGINT NOT NULL,
                    low           BIGINT NOT NULL,
                    close         BIGINT NOT NULL,
                    volume        BIGINT NOT NULL,
                    turnover      DOUBLE NOT NULL,
                    open_interest DOUBLE NOT NULL,
                    tick_count    INTEGER DEFAULT 0,
                    PRIMARY KEY (instrument_id, bar_time)
                )";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Full rebuild (week data is small): truncate first
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM bars_week";
        await delCmd.ExecuteNonQueryAsync(ct);

        var instList = string.Join("', '", contContracts);
        using var insCmd = conn.CreateCommand();
        insCmd.CommandText = $@"
            INSERT INTO bars_week BY NAME
            SELECT
                instrument_id,
                MIN(trading_day) AS trading_day,
                CAST(time_bucket(INTERVAL '7 days', CAST(bar_time AS TIMESTAMP)) AS VARCHAR) AS bar_time,
                FIRST(open ORDER BY bar_time) AS open,
                MAX(high) AS high,
                MIN(low) AS low,
                LAST(close ORDER BY bar_time) AS close,
                SUM(volume) AS volume,
                SUM(turnover) AS turnover,
                LAST(open_interest ORDER BY bar_time) AS open_interest,
                SUM(tick_count) AS tick_count
            FROM bars_day
            WHERE instrument_id IN ('{instList}')
            GROUP BY instrument_id, time_bucket(INTERVAL '7 days', CAST(bar_time AS TIMESTAMP))
            ON CONFLICT (instrument_id, bar_time) DO UPDATE SET
                open          = EXCLUDED.open,
                high          = EXCLUDED.high,
                low           = EXCLUDED.low,
                close         = EXCLUDED.close,
                volume        = EXCLUDED.volume,
                turnover      = EXCLUDED.turnover,
                open_interest = EXCLUDED.open_interest,
                tick_count    = EXCLUDED.tick_count,
                trading_day   = EXCLUDED.trading_day";
        return await insCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> DiscoverContinuousContracts(DuckDBConnection conn, CancellationToken ct)
    {
        var contracts = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT instrument_id FROM bars_1min WHERE instrument_id LIKE '%000' ORDER BY instrument_id";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) contracts.Add(reader.GetString(0));
        return contracts;
    }

    /// <summary>
    /// 从月份合约生成连续合约（xxx000）— 每日取成交量最大的合约作为主力。
    /// DuckDB QUALIFY + ROW_NUMBER 实现，比子查询更高效。
    /// </summary>
    private async Task<long> BuildContinuousContractsAsync(
        DuckDBConnection conn, string? dateFilter, CancellationToken ct)
    {
        var dateClause = dateFilter != null ? $"AND bar_time >= '{dateFilter}'" : "";

        // 生成所有产品的 000 合约：每个交易日取成交量最大合约的 Bar
        // 使用 strftime + CAST 兼容 TIMESTAMP 和 VARCHAR 两种 bar_time 类型
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO bars_1min BY NAME
            WITH daily_best AS (
                SELECT
                    strftime(CAST(bar_time AS TIMESTAMP), '%Y-%m-%d') AS tday,
                    REGEXP_REPLACE(instrument_id, '[0-9]+[A-Za-z]?$', '') || '000' AS cont_id,
                    instrument_id,
                    SUM(volume) AS total_vol
                FROM bars_1min
                WHERE instrument_id NOT LIKE '%000'
                  AND NOT REGEXP_MATCHES(instrument_id, '[0-9]+[A-Za-z]$')  -- 排除期权
                  AND volume > 0
                  {dateClause}
                GROUP BY tday, cont_id, instrument_id
                QUALIFY ROW_NUMBER() OVER (PARTITION BY tday, cont_id ORDER BY total_vol DESC) = 1
            )
            SELECT
                db.cont_id AS instrument_id,
                b1.trading_day,
                b1.bar_time,
                b1.open,
                b1.high,
                b1.low,
                b1.close,
                b1.volume,
                b1.turnover,
                b1.open_interest,
                b1.tick_count
            FROM bars_1min b1
            INNER JOIN daily_best db
                ON b1.instrument_id = db.instrument_id
                AND strftime(CAST(b1.bar_time AS TIMESTAMP), '%Y-%m-%d') = db.tday
            WHERE NOT EXISTS (
                SELECT 1 FROM bars_1min ex
                WHERE ex.instrument_id = db.cont_id
                  AND ex.bar_time = b1.bar_time
            )";
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows;
    }

    private static async Task<string?> GetLastProcessedDate(DuckDBConnection conn, string table, CancellationToken ct)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT MAX(bar_time) FROM {table}";
            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val is DBNull) return null;
            var maxDate = val.ToString() ?? "";
            // Return the max date minus 5 days to ensure overlap coverage
            if (maxDate.Length >= 10)
            {
                var dt = DateTime.Parse(maxDate[..10]);
                return dt.AddDays(-5).ToString("yyyy-MM-dd");
            }
            return null;
        }
        catch { return null; }
    }
}

public class BuildPeriodsResult
{
    public string DbPath { get; set; } = "";
    public long TotalRows { get; set; }
    public Dictionary<string, long> Tables { get; } = new();
    public List<string> Errors { get; } = new();
}
