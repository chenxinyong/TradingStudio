using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TradingStudio.Strategy.ChanLun;
using TradingStudio.Terminal.Services;

namespace TradingStudio.Terminal.ViewModels;

/// <summary>
/// 缠论分析 ViewModel — DuckDB 加载 K 线 → ChanLunAnalyzer → Plotly.js HTML 图表。
/// 通过 WebView2 嵌入展示，复用 ChanLunChart.RenderHtml() 生成交互图表。
/// </summary>
public partial class ChanLunViewModel : ViewModelBase
{
    private readonly ILogger<ChanLunViewModel> _logger;
    private readonly EngineApiClient? _api;

    // ═══ 缓存的 ChanLunResult（供导出等操作复用） ═══
    private ChanLunResult? _lastResult;
    private List<DivergenceSignal> _lastDivs = [];
    private string _lastInstrument = "";

    // ═══ 品种 / 周期 ═══
    public ObservableCollection<ProductItem> AvailableProducts { get; } = new();
    public List<string> AvailableFrequencies { get; } = new()
        { "1min", "5min", "15min", "30min", "day", "week" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ProductItem? _selectedProduct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _selectedFrequency = "day";

    public string WindowTitle => SelectedProduct != null
        ? $"缠论 — {SelectedProduct.Code} {SelectedFrequency}"
        : "缠论分析 — 选择品种";

    // ═══ 参数 ═══
    [ObservableProperty]
    private int _biLen = 5;

    [ObservableProperty]
    private int _maxBiNum = 500;

    [ObservableProperty]
    private double _minZsOverlap = 1.0;

    // ═══ 结果摘要 ═══
    [ObservableProperty] private string _statusText = "就绪 — 选择品种后点击 [分析]";
    [ObservableProperty] private string _trendText = "—";
    [ObservableProperty] private int _biCount;
    [ObservableProperty] private int _zhongshuCount;
    [ObservableProperty] private int _fractalCount;
    [ObservableProperty] private int _divergenceCount;
    [ObservableProperty] private int _upBiCount;
    [ObservableProperty] private int _downBiCount;
    [ObservableProperty] private int _barCount;
    [ObservableProperty] private string _divergenceSummary = "";

    // ═══ HTML 内容（绑定到 WebView2） ═══
    [ObservableProperty]
    private string _htmlContent = "";

    [ObservableProperty]
    private bool _isAnalyzing;

    // ═══ DuckDB 路径 ═══
    private readonly string _dbPath;

    private static string ResolveDbPath()
    {
        foreach (var tryPath in new[] {
            "../../data/bars_history.duckdb",
            "../../../data/bars_history.duckdb",
            "../../../../data/bars_history.duckdb",
            "data/bars_history.duckdb",
            "C:/Works/Datas/bars_history.duckdb",
        }) if (File.Exists(tryPath)) return Path.GetFullPath(tryPath);
        return "C:/Works/Datas/bars_history.duckdb";
    }

    public ChanLunViewModel(ILogger<ChanLunViewModel> logger,
                            EngineApiClient? api = null)
    {
        _logger = logger;
        _api = api;
        _dbPath = ResolveDbPath();
        LoadProductList();

        // 默认选中 SA0 (纯碱连续)
        SelectedProduct = AvailableProducts.FirstOrDefault(p => p.Code == "SA")
                       ?? AvailableProducts.FirstOrDefault();
        if (SelectedProduct != null)
            SelectedFrequency = "week";
    }

    // ═══════════════════════════════════════════
    // 品种列表加载
    // ═══════════════════════════════════════════

    private void LoadProductList()
    {
        try
        {
            using var conn = new DuckDBConnection($"Data Source={_dbPath}");
            conn.Open();

            var sql = """
                SELECT DISTINCT
                    REPLACE(instrument_id, '000', '') as code,
                    instrument_id, COUNT(*) as bars,
                    MIN(bar_time) as first_bar, MAX(bar_time) as last_bar
                FROM bars_day
                WHERE instrument_id LIKE '%000' AND instrument_id NOT LIKE '%F000'
                GROUP BY instrument_id ORDER BY code
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();

            AvailableProducts.Clear();
            while (reader.Read())
            {
                AvailableProducts.Add(new ProductItem(
                    reader.GetString(0).ToUpper(), reader.GetString(1),
                    reader.GetInt64(2), reader.GetString(3), reader.GetString(4)));
            }

            _logger.LogInformation("Loaded {Count} products for ChanLun", AvailableProducts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load product list, using defaults");
            foreach (var code in new[] { "SA", "RB", "AG", "CU", "AU", "MA", "FG", "TA", "I", "M", "FG", "RU" })
                AvailableProducts.Add(new ProductItem(code, code.ToLower() + "000", 0, "", ""));
        }
    }

    // ═══════════════════════════════════════════
    // 参数变更 → 自动分析
    // ═══════════════════════════════════════════

    partial void OnSelectedProductChanged(ProductItem? value)
    {
        if (value != null && IsActive) _ = AnalyzeAsync();
    }

    partial void OnSelectedFrequencyChanged(string value)
    {
        if (SelectedProduct != null && IsActive) _ = AnalyzeAsync();
    }

    partial void OnBiLenChanged(int value)
    {
        if (SelectedProduct != null && IsActive) _ = AnalyzeAsync();
    }

    partial void OnMaxBiNumChanged(int value)
    {
        if (SelectedProduct != null && IsActive) _ = AnalyzeAsync();
    }

    // ═══════════════════════════════════════════
    // 分析管线
    // ═══════════════════════════════════════════

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (SelectedProduct == null) return;

        IsAnalyzing = true;
        StatusText = $"分析中... {SelectedProduct.Code} {SelectedFrequency}";

        try
        {
            var (bars, code, freq) = await Task.Run(() =>
            {
                // 1. Load bars from DuckDB → ChanLunBar
                var loaded = LoadBarsFromDuckDB(SelectedProduct.InstrumentId, SelectedFrequency);
                return (loaded, SelectedProduct.Code, SelectedFrequency);
            });

            if (bars.Count < 5)
            {
                StatusText = $"数据不足 — {code} {freq} 仅 {bars.Count} 根K线";
                IsAnalyzing = false;
                return;
            }

            // 2. Run ChanLun analysis
            var result = ChanLunAnalyzer.Analyze(bars, minBiLen: BiLen, maxBiNum: MaxBiNum, minZsOverlap: MinZsOverlap);

            // 3. Detect divergence
            var divergences = DivergenceDetector.Detect(result.Bis);

            // 4. Generate HTML chart
            var title = $"{code} — 缠论分析 ({freq}, 笔≥{BiLen}K)";
            var html = ChanLunChart.RenderHtml(result, code, title: title);

            // 5. Update UI (must be on UI thread)
            Application.Current.Dispatcher.Invoke(() =>
            {
                _lastResult = result;
                _lastDivs = divergences;
                _lastInstrument = code;

                BarCount = bars.Count;
                BiCount = result.BiCount;
                UpBiCount = result.Bis.Count(b => b.Type == Direction.Up);
                DownBiCount = result.Bis.Count(b => b.Type == Direction.Down);
                ZhongshuCount = result.ZhongshuCount;
                FractalCount = result.FractalCount;
                DivergenceCount = divergences.Count;

                TrendText = result.Trend switch
                {
                    "UP" => "上涨",
                    "DOWN" => "下跌",
                    "CONSOLIDATION" => "盘整",
                    _ => "未分类",
                };

                DivergenceSummary = divergences.Count > 0
                    ? string.Join(" | ", divergences.TakeLast(3).Select(d =>
                        $"{d.LevelLabel} {(d.BiType == Direction.Down ? "底背驰" : "顶背驰")}"))
                    : "无背驰信号";

                var dateRange = bars.Count > 0
                    ? $"{bars[0].Dt:yyyy-MM-dd} ~ {bars[^1].Dt:yyyy-MM-dd}"
                    : "";
                StatusText = $"{code} {freq} | {bars.Count:N0} bars | 笔≥{BiLen}K | {dateRange}";

                HtmlContent = html;
                IsAnalyzing = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChanLun analysis failed for {Instrument} {Freq}",
                SelectedProduct.InstrumentId, SelectedFrequency);
            StatusText = $"分析失败: {ex.Message}";
            IsAnalyzing = false;
        }
    }

    // ═══════════════════════════════════════════
    // 导出 HTML
    // ═══════════════════════════════════════════

    [RelayCommand]
    private void ExportHtml()
    {
        if (_lastResult == null)
        {
            StatusText = "请先运行分析";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "HTML 文件 (*.html)|*.html",
            DefaultExt = "html",
            FileName = $"{_lastInstrument}_{SelectedFrequency}_chanlun.html",
        };

        if (dialog.ShowDialog() == true)
        {
            var title = $"{_lastInstrument} — 缠论分析 ({SelectedFrequency}, 笔≥{BiLen}K)";
            ChanLunChart.SaveHtml(_lastResult, _lastInstrument, dialog.FileName, title: title);
            StatusText = $"已导出: {dialog.FileName}";
        }
    }

    // ═══════════════════════════════════════════
    // DuckDB 加载
    // ═══════════════════════════════════════════

    private List<ChanLunBar> LoadBarsFromDuckDB(string instrumentId, string freq)
    {
        var bars = new List<ChanLunBar>();

        if (!File.Exists(_dbPath))
        {
            _logger.LogWarning("DuckDB not found: {Path}", _dbPath);
            return bars;
        }

        using var conn = new DuckDBConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();

        var (table, timeCol) = ResolveTable(freq);
        cmd.CommandText = BuildSql(table, timeCol, freq, instrumentId);

        using var reader = (DuckDBDataReader)cmd.ExecuteReader();
        var dtIdx = reader.GetOrdinal("dt");
        var oIdx = reader.GetOrdinal("open");
        var hIdx = reader.GetOrdinal("high");
        var lIdx = reader.GetOrdinal("low");
        var cIdx = reader.GetOrdinal("close");
        var vIdx = reader.GetOrdinal("volume");

        while (reader.Read())
        {
            bars.Add(new ChanLunBar
            {
                Dt = reader.GetDateTime(dtIdx),
                Open = reader.GetDouble(oIdx),
                High = reader.GetDouble(hIdx),
                Low = reader.GetDouble(lIdx),
                Close = reader.GetDouble(cIdx),
                Volume = reader.GetInt64(vIdx),
            });
        }

        return bars;
    }

    private static (string table, string timeCol) ResolveTable(string freq) => freq switch
    {
        "1min" => ("bars_1min", "bar_time"),
        "5min" => ("bars_5min", "bar_time"),
        "15min" => ("bars_15min", "bar_time"),
        "30min" => ("bars_15min", "bar_time"),
        "day" => ("bars_day", "trading_day"),
        "week" => ("bars_week", "trading_week"),
        _ => ("bars_day", "trading_day"),
    };

    private static string BuildSql(string table, string timeCol, string freq, string instrumentId)
    {
        // 日/周线: 按 trading_day / trading_week 分组聚合
        if (freq is "day" or "week")
        {
            return $"""
                SELECT {timeCol} as dt,
                    FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                    MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                    SUM(volume) as volume
                FROM {table}
                WHERE instrument_id = '{instrumentId}'
                GROUP BY {timeCol} ORDER BY dt
                """;
        }

        // 30min: 从 15min 聚合
        if (freq == "30min")
        {
            return $"""
                SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                       INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / 30) * 30) MINUTE as dt,
                    FIRST(open)/1e7 as open, MAX(high)/1e7 as high,
                    MIN(low)/1e7 as low, LAST(close)/1e7 as close,
                    SUM(volume) as volume
                FROM {table}
                WHERE instrument_id = '{instrumentId}'
                GROUP BY dt ORDER BY dt
                """;
        }

        // 1min/5min/15min: 从对应表直接读
        return $"""
            SELECT bar_time::TIMESTAMP as dt,
                open/1e7 as open, high/1e7 as high,
                low/1e7 as low, close/1e7 as close,
                volume
            FROM {table}
            WHERE instrument_id = '{instrumentId}'
            ORDER BY dt
            """;
    }
}
