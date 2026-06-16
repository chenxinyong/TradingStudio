using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using Serilog;

namespace TradingStudio.Live;

/// <summary>
/// 实盘数据源 — CTP 行情接收在普通 Task 中运行，通过 Channel 传给迭代器，
/// 彻底避免 yield return 与原生回调的线程冲突。
/// </summary>
public class CtpLiveFeed : IDataFeed, IDisposable
{
    private readonly CtpMdOptions _opts;
    private readonly Serilog.ILogger _log;

    public Channel<(string InstId, TickRecord Tick, DateOnly TradingDay)> PersistChannel { get; }
        = Channel.CreateBounded<(string, TickRecord, DateOnly)>(8192);

    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];
    private bool _disposed;

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;
    public bool IsConnected { get; private set; }

    public CtpLiveFeed(CtpMdOptions opts, Serilog.ILogger? log = null)
    {
        _opts = opts;
        _log = (log ?? Serilog.Log.Logger).ForContext<CtpLiveFeed>();
    }

    public void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments)
    {
        _startTime = startTime;
        _endTime = endTime;
        _instruments = instruments;
    }

    /// <summary>
    /// 迭代器层 — 仅从 Channel 消费，不直接接触 CTP 原生代码。
    /// </summary>
    public async IAsyncEnumerable<DataEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_instruments.Count == 0) yield break;

        var eventChannel = Channel.CreateBounded<DataEvent>(8192);

        // 生产者：在普通 Task 中跑 CTP 连接（无 yield return）
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerTask = RunProducerLoop(eventChannel.Writer, producerCts.Token);

        // 消费者：从 Channel 读取并 yield
        try
        {
            var reader = eventChannel.Reader;
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            producerCts.Cancel();
            try { await producerTask; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// 生产者循环 — 普通 async Task，不使用 yield return。
    /// CTP 连接、回调、订阅全部在此 Task 的线程上下文中完成。
    /// </summary>
    private async Task RunProducerLoop(
        ChannelWriter<DataEvent> writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CTP.MdApi? mdApi = null;
            var barAgg = new BarAggregator();
            var barQueue = new Queue<BarEvent>();
            barAgg.OnBar += b =>
            {
                lock (barQueue)
                    barQueue.Enqueue(new BarEvent { Bar = b, Time = new DateTimeOffset(b.BarTime, TimeSpan.Zero), IsNewBar = true });
            };

            try
            {
                mdApi = new CTP.MdApi();
                var merged = Channel.CreateBounded<(string InstId, TickRecord Tick, DateOnly TradingDay)>(8192);

                var connected = new TaskCompletionSource<bool>();
                var loggedIn = new TaskCompletionSource<bool>();
                var discLock = new object();
                var disconnected = false;

                mdApi.OnFrontConnected += () =>
                {
                    IsConnected = true;
                    connected.TrySetResult(true);
                    mdApi.Login(_opts.BrokerId, _opts.UserId, _opts.Password);
                };
                mdApi.OnFrontDisconnected += r =>
                {
                    lock (discLock) { disconnected = true; }
                    IsConnected = false;
                    _log.Warning("CTP MdApi disconnected (0x{Reason:X})", r);
                };
                mdApi.OnLogin += (err, info) =>
                {
                    if (err.IsOK()) { _log.Information("CTP login OK TradingDay={Day}", info?.TradingDay); loggedIn.TrySetResult(true); }
                    else { _log.Error("CTP login FAIL [{Code}] {Msg}", err.ErrorID, err.ErrorMsg); loggedIn.TrySetResult(false); }
                };
                mdApi.OnQuote += q =>
                {
                    if (string.IsNullOrEmpty(q.InstrumentID)) return;
                    var record = QuoteConverter.FromCTPQuote(q);
                    var instId = ContractCodeGenerator.Normalize(q.InstrumentID);
                    var tradingDay = QuoteConverter.ParseTradingDay(q.TradingDay);
                    merged.Writer.TryWrite((instId, record, tradingDay));
                    PersistChannel.Writer.TryWrite((instId, record, tradingDay));
                };

                _log.Information("CTP: Connecting to {Front}...", _opts.MdFront);
                mdApi.Connect(_opts.MdFront);

                if (!await WaitFor(connected, 15000, ct))
                {
                    _log.Warning("CTP connect timeout");
                    continue;
                }
                if (!await WaitFor(loggedIn, 15000, ct))
                {
                    _log.Warning("CTP login timeout/failed");
                    continue;
                }

                _log.Information("CTP connected: {Front}", _opts.MdFront);

                // 订阅
                for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50)
                {
                    mdApi.Subscribe(_instruments.Skip(i).Take(50).ToArray());
                    await Task.Delay(200, ct);
                }

                // 消费循环 — 向 writer 推送事件
                var reader = merged.Reader;
                while (!ct.IsCancellationRequested)
                {
                    lock (discLock) { if (disconnected) break; }

                    if (!await reader.WaitToReadAsync(ct)) break;
                    while (reader.TryRead(out var item))
                    {
                        var (instId, tick, tradingDay) = item;

                        writer.TryWrite(new TickEvent
                        {
                            Tick = tick, InstrumentId = instId, TradingDay = tradingDay,
                            Time = DateTimeOffset.FromUnixTimeMilliseconds(tick.ExchangeTimestamp),
                        });

                        barAgg.Feed(tick, instId, tradingDay);

                        lock (barQueue)
                            while (barQueue.Count > 0) writer.TryWrite(barQueue.Dequeue());
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.Warning(ex, "CTP stream error — reconnecting in 5s...");
            }
            finally
            {
                // 等待原生回调线程完成
                try { await Task.Delay(300, ct); } catch { }

                if (mdApi != null)
                {
                    IsConnected = false;
                    try { mdApi.Dispose(); } catch (Exception ex) { _log.Warning(ex, "Error disposing MdApi"); }
                }

                barAgg.Flush();
                lock (barQueue) while (barQueue.Count > 0) writer.TryWrite(barQueue.Dequeue());
                barAgg.Dispose();
            }

            _log.Information("Reconnecting in 5s...");
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        }

        writer.TryComplete();
    }

    private static async Task<bool> WaitFor(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms, ct));
        return done == tcs.Task && tcs.Task.Result;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
    }
}

public class CtpMdOptions
{
    public string MdFront { get; init; } = "tcp://182.254.243.31:30011";
    public string BrokerId { get; init; } = "9999";
    public string UserId { get; init; } = "";
    public string Password { get; init; } = "";
    public string? FlowDir { get; init; }
}
