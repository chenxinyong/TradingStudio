using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace TradingStudio.ToolBox;

/// <summary>
/// ToolBox 主机 — 负责解析子命令、构建 DI 容器、执行工具。
/// Program.cs 的 8 行入口直接调用此类。
/// </summary>
public class ToolBoxHost
{
    private readonly ToolRegistry _registry = new();

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var commandName = args[0];
        var tool = _registry.Resolve(commandName);
        if (tool == null)
        {
            Console.Error.WriteLine($"Unknown command: {commandName}");
            Console.Error.WriteLine($"Available: {string.Join(", ", _registry.ListAll().Select(t => t.Name))}");
            Console.Error.WriteLine("Run 'ToolBox --help' for usage.");
            return 1;
        }

        // 构建 DI 容器
        var builder = Host.CreateApplicationBuilder(args[1..]);

        // Serilog（所有工具共享）
        builder.Services.AddSerilog((_, cfg) =>
            cfg.ReadFrom.Configuration(builder.Configuration));

        // 工具自己注册业务服务
        tool.ConfigureServices(builder.Services, builder.Configuration);

        var host = builder.Build();

        try
        {
            return await tool.ExecuteAsync(host.Services, args[1..], CancellationToken.None);
        }
        catch (Exception ex)
        {
            var log = host.Services.GetRequiredService<ILogger<ToolBoxHost>>();
            log.LogCritical(ex, "Tool '{ToolName}' failed", commandName);
            return 1;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private void PrintHelp()
    {
        Console.WriteLine("TradingStudio.ToolBox — 数据工具集");
        Console.WriteLine();
        Console.WriteLine("用法: TradingStudio.ToolBox <command> [options]");
        Console.WriteLine();
        Console.WriteLine("可用命令:");
        foreach (var tool in _registry.ListAll())
            Console.WriteLine($"  {tool.Name,-22} {tool.Alias,-6} {tool.Description}");
        Console.WriteLine();
        Console.WriteLine("全局选项:");
        Console.WriteLine("  -h, --help          显示帮助");
    }
}
