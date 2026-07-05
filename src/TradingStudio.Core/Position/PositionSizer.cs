using TradingStudio.Core.Models;

namespace TradingStudio.Core.Sizing;

/// <summary>
/// 共享仓位计算 — 所有策略统一使用，消除6个重复 CalcLots。
/// ATR止损模式: lots = riskAmount / (stopDist * multiplier)
/// 百分比止损模式: lots = riskAmount / (stopPct * price * multiplier)
/// </summary>
public static class PositionSizer
{
    /// <summary>ATR止损模式 — 止损距离 = stopAtrMult × ATR</summary>
    public static int FromAtrStop(double price, double atr, double stopAtrMult,
        Future? future, double equity, double riskPerTrade,
        double maxMarginRatio = 0.5, int maxPosition = 20)
    {
        if (atr <= 0 || price <= 0) return 0;

        var mult = (double)(future?.TradingUnit ?? 10m);
        var marginRate = (double)(future?.MarginRate ?? 0.08m);
        var stopDist = stopAtrMult * atr;
        var riskAmt = equity * riskPerTrade;
        var riskPerLot = stopDist * mult;

        if (riskPerLot < price * mult * 0.001) return 0; // 止损距离太小不计

        int lots = Math.Max(1, (int)(riskAmt / riskPerLot));
        var marginPerLot = price * mult * marginRate;
        var maxByMargin = (int)(equity * maxMarginRatio / marginPerLot);
        if (lots > maxByMargin) lots = maxByMargin;
        if (lots > maxPosition) lots = maxPosition;
        if (lots < 1) lots = 1;
        return lots;
    }

    /// <summary>百分比止损模式 — 止损距离 = stopPct × price</summary>
    public static int FromPctStop(double price, double stopPct,
        Future? future, double equity, double riskPerTrade,
        double maxMarginRatio = 0.5, int maxPosition = 20)
    {
        if (price <= 0 || stopPct <= 0) return 0;

        var mult = (double)(future?.TradingUnit ?? 10m);
        var marginRate = (double)(future?.MarginRate ?? 0.08m);
        var stopDist = stopPct * price;
        var riskAmt = equity * riskPerTrade;
        var riskPerLot = stopDist * mult;

        int lots = Math.Max(1, (int)(riskAmt / riskPerLot));
        var marginPerLot = price * mult * marginRate;
        var maxByMargin = (int)(equity * maxMarginRatio / marginPerLot);
        if (lots > maxByMargin) lots = maxByMargin;
        if (lots > maxPosition) lots = maxPosition;
        if (lots < 1) lots = 1;
        return lots;
    }
}
