using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using TradingStudio.Core.Models;
using TradingStudio.Core.Models;

namespace TradingStudio.Data.Storage;

/// <summary>
/// Bar 存储器 — 将 Bar 写入 SQLite。
/// 后续切 PostgreSQL 只需替换此类，接口不变。
/// </summary>
public class BarStore : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Channel<Bar> _input = Channel.CreateBounded<Bar>(4096);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private long _written;

    public long WrittenCount => Interlocked.Read(ref _written);

    public BarStore(string dbPath = "bars.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        CreateTable();
        _writerTask = WriteLoop(_cts.Token);
    }

    /// <summary>异步写入（不阻塞调用线程）</summary>
    public ValueTask WriteAsync(Bar bar)
    {
        _input.Writer.TryWrite(bar);
        return ValueTask.CompletedTask;
    }

    /// <summary>批量写入多条</summary>
    public async ValueTask WriteBatchAsync(IEnumerable<Bar> bars, CancellationToken ct = default)
    {
        // Group bars by table
        var groups = bars.GroupBy(TableName).ToList();
        foreach (var g in groups)
        {
            var table = g.Key;
            await using var tx = _conn.BeginTransaction();
            await using var cmd = new SqliteCommand(
                $@"INSERT OR REPLACE INTO {table}
                  (instrument_id, trading_day, bar_time, open, high, low, close, volume, turnover, open_interest, tick_count)
                  VALUES (@inst, @day, @time, @o, @h, @l, @c, @v, @to, @oi, @tc)", _conn, tx);

            var p_inst = cmd.Parameters.Add("@inst", SqliteType.Text);
            var p_day  = cmd.Parameters.Add("@day", SqliteType.Text);
            var p_time = cmd.Parameters.Add("@time", SqliteType.Text);
            var p_o = cmd.Parameters.Add("@o", SqliteType.Integer);
            var p_h = cmd.Parameters.Add("@h", SqliteType.Integer);
            var p_l = cmd.Parameters.Add("@l", SqliteType.Integer);
            var p_c = cmd.Parameters.Add("@c", SqliteType.Integer);
            var p_v = cmd.Parameters.Add("@v", SqliteType.Integer);
            var p_to = cmd.Parameters.Add("@to", SqliteType.Real);
            var p_oi = cmd.Parameters.Add("@oi", SqliteType.Real);
            var p_tc = cmd.Parameters.Add("@tc", SqliteType.Integer);

            foreach (var bar in g)
            {
                if (ct.IsCancellationRequested) break;
                p_inst.Value = bar.InstrumentId;
                p_day.Value  = bar.TradingDay.ToString("yyyy-MM-dd");
                p_time.Value = bar.BarTime.ToString("yyyy-MM-dd HH:mm:ss");
                p_o.Value = bar.Open;
                p_h.Value = bar.High;
                p_l.Value = bar.Low;
                p_c.Value = bar.Close;
                p_v.Value = bar.Volume;
                p_to.Value = bar.Turnover;
                p_oi.Value = bar.OpenInterest;
                p_tc.Value = bar.TickCount;
                await cmd.ExecuteNonQueryAsync(ct);
                Interlocked.Increment(ref _written);
            }
            await tx.CommitAsync(ct);
        }
    }

    private async Task WriteLoop(CancellationToken ct)
    {
        var batch = new List<Bar>(64);
        while (await _input.Reader.WaitToReadAsync(ct))
        {
            batch.Clear();
            while (_input.Reader.TryRead(out var bar) && batch.Count < 64)
                batch.Add(bar);
            if (batch.Count > 0)
                await WriteBatchAsync(batch, ct);
        }
    }

    private void CreateTable()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS bars_1min (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                instrument_id TEXT    NOT NULL,
                trading_day   TEXT    NOT NULL,
                bar_time      TEXT    NOT NULL,
                open          INTEGER NOT NULL,
                high          INTEGER NOT NULL,
                low           INTEGER NOT NULL,
                close         INTEGER NOT NULL,
                volume        INTEGER NOT NULL,
                turnover      REAL    NOT NULL,
                open_interest REAL    NOT NULL,
                tick_count    INTEGER DEFAULT 0,
                UNIQUE(instrument_id, bar_time)
            );
            CREATE TABLE IF NOT EXISTS bars_day (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                instrument_id TEXT    NOT NULL,
                trading_day   TEXT    NOT NULL,
                bar_time      TEXT    NOT NULL,
                open          INTEGER NOT NULL,
                high          INTEGER NOT NULL,
                low           INTEGER NOT NULL,
                close         INTEGER NOT NULL,
                volume        INTEGER NOT NULL,
                turnover      REAL    NOT NULL,
                open_interest REAL    NOT NULL,
                tick_count    INTEGER DEFAULT 0,
                UNIQUE(instrument_id, bar_time)
            );
            CREATE INDEX IF NOT EXISTS idx_bars1m_inst_time ON bars_1min(instrument_id, bar_time);
            CREATE INDEX IF NOT EXISTS idx_barsday_inst_time ON bars_day(instrument_id, bar_time);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>判断是否为日线 Bar (bar_time 的时间部分是 00:00:00)</summary>
    private static string TableName(Bar bar) =>
        bar.BarTime.TimeOfDay == TimeSpan.Zero ? "bars_day" : "bars_1min";

    // ═══════════════════════════════════════════
    // Query — 回测数据源使用
    // ═══════════════════════════════════════════

    /// <summary>查询指定品种和时间范围的 Bar，按 bar_time 排序</summary>
    public async Task<IReadOnlyList<Bar>> QueryBarsAsync(
        string instrumentId, DateTime start, DateTime end,
        string table = "bars_1min", CancellationToken ct = default)
    {
        var bars = new List<Bar>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT instrument_id, trading_day, bar_time, open, high, low, close,
                   volume, turnover, open_interest, tick_count
            FROM {table}
            WHERE instrument_id = @inst AND bar_time >= @start AND bar_time <= @end
            ORDER BY bar_time";
        cmd.Parameters.AddWithValue("@inst", instrumentId);
        cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd HH:mm:ss"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            bars.Add(new Bar
            {
                InstrumentId = reader.GetString(0),
                TradingDay = DateOnly.Parse(reader.GetString(1)),
                BarTime = DateTime.Parse(reader.GetString(2)),
                Open = reader.GetInt64(3),
                High = reader.GetInt64(4),
                Low = reader.GetInt64(5),
                Close = reader.GetInt64(6),
                Volume = reader.GetInt64(7),
                Turnover = reader.GetDouble(8),
                OpenInterest = reader.GetDouble(9),
                TickCount = reader.GetInt32(10),
            });
        }
        return bars;
    }

    /// <summary>列出某表中所有不重复的品种 ID</summary>
    public async Task<IReadOnlyList<string>> QueryInstrumentsAsync(
        string table = "bars_1min", CancellationToken ct = default)
    {
        var list = new List<string>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT instrument_id FROM {table} ORDER BY instrument_id";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _input.Writer.Complete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _conn.Dispose();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _input.Writer.Complete();
        try { await _writerTask; } catch { }
        await _conn.DisposeAsync();
        _cts.Dispose();
    }
}
