using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

/// <summary>
/// 保证金动态调整测试 — 交割月加成 + 长假上调。
/// 修复前所有品种保证金率静态，节假日和临近交割月的上调完全未建模。
/// </summary>
public class DynamicMarginTests
{
    private static Future Rb(decimal baseMargin = 0.08m, double deliveryAdjust = 0,
        int deliveryMonths = 0, double holidayAdjust = 0) => new()
    {
        Code = "rb", TradingUnit = 10, MarginRate = baseMargin,
        DeliveryMarginAdjust = deliveryAdjust, DeliveryMarginMonths = deliveryMonths,
        HolidayMarginAdjust = holidayAdjust,
    };

    // ═══ 基准：无动态调整时 = 基准保证金 ═══

    [Fact]
    public void NoAdjustments_ReturnsBaseRate()
    {
        var f = Rb();
        Assert.Equal(0.08m, f.GetEffectiveMarginRate(new(2024, 6, 15)));
    }

    // ═══ 交割月加成 ═══

    [Fact]
    public void DeliveryMargin_WithinWindow_Raised()
    {
        // rb2408: Aug 2024 delivery, 2mo window → June/July 2024 上调 +3%
        var f = Rb(deliveryAdjust: 0.03, deliveryMonths: 2);
        var rate = f.GetEffectiveMarginRate(new(2024, 7, 1), "rb2408");
        Assert.Equal(0.11m, rate); // 8% + 3%
    }

    [Fact]
    public void DeliveryMargin_OutsideWindow_NoChange()
    {
        var f = Rb(deliveryAdjust: 0.03, deliveryMonths: 2);
        var rate = f.GetEffectiveMarginRate(new(2024, 1, 1), "rb2408");
        Assert.Equal(0.08m, rate); // 距离交割 > 2 个月 → 不上调
    }

    [Fact]
    public void DeliveryMargin_ContinuousContract_NoAdjust()
    {
        // rb000 无交割月 → 不适用交割月加成
        var f = Rb(deliveryAdjust: 0.03, deliveryMonths: 2);
        Assert.Equal(0.08m, f.GetEffectiveMarginRate(new(2024, 7, 1), "rb000"));
    }

    // ═══ 长假加成 ═══

    [Fact]
    public void HolidayMargin_DuringSpringFestival_Raised()
    {
        var f = Rb(holidayAdjust: 0.05);
        // 2024-02-10 在春节窗口内 (2024-02-05 ~ 02-25)
        Assert.Equal(0.13m, f.GetEffectiveMarginRate(new(2024, 2, 10)));
    }

    [Fact]
    public void HolidayMargin_DuringNationalDay_Raised()
    {
        var f = Rb(holidayAdjust: 0.05);
        // 2024-10-01 在国庆窗口内
        Assert.Equal(0.13m, f.GetEffectiveMarginRate(new(2024, 10, 1)));
    }

    [Fact]
    public void HolidayMargin_OutsideWindow_NoChange()
    {
        var f = Rb(holidayAdjust: 0.04);
        Assert.Equal(0.08m, f.GetEffectiveMarginRate(new(2024, 3, 15))); // 非假期
    }

    // ═══ 叠加：交割月 + 长假 ═══

    [Fact]
    public void DeliveryAndHoliday_BothApply()
    {
        // rb2408, 2024-07-01: 交割月(+3%) + 不重叠假期 → 仅交割月
        var f = Rb(deliveryAdjust: 0.03, deliveryMonths: 2);
        Assert.Equal(0.11m, f.GetEffectiveMarginRate(new(2024, 7, 1), "rb2408"));

        // 如果既在交割月又在假期 → 叠加 (0.08 + 0.03 + 0.05 = 0.16)
        var f2 = Rb(deliveryAdjust: 0.03, deliveryMonths: 2, holidayAdjust: 0.05);
        // 2024-02-10 是春节: rb2402 Feb 2024 delivery → 交割月内 + 假期
        var rate = f2.GetEffectiveMarginRate(new(2024, 2, 10), "rb2402");
        Assert.Equal(0.16m, rate);
    }
}
