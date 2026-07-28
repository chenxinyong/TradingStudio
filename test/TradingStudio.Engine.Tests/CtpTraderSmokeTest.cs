namespace TradingStudio.Engine.Tests;

/// <summary>
/// CTP TraderApi 冒烟测试 — 如果 new CTP.FtdcTdAdapter() 崩,说明 P/Invoke 封装有问题。
/// </summary>
public class CtpTraderSmokeTest
{
    [Fact]
    public void TraderApi_ConstructAndDispose_NoCrash()
    {
        var api = new CTP.FtdcTdAdapter("");
        Assert.NotNull(api);
        // 不调 Connect(),纯验证 P/Invoke 构造路径
        api.Release();
    }
}
