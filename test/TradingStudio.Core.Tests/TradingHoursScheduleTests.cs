using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

public class TradingHoursScheduleTests
{
    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    // ── 解析：symbols.json 全部 6 种真实变体 ──

    [Theory]
    [InlineData("09:00-10:15,10:30-11:30,13:30-15:00")]
    [InlineData("09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-23:00")]
    [InlineData("09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-01:00")]
    [InlineData("09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-02:30")]
    [InlineData("09:30-11:30,13:00-15:00")]
    [InlineData("09:30-11:30,13:00-15:15")]
    public void 解析_全部真实变体_成功(string hours)
    {
        Assert.NotNull(TradingHoursSchedule.TryParse(hours));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("25:00-26:00")]
    public void 解析_空或乱格式_返回Null(string? hours)
    {
        Assert.Null(TradingHoursSchedule.TryParse(hours));
    }

    // ── 跨午夜 ──

    [Fact]
    public void 跨午夜0100_零点半在内_零点后在外()
    {
        var s = TradingHoursSchedule.TryParse("09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-01:00")!;
        Assert.True(s.Contains(T(0, 30)));      // 夜盘后半段
        Assert.True(s.Contains(T(23, 59)));     // 夜盘前半段
        Assert.False(s.Contains(T(1, 2)));      // 01:00+60s宽限 之后
        Assert.False(s.Contains(T(3, 0)));
    }

    [Fact]
    public void 跨午夜0230_0229在内_0232在外()
    {
        var s = TradingHoursSchedule.TryParse("09:00-15:00; 夜盘:21:00-02:30")!;
        Assert.True(s.Contains(T(2, 29)));
        Assert.False(s.Contains(T(2, 32)));     // 02:30+60s宽限 之后
    }

    // ── 竞价宽限与收盘边界 ──

    [Fact]
    public void 竞价宽限_0859与2059在内_0858在外()
    {
        var s = TradingHoursSchedule.TryParse("09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-23:00")!;
        Assert.True(s.Contains(T(8, 59)));       // 日盘集合竞价成交
        Assert.True(s.Contains(T(20, 59)));      // 夜盘集合竞价成交
        Assert.False(s.Contains(T(8, 58, 59)));  // 宽限之前
    }

    [Fact]
    public void 收盘边界_113000与150000在内_150200在外()
    {
        var s = TradingHoursSchedule.TryParse("09:00-10:15,10:30-11:30,13:30-15:00")!;
        Assert.True(s.Contains(T(11, 30, 0)));   // 收盘 tick
        Assert.True(s.Contains(T(15, 0, 59)));   // 收盘宽限内
        Assert.False(s.Contains(T(15, 2)));      // 宽限之后
    }

    [Fact]
    public void 午休与盘后_不在内()
    {
        var s = TradingHoursSchedule.TryParse("09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-23:00")!;
        Assert.False(s.Contains(T(12, 0)));      // 午休（2026-07-15 792根垃圾bar场景之一）
        Assert.False(s.Contains(T(10, 20)));     // 小节休息
        Assert.False(s.Contains(T(19, 0)));      // 盘后（799根19时垃圾bar场景）
        Assert.False(s.Contains(T(16, 30)));
    }

    // ── 中金所 ──

    [Fact]
    public void 中金所股指_0900在外_0930在内()
    {
        var s = TradingHoursSchedule.TryParse("09:30-11:30,13:00-15:00")!;
        Assert.False(s.Contains(T(9, 0)));
        Assert.True(s.Contains(T(9, 30)));
        Assert.True(s.Contains(T(13, 0)));
    }

    [Fact]
    public void 中金所国债_1515在内()
    {
        var s = TradingHoursSchedule.TryParse("09:30-11:30,13:00-15:15")!;
        Assert.True(s.Contains(T(15, 15)));
        Assert.False(s.Contains(T(15, 17)));
    }
}
