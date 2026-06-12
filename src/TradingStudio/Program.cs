using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TradingStudio.Options;
using TradingStudio.Services;

// ================================================================
// TradingStudio collect — 行情采集
//
// 用法:
//   TradingStudio [exchange] [symbol] [options]
//
//   位置参数:
//     exchange   交易所过滤  SHFE|DCE|CZCE|CFFEX|INE|GFEX
//     symbol     品种或合约  ag(品种) | ag2608(合约)
//
//   可选参数:
//     --db PATH      数据库路径      默认 bars.db
//     --tick PATH    Tick输出目录    默认 TickData
//     --front ADDR   CTP行情前端     默认 appsettings.json
//     --broker ID    期货公司代码     默认 appsettings.json
//     --user ID      用户代码         默认 appsettings.json
//     --pwd PASS     密码             默认 appsettings.json
//
//   示例:
//     TradingStudio                                       # 全市场
//     TradingStudio SHFE ag                               # 上期所白银
//     TradingStudio SHFE ag2608 --db silver.db            # 指定库
//     TradingStudio DCE i --tick IronTicks --db iron.db   # 铁矿石
// ================================================================

// 1. 位置参数
string? exchange = null, symbol = null;
var i = 0;

// 2. 可选参数
var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
while (i < args.Length)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length)
    {
        overrides[args[i]] = args[i + 1];
        i += 2;
    }
    else if (!args[i].StartsWith("--"))
    {
        if (exchange is null) exchange = args[i];
        else if (symbol is null) symbol = args[i];
        i++;
    }
    else { i++; }
}

// 注入命令行覆盖到 IConfiguration（优先级高于 appsettings.json）
var cliArgs = new List<string>();
if (overrides.TryGetValue("--db",    out var db))    cliArgs.Add($"Collect:Database={db}");
if (overrides.TryGetValue("--tick",  out var tick))  cliArgs.Add($"Collect:TickData={tick}");
if (overrides.TryGetValue("--front", out var front)) cliArgs.Add($"Collect:MdFront={front}");
if (overrides.TryGetValue("--user",  out var user))  cliArgs.Add($"Collect:UserId={user}");
if (overrides.TryGetValue("--pwd",   out var pwd))   cliArgs.Add($"Collect:Password={pwd}");
if (overrides.TryGetValue("--broker", out var broker)) cliArgs.Add($"Collect:BrokerId={broker}");

var builder = Host.CreateApplicationBuilder([..cliArgs, ..args]);

builder.Services.AddSerilog((_, cfg) =>
    cfg.ReadFrom.Configuration(builder.Configuration));

var cfgSection = builder.Configuration.GetSection(CollectOptions.Section);
builder.Services.Configure<CollectOptions>(cfgSection);
builder.Services.PostConfigure<CollectOptions>(opts =>
{
    opts.ExchangeFilter = exchange;
    opts.SymbolFilter   = symbol;
});

builder.Services.AddHostedService<CollectService>();

await builder.Build().RunAsync();
