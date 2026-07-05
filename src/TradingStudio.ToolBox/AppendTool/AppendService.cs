using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.AppendTool;

/// <summary>
/// 增量追加引擎 — 将每日导入 DB 的 Bar 追加到历史 DuckDB，自动去重。
/// 支持 DuckDB→DuckDB 和 SQLite→DuckDB 两种源。
/// </summary>
public class AppendService
{
    private readonly ILogger<AppendService> _log;

    public AppendService(ILogger<AppendService> log) => _log = log;

    /// <summary>
    /// 将 source DB 中指定表的 Bar 追加到 target DuckDB。
    /// 去重策略：跳过 target 中已存在的 (instrument_id, bar_time) 组合。
    /// </summary>
    public async Task<AppendResult> RunAsync(
        string sourcePath, string targetPath,
        string[]? tables = null,
        CancellationToken ct = default)
    {
        tables ??= new[] { "bars_1min", "bars_day" };
        var result = new AppendResult { SourcePath = sourcePath, TargetPath = targetPath };

        var isSourceDuck = sourcePath.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase);

        using var target = new DuckDBConnection($"Data Source={targetPath}");
        await target.OpenAsync(ct);

        foreach (var table in tables)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var tableResult = isSourceDuck
                    ? await AppendFromDuckDB(target, sourcePath, table, ct)
                    : await AppendFromSqlite(target, sourcePath, table, ct);
                result.Tables.Add(tableResult);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Append {Table} failed — skipped", table);
                result.Tables.Add(new AppendTableResult { TableName = table, Status = $"Failed: {ex.Message}" });
            }
        }

        // Rebuild indexes
        if (result.Tables.Any(t => t.RowsAppended > 0))
        {
            _log.LogInformation("Rebuilding indexes...");
            await RebuildIndexes(target, ct);
        }

        result.TotalAppended = result.Tables.Sum(t => t.RowsAppended);
        result.TotalSkipped = result.Tables.Sum(t => t.RowsSkipped);

        _log.LogInformation("Append done: +{Appended:N0} rows, skipped {Skipped:N0} duplicates",
            result.TotalAppended, result.TotalSkipped);

        return result;
    }

    private async Task<AppendTableResult> AppendFromDuckDB(
        DuckDBConnection target, string sourcePath, string table, CancellationToken ct)
    {
        var r = new AppendTableResult { TableName = table };

        // ATTACH source database
        var srcAlias = "src_db";
        using (var attachCmd = target.CreateCommand())
        {
            attachCmd.CommandText = $"ATTACH IF NOT EXISTS '{sourcePath.Replace("\\", "/")}' AS {srcAlias}";
            await attachCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            // Count source rows
            using var cntCmd = target.CreateCommand();
            cntCmd.CommandText = $"SELECT COUNT(*) FROM {srcAlias}.\"{table}\"";
            var srcCount = (long)(await cntCmd.ExecuteScalarAsync(ct))!;
            _log.LogInformation("  [{Table}] Source: {Count:N0} rows", table, srcCount);
            if (srcCount == 0) { r.Status = "Empty source"; return r; }

            // Ensure target table exists (clone schema from attached source)
            await EnsureTableFromAttached(target, srcAlias, table, ct);

            // Build temp table for dedup join
            var tmpTable = $"_tmp_{table}_{DateTime.Now:HHmmss}";
            try
            {
                // Create temp table from attached source
                using (var cmd = target.CreateCommand())
                {
                    cmd.CommandText = $"CREATE TEMP TABLE \"{tmpTable}\" AS SELECT * FROM {srcAlias}.\"{table}\"";
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // Count duplicates
                using (var cmd = target.CreateCommand())
                {
                    cmd.CommandText = $@"
                        SELECT COUNT(*) FROM ""{tmpTable}"" t
                        WHERE EXISTS (SELECT 1 FROM ""{table}"" h
                            WHERE h.instrument_id = t.instrument_id AND h.bar_time = t.bar_time)";
                    r.RowsSkipped = (long)(await cmd.ExecuteScalarAsync(ct))!;
                }

                // Insert only new rows
                using (var cmd = target.CreateCommand())
                {
                    cmd.CommandText = $@"
                        INSERT INTO ""{table}""
                        SELECT t.* FROM ""{tmpTable}"" t
                        WHERE NOT EXISTS (SELECT 1 FROM ""{table}"" h
                            WHERE h.instrument_id = t.instrument_id AND h.bar_time = t.bar_time)";
                    r.RowsAppended = await cmd.ExecuteNonQueryAsync(ct);
                }

                _log.LogInformation("  [{Table}] +{Added:N0} new, skipped {Skipped:N0} existing",
                    table, r.RowsAppended, r.RowsSkipped);
                r.Status = "OK";
            }
            finally
            {
                try { using var c = target.CreateCommand(); c.CommandText = $"DROP TABLE IF EXISTS \"{tmpTable}\""; c.ExecuteNonQuery(); }
                catch { /* cleanup best-effort */ }
            }
        }
        finally
        {
            // DETACH source
            try { using var c = target.CreateCommand(); c.CommandText = $"DETACH {srcAlias}"; c.ExecuteNonQuery(); }
            catch { /* DETACH best-effort, DB conn closing anyway */ }
        }

        return r;
    }

    private async Task<AppendTableResult> AppendFromSqlite(
        DuckDBConnection target, string sourcePath, string table, CancellationToken ct)
    {
        var r = new AppendTableResult { TableName = table };

        using var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
        await source.OpenAsync(ct);

        // Count source rows
        using var cntCmd = source.CreateCommand();
        cntCmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        var srcCount = (long)(await cntCmd.ExecuteScalarAsync(ct))!;
        _log.LogInformation("  [{Table}] Source: {Count:N0} rows (SQLite)", table, srcCount);
        if (srcCount == 0) { r.Status = "Empty source"; return r; }

        // Read all source rows → batch insert with dedup
        // For SQLite source, use DuckDB appender with row-by-row dedup check
        var selectCmd = source.CreateCommand();
        selectCmd.CommandText = $"SELECT * FROM \"{table}\"";
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        // Get column list
        var cols = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));

        await EnsureTableFromColumns(target, table, cols, reader, ct);

        // Use appender for speed
        using var appender = target.CreateAppender(table);
        long appended = 0, skipped = 0;

        while (await reader.ReadAsync(ct))
        {
            var row = appender.CreateRow();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (await reader.IsDBNullAsync(i, ct))
                    row.AppendNullValue();
                else
                {
                    // DuckDB Appender requires typed calls — dispatch by runtime type
                    var val = reader.GetValue(i);
                    if (val is string s) row.AppendValue(s);
                    else if (val is long l) row.AppendValue(l);
                    else if (val is int ii) row.AppendValue((long)ii);
                    else if (val is double d) row.AppendValue(d);
                    else if (val is float f) row.AppendValue((double)f);
                    else if (val is decimal m) row.AppendValue((double)m);
                    else row.AppendValue(val.ToString() ?? "");
                }
            }
            try { row.EndRow(); appended++; }
            catch (DuckDBException) { skipped++; /* duplicate key */ }
        }
        appender.Close();

        r.RowsAppended = appended;
        r.RowsSkipped = skipped;
        _log.LogInformation("  [{Table}] +{Added:N0} new, skipped {Skipped:N0} duplicates", table, appended, skipped);
        r.Status = "OK";
        return r;
    }

    private async Task EnsureTableFromAttached(DuckDBConnection target, string srcAlias, string table, CancellationToken ct)
    {
        // Check if table exists
        using var check = target.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}'";
        var exists = (long)(await check.ExecuteScalarAsync(ct))! > 0;
        if (exists) return;

        // Clone DDL from attached source
        using var ddl = target.CreateCommand();
        ddl.CommandText = $"SELECT sql FROM duckdb_tables() WHERE table_name = '{table}'";
        var sql = (string)(await ddl.ExecuteScalarAsync(ct))!;
        if (string.IsNullOrEmpty(sql)) return;

        using var create = target.CreateCommand();
        create.CommandText = sql;
        await create.ExecuteNonQueryAsync(ct);
        _log.LogInformation("  Created target table: {Table}", table);
    }

    private async Task EnsureTableFromColumns(DuckDBConnection target, string table,
        List<string> cols, System.Data.Common.DbDataReader reader, CancellationToken ct)
    {
        using var check = target.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}'";
        var exists = (long)(await check.ExecuteScalarAsync(ct))! > 0;
        if (exists) return;

        var colDefs = cols.Select(c =>
        {
            var type = reader.GetDataTypeName(cols.IndexOf(c)).ToUpperInvariant() switch
            {
                "INTEGER" => "BIGINT",
                "REAL" or "FLOAT" or "DOUBLE" => "DOUBLE",
                _ => "VARCHAR"
            };
            return $"\"{c}\" {type}";
        });
        var ddl = $"CREATE TABLE \"{table}\" ({string.Join(", ", colDefs)}, PRIMARY KEY (instrument_id, bar_time))";
        using var cmd = target.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("  Created target table: {Table}", table);
    }

    private static async Task RebuildIndexes(DuckDBConnection conn, CancellationToken ct)
    {
        var indexes = new[] {
            ("idx_1min_inst_time", "bars_1min", "instrument_id, bar_time"),
            ("idx_day_inst_time",  "bars_day",  "instrument_id, bar_time"),
        };
        foreach (var (name, table, cols) in indexes)
        {
            try
            {
                using var c = conn.CreateCommand();
                c.CommandText = $"CREATE INDEX IF NOT EXISTS \"{name}\" ON \"{table}\" ({cols})";
                await c.ExecuteNonQueryAsync(ct);
            }
            catch { }
        }
    }
}

public class AppendResult
{
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public long TotalAppended { get; set; }
    public long TotalSkipped { get; set; }
    public List<AppendTableResult> Tables { get; } = new();
}

public class AppendTableResult
{
    public string TableName { get; set; } = "";
    public long RowsAppended { get; set; }
    public long RowsSkipped { get; set; }
    public string Status { get; set; } = "";
}
