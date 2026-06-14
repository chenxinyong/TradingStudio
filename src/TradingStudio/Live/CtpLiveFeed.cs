using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;

namespace TradingStudio.Live;

/// <summary>
/// 实盘数据源 — 直接封装 CTP MdApi，产出 TickEvent + BarEvent 流。
/// 与 CollectService 使用相同的底层 CTP 接口。
/// </summary>
public class CtpLiveFeed : IDataFeed, IDisposable
{
    private readonly CtpMdOptions _opts;
    private readonly Channel<(string InstId, TickRecord Tick)> _merged;
    private CTP.MdApi? _mdApi;
    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];
    private bool _disposed;

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;
    public bool IsConnected { get; private set; }

    public CtpLiveFeed(CtpMdOptions opts)
    {
        _opts = opts;
        _merged = Channel.CreateBounded<(string, TickRecord)>(8192);
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

        _mdApi = new CTP.MdApi();
        using var barAgg = new BarAggregator();
        var barQueue = new Queue<BarEvent>();
        barAgg.OnBar += b =>
        {
            lock (barQueue)
                barQueue.Enqueue(new BarEvent { Bar = b, Time = new DateTimeOffset(b.BarTime, TimeSpan.Zero), IsNewBar = true });
        };

        // 连接 + 登录
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
        _mdApi.OnFrontDisconnected += _ => { lock (discLock) disconnected = true; IsConnected = false; };
        _mdApi.OnLogin += (err, _) =>
        {
            if (err.IsOK()) loggedIn.TrySetResult(true);
            else loggedIn.TrySetResult(false);
        };

        _mdApi.Connect(_opts.MdFront);
        if (!await Wait(connected, 15000, ct)) throw new Exception("CTP connect timeout");
        if (!await Wait(loggedIn, 15000, ct)) throw new Exception("CTP login failed");

        // 行情回调 → 归并 Channel
        _mdApi.OnQuote += q =>
        {
            if (string.IsNullOrEmpty(q.InstrumentID)) return;
            var instId = ContractCodeGenerator.Normalize(q.InstrumentID);
            var record = new TickRecord
            {
                ExchangeTimestamp = q.ExchangeTimestamp,
                LocalTimestamp = q.LocalTimestamp,
                LastPrice = (long)(q.LastPrice * TickRecord.PriceScale),
                Volume = q.Volume, Turnover = q.Turnover, OpenInterest = q.OpenInterest,
                BidPrice1 = (long)(q.BidPrice1 * TickRecord.PriceScale), BidVolume1 = q.BidVolume1,
                AskPrice1 = (long)(q.AskPrice1 * TickRecord.PriceScale), AskVolume1 = q.AskVolume1,
            };
            _merged.Writer.TryWrite((instId, record));
        };

        // 订阅
        for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50)
        {
            _mdApi.Subscribe(_instruments.Skip(i).Take(50).ToArray());
            await Task.Delay(200, ct);
        }

        // 消费归并流
        var reader = _merged.Reader;
        while (!ct.IsCancellationRequested)
        {
            // 断线检查
            lock (discLock) { if (disconnected) break; }

            if (!await reader.WaitToReadAsync(ct)) break;
            while (reader.TryRead(out var item))
            {
                var (instId, tick) = item;
                var tradingDay = DateOnly.FromDateTime(DateTime.Today);

                yield return new TickEvent
                {
                    Tick = tick, InstrumentId = instId, TradingDay = tradingDay,
                    Time = DateTimeOffset.FromUnixTimeMilliseconds(tick.ExchangeTimestamp),
                };

                barAgg.Feed(tick, instId, tradingDay);

                lock (barQueue)
                    while (barQueue.Count > 0) yield return barQueue.Dequeue();
            }
        }

        _merged.Writer.Complete();
        barAgg.Flush();
        lock (barQueue) while (barQueue.Count > 0) yield return barQueue.Dequeue();
    }

    private static async Task<bool> Wait(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms, ct));
        return done == tcs.Task && tcs.Task.Result;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _merged.Writer.Complete();
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
