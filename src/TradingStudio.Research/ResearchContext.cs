namespace TradingStudio.Research;

/// <summary>
/// 研究上下文 — C# 交互式研究的统一入口。
/// 一行加载数据，链式调用分析。
///
/// 用法:
///   var ctx = new ResearchContext(@"D:\FuturesData\bars.db");
///   var bars = ctx.LoadBars("rb2605", "2025-01-01", "2025-06-01");
///   bars.AnalyzeReturns().Describe();
/// </summary>
public class ResearchContext
{
    private readonly BarReader _reader;

    public ResearchContext(string dbPath)
    {
        _reader = new BarReader(dbPath);
    }

    /// <summary>加载指定品种的 1-min Bar</summary>
    public BarSeries LoadBars(string instrumentId, DateTime start, DateTime end)
    {
        var bars = _reader.Load(instrumentId, "bars_1min", start, end);
        return new BarSeries(instrumentId, bars);
    }

    /// <summary>加载日线 Bar</summary>
    public BarSeries LoadDayBars(string instrumentId, DateTime start, DateTime end)
    {
        var bars = _reader.Load(instrumentId, "bars_day", start, end);
        return new BarSeries(instrumentId, bars);
    }

    /// <summary>列出所有可用品种</summary>
    public IReadOnlyList<string> ListInstruments()
    {
        return _reader.GetInstruments();
    }

    /// <summary>品种可交易日期范围</summary>
    public (DateTime Min, DateTime Max) GetDateRange(string instrumentId)
    {
        return _reader.GetDateRange(instrumentId);
    }
}
