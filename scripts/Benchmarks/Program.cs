using DuckDB.NET.Data;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;

const string DbPath = @"C:\Works\ClaudeCode\TradingStudio\data\bars_history.duckdb";
string[] insts = ["rb000", "MA000", "TA000", "FG000", "SA000"];

// ─── 策略: Buy & Hold ──────────────────────────────────
StrategyFactory.Register<BuyAndHold>("BuyAndHold");

var outPath = @"C:\Works\ClaudeCode\TradingStudio\scripts\_baseline_benchmarks.txt";
using var sw = new StreamWriter(outPath);
sw.WriteLine($"═══ Buy & Hold 基准回测 (日线) — {DateTime.Now:yyyy-MM-dd} ═══");
sw.WriteLine("逻辑: 第一根 Bar 买1手,最后一根 Bar 平仓。无过滤,无止损。");
sw.WriteLine("作用: 任何策略的最低参照——必须跑赢这个才有意义。");
sw.WriteLine(string.Format("{0,-6} {1,9} {2,6} {3,10} {4,7}", "品种", "总盈亏", "胜率", "终值", "终/初%"));
sw.WriteLine(new string('-', 55));

var lines = new List<string>();

foreach (var inst in insts)
{
    var code = inst.Replace("000", "");

    // Load bars
    var bars = new List<Bar>();
    using var db = new DuckDBConnection($"Data Source={DbPath}");
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT instrument_id, trading_day, bar_time, open, high, low, close, volume FROM bars_day WHERE instrument_id='{inst}' AND volume>0 ORDER BY bar_time";
    using var reader = (DuckDBDataReader)cmd.ExecuteReader();

    DateTime ReadT(int i) => reader.GetFieldType(i) == typeof(string)
        ? DateTime.Parse(reader.GetString(i)) : reader.GetDateTime(i);
    DateOnly ReadD(int i) => reader.GetFieldType(i) == typeof(string)
        ? DateOnly.Parse(reader.GetString(i)) : DateOnly.FromDateTime(reader.GetDateTime(i));

    while (reader.Read())
        bars.Add(new Bar { InstrumentId = inst, TradingDay = ReadD(1), BarTime = ReadT(2),
            Open = reader.GetInt64(3), High = reader.GetInt64(4), Low = reader.GetInt64(5),
            Close = reader.GetInt64(6), Volume = reader.GetInt64(7) });

    sw.WriteLine($"{code}: loaded {bars.Count} bars, {bars[0].BarTime:yyyy-MM-dd} ~ {bars[^1].BarTime:yyyy-MM-dd}");
    if (bars.Count < 500) { sw.WriteLine($"{code} 数据不足,跳过"); continue; }

    // Run backtest
    var feed = new MockBarFeed(bars);
    feed.Initialize(bars[0].BarTime, bars[^1].BarTime, [inst]);

    var config = new StrategyConfig
    {
        StrategyId = "base", StrategyType = "BuyAndHold",
        Instruments = [inst], Priority = 1, AllocatedCapital = 100_000,
    };

    var options = new EngineOptions
    {
        StartTime = bars[0].BarTime, EndTime = bars[^1].BarTime,
        Instruments = [inst], StrategyConfigs = [config],
        StartingCapital = 100_000, IsLive = false, WarmupDays = 0,
    };

    var registry = FutureRegistry.LoadFromJson($$"""
        {"symbols":[{"id":1,"exchange":"SHFE","code":"{{code}}","name":"","category":"",
        "deliveryType":"PHYSICAL","tradingUnit":10,"unitName":"","tickSize":1,
        "tickValue":10,"priceLimitPct":0.10,"marginRate":0.08,"feePerLot":5,"months":"1-12"}]}
        """);

    try
    {
        var engine = new TradingEngine(
            feed, new ExecutionHandler(new RiskController(999, 999, 1.0m)),
            new PortfolioManager(100_000), new IndicatorManager(),
            new StrategyContainer(), new RiskController(999, 999, 1.0m),
            new FeedbackMonitor(), new TickSnapshot(), options, registry);

        var report = await engine.RunAsync(CancellationToken.None);
        var sr = report.StrategyReports.First();

        var finalEq = report.FinalPortfolio.TotalEquity;
        lines.Add($"{code,-6} {sr.TotalNetProfit,9:F0} {sr.WinRate,6:P1} {finalEq,10:F0} {(finalEq/100000-1)*100,6:F1}%");
    }
    catch (Exception ex)
    {
        sw.WriteLine($"  ERROR: {ex.Message}");
    }
}

sw.WriteLine("");
foreach (var l in lines) sw.WriteLine(l);
sw.WriteLine("\nDone.");

// ─── Buy & Hold 策略 ─────────────────────────────
public sealed class BuyAndHold : IStrategy
{
    public string Name => "BuyAndHold";
    private StrategyContext _ctx = null!;
    private bool _done;
    public void Initialize(StrategyContext ctx) => _ctx = ctx;
    public void OnTick(TickRecord t, string i) { }
    public void OnBar(Bar b)
    {
        if (!_done && !_ctx.IsWarmup && _ctx.GetPosition(b.InstrumentId) is not { Quantity: not 0 })
        {
            _ctx.MarketBuy(b.InstrumentId, 1);
            _done = true;
        }
    }
    public void OnOrderEvent(OrderEvent e) { }
    public void OnEndOfAlgorithm() => _ctx.ClosePosition(_ctx.SubscribedInstruments[0]);
}

// MockBarFeed
public sealed class MockBarFeed(List<Bar> bars) : IDataFeed
{
    public IReadOnlyList<string> Instruments => ["x"];
    public DateTime StartTime { get; private set; }
    public DateTime EndTime { get; private set; }
    public void Initialize(DateTime s, DateTime e, IReadOnlyList<string> _) { StartTime = s; EndTime = e; }
    public async IAsyncEnumerable<DataEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var b in bars) { if (ct.IsCancellationRequested) yield break; yield return new BarEvent { Bar = b, Time = new DateTimeOffset(b.BarTime, TimeSpan.FromHours(8)), IsNewBar = true }; }
        await Task.CompletedTask;
    }
}
