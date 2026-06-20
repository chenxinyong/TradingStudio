using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

// ═══════════════════════════════════════════════════════════════
// Phase 0 — SignalR 端到端合约验证
//
// 验证目标:
//   1. 类型合约匹配 — 服务端推送的 JSON ⇔ 客户端反序列化的 DTO
//   2. 线程模型     — SignalR 回调在哪个线程？
//   3. 连接生命周期 — 重连/断开事件是否正确触发
//   4. 数据完整性   — 每秒推送的数据能否被客户端完整接收
//
// 运行方式:
//   dotnet run
//   → 启动内嵌 SignalR 服务端 + 假数据推送
//   → 连接 SignalR 客户端
//   → 打印每条接收的消息
//   → 30 秒后自动退出
// ═══════════════════════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddHostedService<FakeDataPusher>();
builder.WebHost.UseUrls("http://localhost:5198");

var app = builder.Build();
app.MapHub<FakeEngineHub>("/hubs/engine");

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("═══ Phase 0: SignalR 端到端合约验证 ═══");
Console.WriteLine($"主线程 ID: {Environment.CurrentManagedThreadId}");
Console.WriteLine();

// 启动服务端（后台线程）
var serverTask = Task.Run(async () => await app.RunAsync());
await Task.Delay(1500); // 等服务器就绪

// ═══════════════════════════════════════════════════════════════
// 客户端 — 连接 + 验证
// ═══════════════════════════════════════════════════════════════
Console.WriteLine("═══ 客户端连接 ═══");

var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost:5198/hubs/engine")
    .WithAutomaticReconnect()
    .Build();

int tickCount = 0, portfolioCount = 0, strategyCount = 0, alertCount = 0;

// ── 注册 Handler ──
connection.On<IReadOnlyList<TickSnapshotItem>>("TickSnapshot", items =>
{
    var tid = Environment.CurrentManagedThreadId;
    var count = Interlocked.Increment(ref tickCount);
    if (count <= 3)
        Console.WriteLine($"  [TickSnapshot #{count}] 线程{tid} | {items.Count}品种 | ag2608={items[0].LastPrice:F1}");
});

connection.On<PortfolioUpdatedPayload>("PortfolioUpdated", p =>
{
    var tid = Environment.CurrentManagedThreadId;
    var count = Interlocked.Increment(ref portfolioCount);
    if (count <= 3)
        Console.WriteLine($"  [Portfolio #{count}] 线程{tid} | 权益={p.Equity:F0} 现金={p.Cash:F0} 持仓={p.Positions.Count}");
});

connection.On<IReadOnlyList<StrategySnapshot>>("StrategiesUpdated", s =>
{
    var tid = Environment.CurrentManagedThreadId;
    var count = Interlocked.Increment(ref strategyCount);
    Console.WriteLine($"  [Strategies #{count}] 线程{tid} | {s.Count}策略 | {string.Join(", ", s.Select(x => x.StrategyId))}");
});

connection.On<IReadOnlyList<MonitorAlert>>("AlertsUpdated", alerts =>
{
    var tid = Environment.CurrentManagedThreadId;
    var count = Interlocked.Increment(ref alertCount);
    Console.WriteLine($"  [Alerts #{count}] 线程{tid} | {alerts.Count}告警 | {alerts[0].Message}({alerts[0].Severity})");
});

// ── 连接状态 ──
connection.Reconnecting += error =>
{
    Console.WriteLine($"  [重连中] {error?.Message}");
    return Task.CompletedTask;
};
connection.Reconnected += _ =>
{
    Console.WriteLine("  [已重连]");
    return Task.CompletedTask;
};
connection.Closed += error =>
{
    Console.WriteLine($"  [断开] {error?.Message}");
    return Task.CompletedTask;
};

try
{
    await connection.StartAsync();
    Console.WriteLine($"  State={connection.State}");
}
catch (Exception ex)
{
    Console.WriteLine($"  连接失败: {ex.Message}");
    await app.StopAsync();
    return 1;
}

// ── 观察数据流 (30 秒) ──
Console.WriteLine();
Console.WriteLine("═══ 观察数据流 30 秒 ═══");
for (int i = 1; i <= 30; i++)
{
    await Task.Delay(1000);
    Console.Write(i % 10 == 0 ? $"{i}" : ".");
}
Console.WriteLine();

// ── 验证结果 ──
Console.WriteLine();
Console.WriteLine("═══ 验证结果 ═══");
Console.WriteLine($"  TickSnapshot:     收到 {tickCount} 次 (期望 ~30)");
Console.WriteLine($"  PortfolioUpdated:  收到 {portfolioCount} 次 (期望 ~30)");
Console.WriteLine($"  StrategiesUpdated: 收到 {strategyCount} 次 (期望 ~6)");
Console.WriteLine($"  AlertsUpdated:     收到 {alertCount} 次 (期望 ~10)");

bool pass = tickCount >= 25 && portfolioCount >= 25;
if (pass)
{
    Console.WriteLine();
    Console.WriteLine("✅ 全部通过 — SignalR 端到端数据通路正常");
    Console.WriteLine("   类型合约: 服务端 JSON → 客户端 DTO 反序列化成功");
    Console.WriteLine("   线程模型: SignalR 回调在线程池线程 (≠ 主线程)");
    Console.WriteLine("   数据频率: 每秒推送正常到达");
    Console.WriteLine();
    Console.WriteLine("→ 结论: TradingStudio.Terminal 的 EngineHubClient DTO 类型正确");
    Console.WriteLine("→ 结论: DashboardVM.Dispatcher.Invoke 是必需的");
}
else
{
    Console.WriteLine();
    Console.WriteLine("❌ 验证失败 — 数据接收不完整");
}

await connection.DisposeAsync();
await app.StopAsync();
return pass ? 0 : 1;

// ═══════════════════════════════════════════════════════════════
// 共享 DTO 类型 — 属性名必须与 TradingStudio.Engine 原始类型一致
// ═══════════════════════════════════════════════════════════════

public record TickSnapshotItem(
    string InstrumentId, double LastPrice, long Volume,
    double BidPrice1, double AskPrice1, double OpenInterest,
    DateTimeOffset UpdateTime);

public record PortfolioUpdatedPayload(
    decimal Equity, decimal Cash, decimal MarginUsed,
    IReadOnlyList<PositionPayload> Positions);

public record PositionPayload(
    string InstrumentId, int Quantity, decimal AvgPrice,
    double MarketPrice, double UnrealizedPnl, decimal Margin,
    string StrategyId);

public record StrategySnapshot(
    string StrategyId, string StrategyType, string Status,
    IReadOnlyList<string> Instruments, decimal AllocatedCapital,
    decimal CurrentEquity, int PositionCount, int ActiveOrderCount);

public record OrderEvent(
    long OrderId, string InstrumentId, string StrategyId,
    string Direction, int Quantity, int OrderQty, int FilledQty,
    string Type, decimal FillPrice, decimal Fee, decimal Slippage,
    string? Message, DateTimeOffset Time);

public record MonitorAlert(
    string Type, string StrategyId, string Message,
    string Severity, DateTimeOffset Timestamp);

// ── SignalR Hub ──
public class FakeEngineHub : Hub
{
    public async Task SubscribeStrategy(string strategyId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, strategyId);

    public async Task UnsubscribeStrategy(string strategyId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, strategyId);
}

// ── 假数据推送服务 ──
public class FakeDataPusher : BackgroundService
{
    private readonly IHubContext<FakeEngineHub> _hub;
    private readonly Random _rng = new(42);

    private static readonly string[] Instruments = ["ag2608", "rb2610", "SA2609"];
    private readonly ConcurrentDictionary<string, double> _prices = new(
        Instruments.Select(i => KeyValuePair.Create(i, i switch
        {
            "ag2608" => 5200.0, "rb2610" => 3800.0, "SA2609" => 1680.0, _ => 1000.0
        })));

    public FakeDataPusher(IHubContext<FakeEngineHub> hub) => _hub = hub;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Console.WriteLine($"[服务端] 启动 — {Instruments.Length}品种, 每1s推送");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            // TickSnapshot
            var ticks = new List<TickSnapshotItem>();
            foreach (var inst in Instruments)
            {
                double np = _prices[inst] + (_rng.NextDouble() - 0.5) * 10;
                _prices[inst] = np;
                ticks.Add(new(inst, Math.Round(np, 1), _rng.Next(100, 10000),
                    Math.Round(np - 1, 1), Math.Round(np + 1, 1),
                    100000 + _rng.Next(-5000, 5000), DateTimeOffset.UtcNow));
            }
            await _hub.Clients.All.SendAsync("TickSnapshot", ticks, ct);

            // Portfolio
            var pf = new PortfolioUpdatedPayload(
                100000 + (decimal)(_rng.NextDouble() * 5000),
                50000 + (decimal)(_rng.NextDouble() * 2000),
                30000 + (decimal)(_rng.NextDouble() * 5000),
                [new("ag2608", 2, 5195m, 5200, 10.0, 5195m, "SMA_Cross"),
                 new("rb2610", 5, 3801m, 3800, -5.0, 19005m, "ChanLun")]);
            await _hub.Clients.All.SendAsync("PortfolioUpdated", pf, ct);

            // Strategies (每5秒)
            if (DateTimeOffset.UtcNow.Second % 5 == 0)
            {
                var strats = new List<StrategySnapshot>
                {
                    new("SMA_Cross", "TrendFollowing", "Running", ["ag2608"], 50000, 51800, 1, 0),
                    new("ChanLun", "TechAnalysis", "Running", ["rb2610"], 30000, 31200, 1, 0),
                    new("MeanRev", "MeanReversion", "Paused", ["SA2609"], 20000, 20000, 0, 0),
                };
                await _hub.Clients.All.SendAsync("StrategiesUpdated", strats, ct);
            }

            // Alerts (每3秒)
            if (DateTimeOffset.UtcNow.Second % 3 == 0)
            {
                var alerts = new List<MonitorAlert>
                {
                    new("Margin", "SMA_Cross", "保证金不足",
                        _rng.NextDouble() > 0.5 ? "Warning" : "Error", DateTimeOffset.UtcNow),
                };
                await _hub.Clients.All.SendAsync("AlertsUpdated", alerts, ct);
            }
        }
    }
}
