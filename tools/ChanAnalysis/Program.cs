using ChanAnalysis;

// 用法:
//   dotnet run -- <合约代码> [名称]         期货，终端文本
//   dotnet run -- index <指数代码> [名称]   指数，终端文本（仅周线）
//   dotnet run -- md <合约代码> [名称]      期货，Markdown（追加日志用）
//   dotnet run -- mdindex <指数代码> [名称] 指数，Markdown（追加日志用）

if (args.Length < 1)
{
    Console.Error.WriteLine("用法: dotnet run -- [index|md|mdindex] <代码> [名称]");
    return 1;
}

string mode = args[0];
bool isIndex = false;
bool asMarkdown = false;
int argStart;

switch (mode)
{
    case "index":
        isIndex = true; asMarkdown = false; argStart = 1; break;
    case "md":
        isIndex = false; asMarkdown = true; argStart = 1; break;
    case "mdindex":
        isIndex = true; asMarkdown = true; argStart = 1; break;
    default:
        isIndex = false; asMarkdown = false; argStart = 0; break;
}

if (argStart >= args.Length)
{
    Console.Error.WriteLine("缺少代码参数");
    return 1;
}

string symbol = args[argStart].ToUpperInvariant();
string name = args.Length > argStart + 1 ? args[argStart + 1] : symbol;

try
{
    var daily = isIndex
        ? await DataFetcher.FetchIndexDailyKLineAsync(symbol)
        : await DataFetcher.FetchDailyKLineAsync(symbol);

    // 统一 UTF-8 输出，避免 Windows 控制台 GBK 乱码
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    if (asMarkdown)
    {
        // 指数周线 min_gap=4，期货 min_gap=2；指数仅周线，期货含日线
        string md = ReportGenerator.BuildMarkdown(symbol, name, daily,
            weeklyMinGap: isIndex ? 4 : 2, includeDaily: !isIndex);
        Console.WriteLine(md);
    }
    else
    {
        var report = ReportGenerator.BuildReport(symbol, name, daily);
        Console.WriteLine(report);
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"分析失败: {ex.Message}");
    return 1;
}
