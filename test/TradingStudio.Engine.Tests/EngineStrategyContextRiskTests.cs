using Microsoft.Extensions.Logging.Abstractions;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// 回归测试 — 守护「Buy 侧限价/止损单绕过风控」这个确定性 Bug。
///
/// 历史 Bug：EngineStrategyContext.LimitBuy / StopBuy 调用 _execution.Submit 时
/// 漏传 _portfolio，而 Submit 在 portfolio == null 时整段跳过风控；对应的 Sell 侧
/// 却传了 portfolio。结果 6 个下单入口里 Buy 侧的限价/止损单静默绕过风控。
///
/// 这些测试从 EngineStrategyContext（策略与引擎的唯一触点）发单，断言超限的
/// 限价/止损单——无论买卖——都会被风控拒单。若将来重构再次漏传 portfolio，
/// Buy 侧的两条测试会立即变红。
/// </summary>
public class EngineStrategyContextRiskTests
{
    private const string Sid = "s";

    // maxPosition = 5：任何 > 5 手的开仓单都应被 MaxPositionPerInstrumentRule 拦截。
    private static EngineStrategyContext MakeContext(int maxPosition = 5)
    {
        var risk = new RiskController(maxPosition: maxPosition);
        var execution = new ExecutionHandler(risk);
        var portfolio = new PortfolioManager(100_000);   // 空仓起步

        var registry = FutureRegistry.LoadFromJson(
            """
            {"symbols":[{"id":1,"exchange":"SHFE","code":"rb","name":"螺纹钢",
            "category":"黑色金属","deliveryType":"PHYSICAL","tradingUnit":10,
            "unitName":"吨","tickSize":1,"tickValue":10,"priceLimitPct":0.10,
            "marginRate":0.08,"months":"1~12月"}]}
            """);

        return new EngineStrategyContext(
            Sid, execution, portfolio, new IndicatorManager(), registry,
            ["rb"], new List<Bar>(), NullLogger.Instance)
        {
            IsWarmup = false,   // 非预热，下单才会真正提交
        };
    }

    // ─── Buy 侧：Bug 所在。修复前风控被跳过，会返回 Submitted 而非 Rejected ───

    [Fact]
    public void LimitBuy_OversizedOrder_RejectedByRisk()
    {
        var ctx = MakeContext(maxPosition: 5);
        var ticket = ctx.LimitBuy("rb", quantity: 10, limitPrice: 3000m);
        Assert.Equal(OrderStatus.Rejected, ticket.Status); // 10 > 5 → 必须被风控拦截
    }

    [Fact]
    public void StopBuy_OversizedOrder_RejectedByRisk()
    {
        var ctx = MakeContext(maxPosition: 5);
        var ticket = ctx.StopBuy("rb", quantity: 10, stopPrice: 4000m);
        Assert.Equal(OrderStatus.Rejected, ticket.Status);
    }

    // ─── Sell 侧：对称对照，一直是对的（守护对称性不再被破坏）───

    [Fact]
    public void LimitSell_OversizedOrder_RejectedByRisk()
    {
        var ctx = MakeContext(maxPosition: 5);
        var ticket = ctx.LimitSell("rb", quantity: 10, limitPrice: 4000m);
        Assert.Equal(OrderStatus.Rejected, ticket.Status);
    }

    [Fact]
    public void StopSell_OversizedOrder_RejectedByRisk()
    {
        var ctx = MakeContext(maxPosition: 5);
        var ticket = ctx.StopSell("rb", quantity: 10, stopPrice: 3000m);
        Assert.Equal(OrderStatus.Rejected, ticket.Status);
    }

    // ─── 反向对照：合规单不应被误杀，证明风控不是"一刀切"拒绝 ───

    [Fact]
    public void LimitBuy_WithinLimit_PassesRisk()
    {
        var ctx = MakeContext(maxPosition: 5);
        var ticket = ctx.LimitBuy("rb", quantity: 2, limitPrice: 3000m);
        Assert.Equal(OrderStatus.Submitted, ticket.Status); // 2 ≤ 5 → 放行
    }
}
