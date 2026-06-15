using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingStudio.Terminal.Services;

namespace TradingStudio.Terminal.ViewModels;

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
    [ObservableProperty] private int _connectedInstruments;
    [ObservableProperty] private string _session = "休市";

    [ObservableProperty] private string _statusText = "● 已连接";
    [ObservableProperty] private string _statusColor = "#4EC9B0";

    public ObservableCollection<MonitorAlert> RecentAlerts { get; } = new();
    public ObservableCollection<StrategySnapshot> Strategies { get; } = new();
    public ObservableCollection<OrderEvent> RecentOrders { get; } = new();

    public DashboardViewModel(EngineHubClient hub, INavigationService nav, ILogger<DashboardViewModel> log)
    {
        _hub = hub; _nav = nav; _log = log;
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        _hub.StateChanged += OnStateChanged;
        _hub.PortfolioUpdated += OnPortfolio;
        _hub.TickSnapshotReceived += OnTickSnapshot;
        _hub.StrategiesUpdated += OnStrategies;
        _hub.AlertReceived += OnAlert;
        TryConnect();
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _hub.StateChanged -= OnStateChanged;
        _hub.PortfolioUpdated -= OnPortfolio;
        _hub.TickSnapshotReceived -= OnTickSnapshot;
        _hub.StrategiesUpdated -= OnStrategies;
        _hub.AlertReceived -= OnAlert;
    }

    async void TryConnect() { if (_hub.State == ConnectionState.Disconnected) await _hub.ConnectAsync(); }

    // ── SignalR 事件 → UI 线程 ──

    void OnStateChanged(ConnectionState s) => Dispatcher(() =>
    {
        ConnectionState = s;
        (StatusText, StatusColor) = s switch
        {
            ConnectionState.Connected    => ("● 已连接", "#4EC9B0"),
            ConnectionState.Connecting   => ("◉ 连接中", "#CCA700"),
            ConnectionState.Degraded     => ("◉ 降级", "#CCA700"),
            ConnectionState.Disconnected => ("○ 断开", "#F44747"),
            _ => ("○ 断开", "#F44747")
        };
    });

    void OnPortfolio(PortfolioUpdatedPayload p) => Dispatcher(() =>
    {
        TotalEquity = p.Equity; Cash = p.Cash; MarginUsed = p.MarginUsed;
        MarginPct = TotalEquity > 0 ? (double)(MarginUsed / TotalEquity) * 100 : 0;
        PositionCount = p.Positions.Count;
    });

    void OnTickSnapshot(IReadOnlyList<TickSnapshotItem> ticks) => Dispatcher(() =>
    {
        TickCount = ticks.Sum(t => (int)t.Volume);
        ConnectedInstruments = ticks.Count;
    });

    void OnStrategies(IReadOnlyList<StrategySnapshot> ss) => Dispatcher(() =>
    {
        Strategies.Clear();
        foreach (var s in ss) Strategies.Add(s);
    });

    void OnAlert(MonitorAlert a) => Dispatcher(() =>
    {
        RecentAlerts.Insert(0, a);
        while (RecentAlerts.Count > 100) RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
    });

    void Dispatcher(Action a) => Application.Current.Dispatcher.Invoke(a);

    [RelayCommand] void OpenChart() => _nav.ShowChart();
}
