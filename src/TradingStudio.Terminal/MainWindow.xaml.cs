using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;
using TradingStudio.Terminal.Views;

namespace TradingStudio.Terminal;

public partial class MainWindow : Window
{
    private UIElement? _dashboardView, _chartView, _strategyView, _orderView, _logView;
    private readonly DashboardViewModel _dashVM;

    public MainWindow()
    {
        InitializeComponent();
        _dashVM = App.Services.GetRequiredService<DashboardViewModel>();
        DataContext = _dashVM;  // 状态栏 + 标题绑定
        ShowDashboard();

        KeyDown += (_, e) =>
        {
            if (e.KeyboardDevice.Modifiers != ModifierKeys.Control) return;
            switch (e.Key) { case Key.D1: ShowDashboard(); break; case Key.D2: ShowChart(); break;
                case Key.D3: ShowStrategies(); break; case Key.D4: ShowOrders(); break; case Key.D5: ShowLogs(); break; }
        };
    }

    void ShowDashboard() { _dashboardView ??= new DashboardView(); MainContent.Content = _dashboardView; }
    void ShowChart()     { _chartView     ??= new ChartView();     MainContent.Content = _chartView; }
    void ShowStrategies(){ _strategyView  ??= new PlaceholderView("📋", "策略管理", "Phase 2 — 策略列表、启停控制");     MainContent.Content = _strategyView; }
    void ShowOrders()    { _orderView     ??= new PlaceholderView("📜", "订单监控", "Phase 2 — 活跃订单、持仓管理");     MainContent.Content = _orderView; }
    void ShowLogs()      { _logView       ??= new PlaceholderView("⚙", "系统日志", "Phase 2 — 实时日志尾随");         MainContent.Content = _logView; }

    // 标题栏
    void TitleBar_MouseDown(object s, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize_Click(s, e); else if (e.ChangedButton == MouseButton.Left) DragMove(); }
    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    // 导航按钮
    void NavDashboard_Click(object s, RoutedEventArgs e) => ShowDashboard();
    void NavChart_Click(object s, RoutedEventArgs e)     => ShowChart();
    void NavStrategies_Click(object s, RoutedEventArgs e) => ShowStrategies();
    void NavOrders_Click(object s, RoutedEventArgs e)    => ShowOrders();
    void NavLogs_Click(object s, RoutedEventArgs e)      => ShowLogs();
    void NavAdmin_Click(object s, RoutedEventArgs e)     => ShowAdmin();

    void ShowAdmin()
    {
        var view = new PlaceholderView("⚡", "管理", "Phase 2 — 系统配置、数据库、部署");
        MainContent.Content = view;
    }
}
