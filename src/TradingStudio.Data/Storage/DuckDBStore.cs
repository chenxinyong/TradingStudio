using System.Threading.Channels;
using DuckDB.NET.Data;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Storage;

namespace TradingStudio.Data.Storage;

/// <summary>
/// DuckDB 存储引擎 — 同时实现 Bar 持久化和 Tick 短期滚动窗口。
///
/// 文件布局:
///   bars_history.duckdb  — 历史 Bar（只读，回测用）
///   live.duckdb          — 实时 Bar + 7天 Tick 窗口（采集写入）
///
/// 设计原则:
///   - DuckDB 只允许单写入者。每个操作临时 Open/Dispose 连接。
///   - Bar 写入走 Channel 后台批处理（与 SqliteBarStore 同模式）。
///   - Tick 写入走独立 Channel，7 天自动清理。
/// </summary>
public class DuckDBStore : IBarStore, ITickStore
{
    private readonly string _dbPath;

    /// <summary>DuckDB 文件路径（供外部服务如 PeriodMaintainer 使用）</summary>
    public string DbPath => _dbPath;

    private readonly Channel<Bar> _barChannel;
    private readonly Channel<(TickRecord Tick, string Symbol)> _tickChannel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _barWriterTask;
    private readonly Task _tickWriterTask;
    private readonly Task _purgeLoopTask;
    private long _barWritten;
    private long _tickWritten;

    public long WrittenCount => Interlocked.Read(ref _barWritten);
    public long TickCount => Interlocked.Read(ref _tickWritten);

    /// <param name="dbPath">.duckdb 文件路径</param>
    private readonly bool _readOnly;

    /// <param name="enableTickPurge">是否启用 7 天 Tick 自动清理（历史库应为 false）</param>
    public DuckDBStore(string dbPath, bool enableTickPurge = false, bool readOnly = false)
    {
        _dbPath = dbPath;
        _readOnly = readOnly;
        _barChannel = Channel.CreateBounded<Bar>(4096);
        _tickChannel = Channel.CreateBounded<(TickRecord, string)>(8192);
        _cts = new CancellationTokenSource();

        // 确保目录 + 建表（只读模式跳过——减少连接开销，避免锁冲突）
        if (!readOnly)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (dir != null) Directory.CreateDirectory(dir);
            using var conn = OpenConnection();
            CreateTables(conn);
        }

        _barWriterTask = readOnly ? Task.CompletedTask : BarWriteLoop(_cts.Token);
        _tickWriterTask = readOnly ? Task.CompletedTask : TickWriteLoop(_cts.Token);
        _purgeLoopTask = enableTickPurge && !readOnly ? PurgeLoop(_cts.Token) : Task.CompletedTask;
    }

    // ═══════════════════════════════════════════
    // IBarStore — Bar 写入
    // ═══════════════════════════════════════════

    public ValueTask WriteAsync(Bar bar)
    {
        _barChannel.Writer.TryWrite(bar);
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteBatchAsync(IEnumerable<Bar> bars, CancellationToken ct = default)
    {
        var groups = bars.GroupBy(TableName).ToList();
        foreach (var g in groups)
        {
            var table = g.Key;
            // 批内去重：1min 午夜 Bar 会被 TableName 路由到 bars_day，与日线 Bar 主键冲突
            var deduped = g.DistinctBy(b => (b.InstrumentId, b.BarTime)).ToList();
            if (deduped.Count == 0) continue;

            using var conn = OpenConnection();

            // 经连接级临时表中转 + INSERT OR REPLACE 合并，获得跨批次幂等性。
            // 月末夜盘 Bar 会同时出现在相邻月度归档里；直接用 Appender 写目标表会撞
            // 主键并整批回滚（丢失整月）。临时表无主键约束，Appender 高速写入后再合并去重。
            var stage = $"_stage_{table}_{Guid.NewGuid():N}";
            using (var create = conn.CreateCommand())
            {
                create.CommandText = $"CREATE TEMP TABLE {stage} AS SELECT * FROM {table} WHERE 1=0";
                create.ExecuteNonQuery();
            }

            using (var appender = conn.CreateAppender(stage))
            {
                foreach (var bar in deduped)
                {
                    if (ct.IsCancellationRequested) break;
                    appender.CreateRow()
                        .AppendValue(bar.InstrumentId)
                        .AppendValue(bar.TradingDay.ToDateTime(TimeOnly.MinValue))  // DateOnly→DateTime for DuckDB DATE
                        .AppendValue(bar.BarTime)
                        .AppendValue(bar.Open)
                        .AppendValue(bar.High)
                        .AppendValue(bar.Low)
                        .AppendValue(bar.Close)
                        .AppendValue(bar.Volume)
                        .AppendValue(bar.Turnover)
                        .AppendValue(bar.OpenInterest)
                        .AppendValue(bar.TickCount)
                        .EndRow();
                    Interlocked.Increment(ref _barWritten);
                }
            } // appender 在 Dispose 时 flush 到临时表

            using (var merge = conn.CreateCommand())
            {
                merge.CommandText = $"INSERT OR REPLACE INTO {table} SELECT * FROM {stage}";
                merge.ExecuteNonQuery();
            }
            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = $"DROP TABLE {stage}";
                drop.ExecuteNonQuery();
            }
        }
    }

    /// <summary>后台写循环异常通知（无订阅者时回退 Console.Error）。Data 层不引日志框架。</summary>
    public event Action<string, Exception>? OnWriteError;

    private void ReportWriteError(string what, Exception ex)
    {
        var handler = OnWriteError;
        if (handler != null) handler(what, ex);
        else Console.Error.WriteLine($"[DuckDBStore] {what} 写入失败: {ex.Message}");
    }

    private async Task BarWriteLoop(CancellationToken ct)
    {
        var batch = new List<Bar>(64);
        while (await _barChannel.Reader.WaitToReadAsync(ct))
        {
            batch.Clear();
            while (_barChannel.Reader.TryRead(out var bar) && batch.Count < 64)
                batch.Add(bar);
            if (batch.Count > 0)
            {
                // 单批失败只丢本批（Tick CSV 全量原始落盘可重建），循环必须存活
                try { await WriteBatchAsync(batch, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ReportWriteError($"bar batch({batch.Count})", ex); }
            }
        }
    }

    // ═══════════════════════════════════════════
    // IBarStore — Bar 查询
    // ═══════════════════════════════════════════

    public async Task<IReadOnlyList<Bar>> QueryBarsAsync(
        string instrumentId, DateTime start, DateTime end,
        string table = "bars_1min", CancellationToken ct = default)
    {
        var bars = new List<Bar>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        // 产品代码（无数字）→ LIKE 匹配所有合约; 合约代码 → 精确匹配
        bool isProduct = !instrumentId.Any(char.IsDigit);
        var startStr = start.ToString("yyyy-MM-dd HH:mm:ss");
        var endStr = end.ToString("yyyy-MM-dd HH:mm:ss");

        string whereClause;
        if (isProduct)
            whereClause = string.Format(
                "instrument_id ILIKE '{0}%' AND bar_time >= '{1}' AND bar_time <= '{2}'",
                instrumentId, startStr, endStr);
        else
            whereClause = string.Format(
                "instrument_id = '{0}' AND bar_time >= '{1}' AND bar_time <= '{2}'",
                instrumentId, startStr, endStr);

        cmd.CommandText = string.Format(
            "SELECT instrument_id, trading_day, bar_time, " +
            "open, high, low, close, volume, turnover, open_interest, tick_count " +
            "FROM {0} WHERE {1} ORDER BY bar_time",
            table, whereClause);

        using var reader = (DuckDBDataReader)await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var instId = reader.GetString(0);
            // 产品级查询时，将所有合约代码归一化为产品代码（如 SA601 → SA）
            if (isProduct) instId = instrumentId;

            bars.Add(new Bar
            {
                InstrumentId = instId,
                TradingDay = ReadDateOnly(reader, 1),
                BarTime = DateTime.Parse(reader.GetString(2)),
                Open = reader.GetInt64(3),
                High = reader.GetInt64(4),
                Low = reader.GetInt64(5),
                Close = reader.GetInt64(6),
                Volume = reader.GetInt64(7),
                Turnover = reader.GetDouble(8),
                OpenInterest = reader.GetDouble(9),
                TickCount = (int)reader.GetInt64(10),
            });
        }
        return bars;
    }

    private static DateOnly ReadDateOnly(DuckDBDataReader reader, int ordinal)
    {
        // trading_day may be VARCHAR (bars_1min) or DATE (bars_sa_1min)
        var type = reader.GetFieldType(ordinal);
        if (type == typeof(string))
            return DateOnly.Parse(reader.GetString(ordinal));
        return DateOnly.FromDateTime(reader.GetDateTime(ordinal));
    }

    public async Task<IReadOnlyList<string>> QueryInstrumentsAsync(
        string table = "bars_1min", CancellationToken ct = default)
    {
        var list = new List<string>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT instrument_id FROM {table} ORDER BY instrument_id";
        using var reader = (DuckDBDataReader)await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task BuildMultiPeriodTableAsync(int periodMinutes, CancellationToken ct = default)
    {
        var srcTable = "bars_1min";
        var dstTable = $"bars_{periodMinutes}min";

        // 建表
        using (var conn = OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {dstTable} (
                    instrument_id VARCHAR NOT NULL,
                    trading_day   DATE NOT NULL,
                    bar_time      TIMESTAMP NOT NULL,
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
            cmd.ExecuteNonQuery();
        }

        // 聚合: 使用 DuckDB SQL date_trunc + FIRST/MAX/MIN/LAST 窗口函数
        using (var conn = OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT OR REPLACE INTO {dstTable}
                SELECT
                    instrument_id,
                    trading_day,
                    time_bucket(INTERVAL '{periodMinutes} minutes', bar_time) AS bar_time,
                    FIRST(open) OVER w AS open,
                    MAX(high) OVER w AS high,
                    MIN(low) OVER w AS low,
                    LAST(close) OVER w AS close,
                    SUM(volume) OVER w AS volume,
                    SUM(turnover) OVER w AS turnover,
                    LAST(open_interest) OVER w AS open_interest,
                    SUM(tick_count) OVER w AS tick_count
                FROM {srcTable}
                WINDOW w AS (
                    PARTITION BY instrument_id, time_bucket(INTERVAL '{periodMinutes} minutes', bar_time)
                )
                GROUP BY instrument_id, trading_day, bar_time";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ═══════════════════════════════════════════
    // ITickStore — Tick 写入
    // ═══════════════════════════════════════════

    public ValueTask WriteTickAsync(TickRecord tick, string symbol)
    {
        _tickChannel.Writer.TryWrite((tick, symbol));
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteTicksBatchAsync(
        IEnumerable<TickRecord> ticks, string symbol, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var appender = conn.CreateAppender("ticks_recent");
        foreach (var tick in ticks)
        {
            if (ct.IsCancellationRequested) break;
            appender.CreateRow()
                .AppendValue(symbol)
                .AppendValue(tick.ExchangeTimestamp)
                .AppendValue(tick.LocalTimestamp)
                .AppendValue(tick.LastPrice)
                .AppendValue(tick.BidPrice1)
                .AppendValue(tick.AskPrice1)
                .AppendValue(tick.Volume)
                .AppendValue(tick.Turnover)
                .AppendValue(tick.OpenInterest)
                .AppendValue(tick.BidVolume1)
                .AppendValue(tick.AskVolume1)
                .AppendValue(tick.Flags)
                .EndRow();
            Interlocked.Increment(ref _tickWritten);
        }
    }

    private async Task TickWriteLoop(CancellationToken ct)
    {
        var batch = new List<(TickRecord Tick, string Symbol)>(128);
        while (await _tickChannel.Reader.WaitToReadAsync(ct))
        {
            batch.Clear();
            while (_tickChannel.Reader.TryRead(out var item) && batch.Count < 128)
                batch.Add(item);
            if (batch.Count > 0)
            {
                // 按 symbol 分组写入（同一 symbol 连续写入效率更高）
                // 单批失败只丢本批，循环必须存活
                try
                {
                    foreach (var g in batch.GroupBy(x => x.Symbol))
                        await WriteTicksBatchAsync(g.Select(x => x.Tick), g.Key, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ReportWriteError($"tick batch({batch.Count})", ex); }
            }
        }
    }

    // ═══════════════════════════════════════════
    // ITickStore — Tick 查询 & 清理
    // ═══════════════════════════════════════════

    public async Task<IReadOnlyList<TickRecord>> QueryTicksAsync(
        string symbol, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var ticks = new List<TickRecord>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT exchange_ts, local_ts, last_price, bid_price1, ask_price1,
                   volume, turnover, open_interest, bid_volume1, ask_volume1, flags
            FROM ticks_recent
            WHERE symbol = $sym AND exchange_ts >= $start AND exchange_ts <= $end
            ORDER BY exchange_ts";
        cmd.Parameters.Add(new DuckDBParameter("sym", symbol));
        cmd.Parameters.Add(new DuckDBParameter("start",
            new DateTimeOffset(start).ToUnixTimeMilliseconds()));
        cmd.Parameters.Add(new DuckDBParameter("end",
            new DateTimeOffset(end).ToUnixTimeMilliseconds()));

        using var reader = (DuckDBDataReader)await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ticks.Add(new TickRecord
            {
                ExchangeTimestamp = reader.GetInt64(0),
                LocalTimestamp = reader.GetInt64(1),
                LastPrice = reader.GetInt64(2),
                BidPrice1 = reader.GetInt64(3),
                AskPrice1 = reader.GetInt64(4),
                Volume = reader.GetInt64(5),
                Turnover = reader.GetDouble(6),
                OpenInterest = reader.GetDouble(7),
                BidVolume1 = reader.GetInt32(8),
                AskVolume1 = reader.GetInt32(9),
                Flags = reader.GetInt32(10),
            });
        }
        return ticks;
    }

    public async Task<int> PurgeOldTicksAsync(int retentionDays = 7, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        // retentionDays 来自代码常量，非用户输入，安全
        cmd.CommandText = $@"
            DELETE FROM ticks_recent
            WHERE received_at < CURRENT_TIMESTAMP - INTERVAL {retentionDays} DAYS";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task PurgeLoop(CancellationToken ct)
    {
        // 启动时先清理一次
        try { await PurgeOldTicksAsync(7, ct); } catch { /* 表可能为空，跳过初始清理 */ }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), ct);
                var deleted = await PurgeOldTicksAsync(7, ct);
                if (deleted > 0)
                    Console.WriteLine($"[DuckDB] Purged {deleted:N0} old ticks");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DuckDB] Purge error: {ex.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════
    // Order/Trade 持久化（原则3: 所有事件可回放）
    // ═══════════════════════════════════════════

    /// <summary>批量写入订单事件。线程安全（每次调用创建独立连接）。</summary>
    public void WriteOrderEvents(IReadOnlyList<OrderEvent> events)
    {
        if (_readOnly || events.Count == 0) return;
        try
        {
            using var conn = OpenConnection();
            using var appender = conn.CreateAppender("order_events");
            foreach (var e in events)
            {
                appender.CreateRow()
                    .AppendValue(e.OrderId).AppendValue(e.InstrumentId)
                    .AppendValue(e.StrategyId).AppendValue(e.Direction.ToString())
                    .AppendValue(e.Quantity).AppendValue(e.OrderQty)
                    .AppendValue(e.FilledQty).AppendValue(e.Type.ToString())
                    .AppendValue((double?)e.FillPrice ?? 0).AppendValue((double)e.Fee)
                    .AppendValue((double)e.Slippage).AppendValue(e.Message ?? "")
                    .AppendValue(e.Time.DateTime).EndRow();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[DuckDB] WriteOrderEvents error: {ex.Message}"); }
    }

    /// <summary>批量写入成交记录。</summary>
    public void WriteTrades(IReadOnlyList<Trade> trades)
    {
        if (_readOnly || trades.Count == 0) return;
        try
        {
            using var conn = OpenConnection();
            using var appender = conn.CreateAppender("trades");
            foreach (var t in trades)
            {
                appender.CreateRow()
                    .AppendValue(t.InstrumentId).AppendValue(t.StrategyId)
                    .AppendValue(t.Quantity)
                    .AppendValue((double)t.EntryPrice).AppendValue((double)t.ExitPrice)
                    .AppendValue((double)t.PnL).AppendValue((double)t.Fee)
                    .AppendValue((double)t.Slippage)
                    .AppendValue(t.EntryTime).AppendValue(t.ExitTime).EndRow();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[DuckDB] WriteTrades error: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 内部
    // ═══════════════════════════════════════════

    private DuckDBConnection OpenConnection()
    {
        var cs = _readOnly
            ? $"Data Source={_dbPath};access_mode=read_only"
            : $"Data Source={_dbPath}";
        var conn = new DuckDBConnection(cs);
        conn.Open();
        return conn;
    }

    private static void CreateTables(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS bars_1min (
                instrument_id VARCHAR NOT NULL,
                trading_day   DATE NOT NULL,
                bar_time      TIMESTAMP NOT NULL,
                open          BIGINT NOT NULL,
                high          BIGINT NOT NULL,
                low           BIGINT NOT NULL,
                close         BIGINT NOT NULL,
                volume        BIGINT NOT NULL,
                turnover      DOUBLE NOT NULL,
                open_interest DOUBLE NOT NULL,
                tick_count    INTEGER DEFAULT 0,
                PRIMARY KEY (instrument_id, bar_time)
            );

            CREATE TABLE IF NOT EXISTS bars_day (
                instrument_id VARCHAR NOT NULL,
                trading_day   DATE NOT NULL,
                bar_time      TIMESTAMP NOT NULL,
                open          BIGINT NOT NULL,
                high          BIGINT NOT NULL,
                low           BIGINT NOT NULL,
                close         BIGINT NOT NULL,
                volume        BIGINT NOT NULL,
                turnover      DOUBLE NOT NULL,
                open_interest DOUBLE NOT NULL,
                tick_count    INTEGER DEFAULT 0,
                PRIMARY KEY (instrument_id, bar_time)
            );

            CREATE TABLE IF NOT EXISTS ticks_recent (
                symbol         VARCHAR NOT NULL,
                exchange_ts    BIGINT NOT NULL,
                local_ts       BIGINT NOT NULL,
                last_price     BIGINT NOT NULL,
                bid_price1     BIGINT NOT NULL,
                ask_price1     BIGINT NOT NULL,
                volume         BIGINT NOT NULL,
                turnover       DOUBLE NOT NULL,
                open_interest  DOUBLE NOT NULL,
                bid_volume1    INTEGER NOT NULL,
                ask_volume1    INTEGER NOT NULL,
                flags          SMALLINT NOT NULL,
                received_at    TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_ticks_symbol_ts
                ON ticks_recent(symbol, exchange_ts);
            CREATE INDEX IF NOT EXISTS idx_ticks_received
                ON ticks_recent(received_at);

            -- 订单事件表（原则3: 所有事件可回放）
            CREATE TABLE IF NOT EXISTS order_events (
                order_id    BIGINT NOT NULL,
                instrument_id VARCHAR NOT NULL,
                strategy_id VARCHAR NOT NULL,
                direction   VARCHAR NOT NULL,
                quantity    INTEGER NOT NULL,
                order_qty   INTEGER NOT NULL,
                filled_qty  INTEGER NOT NULL,
                type        VARCHAR NOT NULL,
                fill_price  DOUBLE,
                fee         DOUBLE,
                slippage    DOUBLE,
                message     VARCHAR,
                event_time  TIMESTAMP NOT NULL,
                PRIMARY KEY (order_id, type, event_time)
            );

            -- 成交记录表
            CREATE TABLE IF NOT EXISTS trades (
                instrument_id VARCHAR NOT NULL,
                strategy_id VARCHAR NOT NULL,
                quantity    INTEGER NOT NULL,
                entry_price DOUBLE NOT NULL,
                exit_price  DOUBLE NOT NULL,
                pnl         DOUBLE NOT NULL,
                fee         DOUBLE NOT NULL,
                slippage    DOUBLE,
                entry_time  TIMESTAMP NOT NULL,
                exit_time   TIMESTAMP NOT NULL,
                UNIQUE (instrument_id, strategy_id, entry_time, exit_time)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>判断是否为日线 Bar (bar_time 的时间部分是 00:00:00)</summary>
    private static string TableName(Bar bar) =>
        bar.BarTime.TimeOfDay == TimeSpan.Zero ? "bars_day" : "bars_1min";

    // ═══════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════

    public void Dispose()
    {
        _cts.Cancel();
        _barChannel.Writer.Complete();
        _tickChannel.Writer.Complete();
        try { Task.WaitAll([_barWriterTask, _tickWriterTask, _purgeLoopTask], TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* 进程退出时 channel 未处理数据属正常 */ }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _barChannel.Writer.Complete();
        _tickChannel.Writer.Complete();
        try { await Task.WhenAll(_barWriterTask, _tickWriterTask, _purgeLoopTask); }
        catch (OperationCanceledException) { /* Channel关闭后正常 */ }
        _cts.Dispose();
    }
}
