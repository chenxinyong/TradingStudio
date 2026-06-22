using System.Text;
using TradingStudio.Core.Models;
using TradingStudio.Data.Import;

namespace TradingStudio.Data.Tests;

public class CsvTickImporterTests
{
    [Fact]
    public void ParseFileName_OurFormat_Works()
    {
        var (symbol, day) = CsvTickImporter.ParseFileName(@"C:\TickData\SHFE\ag2608_20260623.csv");
        Assert.Equal("ag2608", symbol);
        Assert.Equal(new DateOnly(2026, 6, 23), day);
    }

    [Fact]
    public void ParseFileName_JinshuyuanFormat_Works()
    {
        var (symbol, day) = CsvTickImporter.ParseFileName(
            @"金数源_商品tick快照样本_cu1603_20160111_CTP格式.csv");
        Assert.Equal("cu1603", symbol);
        Assert.Equal(new DateOnly(2016, 1, 11), day);
    }

    [Fact]
    public void ParseFileName_RarFormat_Works()
    {
        var (symbol, day) = CsvTickImporter.ParseFileName(@"sc\cu2501_20250601.csv");
        Assert.Equal("cu2501", symbol);
        Assert.Equal(new DateOnly(2025, 6, 1), day);
    }

    [Fact]
    public void ParseRarFileName_ContinuousContract_Works()
    {
        var (symbol, day) = CsvTickImporter.ParseRarFileName("ag主力连续_20210104.csv");
        Assert.Equal("ag主力连续", symbol);
        Assert.Equal(new DateOnly(2021, 1, 4), day);
    }

    [Fact]
    public void Parse_ValidCsv_YieldsParseResults()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var csv = string.Join('\n',
            "日期,合约代码,交易所代码,合约在交易所的代码,最新价,上次结算价,昨收盘,昨持仓量,今开盘,最高价,最低价,数量,成交金额,持仓量,今收盘,本次结算价,涨停板价,跌停板价,昨虚实度,今虚实度,最后修改时间,最后修改毫秒,申买价一,申买量一,申卖价一,申卖量一,申买价二,申买量二,申卖价二,申卖量二,申买价三,申买量三,申卖价三,申卖量三,申买价四,申买量四,申卖价四,申卖量四,申买价五,申买量五,申卖价五,申卖量五,当日均价,业务日期",
            "20260623,ag2608,,,15988.0000,16765.0000,16493.0000,245422,15988.0000,15996.0000,15950.0000,1499,359336820.0000,245716,0.0000,0.0000,20118.0000,13412.0000,0,0,09:00:00,500,15980.0000,8,15982.0000,28,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,239717.0000,20260623",
            "20260623,ag2608,,,15982.0000,16765.0000,16493.0000,245422,15988.0000,15996.0000,15950.0000,1500,359400000.0000,245720,0.0000,0.0000,20118.0000,13412.0000,0,0,09:01:00,0,15981.0000,5,15983.0000,15,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,0.0000,0,239600.0000,20260623"
        );

        using var reader = new StringReader(csv);
        var results = CsvTickImporter.Parse(reader, "ag2608", new DateOnly(2026, 6, 23)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(15988.0, results[0].Tick.LastPriceDouble, 4);
        Assert.Equal(1499L, results[0].Tick.Volume);
        Assert.Equal(15982.0, results[1].Tick.LastPriceDouble, 4);
        Assert.Equal(1500L, results[1].Tick.Volume);
    }

    [Fact]
    public void Parse_ShortLine_Skipped()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var csv = "a,b,c\nshort,line" + '\n';  // < 26 fields

        using var reader = new StringReader(csv);
        var results = CsvTickImporter.Parse(reader, "ag", new DateOnly(2026, 1, 1)).ToList();
        Assert.Empty(results);
    }
}
