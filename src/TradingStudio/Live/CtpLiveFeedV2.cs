using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using Serilog;

namespace TradingStudio.Live;

/// <summary>
/// 实盘数据源 v2 — 基于 FtdcNet.CTP(P/Invoke)。完全替代 C++/CLI CTPWrapper。
/// </summary>
public class CtpLiveFeedV2 : IDataFeed, IDisposable
{
    private readonly CtpMdOptions _opts;
    private readonly Serilog.ILogger _log;
    private CTP.FtdcMdAdapter? _api;
    private bool _disposed;

    public Channel<(string InstId, TickRecord Tick, DateOnly TradingDay)> PersistChannel { get; }
        = Channel.CreateBounded<(string, TickRecord, DateOnly)>(8192);

    private DateTime _startTime, _endTime;
    private IReadOnlyList<string> _instruments = [];
    private HashSet<string>? _activeProductSet;
    private bool _filterReady;
    private int _requestId;

    public ContractActivityTracker? ActivityTracker { get; set; }
    public HashSet<string> StrategyInstruments { get; } = new();
    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;
    public bool IsConnected { get; private set; }

    public CtpLiveFeedV2(CtpMdOptions opts, Serilog.ILogger? log = null)
    { _opts = opts; _log = (log ?? Serilog.Log.Logger).ForContext<CtpLiveFeedV2>(); }

    public void Initialize(DateTime s, DateTime e, IReadOnlyList<string> i)
    { _startTime = s; _endTime = e; _instruments = i; }

    public async IAsyncEnumerable<DataEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_instruments.Count == 0) yield break;
        var ch = Channel.CreateBounded<DataEvent>(8192);
        using var pCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prod = RunProducerLoop(ch.Writer, pCts.Token);
        try { var r = ch.Reader; while (await r.WaitToReadAsync(ct)) while (r.TryRead(out var e)) yield return e; }
        finally { pCts.Cancel(); try { await prod; } catch (OperationCanceledException) { } }
    }

    private async Task RunProducerLoop(ChannelWriter<DataEvent> writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _api = null;
            var barAgg = new BarAggregator();
            var barQueue = new Queue<BarEvent>();
            barAgg.OnBar += b => { lock (barQueue) barQueue.Enqueue(new BarEvent { Bar = b, Time = new DateTimeOffset(b.BarTime, TimeSpan.Zero), IsNewBar = true }); };

            try
            {
                _api = new CTP.FtdcMdAdapter("", false, false);
                var merged = Channel.CreateBounded<(string, TickRecord, DateOnly)>(8192);
                var connected = new TaskCompletionSource<bool>();
                var loggedIn = new TaskCompletionSource<bool>();
                var discLock = new object();
                var disconnected = false;

                _api.OnFrontEvent += (_, e) =>
                {
                    if (e.EventType == CTP.EnumOnFrontType.OnFrontConnected) { IsConnected = true; connected.TrySetResult(true); _api.ReqUserLogin(new CTP.ThostFtdcReqUserLoginField { BrokerID = _opts.BrokerId, UserID = _opts.UserId, Password = _opts.Password }, ++_requestId); }
                    else if (e.EventType == CTP.EnumOnFrontType.OnFrontDisconnected) { lock (discLock) { disconnected = true; } IsConnected = false; _log.Warning("CTP MdApi disconnected (0x{Reason:X})", e.Reason); }
                };
                _api.OnRspEvent += (_, e) =>
                {
                    if (e.EventType == CTP.EnumOnRspType.OnRspUserLogin)
                    {
                        if (e.RspInfo == null || e.RspInfo.ErrorID == 0) { _log.Information("CTP login OK TradingDay={Day}", _api.GetTradingDay()); loggedIn.TrySetResult(true); }
                        else { _log.Error("CTP login FAIL [{Code}] {Msg}", e.RspInfo.ErrorID, e.RspInfo.ErrorMsg); loggedIn.TrySetResult(false); }
                    }
                };

                var firstQuote = true;
                var tracker = ActivityTracker;
                _api.OnRtnEvent += (_, e) =>
                {
                    if (e.EventType == CTP.EnumOnRtnType.OnRtnDepthMarketData && e.Param != IntPtr.Zero)
                    {
                        var q = CTP.Conv.P2S<CTP.ThostFtdcDepthMarketDataField>(e.Param);
                        if (string.IsNullOrEmpty(q.InstrumentID)) return;
                        var tick = FromFtdcQuote(q);
                        var instId = ContractCodeGenerator.Normalize(q.InstrumentID);
                        var td = ParseTradingDay(q.TradingDay ?? "");
                        merged.Writer.TryWrite((instId, tick, td));
                        PersistChannel.Writer.TryWrite((instId, tick, td));
                        tracker?.FeedTick(instId, q);
                        if (firstQuote) { firstQuote = false; _log.Information("[CTP-MD] First tick: {InstId} @ {Price}", instId, q.LastPrice); }
                    }
                };

                _log.Information("CTP: Connecting to {Front}...", _opts.MdFront);
                _api.RegisterFront(_opts.MdFront);
                _api.Init();
                if (!await WaitFor(connected, 15000, ct)) { _log.Warning("CTP connect timeout"); continue; }
                if (!await WaitFor(loggedIn, 15000, ct)) { _log.Warning("CTP login timeout/failed"); continue; }
                _log.Information("CTP connected: {Front}", _opts.MdFront);

                if (!_filterReady && tracker != null) { tracker.Start(); _log.Information("Activity observation started ({Sec}s), full list: {Count} instruments", tracker.ObservationSeconds, _instruments.Count); }

                _log.Information("Subscribing to {Count} instruments in batches of 50", _instruments.Count);
                for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50)
                { _api.SubscribeMarketData(_instruments.Skip(i).Take(50).ToArray()); await Task.Delay(200, ct); }
                _log.Information("Subscription completed: {Count} instruments", _instruments.Count);

                var reader = merged.Reader;
                var lastObserveCheck = DateTime.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    lock (discLock) { if (disconnected) break; }
                    if (!_filterReady && tracker is { IsComplete: true })
                    {
                        var top = tracker.GetTopProducts(30); var topR = tracker.GetTopProductRanking(30);
                        _activeProductSet = new HashSet<string>(top);
                        foreach (var inst in StrategyInstruments) _activeProductSet.Add(ProductOf(inst));
                        _filterReady = true;
                        _log.Information("Activity filter ready: {Count} products → engine {F}/{T}", top.Count, _instruments.Count(c => _activeProductSet.Contains(ProductOf(c))), _instruments.Count);
                    }
                    if (!_filterReady) { using var rc = CancellationTokenSource.CreateLinkedTokenSource(ct); rc.CancelAfter(5000); try { if (!await reader.WaitToReadAsync(rc.Token)) break; } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; } }
                    else { if (!await reader.WaitToReadAsync(ct)) break; }
                    while (reader.TryRead(out var item))
                    {
                        var (instId, tick, tradingDay) = item;
                        if (!_filterReady || _activeProductSet!.Contains(ProductOf(instId)))
                        { writer.TryWrite(new TickEvent { Tick = tick, InstrumentId = instId, TradingDay = tradingDay, Time = DateTimeOffset.FromUnixTimeMilliseconds(tick.ExchangeTimestamp) }); barAgg.Feed(tick, instId, tradingDay); lock (barQueue) while (barQueue.Count > 0) writer.TryWrite(barQueue.Dequeue()); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Error(ex, "CTP producer loop crashed — retrying in 5s..."); try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; } }
            finally { try { _api?.Release(); } catch { } _api = null; }
        }
    }

    private static TickRecord FromFtdcQuote(CTP.ThostFtdcDepthMarketDataField q) => new()
    {
        ExchangeTimestamp = DateTimeOffset.Parse($"{q.ActionDay} {q.UpdateTime}").ToUnixTimeMilliseconds(),
        LocalTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        LastPrice = (long)(q.LastPrice * TickRecord.PriceScale),
        BidPrice1 = (long)(q.BidPrice1 * TickRecord.PriceScale),
        AskPrice1 = (long)(q.AskPrice1 * TickRecord.PriceScale),
        Volume = q.Volume, Turnover = q.Turnover, OpenInterest = q.OpenInterest,
        BidVolume1 = q.BidVolume1, AskVolume1 = q.AskVolume1, Flags = 0,
    };

    private static DateOnly ParseTradingDay(string td) => td.Length >= 8 && DateOnly.TryParseExact(td[..8], "yyyyMMdd", out var d) ? d : DateOnly.FromDateTime(DateTime.Today);
    private static string ProductOf(string i) => new(i.Where(c => !char.IsDigit(c)).ToArray());
    private static async Task<bool> WaitFor(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct) { using var c = CancellationTokenSource.CreateLinkedTokenSource(ct); c.CancelAfter(ms); try { await tcs.Task.WaitAsync(c.Token); return tcs.Task.Result; } catch (OperationCanceledException) { return false; } }
    public void Dispose() { if (_disposed) return; _disposed = true; try { _api?.Release(); } catch { } }
}

public static class TrackerExtensions
{
    /// <summary>FtdcNet.CTP 版本的 ActivityTracker.Feed — 直接传 Volume/OpenInterest</summary>
    public static void FeedTick(this ContractActivityTracker t, string instId, CTP.ThostFtdcDepthMarketDataField q)
        => t.Feed(instId, q.Volume, q.OpenInterest, q.LastPrice);
}
