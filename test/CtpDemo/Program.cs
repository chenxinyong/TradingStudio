using CTP;

// ================================================================
// CTP C++/CLI Demo — MdApi + TraderApi 完整调用示例
// 用法:
//   dotnet run                          → 行情模式 (SimNow)
//   dotnet run md                       → 行情模式
//   dotnet run td                       → 交易模式 (需实盘账号)
//   dotnet run all                      → 行情+交易
//   dotnet run <front> <broker> <user> <pwd> [symbols...]
// ================================================================

var mode = args.Length > 0 && args[0] is "md" or "td" or "all" ? args[0] : "md";
var offset = mode is "md" or "td" or "all" ? 1 : 0;

var mdFront  = Arg(args, offset + 0, "tcp://182.254.243.31:30011");  // SimNow 行情
var tdfront  = Arg(args, offset + 1, "tcp://180.168.146.187:10212");  // SimNow 交易
var broker   = Arg(args, offset + 2, "9999");
var user     = Arg(args, offset + 3, "13961193449");
var password = Arg(args, offset + 4, "Chenxy@20240218!");
var symbols  = args.Length > offset + 5 ? args[(offset + 5)..] : new[] { "ag2608" };

PrintBanner(mode);

Console.WriteLine($"MD Front : {mdFront}");
Console.WriteLine($"TD Front : {tdfront}");
Console.WriteLine($"Broker   : {broker}");
Console.WriteLine($"User     : {user}");
if (mode is "md" or "all") Console.WriteLine($"Symbols  : {string.Join(", ", symbols)}");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    if (mode is "md" or "all")
        await RunMdDemo(mdFront, broker, user, password, symbols, cts.Token);

    if (mode is "td" or "all")
        await RunTdDemo(tdfront, broker, user, password, cts.Token);
}
catch (OperationCanceledException) { }

Console.WriteLine("\nDone.");
return 0;

// ================================================================
// MdApi Demo
// ================================================================
static async Task RunMdDemo(string front, string broker, string user, string password,
    string[] symbols, CancellationToken ct)
{
    Console.WriteLine("─── MdApi Demo ───\n");

    using var md = new MdApi();
    var connected = new TaskCompletionSource<bool>();
    var loggedIn = new TaskCompletionSource<bool>();

    md.OnFrontConnected += () => { Console.WriteLine("[Md] Connected → Login"); connected.TrySetResult(true); md.Login(broker, user, password); };
    md.OnFrontDisconnected += r => Console.WriteLine($"[Md] Disconnected (0x{r:X})");
    md.OnLogin += (err, info) =>
    {
        if (err.IsOK()) { Console.WriteLine($"[Md] Login OK  TradingDay={info.TradingDay}"); loggedIn.TrySetResult(true); }
        else { Console.WriteLine($"[Md] Login FAIL [{err.ErrorID}] {err.ErrorMsg}"); loggedIn.TrySetResult(false); }
    };
    md.OnSubscribeRsp += (id, err, _) => Console.WriteLine($"[Md] Subscribe {id} → {(err.IsOK() ? "OK" : err.ErrorMsg)}");
    md.OnError += (err, req) => Console.WriteLine($"[Md] Error req={req}: [{err.ErrorID}] {err.ErrorMsg}");

    var tickCount = 0L;

    md.OnQuote += q =>
    {
        var n = Interlocked.Increment(ref tickCount);
        Console.WriteLine($"{q.UpdateTime}.{q.UpdateMillisec:D3} {q.InstrumentID} P={q.LastPrice:F2} V={q.Volume} OI={q.OpenInterest:F0} B={q.BidPrice1:F2}x{q.BidVolume1} A={q.AskPrice1:F2}x{q.AskVolume1} {(n % 10 == 0 ? n.ToString() : "")}");
    };

    md.Connect(front);
    if (!await WaitFor(connected, 10000)) { Console.WriteLine("[Md] Connection timeout"); return; }
    if (!await WaitFor(loggedIn, 10000))   { Console.WriteLine("[Md] Login timeout"); return; }

    Console.WriteLine($"\n[Md] Subscribing {symbols.Length} symbols...");
    md.Subscribe(symbols);

    Console.WriteLine("[Md] Running (Ctrl+C to stop)...\n");
    try { await Task.Delay(Timeout.Infinite, ct); }
    catch (OperationCanceledException) { }

    md.Unsubscribe(symbols);
    Console.WriteLine($"\n[Md] Total: {tickCount} ticks");
}

// ================================================================
// TraderApi Demo
// ================================================================
static async Task RunTdDemo(string front, string broker, string user, string password, CancellationToken ct)
{
    Console.WriteLine("─── TraderApi Demo ───\n");

    using var td = new TraderApi();
    var connected = new TaskCompletionSource<bool>();
    var loggedIn = new TaskCompletionSource<bool>();
    var accountDone = new TaskCompletionSource<bool>();
    var positionDone = new TaskCompletionSource<bool>();

    td.OnFrontConnected += () => { Console.WriteLine("[Td] Connected → Login"); connected.TrySetResult(true); td.Login(broker, user, password); };
    td.OnFrontDisconnected += r => Console.WriteLine($"[Td] Disconnected (0x{r:X})");
    td.OnLogin += (err, info) =>
    {
        if (err.IsOK()) { Console.WriteLine($"[Td] Login OK  TradingDay={info.TradingDay}  SessionID={info.SessionID}"); loggedIn.TrySetResult(true); }
        else { Console.WriteLine($"[Td] Login FAIL [{err.ErrorID}] {err.ErrorMsg}"); loggedIn.TrySetResult(false); }
    };
    td.OnError += (err, req) => Console.WriteLine($"[Td] Error req={req}: [{err.ErrorID}] {err.ErrorMsg}");

    // Account query
    td.OnAccount += (acc, err, isLast) =>
    {
        if (err.IsOK() && acc is not null)
        {
            Console.WriteLine($"\n  ═══ Account ═══");
            Console.WriteLine($"  Balance        : {acc.Balance,12:F2}");
            Console.WriteLine($"  Available      : {acc.Available,12:F2}");
            Console.WriteLine($"  CurrMargin     : {acc.CurrMargin,12:F2}");
            Console.WriteLine($"  CloseProfit    : {acc.CloseProfit,12:F2}");
            Console.WriteLine($"  PositionProfit : {acc.PositionProfit,12:F2}");
            Console.WriteLine($"  Commission     : {acc.Commission,12:F2}");
            Console.WriteLine($"  FrozenMargin   : {acc.FrozenMargin,12:F2}");
            Console.WriteLine($"  WithdrawQuota  : {acc.WithdrawQuota,12:F2}");
        }
        if (isLast) accountDone.TrySetResult(true);
    };

    // Position query
    var posCount = 0;
    td.OnPosition += (pos, isLast) =>
    {
        if (pos is not null && pos.Position != 0)
        {
            if (posCount++ == 0) Console.WriteLine($"\n  ═══ Positions ═══");
            var dir = pos.Direction == '0' ? "Long" : "Short";
            Console.WriteLine($"  {pos.InstrumentID,-10} {dir,-6}  Total:{pos.Position,5}  Today:{pos.PositionToday,5}  Yd:{pos.PositionYesterday,5}  Margin:{pos.UseMargin,10:F2}  PnL:{pos.PositionProfit,8:F2}");
        }
        if (isLast) { Console.WriteLine($"  ({posCount} positions)\n"); positionDone.TrySetResult(true); }
    };

    // Order/Trade feedback
    td.OnOrder += o => Console.WriteLine($"[Td] Order: {o.InstrumentID} {o.OrderRef} status={o.OrderStatus} traded={o.VolumeTraded}/{o.VolumeTotalOriginal}");
    td.OnTrade += t => Console.WriteLine($"[Td] Fill:  {t.InstrumentID} {t.Direction} {t.OffsetFlag}  {t.Price:F2} x {t.Volume}  {t.TradeID}");

    td.Connect(front);
    if (!await WaitFor(connected, 10000)) { Console.WriteLine("[Td] Connection timeout"); return; }
    if (!await WaitFor(loggedIn, 10000))   { Console.WriteLine("[Td] Login timeout"); return; }

    // Confirm settlement (required before trading)
    Console.WriteLine("[Td] Confirming settlement...");
    td.ConfirmSettlement();
    await Task.Delay(500, ct);

    // Query account
    Console.WriteLine("[Td] Querying account...");
    td.QueryAccount();
    if (!await WaitFor(accountDone, 8000)) Console.WriteLine("[Td] Account query timeout");

    // Query position
    Console.WriteLine("[Td] Querying positions...");
    td.QueryPosition();
    if (!await WaitFor(positionDone, 8000)) Console.WriteLine("[Td] Position query timeout");

    Console.WriteLine("[Td] Demo complete.\n");
}

// ================================================================
// Helpers
// ================================================================
static async Task<bool> WaitFor(TaskCompletionSource<bool> tcs, int ms)
{
    var done = await Task.WhenAny(tcs.Task, Task.Delay(ms));
    return done == tcs.Task && tcs.Task.Result;
}

static string Arg(string[] args, int i, string def) => i < args.Length ? args[i] : def;
static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

static void PrintBanner(string mode)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔══════════════════════════════════════════╗");
    Console.WriteLine("║   CTP C++/CLI Wrapper Demo               ║");
    Console.WriteLine("║   TradingStudio                           ║");
    Console.WriteLine($"║   Mode: {mode,-32}║");
    Console.WriteLine("╚══════════════════════════════════════════╝");
    Console.WriteLine();
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    var pass = "";
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && pass.Length > 0) pass = pass[..^1];
        else if (key.Key != ConsoleKey.Backspace) pass += key.KeyChar;
    }
    Console.WriteLine();
    return pass;
}
