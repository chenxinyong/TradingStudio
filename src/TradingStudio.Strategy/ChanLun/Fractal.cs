namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 分型 — spec §2.1。
/// Top=顶分型 (取K线High), Bottom=底分型 (取K线Low)。
/// </summary>
public record Fractal(
    FractalType Type,
    int Index,          // 在标准K线数组中的位置
    double Price,       // Top=K线.High, Bottom=K线.Low
    DateTime Dt
);
