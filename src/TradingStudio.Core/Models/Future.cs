namespace TradingStudio.Core.Models;

/// <summary>
/// 期货品种 — 品种是交易规则的载体，同一品种下所有合约共享规则。
/// 75 个品种覆盖六大交易所。
/// </summary>
public sealed record Future
{
    // === 标识 ===
    public int Id { get; init; }
    public ExchangeCode Exchange { get; init; }
    public string Code { get; init; } = "";       // "cu", "IF"
    public string Name { get; init; } = "";        // "铜", "沪深300"

    // === 分类 ===
    public string Category { get; init; } = "";    // 有色金属/黑色金属/农产品/化工/能源/贵金属/股指/利率/新能源
    public string DeliveryType { get; init; } = "PHYSICAL";  // PHYSICAL | CASH

    // === 交易规则 ===
    public decimal TradingUnit { get; init; }       // 5 吨, 300 元/点...
    public string UnitName { get; init; } = "";      // "吨", "克", "元/点"...
    public decimal TickSize { get; init; }           // 最小变动价位
    public decimal TickValue { get; init; }          // 1跳价值 = TickSize × TradingUnit
    public decimal PriceLimitPct { get; init; }      // 涨跌停板 % (0.10 = ±10%)
    public decimal MarginRate { get; init; }         // 交易所基准保证金率
    public double FeeRate { get; init; }              // 开仓/平昨手续费率 (合约价值百分比, 默认 0.0001 = 万1)
    public double CloseTodayFeeRate { get; init; }    // 平今手续费率 (0=平今免, =FeeRate=正常, >FeeRate=平今加倍)
    public string Months { get; init; } = "";        // "1～12月" | "1,3,5,7,9,11" | "季月(3,6,9,12)"
    public string TradingHours { get; init; } = "";  // 交易时间描述

    // === Phase 3 品种研究 ===
    public bool IsTop30 { get; init; }
    public string ContractCycle { get; init; } = "";   // "三主力轮换" | "多月活跃" | "单合约主导" | "少合约(新品种)"

    // === 计算 ===
    public decimal ContractValue(decimal price) => price * TradingUnit;
    public decimal RoundToTick(decimal price) => Math.Round(price / TickSize) * TickSize;

    public override string ToString() => $"{Exchange.ShortName()}/{Code} {Name}";
}
