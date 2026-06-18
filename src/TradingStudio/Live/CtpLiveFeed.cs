using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using Serilog;
using System.Linq;

namespace TradingStudio.Live;

/// <summary>
/// 实盘数据源 — CTP 行情接收在普通 Task 中运行，通过 Channel 传给迭代器，
/// 彻底避免 yield return 与原生回调的线程冲突。
/// </summary>
public class CtpLiveFeed : IDataFeed, IDisposable
{
    private readonly CtpMdOptions _opts;
    private readonly Serilog.ILogger _log;

    /// <summary>数据持久化通道 — 传递原始 CTP Quote（全42字段），供 TickCsvWriter 落盘</summary>
    public Channel<(string InstId, CTP.Quote Quote, DateOnly TradingDay)> PersistChannel { get; }
        = Channel.CreateBounded<(string, CTP.Quote, DateOnly)>(8192);

    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];
    private bool _disposed;

    /// <summary>活跃度追踪器：观察期后筛选高活跃合约，减少 CTP 订阅量</summary>
    public ContractActivityTracker? ActivityTracker { get; set; }

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;
    public bool IsConnected { get; private set; }

    /// <summary>活跃品种集合（观察期后填充），用于引擎事件过滤，不影响数据落盘</summary>
    private HashSet<string>? _activeProductSet;
    private bool _filterReady;

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
                var firstQuote = true;
                var tracker = ActivityTracker;
                mdApi.OnQuote += q =>
                {
                    if (string.IsNullOrEmpty(q.InstrumentID)) return;
                    var record = QuoteConverter.FromCTPQuote(q);
                    var instId = ContractCodeGenerator.Normalize(q.InstrumentID);
                    var tradingDay = QuoteConverter.ParseTradingDay(q.TradingDay);
                    merged.Writer.TryWrite((instId, record, tradingDay));
                    PersistChannel.Writer.TryWrite((instId, q, tradingDay));  // 原始 Quote 全42字段
                    tracker?.Feed(instId, q);
                    if (firstQuote) { firstQuote = false; Console.WriteLine($"[CTP-MD] First tick: {instId} @ {q.LastPrice}"); }
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

                // 始终订阅全量合约（数据落盘不丢）
                if (!_filterReady && tracker != null)
                {
                    tracker.Start();
                    _log.Information("Activity observation started ({Sec}s), full list: {Count} instruments",
                        tracker.ObservationSeconds, _instruments.Count);
                }

                _log.Information("Subscribing to {Count} instruments in batches of 50", _instruments.Count);
                for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50)
                {
                    var batch = _instruments.Skip(i).Take(50).ToArray();
                    mdApi.Subscribe(batch);
                    await Task.Delay(200, ct);
                }
                _log.Information("Subscription completed: {Count} instruments", _instruments.Count);

                // 消费循环 — 软过滤：全量落盘，仅活跃品种推送引擎
                var reader = merged.Reader;
                var lastObserveCheck = DateTime.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    lock (discLock) { if (disconnected) break; }

                    // 观察期结束 → 计算活跃品种集合（不打断连接，零数据丢失）
                    if (!_filterReady && tracker is { IsComplete: true })
                    {
                        var topProducts = tracker.GetTopProducts(30);
                        var topRanking = tracker.GetTopProductRanking(30);
                        _activeProductSet = new HashSet<string>(topProducts);
                        var filteredCount = _instruments.Count(c => _activeProductSet.Contains(ProductOf(c)));
                        _filterReady = true;

                        _log.Information(
                            "Activity soft filter: Top {ProductCount} products → {Filtered}/{Total} engine. " +
                            "All {Total} persisted. Top5: {Top5}",
                            topProducts.Count, filteredCount, _instruments.Count,
                            string.Join(", ", topRanking.Take(5).Select(x =>
                                $"{x.Product}(V{x.TotalVol},OI{x.TotalOI:F0},{x.Contracts}ct)")));
                        Console.WriteLine(
                            $"[Activity] Soft filter ready: engine {filteredCount}/{_instruments.Count} contracts. " +
                            $"Top: {string.Join(", ", topRanking.Take(5).Select(x => x.Product))}");
                    }

                    // 过滤未就绪前：定期检查观察期（每 5s 超时一次）
                    if (!_filterReady)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(5000);
                        try
                        {
                            if (!await reader.WaitToReadAsync(readCts.Token)) break;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (!await reader.WaitToReadAsync(ct)) break;
                    }

                    while (reader.TryRead(out var item))
                    {
                        var (instId, tick, tradingDay) = item;

                        // 引擎推送：仅活跃品种（落盘 PersistChannel 已在 OnQuote 中全量写入）
                        if (!_filterReady || _activeProductSet!.Contains(ProductOf(instId)))
                        {
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

    /// <summary>从合约代码提取品种代码（ag2608 → ag, TA608 → TA, IF2606 → IF）</summary>
    private static string ProductOf(string instId)
    {
        var span = instId.AsSpan();
        int i = 0;
        while (i < span.Length && !char.IsDigit(span[i])) i++;
        return i > 0 ? span[..i].ToString() : instId;
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
