using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

public class StaleTickDetectorTests
{
    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    [Fact]
    public void 圆环距离_同刻为零()
    {
        Assert.Equal(0, StaleTickDetector.CircularDistanceSeconds(T(11, 30), T(11, 30)));
    }

    [Fact]
    public void 圆环距离_2359与0001_为120秒()
    {
        Assert.Equal(120, StaleTickDetector.CircularDistanceSeconds(T(23, 59), T(0, 1)));
        Assert.Equal(120, StaleTickDetector.CircularDistanceSeconds(T(0, 1), T(23, 59)));
    }

    [Fact]
    public void 正常推送延迟3秒_不陈旧()
    {
        Assert.False(StaleTickDetector.IsStale(T(11, 29, 59), T(11, 30, 2)));
    }

    [Fact]
    public void 午休重连快照_1130对1215_陈旧()
    {
        // 2026-07-15 午休重连场景：快照带 11:30:00 旧时间戳，本地 12:15 收到
        Assert.True(StaleTickDetector.IsStale(T(11, 30), T(12, 15)));
    }

    [Fact]
    public void 阈值边界_179秒否_181秒是()
    {
        Assert.False(StaleTickDetector.IsStale(T(9, 0, 0), T(9, 2, 59)));   // 179s
        Assert.True(StaleTickDetector.IsStale(T(9, 0, 0), T(9, 3, 1)));     // 181s
    }
}
