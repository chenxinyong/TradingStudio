namespace TradingStudio.Live;

public class CtpMdOptions
{
    public string MdFront { get; init; } = "tcp://182.254.243.31:30011";
    public string BrokerId { get; init; } = "9999";
    public string UserId { get; init; } = "";
    public string Password { get; init; } = "";
    public string? FlowDir { get; init; }
}

public class CtpTraderOptions
{
    public string TraderFront { get; init; } = "";
    public string BrokerId { get; init; } = "9999";
    public string UserId { get; init; } = "";
    public string Password { get; init; } = "";
    public string? AuthCode { get; init; }
    public string? AppId { get; init; }
}
