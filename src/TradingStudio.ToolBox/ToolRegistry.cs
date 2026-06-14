namespace TradingStudio.ToolBox;

/// <summary>
/// 工具注册表 — 维护所有可用命令的注册信息。
/// 新增工具时在此构造函数中加一行 Register()。
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, IToolCommand> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry()
    {
        Register(new ImportTool.ImportTool());
        Register(new JinshuyuanTool.JinshuyuanTool());
        // 未来扩展：只需加下面这类行
        // Register(new ExportTool.ExportTool());
        // Register(new VerifyTool.VerifyTool());
        // Register(new InfoTool.InfoTool());
        // Register(new ConvertTool.ConvertTool());
    }

    public void Register(IToolCommand tool)
    {
        _tools[tool.Name] = tool;
        if (tool.Alias != null)
            _tools[tool.Alias] = tool;
    }

    public IToolCommand? Resolve(string name)
        => _tools.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyList<IToolCommand> ListAll()
        => _tools.Values.Distinct().OrderBy(t => t.Name).ToList();
}
