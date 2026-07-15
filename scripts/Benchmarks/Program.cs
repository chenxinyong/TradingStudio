using DuckDB.NET.Data;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;
using TradingStudio.Engine.Examples;

const string DbPath = @"C:\Works\ClaudeCode\TradingStudio\data\bars_history.duckdb";
string[] insts = ["rb000", "MA000", "TA000", "FG000", "SA000"];
const string Period = "15min";
string Table = $"bars_{Period}";

// Register strategies
StrategyFactory.Register<MaCrossStrategy>("MaCross");
StrategyFactory.Register<BuyAndHold>("BuyAndHold");

var outPath = @$"C:\Works\ClaudeCode\TradingStudio\scripts\_strategy_matrix.txt";
using var sw = new StreamWriter(outPath);
sw.WriteLine($"═══ 策略对比矩阵 ({Period} K线) — {DateTime.Now:yyyy-MM-dd HH:mm} ═══");
sw.WriteLine($"Top5 品种: {string.Join(", ", insts)}, 100k本金, 固定5元/手");
sw.WriteLine("");

foreach (var inst in insts)
{
    var code = inst.Replace("000", "");
    // Load bars
    var bars = new List<Bar>();
    using var db = new DuckDBConnection($"Data Source={DbPath}");
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT instrument_id, trading_day, bar_time, open, high, low, close, volume FROM {Table} WHERE instrument_id='{inst}' AND volume>0 ORDER BY bar_time";
    using var reader = (DuckDBDataReader)cmd.ExecuteReader();
    DateTime ReadT(int i) => reader.GetFieldType(i) == typeof(string) ? DateTime.Parse(reader.GetString(i)) : reader.GetDateTime(i);
    DateOnly ReadD(int i) => reader.GetFieldType(i) == typeof(string) ? DateOnly.Parse(reader.GetString(i)) : DateOnly.FromDateTime(reader.GetDateTime(i));
    while (reader.Read())
        bars.Add(new Bar { InstrumentId = inst, TradingDay = ReadD(1), BarTime = ReadT(2), Open = reader.GetInt64(3), High = reader.GetInt64(4), Low = reader.GetInt64(5), Close = reader.GetInt64(6), Volume = reader.GetInt64(7) });
    sw.WriteLine($"{code}: loaded {bars.Count} {Period} bars, {bars[0].BarTime:yyyy-MM-dd} ~ {bars[^1].BarTime:yyyy-MM-dd}");
    if (bars.Count < 5000) { sw.WriteLine($"  ⚠️ 数据不足,跳过\n"); continue; }

    var registry = FutureRegistry.LoadFromJson($$"""{"symbols":[{"id":1,"exchange":"SHFE","code":"{{code}}","name":"","category":"","deliveryType":"PHYSICAL","tradingUnit":10,"unitName":"","tickSize":1,"tickValue":10,"priceLimitPct":0.10,"marginRate":0.08,"feePerLot":5,"months":"1-12"}]}""");

    foreach (var (sType, sParams) in new (string, Action<StrategyParameters>)[]
    {
        ("BuyAndHold", p => { }),
        ("MaCross", p => { p.Add("FastPeriod","10"); p.Add("SlowPeriod","30"); p.Add("AdxPeriod","0"); p.Add("MinAdx","0"); p.Add("DailyTrendFilter","false"); p.Add("MaxPosition","4"); p.Add("MaxMarginRatio","0.50"); p.Add("RiskPerTrade","0.02"); }),
    })
    {
        var feed = new MockBarFeed(bars); feed.Initialize(bars[0].BarTime, bars[^1].BarTime, [inst]);
        var sp = new StrategyParameters(); sParams(sp);
        var config = new StrategyConfig { StrategyId = code, StrategyType = sType, Instruments = [inst], Priority = 1, AllocatedCapital = 100_000m };
        foreach (var (k, v) in sp) config.Parameters.Add(k, v);
        var opts = new EngineOptions { StartTime=bars[0].BarTime, EndTime=bars[^1].BarTime, Instruments=[inst], StrategyConfigs=[config], StartingCapital=100_000, IsLive=false, WarmupDays=0 };

        try
        {
            var engine = new TradingEngine(feed, new ExecutionHandler(new RiskController(999,999,1.0m)), new PortfolioManager(100_000), new IndicatorManager(), new StrategyContainer(), new RiskController(999,999,1.0m), new FeedbackMonitor(), new TickSnapshot(), opts, registry);
            var report = await engine.RunAsync(CancellationToken.None);
            var sr = report.StrategyReports.First();
            var eq = report.FinalPortfolio.TotalEquity;
            sw.WriteLine($"  {sType,-12}: FinalEq={eq,10:F0}  uPnL={sr.TotalNetProfit,8:F0}  Trades={sr.TotalTrades,3}  MaxDD={sr.MaxDrawdown,5:P2}  Fees={sr.TotalFees,7:F0}");
        }
        catch (Exception ex) { sw.WriteLine($"  {sType,-12}: ERROR - {ex.Message}"); }
    }
    sw.WriteLine("");
}

sw.WriteLine("Done.");
sw.Flush();

// ─── Strategies ────────────────────────────────────────
public sealed class BuyAndHold : IStrategy
{ public string Name => "BuyAndHold"; private StrategyContext _c=null!; private int _bars; public void Initialize(StrategyContext c)=>_c=c; public void OnTick(TickRecord t,string i){} public void OnBar(Bar b){_bars++; if(_bars==2&&!_c.IsWarmup) _c.MarketBuy(b.InstrumentId,1);} public void OnOrderEvent(OrderEvent e){} public void OnEndOfAlgorithm(){} }

// MockBarFeed
public sealed class MockBarFeed(List<Bar> bars) : IDataFeed
{ public IReadOnlyList<string> Instruments => ["x"]; public DateTime StartTime{get;private set;} public DateTime EndTime{get;private set;} public void Initialize(DateTime s,DateTime e,IReadOnlyList<string> _){StartTime=s;EndTime=e;} public async IAsyncEnumerable<DataEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct){foreach(var b in bars){if(ct.IsCancellationRequested)yield break; yield return new BarEvent{Bar=b,Time=new DateTimeOffset(b.BarTime,TimeSpan.FromHours(8)),IsNewBar=true};} await Task.CompletedTask;} }
