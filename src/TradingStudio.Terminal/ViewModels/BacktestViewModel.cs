using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace TradingStudio.Terminal.ViewModels;

public partial class BacktestViewModel : ViewModelBase
{
    private readonly ILogger<BacktestViewModel> _log;
    private readonly HttpClient _http;
    private string _apiBase = "http://localhost:5199";

    // ── 表单 ──
    public ObservableCollection<string> StrategyTypes { get; } = new()
        { "BollingerReversion", "MaCross", "SmaMacd", "DonchianTrend", "ChanLun" };

    [ObservableProperty] private string _selectedStrategy = "BollingerReversion";
    [ObservableProperty] private string _instrumentId = "rb000";
    [ObservableProperty] private string _barPeriod = "15";
    [ObservableProperty] private string _capital = "1000000";
    [ObservableProperty] private string _parameters = "{\n  \"Period\": 20,\n  \"StdDevMult\": 2.0\n}";

    // ── 状态 ──
    [ObservableProperty] private string _statusText = "就绪 — 配置参数后点击 [▶ 执行回测]";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _taskId;

    // ── 结果 ──
    [ObservableProperty] private string _resultStrategy = "";
    [ObservableProperty] private string _resultPnL = "";
    [ObservableProperty] private string _resultTrades = "";
    [ObservableProperty] private string _resultWinRate = "";
    [ObservableProperty] private string _resultPLR = "";
    [ObservableProperty] private string _resultDD = "";
    [ObservableProperty] private string _resultSharpe = "";
    [ObservableProperty] private string _resultAnnual = "";

    public PlotModel? EquityModel { get; private set; }

    public BacktestViewModel(ILogger<BacktestViewModel> log)
    {
        _log = log;
        _http = new HttpClient { BaseAddress = new Uri(_apiBase), Timeout = TimeSpan.FromMinutes(5) };
    }

    [RelayCommand]
    private async Task RunBacktest()
    {
        if (IsRunning) return;
        IsRunning = true;
        StatusText = "提交回测请求...";
        ResultStrategy = "";

        try
        {
            var config = new
            {
                StrategyType = SelectedStrategy,
                StrategyId = $"WPF-{SelectedStrategy}-{InstrumentId}-{DateTime.Now:HHmmss}",
                Instruments = new[] { InstrumentId },
                PrimaryBarType = "bars_1min",
                BarPeriodMinutes = int.TryParse(BarPeriod, out var bp) ? bp : 15,
                AllocatedCapital = decimal.TryParse(Capital, out var c) ? c : 1_000_000m,
                MaxDrawdownPct = 0.30,
                MaxPositionPerInstrument = 2,
                SkipAuction = true,
            };

            var resp = await _http.PostAsJsonAsync("/api/backtest/start", config);
            if (!resp.IsSuccessStatusCode)
            {
                StatusText = $"API 错误: {resp.StatusCode}";
                return;
            }

            var startResult = await resp.Content.ReadFromJsonAsync<StartResponse>();
            TaskId = startResult?.TaskId;
            StatusText = $"回测已启动 — TaskId={TaskId} — 等待完成...";

            // 轮询状态
            for (int i = 0; i < 120; i++) // 最多等10分钟
            {
                await Task.Delay(2000);
                var statusResp = await _http.GetAsync($"/api/backtest/{TaskId}/status");
                if (!statusResp.IsSuccessStatusCode) continue;
                var status = await statusResp.Content.ReadFromJsonAsync<TaskStatusResponse>();
                if (status?.Status == "completed")
                {
                    await LoadResults(TaskId!);
                    return;
                }
                if (status?.Status == "failed")
                {
                    StatusText = $"回测失败: {status.Error}";
                    return;
                }
                StatusText = $"运行中... ({i * 2}s)";
            }
            StatusText = "超时 — 回测未在10分钟内完成";
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
            _log.LogError(ex, "Backtest failed");
        }
        finally { IsRunning = false; }
    }

    private async Task LoadResults(string taskId)
    {
        var resp = await _http.GetAsync($"/api/backtest/{taskId}/summary");
        if (!resp.IsSuccessStatusCode) return;
        var s = await resp.Content.ReadFromJsonAsync<SummaryResponse>();
        if (s == null) return;

        ResultStrategy = s.StrategyId ?? "";
        ResultPnL = $"¥{s.TotalNetProfit:N0}";
        ResultTrades = $"{s.TotalTrades} 笔";
        ResultWinRate = $"{s.WinRate * 100:F0}%";
        ResultPLR = $"{s.ProfitLossRatio:F2}";
        ResultDD = $"{s.MaxDrawdown * 100:F1}%";
        ResultSharpe = $"{s.SharpeRatio:F2}";
        ResultAnnual = $"{s.CompoundingAnnualReturn * 100:F1}%";

        StatusText = $"✅ 回测完成 — {s.TotalTrades}笔 PnL=¥{s.TotalNetProfit:N0}";
        _log.LogInformation("Backtest {Id} loaded: {PnL}", taskId, s.TotalNetProfit);
    }

    private record StartResponse(string TaskId, string Status);
    private record TaskStatusResponse(string Status, string? Error, DateTime StartedAt, DateTime? CompletedAt);
    private record SummaryResponse(string StrategyId, int TotalTrades, decimal TotalNetProfit,
        decimal WinRate, decimal ProfitLossRatio, decimal MaxDrawdown, decimal SharpeRatio,
        decimal CompoundingAnnualReturn);
}
