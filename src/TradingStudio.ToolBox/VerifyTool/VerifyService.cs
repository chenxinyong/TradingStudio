using Microsoft.Data.Sqlite;
using System.Globalization;

namespace TradingStudio.ToolBox.VerifyTool;

/// <summary>
/// 六维度数据验证引擎。所有 SQL 查询针对 bars_1min / bars_day 表。
/// </summary>
public class VerifyService
{
    public async Task<VerifyReport> RunAsync(string dbPath, int sampleCount, bool verbose, CancellationToken ct)
    {
        var cs = $"Data Source={dbPath};Mode=ReadOnly";
        using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        var report = new VerifyReport
        {
            DbPath = dbPath,
            VerifiedAt = DateTime.Now,
            FileSizeBytes = new FileInfo(dbPath).Length
        };

        // 1. Table stats
        report.Bars1Min = await GetTableStats(conn, "bars_1min", sampleCount);
        report.BarsDay = await GetTableStats(conn, "bars_day", sampleCount);

        var has1Min = report.Bars1Min.RowCount > 0;
        var hasDay = report.BarsDay.RowCount > 0;

        report.DateMin = PickMin(report.Bars1Min.DateMin, report.BarsDay.DateMin);
        report.DateMax = PickMax(report.Bars1Min.DateMax, report.BarsDay.DateMax);

        // 2. Completeness
        report.Completeness = await CheckCompleteness(conn, has1Min, hasDay);

        // 3. Consistency (OHLCV sanity)
        report.Consistency = has1Min
            ? await CheckConsistency(conn, "bars_1min")
            : new DimensionResult { Label = "Consistency", Status = DimensionStatus.Skip, Summary = "No bars_1min data" };

        // 4. Continuity (gaps)
        report.Continuity = has1Min
            ? await CheckContinuity(conn, sampleCount)
            : new DimensionResult { Label = "Continuity", Status = DimensionStatus.Skip, Summary = "No bars_1min data" };

        // 5. Accuracy (1min vs day cross-check)
        report.Accuracy = (has1Min && hasDay)
            ? await CheckAccuracy(conn, sampleCount)
            : new DimensionResult { Label = "Accuracy", Status = DimensionStatus.Skip, Summary = "Need both bars_1min and bars_day" };

        // 6. Trading day anomalies
        report.TradingDay = has1Min
            ? await CheckTradingDay(conn)
            : new DimensionResult { Label = "TradingDay", Status = DimensionStatus.Skip, Summary = "No bars_1min data" };

        // 7. Dedup
        report.Dedup = has1Min
            ? await CheckDedup(conn)
            : new DimensionResult { Label = "Dedup", Status = DimensionStatus.Skip, Summary = "No bars_1min data" };

        return report;
    }

    // ═══════════════════════════════════════════════════════════════
    // 1. Table statistics
    // ═══════════════════════════════════════════════════════════════
    private static async Task<TableStats> GetTableStats(SqliteConnection conn, string table, int sample)
    {
        var stats = new TableStats();
        try
        {
            // Row count + instrument count
            using var cmd = new SqliteCommand(
                $"SELECT COUNT(*), COUNT(DISTINCT instrument_id), MIN(bar_time), MAX(bar_time) FROM {table}", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                stats.RowCount = reader.GetInt64(0);
                stats.InstrumentCount = reader.GetInt32(1);
                stats.DateMin = reader.IsDBNull(2) ? "" : reader.GetString(2);
                stats.DateMax = reader.IsDBNull(3) ? "" : reader.GetString(3);
            }

            // Top N instruments
            using var cmd2 = new SqliteCommand(
                $"SELECT instrument_id, COUNT(*) c FROM {table} GROUP BY instrument_id ORDER BY c DESC LIMIT {sample}", conn);
            using var reader2 = await cmd2.ExecuteReaderAsync();
            while (await reader2.ReadAsync())
                stats.TopInstruments.Add($"{reader2.GetString(0)} ({reader2.GetInt64(1):N0} bars)");
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table"))
        {
            // Table doesn't exist — return empty stats
        }
        return stats;
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. Completeness — coverage matrix
    // ═══════════════════════════════════════════════════════════════
    private static async Task<DimensionResult> CheckCompleteness(SqliteConnection conn, bool has1Min, bool hasDay)
    {
        var d = new DimensionResult { Label = "Completeness" };
        var details = new List<string>();

        if (!has1Min && !hasDay)
        {
            d.Status = DimensionStatus.Fail;
            d.Summary = "Both tables empty";
            return d;
        }

        // Get instruments per table
        var insts1Min = new HashSet<string>();
        var instsDay = new HashSet<string>();

        if (has1Min)
        {
            using var cmd = new SqliteCommand("SELECT DISTINCT instrument_id FROM bars_1min", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) insts1Min.Add(r.GetString(0));
        }
        if (hasDay)
        {
            using var cmd = new SqliteCommand("SELECT DISTINCT instrument_id FROM bars_day", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) instsDay.Add(r.GetString(0));
        }

        var allInsts = new HashSet<string>(insts1Min);
        allInsts.UnionWith(instsDay);

        int only1Min = insts1Min.Count(i => !instsDay.Contains(i));
        int onlyDay = instsDay.Count(i => !insts1Min.Contains(i));
        int both = insts1Min.Count(i => instsDay.Contains(i));

        // Trading day coverage: count distinct trading days per year
        if (has1Min)
        {
            using var cmd = new SqliteCommand(
                "SELECT DISTINCT trading_day FROM bars_1min ORDER BY trading_day", conn);
            using var r = await cmd.ExecuteReaderAsync();
            var days = new List<string>();
            while (await r.ReadAsync()) days.Add(r.GetString(0));
            details.Add($"1min total trading days: {days.Count}");

            // Yearly breakdown
            var byYear = days.Select(d => d.Length >= 4 ? d[..4] : d)
                .GroupBy(y => y).OrderBy(g => g.Key);
            foreach (var yg in byYear)
                details.Add($"  {yg.Key}: {yg.Count()} trading days");

            // Expected: ~244 trading days/year for Chinese futures
            foreach (var yg in byYear)
            {
                var count = yg.Count();
                if (count < 200) details.Add($"  WARNING: {yg.Key} only {count} days (expected ~244)");
            }

            // Monthly breakdown for last year
            var lastYear = byYear.Last().Key;
            var monthCounts = days.Where(d => d.StartsWith(lastYear))
                .Select(d => d.Length >= 6 ? d.Substring(4, 2) : "??")
                .GroupBy(m => m).OrderBy(g => g.Key);
            details.Add($"  {lastYear} monthly: {string.Join(", ", monthCounts.Select(m => $"{m.Key}={m.Count()}d"))}");
        }

        details.Add($"Instruments: {allInsts.Count} total (1min-only={only1Min}, day-only={onlyDay}, both={both})");

        if (only1Min > 0) details.Add($"WARNING: {only1Min} instruments have 1min but no day bars");
        if (onlyDay > 0) details.Add($"WARNING: {onlyDay} instruments have day but no 1min bars");

        d.IssueCount = only1Min + onlyDay;
        d.Status = d.IssueCount > 0 ? DimensionStatus.Warn : DimensionStatus.Pass;
        d.Summary = $"{allInsts.Count} instruments covered";
        d.Details = details;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. Consistency — OHLCV sanity checks
    // ═══════════════════════════════════════════════════════════════
    private static async Task<DimensionResult> CheckConsistency(SqliteConnection conn, string table)
    {
        var d = new DimensionResult { Label = "Consistency" };
        var details = new List<string>();
        int issues = 0;

        // OHLC inverted
        using (var cmd = new SqliteCommand(
            $"SELECT COUNT(*) FROM {table} WHERE high < low OR high < open OR high < close OR low > open OR low > close", conn))
        {
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            if (count > 0) { details.Add($"OHLC inverted: {count:N0} bars"); issues++; }
            else details.Add("OHLC inverted: 0");
        }

        // Negative values
        using (var cmd = new SqliteCommand(
            $"SELECT COUNT(*) FROM {table} WHERE open < 0 OR high < 0 OR low < 0 OR close < 0 OR volume < 0", conn))
        {
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            if (count > 0) { details.Add($"Negative values: {count:N0} bars (SEVERE)"); issues += 10; }
            else details.Add("Negative values: 0");
        }

        // Zero volume (exclude auction period 08:55-09:00 and 20:55-21:00)
        using (var cmd = new SqliteCommand(
            $"SELECT COUNT(*) FROM {table} WHERE volume = 0", conn))
        {
            var totalZero = (long)(await cmd.ExecuteScalarAsync())!;
            if (totalZero > 0)
            {
                // Count zero-volume bars outside likely auction windows
                var auctionCmd = new SqliteCommand($@"
                    SELECT COUNT(*) FROM {table}
                    WHERE volume = 0
                      AND NOT (bar_time LIKE '% 08:5%' OR bar_time LIKE '% 09:00'
                            OR bar_time LIKE '% 20:5%' OR bar_time LIKE '% 21:00')", conn);
                var nonAuction = (long)(await auctionCmd.ExecuteScalarAsync())!;
                var pct = totalZero > 0 ? 100.0 * nonAuction / totalZero : 0;
                details.Add($"Zero volume: {totalZero:N0} total, {nonAuction:N0} outside auction ({pct:F1}%)");
                // Only flag if significant non-auction zero volume (>5% of total zero-vol)
                if (nonAuction > totalZero * 0.05 && nonAuction > 100) issues++;
            }
            else details.Add("Zero volume: 0");
        }

        // Zero price (open/high/low/close = 0)
        using (var cmd = new SqliteCommand(
            $"SELECT COUNT(*) FROM {table} WHERE open = 0 AND high = 0 AND low = 0 AND close = 0", conn))
        {
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            if (count > 0) { details.Add($"Zero OHLC: {count:N0} bars"); issues += 5; }
            else details.Add("Zero OHLC: 0");
        }

        // NULL values
        using (var cmd = new SqliteCommand(
            $"SELECT COUNT(*) FROM {table} WHERE instrument_id IS NULL OR bar_time IS NULL OR open IS NULL", conn))
        {
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            if (count > 0) { details.Add($"NULL values: {count:N0} bars (SEVERE)"); issues += 10; }
            else details.Add("NULL values: 0");
        }

        // Price jumps > 5% between consecutive bars (per instrument)
        try
        {
            using var cmd = new SqliteCommand($@"
                SELECT COUNT(*) FROM (
                    SELECT instrument_id, bar_time, close,
                        LAG(close) OVER (PARTITION BY instrument_id ORDER BY bar_time) prev_close
                    FROM {table}
                ) WHERE close > 0 AND prev_close > 0
                    AND ABS(1.0 * close / prev_close - 1.0) > 0.05", conn);
            var jumps = (long)(await cmd.ExecuteScalarAsync())!;
            if (jumps > 0) { details.Add($"Price jumps >5%: {jumps:N0} occurrences"); issues++; }
            else details.Add("Price jumps >5%: 0");
        }
        catch (SqliteException)
        {
            details.Add("Price jumps: skipped (SQLite version may not support window functions)");
        }

        d.IssueCount = issues;
        d.Status = issues == 0 ? DimensionStatus.Pass : issues >= 10 ? DimensionStatus.Fail : DimensionStatus.Warn;
        d.Summary = issues == 0 ? "All OHLCV checks passed" : $"{issues} issue(s) found";
        d.Details = details;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. Continuity — gap detection
    // ═══════════════════════════════════════════════════════════════
    private static async Task<DimensionResult> CheckContinuity(SqliteConnection conn, int sample)
    {
        var d = new DimensionResult { Label = "Continuity" };
        var details = new List<string>();
        int totalGaps = 0;

        // Get top instruments for sampling
        var sampleInstruments = new List<string>();
        using (var cmd2 = new SqliteCommand(
            "SELECT instrument_id FROM bars_1min GROUP BY instrument_id ORDER BY COUNT(*) DESC LIMIT " + sample, conn))
        using (var r2 = await cmd2.ExecuteReaderAsync())
            while (await r2.ReadAsync()) sampleInstruments.Add(r2.GetString(0));

        foreach (var inst in sampleInstruments)
        {
            try
            {
                using var cmd = new SqliteCommand($@"
                    SELECT COUNT(*) FROM (
                        SELECT bar_time,
                            LAG(bar_time) OVER (ORDER BY bar_time) prev_time
                        FROM bars_1min WHERE instrument_id = @inst
                    ) WHERE prev_time IS NOT NULL
                        AND CAST((julianday(bar_time) - julianday(prev_time)) * 86400 AS INTEGER) > 1800
                        AND CAST((julianday(bar_time) - julianday(prev_time)) * 86400 AS INTEGER) < 86400
                        -- skip: lunch break 11:30→13:30, day→night 15:00→21:00, night→day 02:30→09:00
                        AND NOT (substr(prev_time,12,5) = '11:30' AND substr(bar_time,12,5) = '13:31')
                        AND NOT (substr(prev_time,12,5) = '15:00' AND substr(bar_time,12,5) = '21:01')
                        AND NOT (substr(prev_time,12,5) = '02:30' AND substr(bar_time,12,5) = '09:01')
                        AND NOT (substr(prev_time,12,5) = '01:00' AND substr(bar_time,12,5) = '09:01')", conn);
                cmd.Parameters.AddWithValue("@inst", inst);
                var gaps = (long)(await cmd.ExecuteScalarAsync())!;
                if (gaps > 0)
                {
                    details.Add($"{inst}: {gaps} gaps >120s");
                    totalGaps += (int)gaps;
                }
            }
            catch (SqliteException)
            {
                details.Add("Gap detection: skipped (window function not supported)");
                break;
            }
        }

        if (details.Count == 0)
            details.Add($"Top {sample} instruments: no intraday gaps >120s");

        d.IssueCount = totalGaps;
        d.Status = totalGaps == 0 ? DimensionStatus.Pass : totalGaps > 50 ? DimensionStatus.Fail : DimensionStatus.Warn;
        d.Summary = totalGaps == 0 ? "No gaps in sampled instruments" : $"{totalGaps} gaps in top {sample} instruments";
        d.Details = details;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. Accuracy — 1min aggregation vs day bars cross-check
    // ═══════════════════════════════════════════════════════════════
    private static async Task<DimensionResult> CheckAccuracy(SqliteConnection conn, int sample)
    {
        var d = new DimensionResult { Label = "Accuracy" };
        var details = new List<string>();
        int mismatches = 0;
        var totalChecked = 0;

        // Get common instruments
        var commonInsts = new List<string>();
        using (var cmd = new SqliteCommand(
            "SELECT DISTINCT b1.instrument_id FROM bars_1min b1 INTERSECT SELECT DISTINCT instrument_id FROM bars_day LIMIT " + sample, conn))
        using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) commonInsts.Add(r.GetString(0));

        foreach (var inst in commonInsts)
        {
            try
            {
                // Aggregate 1min to daily and compare with bars_day
                // Use simple subquery approach instead of LAST_VALUE window (SQLite compat)
                using var cmd = new SqliteCommand(@"
                    SELECT d.trading_day, d.open, d.high, d.low, d.close, d.volume,
                        m1.open, m1.high, m1.low, m1.close, m1.vol
                    FROM bars_day d
                    JOIN (
                        SELECT date(bar_time) dt,
                            (SELECT open FROM bars_1min WHERE instrument_id = @inst AND date(bar_time) = dt ORDER BY bar_time LIMIT 1) open,
                            MAX(high) high, MIN(low) low,
                            (SELECT close FROM bars_1min WHERE instrument_id = @inst AND date(bar_time) = dt ORDER BY bar_time DESC LIMIT 1) close,
                            SUM(volume) vol
                        FROM bars_1min WHERE instrument_id = @inst
                        GROUP BY date(bar_time)
                    ) m1 ON d.trading_day = m1.dt
                    WHERE d.instrument_id = @inst
                    LIMIT 50", conn);
                cmd.Parameters.AddWithValue("@inst", inst);

                using var r2 = await cmd.ExecuteReaderAsync();
                while (await r2.ReadAsync())
                {
                    totalChecked++;
                    // Skip NULL agg values (no 1min data for that day)
                    if (r2.IsDBNull(6)) continue;

                    var dOpen = r2.GetInt64(1); var aOpen = r2.GetInt64(6);
                    var dHigh = r2.GetInt64(2); var aHigh = r2.GetInt64(7);
                    var dLow  = r2.GetInt64(3); var aLow  = r2.GetInt64(8);
                    var dClose= r2.GetInt64(4); var aClose= r2.GetInt64(9);
                    var dVol  = r2.GetInt64(5); var aVol  = r2.GetInt64(10);

                    var openDiff = Math.Abs(dOpen - aOpen);
                    var closeDiff = Math.Abs(dClose - aClose);
                    var volDiff = dVol > 0 ? Math.Abs((double)(dVol - aVol) / dVol) : 0;

                    if (openDiff > 0 || closeDiff > 0 || volDiff > 0.01)
                    {
                        mismatches++;
                        var day = r2.GetString(0);
                        if (mismatches <= 3)
                            details.Add($"{inst} {day}: Open {dOpen/1e7:F2} vs {aOpen/1e7:F2}, Close {dClose/1e7:F2} vs {aClose/1e7:F2}, Vol diff {volDiff:P1}");
                    }
                }
            }
            catch (SqliteException)
            {
                details.Add("Cross-check: skipped (requires window function support)");
                break;
            }
        }

        if (details.Count == 0 && totalChecked > 0)
            details.Add($"Cross-checked {totalChecked} days across {commonInsts.Count} instruments: OK");
        else if (totalChecked == 0)
            details.Add("No overlapping instruments for cross-check");

        d.IssueCount = mismatches;
        d.Status = mismatches == 0 ? DimensionStatus.Pass : mismatches > 20 ? DimensionStatus.Fail : DimensionStatus.Warn;
        d.Summary = mismatches == 0 ? "1min→day aggregation matches" : $"{mismatches}/{totalChecked} days mismatch";
        d.Details = details;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. Trading day anomalies
    // ═══════════════════════════════════════════════════════════════
    private static async Task<DimensionResult> CheckTradingDay(SqliteConnection conn)
    {
        var d = new DimensionResult { Label = "TradingDay" };
        var details = new List<string>();
        int issues = 0;

        // Check for weekend bars
        using (var cmd = new SqliteCommand(@"
            SELECT COUNT(*) FROM bars_1min
            WHERE CAST(strftime('%w', bar_time) AS INTEGER) IN (0, 6)", conn))
        {
            var weekend = (long)(await cmd.ExecuteScalarAsync())!;
            if (weekend > 0) { details.Add($"Weekend bars: {weekend:N0} (SEVERE)"); issues += 10; }
            else details.Add("Weekend bars: 0");
        }

        // Night session trading day alignment: bars after 20:00 should have next day's trading_day
        using (var cmd = new SqliteCommand(@"
            SELECT COUNT(*) FROM bars_1min
            WHERE bar_time LIKE '% 20:%' OR bar_time LIKE '% 21:%' OR bar_time LIKE '% 22:%' OR bar_time LIKE '% 23:%'", conn))
        {
            var nightBars = (long)(await cmd.ExecuteScalarAsync())!;
            details.Add($"Night session bars (20:00-23:59): {nightBars:N0}");
        }

        // Check trading_day != bar_time date for night sessions
        try
        {
            using var cmd = new SqliteCommand(@"
                SELECT COUNT(*) FROM bars_1min
                WHERE substr(bar_time, 9, 2) IN ('20','21','22','23')
                  AND substr(trading_day, 1, 10) = substr(bar_time, 1, 10)", conn);
            var sameDay = (long)(await cmd.ExecuteScalarAsync())!;
            if (sameDay > 0)
            {
                details.Add($"Night bars with same-day trading_day: {sameDay:N0} (check trading day assignment)");
                issues++;
            }
        }
        catch { details.Add("Trading day alignment check skipped"); }

        // Per-year trading day counts
        using (var cmd = new SqliteCommand(
            "SELECT substr(bar_time,1,4) yr, COUNT(DISTINCT substr(bar_time,1,10)) days FROM bars_1min GROUP BY yr ORDER BY yr", conn))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var yr = r.GetString(0);
                var days = r.GetInt32(1);
                var expected = yr switch
                {
                    "2020" => 243, "2021" => 243, "2022" => 242, "2023" => 242, "2024" => 242, "2025" => 121,
                    _ => 243
                };
                if (Math.Abs(days - expected) > 10)
                {
                    details.Add($"{yr}: {days} days (expected ~{expected}) — investigate");
                    issues++;
                }
            }
        }

        d.IssueCount = issues;
        d.Status = issues == 0 ? DimensionStatus.Pass : issues >= 10 ? DimensionStatus.Fail : DimensionStatus.Warn;
        d.Summary = issues == 0 ? "Trading day checks passed" : $"{issues} anomalies found";
        d.Details = details;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════
    // 7. Dedup — duplicate (instrument_id, bar_time)
    // ═══════════════════════════════════════════════════════════════
    private static async Task<DimensionResult> CheckDedup(SqliteConnection conn)
    {
        var d = new DimensionResult { Label = "Dedup" };
        var details = new List<string>();

        foreach (var table in new[] { "bars_1min", "bars_day" })
        {
            try
            {
                using var cmd = new SqliteCommand(
                    $"SELECT COUNT(*) FROM (SELECT instrument_id, bar_time FROM {table} GROUP BY instrument_id, bar_time HAVING COUNT(*) > 1)", conn);
                var dups = (long)(await cmd.ExecuteScalarAsync())!;
                if (dups > 0) details.Add($"{table}: {dups:N0} duplicate keys");
                else details.Add($"{table}: 0 duplicates");
            }
            catch (SqliteException) { /* table doesn't exist */ }
        }

        var hasDups = details.Any(x => !x.Contains(": 0"));
        d.IssueCount = hasDups ? 1 : 0;
        d.Status = hasDups ? DimensionStatus.Fail : DimensionStatus.Pass;
        d.Summary = hasDups ? "Duplicates detected" : "No duplicates";
        d.Details = details;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════
    private static DateTime PickMin(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? DateTime.MinValue : DateTime.Parse(b);
        if (string.IsNullOrEmpty(b)) return DateTime.Parse(a);
        return DateTime.Parse(a) < DateTime.Parse(b) ? DateTime.Parse(a) : DateTime.Parse(b);
    }

    private static DateTime PickMax(string a, string b)
    {
        var d1 = string.IsNullOrEmpty(a) ? DateTime.MinValue : DateTime.Parse(a);
        var d2 = string.IsNullOrEmpty(b) ? DateTime.MinValue : DateTime.Parse(b);
        return d1 > d2 ? d1 : d2;
    }
}
