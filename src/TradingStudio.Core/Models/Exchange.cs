namespace TradingStudio.Core.Models;

/// <summary>六大期货交易所</summary>
public enum ExchangeCode : short
{
    SHFE  = 1,  // 上海期货交易所
    INE   = 2,  // 上海国际能源交易中心
    DCE   = 3,  // 大连商品交易所
    CZCE  = 4,  // 郑州商品交易所
    CFFEX = 5,  // 中国金融期货交易所
    GFEX  = 6,  // 广州期货交易所
}

public static class ExchangeCodeExtensions
{
    public static ExchangeCode FromCtp(string id) => id switch
    {
        "SHFE" => ExchangeCode.SHFE, "INE" => ExchangeCode.INE,
        "DCE" => ExchangeCode.DCE, "CZCE" => ExchangeCode.CZCE,
        "CFFEX" => ExchangeCode.CFFEX, "GFEX" => ExchangeCode.GFEX,
        _ => throw new ArgumentException(id)
    };

    public static string ToCtp(this ExchangeCode x) => x switch
    {
        ExchangeCode.SHFE => "SHFE", ExchangeCode.INE => "INE",
        ExchangeCode.DCE => "DCE", ExchangeCode.CZCE => "CZCE",
        ExchangeCode.CFFEX => "CFFEX", ExchangeCode.GFEX => "GFEX",
        _ => ""
    };

    public static string ShortName(this ExchangeCode x) => x switch
    {
        ExchangeCode.SHFE => "上期所", ExchangeCode.INE => "上能源",
        ExchangeCode.DCE => "大商所", ExchangeCode.CZCE => "郑商所",
        ExchangeCode.CFFEX => "中金所", ExchangeCode.GFEX => "广期所",
        _ => ""
    };
}
