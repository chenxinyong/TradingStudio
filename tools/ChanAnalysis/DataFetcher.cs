using System.Text;
using System.Text.Json;

namespace ChanAnalysis;

/// <summary>从新浪财经 API 获取期货日K线。</summary>
public static class DataFetcher
{
    private static readonly HttpClient Client = new();

    static DataFetcher()
    {
        Client.DefaultRequestHeaders.Referrer = new Uri("https://finance.sina.com.cn");
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    }

    /// <summary>
    /// 获取某合约的日K线。
    /// symbol 例如 "AG2609"、"V2701"、"SA2701"。
    /// </summary>
    public static async Task<List<Bar>> FetchDailyKLineAsync(string symbol)
    {
        // 注意：路径里的 "var%20d=" 是新浪接口的 JSONP 回调占位符，需原样保留
        string url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20d=" +
                     "/InnerFuturesNewService.getDailyKLine?symbol=" + symbol;

        // 新浪返回的 Content-Type 字符集可能无效，故按字节读取后手动按 UTF-8 解码
        byte[] bytes = await Client.GetByteArrayAsync(url);
        string body = Encoding.UTF8.GetString(bytes);

        // 剥掉 JSONP 包装：前面可能带 "/*...*/" 注释，数据在 "var d=( [...])" 里
        int start = body.IndexOf('[');
        int end = body.LastIndexOf(']');
        if (start < 0 || end < start)
            throw new InvalidDataException($"无法解析返回数据: {body[..Math.Min(body.Length, 200)]}");

        string json = body[start..(end + 1)];
        using var doc = JsonDocument.Parse(json);
        var bars = new List<Bar>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            // 新浪接口所有数值字段均为字符串，需手动转换
            var d = el.GetProperty("d").GetString()!;
            var o = el.GetProperty("o").GetString()!;
            var h = el.GetProperty("h").GetString()!;
            var l = el.GetProperty("l").GetString()!;
            var c = el.GetProperty("c").GetString()!;
            long oi = el.TryGetProperty("p", out var p) ? long.Parse(p.GetString()!) : 0;

            bars.Add(new Bar(
                DateTime.Parse(d),
                double.Parse(o), double.Parse(h), double.Parse(l), double.Parse(c),
                oi));
        }
        return bars;
    }

    /// <summary>
    /// 获取股票指数的日K线。
    /// symbol 例如 "sz399317"(国证A股)、"sz399006"(创业板)、"sh000688"(科创50)。
    /// 接口与期货不同：返回纯 JSON 数组，字段为 day/open/high/low/close。
    /// </summary>
    public static async Task<List<Bar>> FetchIndexDailyKLineAsync(string symbol)
    {
        string url = "https://money.finance.sina.com.cn/quotes_service/api/json_v2.php/" +
                     $"CN_MarketData.getKLineData?symbol={symbol}&scale=240&ma=no&datalen=300";

        byte[] bytes = await Client.GetByteArrayAsync(url);
        string body = Encoding.UTF8.GetString(bytes);

        using var doc = JsonDocument.Parse(body);
        var bars = new List<Bar>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var d = el.GetProperty("day").GetString()!;
            var o = el.GetProperty("open").GetString()!;
            var h = el.GetProperty("high").GetString()!;
            var l = el.GetProperty("low").GetString()!;
            var c = el.GetProperty("close").GetString()!;

            bars.Add(new Bar(
                DateTime.Parse(d),
                double.Parse(o), double.Parse(h), double.Parse(l), double.Parse(c)));
        }
        return bars;
    }
}
