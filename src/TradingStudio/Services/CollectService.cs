using CTP;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TradingStudio.Core.Storage;
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
    private readonly IBarStore _store;
    private readonly QuotePipeline _pipeline;
    private readonly Serilog.ILogger _log;

    private long _reconnectCount;
    private DateTime _lastConnect, _lastQuote, _lastHealth;

    public CollectService(IOptions<CollectOptions> options, IBarStore store,
                          TickCsvWriter tickWriter, Serilog.ILogger logger)
    {
        _cfg = options.Value;
        _store = store;
        _log = logger;
        _health = new HealthMonitor();

        var registry = FutureRegistry.Load(_cfg.SymbolsPath);
        _pipeline = new QuotePipeline(tickWriter, registry.Top30Codes);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("════════════════════════════════");
        _log.Information("  TradingStudio collect — 行情采集");
        _log.Information("════════════════════════════════");

        var registry = FutureRegistry.Load(_cfg.SymbolsPath);
        _log.Information("Top 30 filter loaded: {Count} varieties", registry.Top30Codes.Count);

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

        var batches = ContractCodeGenerator.BatchSubscribe(filtered, 50)  // 50/batch减少CTP压力
            .Select(b => _cfg.SymbolFilter?.Any(char.IsDigit) == true
                ? b.Where(c => c.Equals(_cfg.SymbolFilter, StringComparison.OrdinalIgnoreCase)).ToArray()
                : b)
            .Where(b => b.Length > 0)
            .ToList();

        _log.Information("Loaded {Count} futures → {Total} contracts in {Batches} batches",
            filtered.Count, batches.Sum(b => b.Length), batches.Count);
        // CZCE诊断: 检查郑商所合约是否在订阅列表中
        var czceCodes = batches.SelectMany(b => b).Where(c => c.Length == 5 && char.IsUpper(c[0]) && char.IsUpper(c[1])).Take(10).ToList();
        if (czceCodes.Count > 0)
            _log.Information("CZCE samples: {Codes}...", string.Join(", ", czceCodes));
        else
            _log.Warning("NO CZCE contracts in subscription list!");

        // 确保数据目录存在
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(_cfg.Database));
        if (dbDir != null) Directory.CreateDirectory(dbDir);

        using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var healthTask = HealthLoop(healthCts.Token);

        // Wire Bar → Store
        _pipeline.Agg1Min.OnBar += bar => _store.WriteAsync(bar);
        _pipeline.AggDay.OnBar  += bar => _store.WriteAsync(bar);

        while (!ct.IsCancellationRequested)
        {
            if (!_scheduler.IsInSession())
            {
                var wait = _scheduler.WaitUntilNextSession();
                _log.Information("休市中，{Wait:F0}分钟后开盘（{Time}）",
                    wait.TotalMinutes, SessionScheduler.BeijingNow.Add(wait).ToString("HH:mm"));
                _health.Update("Idle", _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped,
                    _reconnectCount, "休市", _lastConnect, _lastQuote, _lastHealth);
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; } catch { /* retry */ }
                if (ct.IsCancellationRequested) break;
            }

            var session = _scheduler.SessionName();
            _log.Information("进入{session}时段，开始采集", session);
            _lastConnect = DateTime.Now;

            while (_scheduler.IsInSession() && !ct.IsCancellationRequested)
            {
                try
                {
                    await RunSession(batches, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.Error(ex, "Session error"); }

                if (!_scheduler.IsInSession() || ct.IsCancellationRequested) break;

                // 重连前flush BarAggregator，避免状态残留导致Bar卡住
                _pipeline.Flush();
                Interlocked.Increment(ref _reconnectCount);
                // 快速重试：5s→10s→20s→30s，不在交易时段内浪费
                var delay = Math.Min(30, 5 * Math.Pow(2, Math.Min(_reconnectCount - 1, 3)));
                _log.Warning("Reconnect #{Count} in {Delay:F0}s (session={Session})",
                    _reconnectCount, delay, _scheduler.SessionName());
                _health.Update("Reconnecting", _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped,
                    _reconnectCount, _scheduler.SessionName(), _lastConnect, _lastQuote, _lastHealth);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; } catch { /* retry */ }
            }

            _log.Information("{Session}收盘，flush 数据", session);
            _pipeline.Flush();
            await Task.Delay(500, ct);
        }

        healthCts.Cancel();
        try { await healthTask; } catch (OperationCanceledException) { }

        _log.Information("Done. quotes={Quotes} bars={Bars} csv={Csv}",
            _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped);
    }

    private async Task RunSession(List<string[]> batches, CancellationToken ct)
    {
        using var md = new MdApi();
        var connected = new TaskCompletionSource<bool>();
        var loggedIn = new TaskCompletionSource<bool>();
        var session = _scheduler.SessionName();

        var discLock = new object();
        var disconnected = false;
        var discTcs = new TaskCompletionSource<bool>();

        md.OnFrontConnected += () =>
        {
            _log.Information("[{Session}] Connected → Login", session);
            connected.TrySetResult(true);
            md.Login(_cfg.BrokerId, _cfg.UserId, _cfg.Password);
        };
        md.OnFrontDisconnected += r =>
        {
            _log.Warning("[{Session}] Disconnected (0x{Reason:X})", session, r);
            lock (discLock) { disconnected = true; }
            discTcs.TrySetResult(true);
        };
        md.OnLogin += (err, info) =>
        {
            if (err.IsOK()) { _log.Information("[{Session}] Login OK TradingDay={Day}", session, info?.TradingDay); loggedIn.TrySetResult(true); }
            else { _log.Error("[{Session}] Login FAIL [{Code}] {Msg}", session, err.ErrorID, err.ErrorMsg); loggedIn.TrySetResult(false); }
        };
        // CTP错误回调 + 订阅错误 → 强制重连
        var errorTcs = new TaskCompletionSource<bool>();
        md.OnError += (err, req) =>
        {
            if (err.ErrorID != 0)
            {
                _log.Error("[{Session}] CTP Error [{Code}] {Msg}", session, err.ErrorID, err.ErrorMsg);
                if (err.ErrorID < 0) errorTcs.TrySetResult(true); // 严重错误→重连
            }
        };
        md.OnRspSubscribe += (err, insts) =>
        {
            if (!err.IsOK())
                _log.Warning("[{Session}] Subscribe error [{Code}] {Msg} for {Count} instruments",
                    session, err.ErrorID, err.ErrorMsg, insts?.Length ?? 0);
        };
        var lastQuoteTime = DateTime.Now;
        md.OnQuote += q =>
        {
            try { _pipeline.Feed(q); var now = DateTime.Now; _lastQuote = now; lastQuoteTime = now; }
            catch (Exception ex) { _log.Error(ex, "Quote handler error"); }
        };

        md.Connect(_cfg.MdFront);
        if (!await WaitFor(connected, 15000, ct)) throw new Exception("Connection timeout");
        if (!await WaitFor(loggedIn, 15000, ct)) throw new Exception("Login timeout");

        _reconnectCount = 0;
        _log.Information("[{Session}] Subscribing {Count} contracts in {Batches} batches (50/batch)...",
            session, batches.Sum(b => b.Length), batches.Count);
        for (int i = 0; i < batches.Count && _scheduler.IsInSession(); i++)
        {
            md.Subscribe(batches[i]);
            if (i % 5 == 4) await Task.Delay(500, ct); else await Task.Delay(150, ct); // 每5批多歇一下
        }
        _log.Information("[{Session}] Subscription done", session);

        // 事件驱动+心跳：30s无行情→强制重连，每30min强制刷新
        var sessionStart = DateTime.Now;
        while (_scheduler.IsInSession() && !ct.IsCancellationRequested)
        {
            var timeout = Task.Delay(30000, ct);
            var winner = await Task.WhenAny(timeout, discTcs.Task, errorTcs.Task);

            bool discSignaled = winner == discTcs.Task || winner == errorTcs.Task;
            bool timedOut = winner == timeout;
            bool noQuotes = (DateTime.Now - lastQuoteTime).TotalSeconds > 30;
            bool forceRefresh = (DateTime.Now - sessionStart).TotalMinutes > 30;

            lock (discLock) { if (disconnected) discSignaled = true; }

            if (discSignaled || (timedOut && noQuotes) || forceRefresh)
            {
                var reason = discSignaled ? "CTP断连/错误" :
                             forceRefresh ? "30min定时刷新" : "30s无行情";
                _log.Warning("[{Session}] 重连触发: {Reason}", session, reason);
                break;
            }
            if (discTcs.Task.IsCompleted) discTcs = new TaskCompletionSource<bool>();
            if (errorTcs.Task.IsCompleted) errorTcs = new TaskCompletionSource<bool>();
        }
    }

    private async Task HealthLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(60_000, ct);
            _lastHealth = DateTime.Now;
            var session = _scheduler.SessionName();
            _health.Update(
                _scheduler.IsInSession() ? "Connected" : "Idle",
                _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped,
                _reconnectCount, session, _lastConnect, _lastQuote, _lastHealth);
            _log.Information("quotes={Quotes} bars={Bars} reconnect={Reconnects} skipped={Skipped} [{Session}]",
                _pipeline.QuoteCount, _store.WrittenCount, _reconnectCount, _pipeline.TickSkipped, session);
        }
    }

    private static async Task<bool> WaitFor(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms, ct));
        return done == tcs.Task && tcs.Task.Result;
    }
}
