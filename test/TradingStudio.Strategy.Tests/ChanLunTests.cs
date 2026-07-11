using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Strategy.Tests;

/// <summary>
/// 缠论核心算法测试 — 包含处理 / 分型 / 笔 / 中枢。
/// 这是全系统最复杂、直接驱动 ChanLunStrategy 真实下单的模块，此前零自动化覆盖。
/// 用手工可验证的 K 线序列钉死每个阶段。
///
/// 核心夹具 Zigzag()：21 根严格锯齿标准 K 线（无包含），
/// 顶@4 底@8 顶@12 底@16，每段间隔 5 根（满足 MinBiLen=5）→
/// 产出 4 分型 → 3 笔 → 1 中枢（Zg=14, Zd=8）。
/// </summary>
public class ChanLunTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0);
    private static ChanLunBar B(int i, double high, double low) => new()
    {
        Dt = T0.AddMinutes(i), Open = (high + low) / 2, High = high, Low = low,
        Close = (high + low) / 2, Volume = 100
    };

    // 21 根锯齿：上5→顶@4→下→底@8→上→顶@12→下→底@16→上（末根@20非分型）
    private static List<ChanLunBar> Zigzag()
    {
        var hl = new (double h, double l)[]
        {
            (10,8),(11,9),(12,10),(13,11),(14,12),   // 0-4 ↑ 顶@4
            (13,11),(12,10),(11,9),(10,8),            // 5-8 ↓ 底@8
            (11,9),(12,10),(13,11),(14,12),           // 9-12 ↑ 顶@12
            (13,11),(12,10),(11,9),(10,8),            // 13-16 ↓ 底@16
            (11,9),(12,10),(13,11),(14,12),           // 17-20 ↑
        };
        var bars = new List<ChanLunBar>();
        for (int i = 0; i < hl.Length; i++) bars.Add(B(i, hl[i].h, hl[i].l));
        return bars;
    }

    // ═══════════════ 包含处理 ═══════════════

    [Fact]
    public void Inclusion_HasInclusion_DetectsContainment()
    {
        Assert.True(InclusionProcessor.HasInclusion(B(0, 12, 8), B(1, 11, 9)));  // a 含 b
        Assert.False(InclusionProcessor.HasInclusion(B(0, 10, 8), B(1, 12, 10))); // 上移无包含
    }

    [Fact]
    public void Inclusion_MergesContainedBar_UpDirection()
    {
        // bar0(10,8) → bar1(12,9) 上移 → bar2(11,9.5) 被 bar1 包含 → 向上合并
        var raw = new List<ChanLunBar> { B(0, 10, 8), B(1, 12, 9), B(2, 11, 9.5) };
        var std = InclusionProcessor.Process(raw);

        Assert.Equal(2, std.Count);
        Assert.Equal(12, std[1].High, 9);   // 向上合并取高高
        Assert.Equal(9.5, std[1].Low, 9);   // 向上合并取高低
        Assert.True(InclusionProcessor.Validate(raw, std));
    }

    [Fact]
    public void Inclusion_NonInclusionInput_Unchanged()
    {
        var raw = Zigzag();
        var std = InclusionProcessor.Process(raw);
        Assert.Equal(raw.Count, std.Count);                 // 锯齿无包含 → 不合并
        Assert.True(InclusionProcessor.Validate(raw, std));
    }

    // ═══════════════ 分型 ═══════════════

    [Fact]
    public void Fractal_FindsTop_WhenMiddleIsHighest()
    {
        var top = FractalDetector.Find(new() { B(0, 10, 8), B(1, 12, 9), B(2, 11, 8.5) });
        Assert.Single(top);
        Assert.Equal(FractalType.Top, top[0].Type);
        Assert.Equal(1, top[0].Index);
        Assert.Equal(12, top[0].Price, 9);
    }

    [Fact]
    public void Fractal_FindsBottom_WhenMiddleIsLowest()
    {
        var bot = FractalDetector.Find(new() { B(0, 12, 9), B(1, 10, 7), B(2, 11, 8) });
        Assert.Single(bot);
        Assert.Equal(FractalType.Bottom, bot[0].Type);
        Assert.Equal(7, bot[0].Price, 9);
    }

    [Fact]
    public void Fractal_MonotonicBars_NoFractal()
    {
        Assert.Empty(FractalDetector.Find(new() { B(0, 10, 8), B(1, 11, 9), B(2, 12, 10) }));
    }

    [Fact]
    public void Fractal_Deduplicate_KeepsMoreExtreme()
    {
        var tops = FractalDetector.Deduplicate(new()
        {
            new Fractal(FractalType.Top, 1, 10, T0), new Fractal(FractalType.Top, 3, 12, T0)
        });
        Assert.Single(tops);
        Assert.Equal(12, tops[0].Price, 9);   // 连续两顶保留更高

        var bots = FractalDetector.Deduplicate(new()
        {
            new Fractal(FractalType.Bottom, 1, 10, T0), new Fractal(FractalType.Bottom, 3, 8, T0)
        });
        Assert.Single(bots);
        Assert.Equal(8, bots[0].Price, 9);    // 连续两底保留更低
    }

    [Fact]
    public void Fractal_Detect_OnZigzag_AlternatingFourFractals()
    {
        var std = InclusionProcessor.Process(Zigzag());
        var fx = FractalDetector.Detect(std);

        Assert.Equal(4, fx.Count);
        Assert.Equal((FractalType.Top, 4), (fx[0].Type, fx[0].Index));
        Assert.Equal((FractalType.Bottom, 8), (fx[1].Type, fx[1].Index));
        Assert.Equal((FractalType.Top, 12), (fx[2].Type, fx[2].Index));
        Assert.Equal((FractalType.Bottom, 16), (fx[3].Type, fx[3].Index));
        Assert.True(FractalDetector.Validate(fx, std));
    }

    // ═══════════════ 笔 ═══════════════

    [Fact]
    public void Bi_Build_OnZigzag_ThreeAlternatingBis()
    {
        var std = InclusionProcessor.Process(Zigzag());
        var fx = FractalDetector.Detect(std);
        var bis = BiBuilder.Build(fx, std);

        Assert.Equal(3, bis.Count);
        Assert.Equal(Direction.Down, bis[0].Type);   // 顶@4 → 底@8
        Assert.Equal(Direction.Up, bis[1].Type);     // 底@8 → 顶@12
        Assert.Equal(Direction.Down, bis[2].Type);   // 顶@12 → 底@16
        Assert.All(bis, bi => Assert.Equal(5, bi.BarCount));  // 每笔恰好 5 根
        Assert.True(BiBuilder.Validate(bis));
    }

    [Fact]
    public void Bi_MinLength_Enforced()
    {
        // 分型间隔 3 根 (< MinBiLen=5) → 不成笔
        var std = Zigzag();
        var tooClose = new List<Fractal>
        {
            new(FractalType.Top, 1, 11, T0), new(FractalType.Bottom, 3, 11, T0)
        };
        Assert.Empty(BiBuilder.Build(tooClose, std));
    }

    // ═══════════════ 中枢 ═══════════════

    [Fact]
    public void Zhongshu_Build_OnZigzag_OneCentralZone()
    {
        var std = InclusionProcessor.Process(Zigzag());
        var bis = BiBuilder.Build(FractalDetector.Detect(std), std);
        var zs = ZhongshuBuilder.Build(bis);

        Assert.Single(zs);
        Assert.Equal(14, zs[0].Zg, 9);   // 上沿 = min(三笔高) = 14
        Assert.Equal(8, zs[0].Zd, 9);    // 下沿 = max(三笔低) = 8
        Assert.Equal(11, zs[0].Zz, 9);   // 中轨
        Assert.Equal(3, zs[0].BiCount);
    }

    [Fact]
    public void Zhongshu_NoOverlap_NoZone()
    {
        // 三笔区间阶梯上移、互不重叠 → 无中枢
        var bis = new List<Bi>
        {
            MkBi(Direction.Up, low: 8, high: 10),
            MkBi(Direction.Down, low: 10.5, high: 12),
            MkBi(Direction.Up, low: 12.5, high: 14),
        };
        Assert.Empty(ZhongshuBuilder.Build(bis));
    }

    [Fact]
    public void Zhongshu_FewerThanThreeBis_NoZone()
    {
        var bis = new List<Bi> { MkBi(Direction.Up, 8, 14), MkBi(Direction.Down, 8, 14) };
        Assert.Empty(ZhongshuBuilder.Build(bis));
    }

    [Fact]
    public void Zhongshu_ClassifyTrend()
    {
        Assert.Equal("UNCLASSIFIED", ZhongshuBuilder.ClassifyTrend(new()));
        Assert.Equal("CONSOLIDATION", ZhongshuBuilder.ClassifyTrend(new() { MkZs(14, 8) }));
        // zs2 下沿(20) > zs1 上沿(14) → 中枢上移 → UP
        Assert.Equal("UP", ZhongshuBuilder.ClassifyTrend(new() { MkZs(14, 8), MkZs(26, 20) }));
        // zs2 上沿(8) < zs1 下沿(14) → 中枢下移 → DOWN
        Assert.Equal("DOWN", ZhongshuBuilder.ClassifyTrend(new() { MkZs(20, 14), MkZs(8, 2) }));
    }

    // ─── 构造辅助 ───
    private static Bi MkBi(Direction dir, double low, double high)
    {
        var fx = new Fractal(FractalType.Top, 0, high, T0);
        return new Bi(dir, fx, fx, 0, 0, 5, low, high, T0, T0);
    }

    private static Zhongshu MkZs(double zg, double zd) =>
        new(zg, zd, (zg + zd) / 2, 0, 2, 3);
}
