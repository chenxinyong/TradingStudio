using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

/// <summary>
/// 手续费模型测试 — 支持"元/手"固定费与合约价值百分比两种，及平今费。
/// 修复前只支持百分比；螺纹/股指等按手固定收费的品种成本被错算。
/// </summary>
public class FutureFeeTests
{
    private static Future Rb(double feeRate = 0.0001, double feePerLot = 0,
        double closeTodayRate = 0, double closeTodayPerLot = 0) => new()
    {
        Code = "rb", TradingUnit = 10,
        FeeRate = feeRate, FeePerLot = feePerLot,
        CloseTodayFeeRate = closeTodayRate, CloseTodayFeePerLot = closeTodayPerLot,
    };

    [Fact]
    public void OpenFee_Percentage_OfContractValue()
    {
        // 3500 × 10 × 2 × 0.0001 = 7
        Assert.Equal(7m, Rb(feeRate: 0.0001).OpenFee(3500m, 2));
    }

    [Fact]
    public void OpenFee_FixedPerLot_OverridesPercentage()
    {
        // 固定 3 元/手 × 5 = 15（即便同时设了百分比也优先固定）
        Assert.Equal(15m, Rb(feeRate: 0.0001, feePerLot: 3).OpenFee(3500m, 5));
    }

    [Fact]
    public void OpenFee_MinimumOneYuan()
    {
        // 极小合约：0.01 → 地板 1 元
        var tiny = new Future { Code = "x", TradingUnit = 1, FeeRate = 0.0001 };
        Assert.Equal(1m, tiny.OpenFee(100m, 1));
    }

    [Fact]
    public void OpenFee_ZeroQty_IsZero() => Assert.Equal(0m, Rb().OpenFee(3500m, 0));

    [Fact]
    public void CloseTodayFee_FixedPerLot()
    {
        Assert.Equal(12m, Rb(closeTodayPerLot: 6).CloseTodayFee(3500m, 2));
    }

    [Fact]
    public void CloseTodayFee_Percentage()
    {
        // 3500 × 10 × 1 × 0.0002 = 7
        Assert.Equal(7m, Rb(closeTodayRate: 0.0002).CloseTodayFee(3500m, 1));
    }

    [Fact]
    public void CloseTodayFee_FallsBackToOpenFee_WhenUnset()
    {
        var f = Rb(feeRate: 0.0001);   // 未设平今费
        Assert.Equal(f.OpenFee(3500m, 3), f.CloseTodayFee(3500m, 3));
    }

    [Fact]
    public void HasDistinctCloseTodayFee()
    {
        Assert.True(Rb(closeTodayPerLot: 6).HasDistinctCloseTodayFee);            // 固定平今费
        Assert.True(Rb(feeRate: 0.0001, closeTodayRate: 0.0002).HasDistinctCloseTodayFee); // 费率不同
        Assert.False(Rb(feeRate: 0.0001).HasDistinctCloseTodayFee);              // 未设平今费
        Assert.False(Rb(feeRate: 0.0001, closeTodayRate: 0.0001).HasDistinctCloseTodayFee); // 与开仓费相同
    }
}
