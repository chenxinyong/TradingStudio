using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Aggregation;
using TradingStudio.Core.Models;
using TradingStudio.Data.Storage;
using TradingStudio.Options;
using CTP;

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
        _pipeline = new QuotePipeline(tickWriter, registry.Top30Codes, registry);   // registry 启用聚合闸门（时段+陈旧快照过滤）
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
        if (_store is DuckDBStore duckStore)
            duckStore.OnWriteError += (what, ex) => _log.Error(ex, "DuckDB {What} 写入失败", what);

        while (!ct.IsCancellationRequested)
        {
            if (!_scheduler.IsInSession())
            {
                var wait = _scheduler.WaitUntilNextSession();
                _log.Information("休市中，{Wait:F0}分钟后开盘（{Time}）",
                    wait.TotalMinutes, SessionScheduler.BeijingNow.Add(wait).ToString("HH:mm"));
                _health.Update("Idle", _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped,
                    _reconnectCount, "休市", _lastConnect, _lastQuote, _lastHealth);

                // 分片等待（同 EngineHost）：系统休眠会冻结单次长 Task.Delay，导致错过开盘
                while (!_scheduler.IsInSession() && !ct.IsCancellationRequested)
                {
                    var remaining = _scheduler.WaitUntilNextSession();
                    var slice = remaining > TimeSpan.FromSeconds(30)
                        ? TimeSpan.FromSeconds(30)
                        : remaining;
                    if (slice <= TimeSpan.Zero) continue;
                    try { await Task.Delay(slice, ct); } catch (OperationCanceledException) { break; } catch { /* retry */ }
                }
                if (ct.IsCancellationRequested) break;
            }

            var session = _scheduler.SessionName();
            _log.Information("进入{session}时段，开始采集", session);
            _lastConnect = DateTime.Now;

            // 发射并清理已结束交易日的日线状态（FlushDay 只清 Day < 参数，
            // 周五夜盘 GetFuturesTradingDay 算出周六 < CTP 实际的周一——方向永远"少清不多清"）
            _pipeline.AggDay.FlushDay(QuoteConverter.GetFuturesTradingDay(DateTime.Now));

            while (_scheduler.IsInSession() && !ct.IsCancellationRequested)
            {
                try
                {
                    await RunSession(batches, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.Error(ex, "Session error"); }

                if (!_scheduler.IsInSession() || ct.IsCancellationRequested) break;

                // 重连前发射快照
                _pipeline.FlushSnapshots();
                Interlocked.Increment(ref _reconnectCount);
                // 指数退避：5s→10s→20s→40s→80s→160s→300s
                var delay = Math.Min(300, 5 * (int)Math.Pow(2, Math.Min(_reconnectCount - 1, 6)));
                _log.Warning("Reconnect #{Count} in {Delay:F0}s (session={Session})",
                    _reconnectCount, delay, _scheduler.SessionName());
                _health.Update("Reconnecting", _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped,
                    _reconnectCount, _scheduler.SessionName(), _lastConnect, _lastQuote, _lastHealth);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; } catch { /* retry */ }
            }

            _log.Information("{Session}收盘，flush 数据", session);
            // 收盘只发快照：夜盘收盘时交易日尚未结束（次日日盘同属该交易日），清状态会丢夜盘 OHLCV
            _pipeline.FlushSnapshots();
            await Task.Delay(500, ct);
        }

        healthCts.Cancel();
        try { await healthTask; } catch (OperationCanceledException) { }

        _log.Information("Done. quotes={Quotes} bars={Bars} csv={Csv}",
            _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped);
    }

    private async Task RunSession(List<string[]> batches, CancellationToken ct)
    {
        using var api = new CTP.FtdcMdAdapter("", false, false);
        var connected = new TaskCompletionSource<bool>();
        var loggedIn = new TaskCompletionSource<bool>();
        var session = _scheduler.SessionName();

        var discLock = new object();
        var disconnected = false;
        var discTcs = new TaskCompletionSource<bool>();
        var errorTcs = new TaskCompletionSource<bool>();
        int requestId = 0;

        api.OnFrontEvent += (_, e) =>
        {
            if (e.EventType == CTP.EnumOnFrontType.OnFrontConnected)
            {
                _log.Information("[{Session}] Connected → Login", session);
                connected.TrySetResult(true);
                api.ReqUserLogin(new CTP.ThostFtdcReqUserLoginField
                { BrokerID = _cfg.BrokerId, UserID = _cfg.UserId, Password = _cfg.Password }, ++requestId);
            }
            else if (e.EventType == CTP.EnumOnFrontType.OnFrontDisconnected)
            {
                _log.Warning("[{Session}] Disconnected (0x{Reason:X})", session, e.Reason);
                lock (discLock) { disconnected = true; }
                discTcs.TrySetResult(true);
            }
        };

        api.OnRspEvent += (_, e) =>
        {
            if (e.EventType == CTP.EnumOnRspType.OnRspUserLogin)
            {
                if (e.RspInfo == null || e.RspInfo.ErrorID == 0)
                { _log.Information("[{Session}] Login OK TradingDay={Day}", session, api.GetTradingDay()); loggedIn.TrySetResult(true); }
                else { _log.Error("[{Session}] Login FAIL [{Code}] {Msg}", session, e.RspInfo.ErrorID, e.RspInfo.ErrorMsg); loggedIn.TrySetResult(false); }
            }
            else if (e.RspInfo != null && e.RspInfo.ErrorID != 0)
            {
                _log.Error("[{Session}] CTP Rsp Error [{Code}] {Msg}", session, e.RspInfo.ErrorID, e.RspInfo.ErrorMsg);
                if (e.RspInfo.ErrorID < 0) errorTcs.TrySetResult(true);
            }
        };

        var lastQuoteTime = DateTime.Now;
        api.OnRtnEvent += (_, e) =>
        {
            if (e.EventType == CTP.EnumOnRtnType.OnRtnDepthMarketData && e.Param != IntPtr.Zero)
            {
                var q = CTP.Conv.P2S<CTP.ThostFtdcDepthMarketDataField>(e.Param);
                if (string.IsNullOrEmpty(q.InstrumentID)) return;
                try { _pipeline.Feed(q); var now = DateTime.Now; _lastQuote = now; lastQuoteTime = now; }
                catch (Exception ex) { _log.Error(ex, "Quote handler error"); }
            }
        };

        api.RegisterFront(_cfg.MdFront);
        api.Init();
        if (!await WaitFor(connected, 15000, ct)) throw new Exception("Connection timeout");
        if (!await WaitFor(loggedIn, 15000, ct)) throw new Exception("Login timeout");

        _reconnectCount = 0;
        _log.Information("[{Session}] Login OK, subscribing {Count} contracts in {Batches} batches (50/batch)...",
            session, batches.Sum(b => b.Length), batches.Count);
        for (int i = 0; i < batches.Count && _scheduler.IsInSession(); i++)
        {
            api.SubscribeMarketData(batches[i]);
            if (i % 5 == 4) await Task.Delay(500, ct); else await Task.Delay(150, ct);
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
            try
            {
                await Task.Delay(60_000, ct);
                _lastHealth = DateTime.Now;
                var session = _scheduler.SessionName();
                _health.Update(
                    _scheduler.IsInSession() ? "Connected" : "Idle",
                    _pipeline.QuoteCount, _store.WrittenCount, _pipeline.TickSkipped,
                    _reconnectCount, session, _lastConnect, _lastQuote, _lastHealth);
                _log.Information("quotes={Quotes} bars={Bars} reconnect={Reconnects} skipped={Skipped} filtered={Filtered} [{Session}]",
                    _pipeline.QuoteCount, _store.WrittenCount, _reconnectCount, _pipeline.TickSkipped, _pipeline.AggFiltered, session);
                CheckDiskSpace();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 一次异常（如 health.json 被外部占用）不能永久杀死健康日志循环
                try { _log.Error(ex, "HealthLoop 异常，继续运行"); } catch { }
            }
        }
    }

    /// <summary>
    /// 数据盘剩余空间检查。低于 10GB 预警、低于 2GB 报错。
    /// 磁盘写满会让 DuckDB WAL 无法落盘（2026-08-24 事故根因），提前告警留出清理/扩容窗口。
    /// </summary>
    private void CheckDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_cfg.Database));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            const double warnGb = 10;
            const double critGb = 2;
            var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;

            if (freeGb < critGb)
                _log.Error("磁盘剩余空间仅 {Free:F1} GB（{Root}），写入将失败！请立即清理/扩容", freeGb, root);
            else if (freeGb < warnGb)
                _log.Warning("磁盘剩余空间不足 {Free:F1} GB（{Root}），请关注", freeGb, root);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "磁盘空间检查失败");
        }
    }

    private static async Task<bool> WaitFor(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms, ct));
        return done == tcs.Task && tcs.Task.Result;
    }
}
