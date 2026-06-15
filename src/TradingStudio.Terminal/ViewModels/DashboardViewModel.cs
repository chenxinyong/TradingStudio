using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingStudio.Terminal.Services;

namespace TradingStudio.Terminal.ViewModels;

/// <summary>
/// Dashboard 主面板 ViewModel — 健康/风控/告警/策略/成交 实时数据。
/// Phase 1: 基础框架，SignalR 事件对接 Phase 2。
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly EngineHubClient _hub;
    private readonly INavigationService _nav;
    private readonly ILogger<DashboardViewModel> _log;

    [ObservableProperty] private ConnectionState _connectionState = ConnectionState.Disconnected;
    [ObservableProperty] private decimal _totalEquity;
    [ObservableProperty] private decimal _cash;
    [ObservableProperty] private decimal _marginUsed;
    [ObservableProperty] private double _marginPct;
    [ObservableProperty] private int _tickCount;
    [ObservableProperty] private int _positionCount;
    [ObservableProperty] private string _session = "休市";

    public ObservableCollection<MonitorAlert> RecentAlerts { get; } = new();
    public ObservableCollection<StrategySnapshot> Strategies { get; } = new();
    public ObservableCollection<OrderEvent> RecentOrders { get; } = new();

    public DashboardViewModel(EngineHubClient hub, INavigationService nav,
                               ILogger<DashboardViewModel> log)
    {
        _hub = hub;
        _nav = nav;
        _log = log;
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        _hub.StateChanged += OnStateChanged;
        TryConnect();
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _hub.StateChanged -= OnStateChanged;
    }

    private async void TryConnect()
    {
        if (_hub.State == ConnectionState.Disconnected)
            await _hub.ConnectAsync();
    }

    private void OnStateChanged(ConnectionState state)
    {
        Application.Current.Dispatcher.Invoke(() => ConnectionState = state);
    }

    [RelayCommand] private void OpenChart() => _nav.ShowChart();
}
