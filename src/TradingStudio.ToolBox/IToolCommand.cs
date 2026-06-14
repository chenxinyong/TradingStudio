using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradingStudio.ToolBox;

/// <summary>
/// 工具命令接口 — ToolBox 中所有子命令的契约。
/// 新增工具 = 实现此接口 + 在 ToolRegistry 中注册一行。
/// </summary>
public interface IToolCommand
{
    /// <summary>命令名称（CLI 第一个参数匹配）</summary>
    string Name { get; }

    /// <summary>短别名，如 "i" for "import"</summary>
    string? Alias { get; }

    /// <summary>一行描述，帮助文本</summary>
    string Description { get; }

    /// <summary>注册该工具所需的 DI 服务</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>执行工具逻辑。返回 0 = 成功</summary>
    Task<int> ExecuteAsync(IServiceProvider services, string[] args, CancellationToken ct);
}
