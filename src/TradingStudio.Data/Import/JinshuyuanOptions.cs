namespace TradingStudio.Data.Import;

/// <summary>
/// import-jinshuyuan 命令的配置参数。
/// 由 Program.cs 从 CLI 参数构建，传入 JinshuyuanImportService。
/// </summary>
public sealed class JinshuyuanOptions
{
    /// <summary>金数源数据根目录</summary>
    public string DataDir { get; init; } = @"C:\Works\Datas\Jinshuyuan";

    /// <summary>输出 SQLite 路径</summary>
    public string DbPath { get; init; } = "bars_history.db";

    /// <summary>导入层: "main" | "active" | "all"</summary>
    public string Layer { get; init; } = "all";

    /// <summary>品种过滤 (product code 小写)</summary>
    public HashSet<string> Symbols { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>交易所过滤 (null=全部, "SHFE"/"DCE"/...)</summary>
    public string? ExchangeCode { get; init; }

    /// <summary>起始月份 YYYYMM</summary>
    public string FromMonth { get; init; } = "202001";

    /// <summary>结束月份 YYYYMM</summary>
    public string ToMonth { get; init; } = "202512";

    /// <summary>临时解压目录 (null=系统临时目录)</summary>
    public string? TempDir { get; init; }

    /// <summary>只列不导</summary>
    public bool DryRun { get; init; }

    /// <summary>RAR 密码</summary>
    public string Password => "www.jinshuyuan.net";

    /// <summary>已知品种代码 (从 symbols.json 加载，--layer active 用)</summary>
    public HashSet<string> KnownProducts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
