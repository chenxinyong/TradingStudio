namespace TradingStudio.Engine.Tests;

/// <summary>
/// CTP TraderApi 冒烟测试 — 如果 new CTP.TraderApi() 崩,说明 C++/CLI 封装有问题。
/// </summary>
public class CtpTraderSmokeTest
{
    [Fact]
    public void TraderApi_ConstructAndDispose_NoCrash()
    {
        var api = new CTP.TraderApi();
        Assert.NotNull(api);
        // 不调 Connect(),纯验证 C++/CLI 构造路径
        api.Dispose();
    }
}
