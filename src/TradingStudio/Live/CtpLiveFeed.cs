using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using Serilog;

namespace TradingStudio.Live;

/// <summary>
/// 实盘数据源 — 直接封装 CTP MdApi，产出 TickEvent + BarEvent 流。
/// 内置断线自动重连。使用 CTP 交易日字段正确处理夜盘归属。
/// </summary>
public class CtpLiveFeed : IDataFeed, IDisposable
{
    private readonly CtpMdOptions _opts;
    private readonly Serilog.ILogger _log;
    private readonly Channel<(string InstId, TickRecord Tick, DateOnly TradingDay)> _merged;

    /// <summary>数据持久化通道 — 独立于引擎消费，供 LiveDataCollector 读取</summary>
    public Channel<(string InstId, TickRecord Tick, DateOnly TradingDay)> PersistChannel { get; }
        = Channel.CreateBounded<(string, TickRecord, DateOnly)>(8192);

    private CTP.MdApi? _mdApi;
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
        _merged = Channel.CreateBounded<(string, TickRecord, DateOnly)>(8192);
    }

    public void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments)
    {
        _startTime = startTime;
        _endTime = endTime;
        _instruments = instruments;
    }

    public async IAsyncEnumerable<DataEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_instruments.Count == 0) yield break;

        while (!ct.IsCancellationRequested)
        {
            _mdApi = new CTP.MdApi();
            using var barAgg = new BarAggregator();
            var barQueue = new Queue<BarEvent>();
            barAgg.OnBar += b =>
            {
                lock (barQueue)
                    barQueue.Enqueue(new BarEvent { Bar = b, Time = new DateTimeOffset(b.BarTime, TimeSpan.Zero), IsNewBar = true });
            };

            // ── 连接 + 登录 ──
            var connected = new TaskCompletionSource<bool>();
            var loggedIn = new TaskCompletionSource<bool>();
            var discLock = new object();
            var disconnected = false;

            _mdApi.OnFrontConnected += () =>
            {
                IsConnected = true;
                connected.TrySetResult(true);
                _mdApi.Login(_opts.BrokerId, _opts.UserId, _opts.Password);
            };
            _mdApi.OnFrontDisconnected += _ =>
            {
                lock (discLock) { disconnected = true; }
                IsConnected = false;
                _log.Warning("CTP MdApi disconnected");
            };
            _mdApi.OnLogin += (err, _) =>
            {
                if (err.IsOK()) loggedIn.TrySetResult(true);
                else { _log.Error("CTP login failed: {Err}", err.ErrorMsg); loggedIn.TrySetResult(false); }
            };

            // 行情回调 → 归并 Channel（使用 CTP 交易日字段）
            _mdApi.OnQuote += q =>
            {
                if (string.IsNullOrEmpty(q.InstrumentID)) return;
                var record = QuoteConverter.FromQuote(q);
                var instId = ContractCodeGenerator.Normalize(q.InstrumentID);
                var tradingDay = QuoteConverter.ParseTradingDay(q.TradingDay);
                _merged.Writer.TryWrite((instId, record, tradingDay));
                PersistChannel.Writer.TryWrite((instId, record, tradingDay));
            };

            // ── 消费循环（缓冲事件以避免 yield 在 try-catch 中）──
            var sessionEvents = new List<DataEvent>();
            Exception? streamError = null;

            try
            {
                _mdApi.Connect(_opts.MdFront);
                if (!await Wait(connected, 15000, ct)) throw new Exception("CTP connect timeout");
                if (!await Wait(loggedIn, 15000, ct)) throw new Exception("CTP login failed");

                _log.Information("CTP connected: {Front}", _opts.MdFront);

                // 订阅
                for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50)
                {
                    _mdApi.Subscribe(_instruments.Skip(i).Take(50).ToArray());
                    await Task.Delay(200, ct);
                }

                // 消费循环
                var reader = _merged.Reader;
                while (!ct.IsCancellationRequested)
                {
                    lock (discLock) { if (disconnected) break; }

                    if (!await reader.WaitToReadAsync(ct)) break;
                    while (reader.TryRead(out var item))
                    {
                        var (instId, tick, tradingDay) = item;

                        sessionEvents.Add(new TickEvent
                        {
                            Tick = tick, InstrumentId = instId, TradingDay = tradingDay,
                            Time = DateTimeOffset.FromUnixTimeMilliseconds(tick.ExchangeTimestamp),
                        });

                        barAgg.Feed(tick, instId, tradingDay);

                        lock (barQueue)
                            while (barQueue.Count > 0) sessionEvents.Add(barQueue.Dequeue());
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                streamError = ex;
                _log.Warning(ex, "CTP stream error — reconnecting in 5s...");
            }
            finally
            {
                _merged.Writer.TryComplete();
                barAgg.Flush();
                lock (barQueue) while (barQueue.Count > 0) sessionEvents.Add(barQueue.Dequeue());
                _mdApi?.Dispose();
            }

            // yield 在 try-catch 外部
            foreach (var evt in sessionEvents)
                yield return evt;

            if (ct.IsCancellationRequested) break;

            _log.Information("Reconnecting in 5s...");
            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<bool> Wait(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms, ct));
        return done == tcs.Task && tcs.Task.Result;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _merged.Writer.TryComplete();
        _mdApi?.Dispose();
    }
}

public class CtpMdOptions
{
    public string MdFront { get; init; } = "tcp://180.168.146.187:10131";
    public string BrokerId { get; init; } = "9999";
    public string UserId { get; init; } = "";
    public string Password { get; init; } = "";
    public string? FlowDir { get; init; }
}
