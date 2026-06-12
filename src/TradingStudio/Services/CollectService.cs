using CTP;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TradingStudio.Data.Aggregation;
using TradingStudio.Core.Models;
using TradingStudio.Data.Storage;
using TradingStudio.Options;

namespace TradingStudio.Services;

public class CollectService : BackgroundService
{
    private readonly CollectOptions _cfg;
    private readonly SessionScheduler _scheduler = SessionScheduler.CreateWithHolidays();
    private readonly HealthMonitor _health;
    private readonly Serilog.ILogger _log;

    private long _quoteCount, _reconnectCount;
    private DateTime _lastConnect, _lastQuote, _lastHealth;

    public CollectService(IOptions<CollectOptions> options, Serilog.ILogger logger)
    {
        _cfg = options.Value;
        _log = logger;
        _health = new HealthMonitor();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("════════════════════════════════");
        _log.Information("  TradingStudio collect — 行情采集");
        _log.Information("════════════════════════════════");

        var registry = FutureRegistry.Load(_cfg.SymbolsPath);

        // 命令行过滤
        var futures = registry.All.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(_cfg.ExchangeFilter))
            futures = futures.Where(f => f.Exchange.ToString().Equals(_cfg.ExchangeFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(_cfg.SymbolFilter))
        {
            var sf = _cfg.SymbolFilter;
            if (sf.Any(char.IsDigit))
                futures = futures.Where(f => sf.StartsWith(f.Code, StringComparison.OrdinalIgnoreCase));
            else
                futures = futures.Where(f => f.Code.Equals(sf, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = futures.ToList();

        var batches = ContractCodeGenerator.BatchSubscribe(filtered, 80)
            .Select(b => _cfg.SymbolFilter?.Any(char.IsDigit) == true
                ? b.Where(c => c.Equals(_cfg.SymbolFilter, StringComparison.OrdinalIgnoreCase)).ToArray()
                : b)
            .Where(b => b.Length > 0)
            .ToList();

        _log.Information("Loaded {Count} futures → {Total} contracts in {Batches} batches",
            filtered.Count, batches.Sum(b => b.Length), batches.Count);

        using var store = new BarStore(_cfg.Database);
        using var tickWriter = new TickCsvWriter(_cfg.TickData);

        _ = HealthLoop(store, tickWriter, ct);

        while (!ct.IsCancellationRequested)
        {
            // 等下一个交易时段
            if (!_scheduler.IsInSession())
            {
                var wait = _scheduler.WaitUntilNextSession();
                _log.Information("休市中，{Wait:F0}分钟后开盘（{Time}）",
                    wait.TotalMinutes, SessionScheduler.BeijingNow.Add(wait).ToString("HH:mm"));
                _health.Update("Idle", _quoteCount, store.WrittenCount, tickWriter.WrittenCount,
                    _reconnectCount, "休市", _lastConnect, _lastQuote, _lastHealth);
                try { await Task.Delay(wait, ct); } catch { break; }
                if (ct.IsCancellationRequested) break;
            }

            var session = _scheduler.SessionName();
            _log.Information("进入{session}时段，开始采集", session);
            _lastConnect = DateTime.Now;

            using var agg1Min = new BarAggregator();
            using var aggDay = new DailyBarAggregator();
            agg1Min.OnBar += bar => store.WriteAsync(bar);
            aggDay.OnBar  += bar => store.WriteAsync(bar);

            // 日线定时刷新（每分钟）+ 日盘收盘前强制 flush
            _ = DayFlushLoop(aggDay, ct);

            // 会话内：自动重连
            while (_scheduler.IsInSession() && !ct.IsCancellationRequested)
            {
                try
                {
                    await RunSession(agg1Min, aggDay, tickWriter, batches, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.Error(ex, "Session error"); }

                if (!_scheduler.IsInSession() || ct.IsCancellationRequested) break;

                Interlocked.Increment(ref _reconnectCount);
                var delay = Math.Min(30, Math.Pow(2, Math.Min(_reconnectCount, 5)));
                _log.Warning("Reconnect #{Count} in {Delay:F0}s", _reconnectCount, delay);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch { break; }
            }

            // 收盘：flush 所有未完成的 Bar
            _log.Information("{Session}收盘，flush 数据", session);
            agg1Min.Flush();
            aggDay.FlushAll();
            await Task.Delay(500, ct);
        }

        _log.Information("Done. quotes={Quotes} bars={Bars} csv={Csv}",
            _quoteCount, store.WrittenCount, tickWriter.WrittenCount);
    }

    private async Task RunSession(BarAggregator agg1Min, DailyBarAggregator aggDay,
        TickCsvWriter tickWriter, List<string[]> batches, CancellationToken ct)
    {
        using var md = new MdApi();
        var connected = new TaskCompletionSource<bool>();
        var loggedIn = new TaskCompletionSource<bool>();
        var session = _scheduler.SessionName();

        md.OnFrontConnected += () =>
        {
            _log.Information("[{Session}] Connected → Login", session);
            connected.TrySetResult(true);
            md.Login(_cfg.BrokerId, _cfg.UserId, _cfg.Password);
        };
        md.OnFrontDisconnected += r =>
        {
            _log.Warning("[{Session}] Disconnected (0x{Reason:X})", session, r);
            connected.TrySetResult(false); loggedIn.TrySetResult(false);
        };
        md.OnLogin += (err, info) =>
        {
            if (err.IsOK()) { _log.Information("[{Session}] Login OK TradingDay={Day}", session, info?.TradingDay); loggedIn.TrySetResult(true); }
            else { _log.Error("[{Session}] Login FAIL [{Code}] {Msg}", session, err.ErrorID, err.ErrorMsg); loggedIn.TrySetResult(false); }
        };
        md.OnError += (err, req) =>
        { if (err.ErrorID != 0) _log.Error("[{Session}] [{Code}] {Msg}", session, err.ErrorID, err.ErrorMsg); };
        md.OnQuote += q =>
        {
            try { HandleQuote(q, agg1Min, aggDay, tickWriter); _lastQuote = DateTime.Now; }
            catch (Exception ex) { _log.Error(ex, "Quote handler error"); }
        };

        md.Connect(_cfg.MdFront);
        if (!await WaitFor(connected, 15000, ct)) throw new Exception("Connection timeout");
        if (!await WaitFor(loggedIn, 15000, ct)) throw new Exception("Login timeout");

        _reconnectCount = 0; // 重置重连计数
        for (int i = 0; i < batches.Count && _scheduler.IsInSession(); i++)
        {
            md.Subscribe(batches[i]);
            await Task.Delay(200, ct);
        }

        while (_scheduler.IsInSession() && !ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            if (connected.Task.IsCompleted && !connected.Task.Result) break;
            if (loggedIn.Task.IsCompleted && !loggedIn.Task.Result) break;
        }
    }

    private void HandleQuote(CTP.Quote q, BarAggregator agg1Min, DailyBarAggregator aggDay, TickCsvWriter tickWriter)
    {
        if (string.IsNullOrEmpty(q.InstrumentID)) return;
        var instId = ContractCodeGenerator.Normalize(q.InstrumentID);
        var tradingDay = DateOnly.TryParseExact(q.TradingDay, "yyyyMMdd", out var d)
            ? d : DateOnly.FromDateTime(DateTime.Today);

        var record = new TickRecord
        {
            ExchangeTimestamp = q.ExchangeTimestamp, LocalTimestamp = q.LocalTimestamp,
            LastPrice = (long)(q.LastPrice * TickRecord.PriceScale),
            Volume = q.Volume, Turnover = q.Turnover, OpenInterest = q.OpenInterest,
            BidPrice1 = (long)(q.BidPrice1 * TickRecord.PriceScale), BidVolume1 = q.BidVolume1,
            AskPrice1 = (long)(q.AskPrice1 * TickRecord.PriceScale), AskVolume1 = q.AskVolume1,
            Flags = (q.LastPrice >= q.UpperLimitPrice && q.UpperLimitPrice > 0 ? 1 : 0)
                  | (q.LastPrice <= q.LowerLimitPrice && q.LowerLimitPrice > 0 ? 2 : 0)
        };

        agg1Min.Feed(record, instId, tradingDay); aggDay.Feed(record, instId, tradingDay);
        tickWriter.Write(instId, q.ExchangeID, q.TradingDay,
            q.UpdateTime, q.UpdateMillisec, q.LastPrice, q.PreSettlementPrice, q.PreClosePrice,
            q.PreOpenInterest, q.OpenPrice, q.HighestPrice, q.LowestPrice,
            q.Volume, q.Turnover, q.OpenInterest,
            q.ClosePrice, q.SettlementPrice, q.UpperLimitPrice, q.LowerLimitPrice,
            q.BidPrice1, q.BidVolume1, q.AskPrice1, q.AskVolume1,
            q.BidPrice2, q.BidVolume2, q.AskPrice2, q.AskVolume2,
            q.BidPrice3, q.BidVolume3, q.AskPrice3, q.AskVolume3,
            q.BidPrice4, q.BidVolume4, q.AskPrice4, q.AskVolume4,
            q.BidPrice5, q.BidVolume5, q.AskPrice5, q.AskVolume5, q.AveragePrice);
        Interlocked.Increment(ref _quoteCount);
    }

    private async Task DayFlushLoop(DailyBarAggregator aggDay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(60_000, ct);
            try { aggDay.FlushAll(); } catch { }
        }
    }

    private async Task HealthLoop(BarStore store, TickCsvWriter tickWriter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(60_000, ct);
            _lastHealth = DateTime.Now;
            var session = _scheduler.SessionName();
            _health.Update(
                _scheduler.IsInSession() ? "Connected" : "Idle",
                _quoteCount, store.WrittenCount, tickWriter.WrittenCount,
                _reconnectCount, session, _lastConnect, _lastQuote, _lastHealth);
            _log.Information("quotes={Quotes} bars={Bars} reconnect={Reconnects} csv={Csv} [{Session}]",
                _quoteCount, store.WrittenCount, _reconnectCount, tickWriter.WrittenCount, session);
        }
    }

    private static async Task<bool> WaitFor(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms, ct));
        return done == tcs.Task && tcs.Task.Result;
    }
}
