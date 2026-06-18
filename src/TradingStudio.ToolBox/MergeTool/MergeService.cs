using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.MergeTool;

/// <summary>
/// 数据合并引擎 — SQLite per-year DBs → 单个 DuckDB。
/// 迁移策略：逐库逐表分批读取 SQLite，DuckDB Appender 高速写入。
/// 完成后创建复合索引以支撑品种+时间范围查询。
/// </summary>
public class MergeService
{
    private readonly ILogger<MergeService> _log;

    public MergeService(ILogger<MergeService> log)
    {
        _log = log;
    }

    /// <summary>
    /// 执行合并。
    /// </summary>
    /// <param name="sourceDir">包含 bars_*.db 的目录</param>
    /// <param name="outputPath">输出 DuckDB 文件路径</param>
    /// <param name="tables">要合并的表名，默认 bars_1min + bars_day</param>
    /// <param name="ct">取消令牌</param>
    public async Task<MergeResult> RunAsync(
        string sourceDir,
        string outputPath,
        string[] tables,
        CancellationToken ct = default)
    {
        var result = new MergeResult();
        var sourceFiles = DiscoverSourceFiles(sourceDir);
        if (sourceFiles.Length == 0)
        {
            _log.LogError("No bars_*.db found in {Dir}", sourceDir);
            return result;
        }

        _log.LogInformation("Found {Count} source DBs: {Files}",
            sourceFiles.Length, string.Join(", ", sourceFiles.Select(Path.GetFileName)));

        // 删除旧目标文件
        if (File.Exists(outputPath))
        {
            _log.LogInformation("Removing existing output: {Path}", outputPath);
            File.Delete(outputPath);
        }

        using var duck = new DuckDBConnection($"Data Source={outputPath}");
        await duck.OpenAsync(ct);

        // 逐表处理
        foreach (var table in tables)
        {
            var tableResult = await MergeTableAsync(duck, sourceFiles, table, ct);
            result.Tables.Add(tableResult);
        }

        // 创建索引
        _log.LogInformation("Creating indexes...");
        await CreateIndexesAsync(duck, ct);
        _log.LogInformation("Indexes created.");

        // 统计
        var fileInfo = new FileInfo(outputPath);
        result.OutputPath = outputPath;
        result.OutputSizeBytes = fileInfo.Exists ? fileInfo.Length : 0;
        result.TotalRows = result.Tables.Sum(t => t.TotalRows);

        _log.LogInformation("Merge complete: {Rows:N0} rows → {Path} ({Size:F1} MB)",
            result.TotalRows, outputPath, result.OutputSizeBytes / 1024.0 / 1024.0);

        return result;
    }

    private async Task<MergeResult.TableResult> MergeTableAsync(
        DuckDBConnection duck,
        string[] sourceFiles,
        string table,
        CancellationToken ct)
    {
        var tableResult = new MergeResult.TableResult { TableName = table };
        bool tableCreated = false;

        foreach (var sourceFile in sourceFiles)
        {
            var fileName = Path.GetFileName(sourceFile);
            _log.LogInformation("  [{Table}] ← {File}", table, fileName);

            using var sqlite = new SqliteConnection($"Data Source={sourceFile}");
            await sqlite.OpenAsync(ct);

            // 检查源表是否存在
            var checkCmd = sqlite.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table";
            checkCmd.Parameters.AddWithValue("@table", table);
            var exists = (long)(await checkCmd.ExecuteScalarAsync(ct))! > 0;
            if (!exists)
            {
                _log.LogWarning("    Table {Table} not found in {File}, skipping.", table, fileName);
                continue;
            }

            // 查询行数
            var countCmd = sqlite.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
            var rowCount = (long)(await countCmd.ExecuteScalarAsync(ct))!;
            _log.LogInformation("    {Rows:N0} rows to copy", rowCount);
            if (rowCount == 0) continue;

            // 读取列信息
            var pragmaCmd = sqlite.CreateCommand();
            pragmaCmd.CommandText = $"PRAGMA table_info([{table}])";
            var columns = new List<(string Name, string Type)>();
            await using var reader = await pragmaCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var colName = reader.GetString(1);
                // 跳过 SQLite 自增 id 列 — DuckDB 用 rowid
                if (colName.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
                columns.Add((colName, reader.GetString(2)));
            }
            await reader.DisposeAsync();

            // 首次创建 DuckDB 表
            if (!tableCreated)
            {
                CreateDuckTable(duck, table, columns);
                tableCreated = true;
            }

            // 使用 DuckDB Appender 高速写入
            using var appender = duck.CreateAppender(table);
            var selectSql = $"SELECT {string.Join(", ", columns.Select(c => $"[{c.Name}]"))} FROM [{table}] ORDER BY id";

            var selectCmd = sqlite.CreateCommand();
            selectCmd.CommandText = selectSql;
            await using var selectReader = await selectCmd.ExecuteReaderAsync(ct);

            long copied = 0;
            var batch = 0L;

            // 预取列索引
            var colIndices = new int[columns.Count];
            for (int i = 0; i < columns.Count; i++)
                colIndices[i] = selectReader.GetOrdinal(columns[i].Name);

            while (await selectReader.ReadAsync(ct))
            {
                var row = appender.CreateRow();
                for (int i = 0; i < columns.Count; i++)
                {
                    if (await selectReader.IsDBNullAsync(colIndices[i], ct))
                    {
                        row.AppendNullValue();
                    }
                    else
                    {
                        var colType = columns[i].Type.ToUpperInvariant();
                        if (colType == "INTEGER")
                            row.AppendValue(selectReader.GetInt64(colIndices[i]));
                        else if (colType == "REAL" || colType == "FLOAT" || colType == "DOUBLE")
                            row.AppendValue(selectReader.GetDouble(colIndices[i]));
                        else
                            row.AppendValue(selectReader.GetString(colIndices[i]));
                    }
                }
                row.EndRow();
                copied++;
                batch++;

                // 每 10 万行报告一次进度
                if (batch % 100_000 == 0)
                {
                    _log.LogInformation("    {Copied:N0} / {Total:N0}", copied, rowCount);
                    // Appender auto-flushes, but yield periodically for UI responsiveness
                    await Task.Yield();
                }
            }

            await selectReader.DisposeAsync();
            appender.Close();

            tableResult.RowsCopied += copied;
            _log.LogInformation("    Done: {Copied:N0} rows from {File}", copied, fileName);
        }

        tableResult.TotalRows = tableResult.RowsCopied;
        return tableResult;
    }

    private void CreateDuckTable(DuckDBConnection duck, string table, List<(string Name, string Type)> columns)
    {
        var colDefs = columns.Select(c =>
        {
            var duckType = c.Type.ToUpperInvariant() switch
            {
                "INTEGER" => "BIGINT",
                "REAL" or "FLOAT" or "DOUBLE" => "DOUBLE",
                _ => "VARCHAR"
            };
            return $"\"{c.Name}\" {duckType}";
        });

        var ddl = $"CREATE TABLE \"{table}\" ({string.Join(", ", colDefs)})";
        using var cmd = duck.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();

        _log.LogInformation("    Created DuckDB table: {Table}", table);
    }

    private async Task CreateIndexesAsync(DuckDBConnection duck, CancellationToken ct)
    {
        // 为主查询模式建索引：(instrument_id, bar_time)
        // DuckDB 的索引创建语法兼容标准 SQL
        var indexes = new[]
        {
            ("idx_1min_inst_time", "bars_1min", "instrument_id, bar_time"),
            ("idx_1min_trading_day", "bars_1min", "trading_day"),
            ("idx_day_inst_time", "bars_day", "instrument_id, bar_time"),
            ("idx_day_trading_day", "bars_day", "trading_day"),
        };

        foreach (var (idxName, table, cols) in indexes)
        {
            try
            {
                using var cmd = duck.CreateCommand();
                cmd.CommandText = $"CREATE INDEX IF NOT EXISTS \"{idxName}\" ON \"{table}\" ({cols})";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                // 表可能不存在
                _log.LogWarning("Index {Idx} skipped: {Msg}", idxName, ex.Message);
            }
        }
    }

    private string[] DiscoverSourceFiles(string sourceDir)
    {
        return Directory.GetFiles(sourceDir, "bars_20*.db")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                // 匹配 bars_2020.db ~ bars_2025.db，排除 bars_history.db
                return name.StartsWith("bars_20") && name.EndsWith(".db")
                    && !name.Contains("history");
            })
            .OrderBy(f => f)
            .ToArray();
    }
}

public class MergeResult
{
    public string OutputPath { get; set; } = "";
    public long OutputSizeBytes { get; set; }
    public long TotalRows { get; set; }
    public List<TableResult> Tables { get; } = new();

    public class TableResult
    {
        public string TableName { get; set; } = "";
        public long RowsCopied { get; set; }
        public long TotalRows { get; set; }
    }
}
