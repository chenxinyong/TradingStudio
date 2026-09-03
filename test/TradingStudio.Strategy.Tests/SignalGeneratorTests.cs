using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Strategy.Tests;

/// <summary>
/// 缠论买卖点层测试 — MACD面积背驰 / B1/B2/B3 / S1/S2/S3。
/// 几何层（包含/分型/笔/中枢）由 ChanLunTests 覆盖；这里聚焦 SignalGenerator 的信号逻辑。
/// 用手工构造的笔(Bi)/中枢(Zhongshu)直接钉死：方向判定、止损价计算、置信度、边界条件。
/// </summary>
public class SignalGeneratorTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0);

    private static ChanLunBar CBar(int i, double close) => new()
    {
        Dt = T0.AddMinutes(i), Open = close, High = close, Low = close, Close = close, Volume = 100
    };

    /// <summary>构造一笔：精确控制方向、笔内极值、起止分型价格（确保值唯一，避免 record Equals 误匹配）。</summary>
    private static Bi MkBi(Direction dir, double low, double high, double startFxPrice, double endFxPrice)
    {
        var startFx = dir == Direction.Up
            ? new Fractal(FractalType.Bottom, 0, startFxPrice, T0)
            : new Fractal(FractalType.Top, 0, startFxPrice, T0);
        var endFx = dir == Direction.Up
            ? new Fractal(FractalType.Top, 5, endFxPrice, T0)
            : new Fractal(FractalType.Bottom, 5, endFxPrice, T0);
        return new Bi(dir, startFx, endFx, 0, 5, 5, low, high, T0, T0.AddMinutes(5));
    }

    private static Zhongshu MkZs(double zg, double zd) => new(zg, zd, (zg + zd) / 2, 0, 2, 3);

    private static MacdDivergence MkDiv(string type, int zsIndex, Bi cBi, double aArea = 100, double cArea = 30) =>
        new(type, zsIndex, aArea, cArea, aArea > 0 ? cArea / aArea : 1.0, cBi, cBi, T0);

    // ═══════════════ MACD 面积 ═══════════════

    [Fact]
    public void MacdArea_RisingBars_RedAreaDominant()
    {
        var bars = new List<ChanLunBar>();
        for (int i = 0; i < 40; i++) bars.Add(CBar(i, 100 + i));
        var (red, green) = SignalGenerator.MacdArea(bars, 0, 39);
        Assert.True(red > 0);
        Assert.True(red > green);
    }

    [Fact]
    public void MacdArea_FallingBars_GreenAreaDominant()
    {
        var bars = new List<ChanLunBar>();
        for (int i = 0; i < 40; i++) bars.Add(CBar(i, 100 - i));
        var (red, green) = SignalGenerator.MacdArea(bars, 0, 39);
        Assert.True(green > 0);
        Assert.True(green > red);
    }

    // ═══════════════ 背驰（非趋势短路） ═══════════════

    [Fact]
    public void CheckDivergence_NoTrend_ReturnsEmpty()
    {
        // 三笔重叠 → 单中枢 → CONSOLIDATION → 无背驰
        var bis = new List<Bi>
        {
            MkBi(Direction.Up, 8, 14, 8, 14),
            MkBi(Direction.Down, 8, 14, 14, 8),
            MkBi(Direction.Up, 8, 14, 8, 14),
        };
        var zs = ZhongshuBuilder.Build(bis);
        var std = new List<ChanLunBar>();
        for (int i = 0; i < 20; i++) std.Add(CBar(i, 10));
        Assert.Empty(SignalGenerator.CheckDivergence(bis, zs, std));
    }

    // ═══════════════ 一买 / 一卖 ═══════════════

    [Fact]
    public void SignalB1_NoConfirmBi_ReturnsNull()
    {
        var cBi = MkBi(Direction.Down, 8, 12, 12, 8);
        var bis = new List<Bi> { cBi };   // 无后续确认笔
        var zsList = new List<Zhongshu> { MkZs(14, 8), MkZs(26, 20) };
        Assert.Null(SignalGenerator.SignalB1(MkDiv("BOTTOM_DIVERGENCE", 1, cBi), bis, zsList));
    }

    [Fact]
    public void SignalB1_UpConfirm_ReturnsBuyWithStop()
    {
        var cBi = MkBi(Direction.Down, 8, 12, 12, 8);     // C段下跌, 笔内最低 8
        var confirm = MkBi(Direction.Up, 9, 15, 9, 15);   // 确认向上, 底分型价 9
        var bis = new List<Bi> { cBi, confirm };
        var zsList = new List<Zhongshu> { MkZs(14, 8), MkZs(26, 20) };

        var sig = SignalGenerator.SignalB1(MkDiv("BOTTOM_DIVERGENCE", 1, cBi), bis, zsList);
        Assert.NotNull(sig);
        Assert.Equal(ChanSignalType.B1, sig!.Type);
        Assert.Equal(9, sig.Price, 9);
        Assert.Equal(8 * 0.995, sig.StopLoss, 9);
        Assert.Equal(2, sig.Confidence);   // zsList.Count >= 2
    }

    [Fact]
    public void SignalS1_DownConfirm_ReturnsSellWithStop()
    {
        var cBi = MkBi(Direction.Up, 10, 20, 10, 20);     // C段上涨, 笔内最高 20
        var confirm = MkBi(Direction.Down, 8, 19, 19, 8); // 确认向下, 顶分型价 19
        var bis = new List<Bi> { cBi, confirm };
        var zsList = new List<Zhongshu> { MkZs(14, 8), MkZs(26, 20) };

        var sig = SignalGenerator.SignalS1(MkDiv("TOP_DIVERGENCE", 1, cBi), bis, zsList);
        Assert.NotNull(sig);
        Assert.Equal(ChanSignalType.S1, sig!.Type);
        Assert.Equal(19, sig.Price, 9);
        Assert.Equal(20 * 1.005, sig.StopLoss, 9);
    }

    // ═══════════════ 二买 ═══════════════

    [Fact]
    public void SignalB2_PullbackNewLow_ReturnsNull()
    {
        var cBi = MkBi(Direction.Down, 8, 12, 12, 8);
        var upBi = MkBi(Direction.Up, 9, 15, 9, 15);
        var pullback = MkBi(Direction.Down, 7, 14, 14, 7);  // Low=7 <= 8 → 创新低
        var confirm = MkBi(Direction.Up, 8, 16, 8, 16);
        var bis = new List<Bi> { cBi, upBi, pullback, confirm };
        var zsList = new List<Zhongshu> { MkZs(14, 8), MkZs(26, 20) };
        Assert.Null(SignalGenerator.SignalB2(bis, zsList, MkDiv("BOTTOM_DIVERGENCE", 1, cBi)));
    }

    [Fact]
    public void SignalB2_Valid_ReturnsSignal()
    {
        var cBi = MkBi(Direction.Down, 8, 12, 12, 8);
        var upBi = MkBi(Direction.Up, 9, 15, 9, 15);
        var pullback = MkBi(Direction.Down, 10, 14, 14, 10); // Low=10 > 8 → 不创新低
        var confirm = MkBi(Direction.Up, 11, 17, 11, 17);
        var bis = new List<Bi> { cBi, upBi, pullback, confirm };
        var zsList = new List<Zhongshu> { MkZs(14, 8), MkZs(26, 20) };

        var sig = SignalGenerator.SignalB2(bis, zsList, MkDiv("BOTTOM_DIVERGENCE", 1, cBi));
        Assert.NotNull(sig);
        Assert.Equal(ChanSignalType.B2, sig!.Type);
        Assert.Equal(11, sig.Price, 9);
        Assert.Equal(8 * 0.995, sig.StopLoss, 9);
    }

    // ═══════════════ 三买 ═══════════════

    [Fact]
    public void SignalB3_Valid_ReturnsSignal()
    {
        // 中枢 Zg=14, EndBiIdx=2；离开笔 Low=15 > Zg，回抽不入中枢
        var bis = new List<Bi>
        {
            MkBi(Direction.Up, 8, 14, 8, 14),     // 中枢内 0
            MkBi(Direction.Down, 8, 14, 14, 8),   // 中枢内 1
            MkBi(Direction.Up, 8, 14, 8, 14),     // 中枢内 2
            MkBi(Direction.Up, 15, 22, 15, 22),   // 3 离开中枢, Low=15 > Zg=14
            MkBi(Direction.Down, 15, 21, 21, 15), // 4 回抽, Low=15 > Zg
            MkBi(Direction.Up, 16, 24, 16, 24),   // 5 确认
        };
        var zsList = new List<Zhongshu> { MkZs(14, 8) };

        var sigs = SignalGenerator.SignalB3(bis, zsList);
        Assert.Single(sigs);
        Assert.Equal(ChanSignalType.B3, sigs[0].Type);
        Assert.Equal(16, sigs[0].Price, 9);
        Assert.Equal(14 * 0.998, sigs[0].StopLoss, 9);
        Assert.Equal(3, sigs[0].Confidence);   // dist=(15-14)/14*10000 ≈ 714 > 50
    }

    // ═══════════════ 三卖（镜像） ═══════════════

    [Fact]
    public void SignalS3_Valid_ReturnsSignal()
    {
        // 中枢 Zd=14, EndBiIdx=2；离开笔 High=13 < Zd，反弹不入中枢
        var bis = new List<Bi>
        {
            MkBi(Direction.Up, 8, 20, 8, 20),
            MkBi(Direction.Down, 8, 20, 20, 8),
            MkBi(Direction.Up, 8, 20, 8, 20),
            MkBi(Direction.Down, 4, 13, 13, 4),  // 3 向下离开, High=13 < Zd=14
            MkBi(Direction.Up, 5, 13, 5, 13),    // 4 反弹, High=13 < Zd
            MkBi(Direction.Down, 3, 12, 12, 3),  // 5 确认
        };
        var zsList = new List<Zhongshu> { MkZs(20, 14) };

        var sigs = SignalGenerator.SignalS3(bis, zsList);
        Assert.Single(sigs);
        Assert.Equal(ChanSignalType.S3, sigs[0].Type);
        Assert.Equal(14 * 1.002, sigs[0].StopLoss, 9);
        Assert.Equal(3, sigs[0].Confidence);
    }
}
