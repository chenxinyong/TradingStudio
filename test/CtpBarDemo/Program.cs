using CTP;
using TradingStudio.Data.Aggregation;
using TradingStudio.Core.Models;
using TradingStudio.Data.Storage;

var mdFront  = "tcp://182.254.243.31:30011";
var broker   = "9999";
var user     = "13961193449";
var password = "Chenxy@20240218!";
var dbPath   = "bars.db";

Log("═══════════════════════════════════════");
Log("  CTP 7×24 Pipeline");
Log("═══════════════════════════════════════");

// ========== 1. 加载品种 ==========
var registry = FutureRegistry.Load("../../../../../docs/contracts/symbols.json");
var batches = ContractCodeGenerator.BatchSubscribe(registry.All.Values, 80).ToList();
Log($"Loaded {registry.Count} futures, {batches.Sum(b => b.Length)} contracts in {batches.Count} batches");

// ========== 2. 存储器（持久化，整个生命周期复用）==========
using var store = new BarStore(dbPath);
using var tickWriter = new TickCsvWriter("TickData");
var agg1Min = new BarAggregator();
var aggDay = new DailyBarAggregator();
var quoteCount = 0L;
var reconnectCount = 0;
var cts = new CancellationTokenSource();

agg1Min.OnBar += bar => store.WriteAsync(bar);
aggDay.OnBar  += bar => store.WriteAsync(bar);

// 每分钟刷日线到 DB
var dayTimer = new System.Timers.Timer(60_000);
dayTimer.Elapsed += (_, _) => { try { aggDay.FlushAll(); } catch { } };
dayTimer.Start();

// 每分钟健康日志
var healthTimer = new System.Timers.Timer(60_000);
healthTimer.Elapsed += (_, _) =>
{
    var qc = Interlocked.Read(ref quoteCount);
    Log($"HEALTH quotes={qc} bars1m={store.WrittenCount} reconnects={reconnectCount} files={tickWriter.WrittenCount}");
};
healthTimer.Start();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ========== 3. 主循环：自动重连 ==========
while (!cts.IsCancellationRequested)
{
    try
    {
        await RunSession(cts.Token);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Log($"SESSION ERROR: {ex.Message}");
    }

    if (cts.IsCancellationRequested) break;

    // 指数退避重连
    reconnectCount++;
    var delay = Math.Min(60, Math.Pow(2, Math.Min(reconnectCount, 6)));
    Log($"Reconnect #{reconnectCount} in {delay:F0}s...");
    try { await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token); } catch { break; }
}

// ========== 4. 清理 ==========
Log("Shutting down...");
dayTimer.Stop(); dayTimer.Dispose();
healthTimer.Stop(); healthTimer.Dispose();
agg1Min.Flush(); agg1Min.Dispose();
aggDay.FlushAll(); aggDay.Dispose();
await Task.Delay(500);
tickWriter.Dispose();

Log($"═══ Done: quotes={quoteCount} bars={store.WrittenCount} csv={tickWriter.WrittenCount} ═══");

// ================================================================
// 单次会话：连接→登录→订阅→接收数据
// ================================================================
async Task RunSession(CancellationToken ct)
{
    using var md = new MdApi();
    var connected = new TaskCompletionSource<bool>();
    var loggedIn  = new TaskCompletionSource<bool>();

    md.OnFrontConnected += () =>
    {
        Log("Connected → Login");
        connected.TrySetResult(true);
        md.Login(broker, user, password);
    };

    md.OnFrontDisconnected += r =>
    {
        Log($"Disconnected (0x{r:X})");
        connected.TrySetResult(false);
        loggedIn.TrySetResult(false);
    };

    md.OnLogin += (err, info) =>
    {
        if (err.IsOK())
        {
            Log($"Login OK TradingDay={info?.TradingDay}");
            loggedIn.TrySetResult(true);
        }
        else
        {
            Log($"Login FAIL [{err.ErrorID}] {err.ErrorMsg}");
            loggedIn.TrySetResult(false);
        }
    };

    md.OnQuote += q =>
    {
        try
        {
            if (string.IsNullOrEmpty(q.InstrumentID)) return;
            var instId = ContractCodeGenerator.Normalize(q.InstrumentID);
            var tradingDay = DateOnly.TryParseExact(q.TradingDay, "yyyyMMdd", out var d) ? d : DateOnly.FromDateTime(DateTime.Today);

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

            agg1Min.Feed(record, instId, tradingDay);
            aggDay.Feed(record, instId, tradingDay);
            tickWriter.Write(instId, q.ExchangeID, q.TradingDay,
                q.UpdateTime, q.UpdateMillisec,
                q.LastPrice, q.PreSettlementPrice, q.PreClosePrice, q.PreOpenInterest,
                q.OpenPrice, q.HighestPrice, q.LowestPrice,
                q.Volume, q.Turnover, q.OpenInterest,
                q.ClosePrice, q.SettlementPrice, q.UpperLimitPrice, q.LowerLimitPrice,
                q.BidPrice1, q.BidVolume1, q.AskPrice1, q.AskVolume1,
                q.BidPrice2, q.BidVolume2, q.AskPrice2, q.AskVolume2,
                q.BidPrice3, q.BidVolume3, q.AskPrice3, q.AskVolume3,
                q.BidPrice4, q.BidVolume4, q.AskPrice4, q.AskVolume4,
                q.BidPrice5, q.BidVolume5, q.AskPrice5, q.AskVolume5,
                q.AveragePrice);

            Interlocked.Increment(ref quoteCount);
        }
        catch (Exception ex) { Log($"Quote error: {ex.Message}"); }
    };

    md.OnError += (err, req) => { if (err.ErrorID != 0) Log($"Error [{err.ErrorID}] {err.ErrorMsg}"); };
    md.OnSubscribeRsp += (id, err, _) => { if (err.ErrorID != 0 && id != null) Log($"Sub FAIL {id}: [{err.ErrorID}] {err.ErrorMsg}"); };

    // 连接
    Log($"Connecting {mdFront}...");
    md.Connect(mdFront);
    if (!await WaitFor(connected, 15000, ct)) throw new Exception("Connection timeout");
    if (!await WaitFor(loggedIn, 15000, ct)) throw new Exception("Login timeout");

    // 订阅
    Log($"Subscribing {batches.Count} batches...");
    for (int i = 0; i < batches.Count; i++)
    {
        md.Subscribe(batches[i]);
        await Task.Delay(200, ct);
    }
    Log($"Subscribed OK. Receiving data...");

    // 运行直到断连或取消
    while (!ct.IsCancellationRequested)
    {
        try { await Task.Delay(1000, ct); } catch { break; }
        // 如果断连了，退出循环触发重连
        if (connected.Task.IsCompleted && !connected.Task.Result) break;
        if (loggedIn.Task.IsCompleted && !loggedIn.Task.Result) break;
    }
}

static async Task<bool> WaitFor(TaskCompletionSource<bool> tcs, int ms, CancellationToken ct)
{
    var done = await Task.WhenAny(tcs.Task, Task.Delay(ms, ct));
    return done == tcs.Task && tcs.Task.Result;
}

static void Log(string msg)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
