using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

public class TickAggregationGateTests
{
    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    /// <summary>rb 有完整 tradingHours；xx 无 tradingHours 字段（fail-open）</summary>
    private static FutureRegistry MakeRegistry() => FutureRegistry.LoadFromJson("""
        {
          "symbols": [
            {
              "id": 1, "exchange": "SHFE", "code": "rb", "name": "螺纹钢", "category": "黑色",
              "deliveryType": "实物", "tradingUnit": 10, "unitName": "吨", "tickSize": 1,
              "tickValue": 10, "priceLimitPct": 0.08, "marginRate": 0.10, "months": "1-12",
              "tradingHours": "09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-23:00"
            },
            {
              "id": 2, "exchange": "SHFE", "code": "xx", "name": "无时段品种", "category": "测试",
              "deliveryType": "实物", "tradingUnit": 10, "unitName": "吨", "tickSize": 1,
              "tickValue": 10, "priceLimitPct": 0.08, "marginRate": 0.10, "months": "1-12"
            }
          ]
        }
        """);

    [Fact]
    public void 正常盘中Tick_放行()
    {
        var gate = new TickAggregationGate(MakeRegistry());
        Assert.True(gate.ShouldAggregate("rb2610", T(9, 35, 0), T(9, 35, 1)));
        Assert.Equal(0, gate.RejectedBySession);
        Assert.Equal(0, gate.RejectedAsStale);
    }

    [Fact]
    public void 盘外时间_时段层拒绝且计数递增()
    {
        var gate = new TickAggregationGate(MakeRegistry());
        Assert.False(gate.ShouldAggregate("rb2610", T(19, 8), T(19, 8)));   // 19时垃圾快照场景
        Assert.Equal(1, gate.RejectedBySession);
        Assert.Equal(0, gate.RejectedAsStale);
    }

    [Fact]
    public void 午休重连旧快照_陈旧层拒绝且计数递增()
    {
        var gate = new TickAggregationGate(MakeRegistry());
        // 11:30 是合法收盘边界（时段层放行），但本地 12:15 收到 → 陈旧层拒
        Assert.False(gate.ShouldAggregate("rb2610", T(11, 30), T(12, 15)));
        Assert.Equal(0, gate.RejectedBySession);
        Assert.Equal(1, gate.RejectedAsStale);
    }

    [Fact]
    public void 真实收盘Tick_两层都放行()
    {
        var gate = new TickAggregationGate(MakeRegistry());
        Assert.True(gate.ShouldAggregate("rb2610", T(11, 30, 0), T(11, 30, 1)));
    }

    [Fact]
    public void 集合竞价成交_宽限放行()
    {
        var gate = new TickAggregationGate(MakeRegistry());
        Assert.True(gate.ShouldAggregate("rb2610", T(20, 59, 0), T(20, 59, 1)));
    }

    [Fact]
    public void 未知品种_时段层失败开放_仅陈旧层生效()
    {
        var gate = new TickAggregationGate(MakeRegistry());
        // registry 里没有 au → 时段层放行（即使 19 时盘外）
        Assert.True(gate.ShouldAggregate("au2612", T(19, 8), T(19, 8)));
        // 但陈旧层仍生效
        Assert.False(gate.ShouldAggregate("au2612", T(11, 30), T(12, 15)));
        Assert.Equal(1, gate.RejectedAsStale);
    }

    [Fact]
    public void 无tradingHours字段_时段层失败开放()
    {
        var gate = new TickAggregationGate(MakeRegistry());
        Assert.True(gate.ShouldAggregate("xx2610", T(19, 8), T(19, 8)));
        Assert.Equal(0, gate.RejectedBySession);
    }
}
