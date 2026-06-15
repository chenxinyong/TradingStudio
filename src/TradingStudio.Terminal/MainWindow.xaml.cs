using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;
using TradingStudio.Terminal.Views;

namespace TradingStudio.Terminal;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _dashVM;

    public MainWindow()
    {
        InitializeComponent();
        _dashVM = App.Services.GetRequiredService<DashboardViewModel>();
        DataContext = _dashVM;

        // 首页默认打开仪表盘
        OpenTab("📊 仪表盘", () => new DashboardView());
        OpenTab("📈 行情",    () => new ChartView());

        KeyDown += (_, e) =>
        {
            if (e.KeyboardDevice.Modifiers != ModifierKeys.Control) return;
            switch (e.Key) { case Key.D1: OpenTab("📊 仪表盘", () => new DashboardView()); break;
                case Key.D2: OpenTab("📈 行情", () => new ChartView()); break; }
        };
    }

    void OpenTab(string title, Func<UIElement> factory)
    {
        // 已有同标题标签 → 切换到它
        foreach (TabItem tab in MainTabs.Items)
            if (tab.Tag?.ToString() == title) { tab.IsSelected = true; return; }

        var item = new TabItem { Header = title, Tag = title, Content = factory() };
        MainTabs.Items.Add(item);
        item.IsSelected = true;
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string title)
        {
            foreach (TabItem tab in MainTabs.Items)
                if (tab.Tag?.ToString() == title) { MainTabs.Items.Remove(tab); break; }
        }
    }

    void TitleBar_MouseDown(object s, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize_Click(s, e); else if (e.ChangedButton == MouseButton.Left) DragMove(); }
    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    void NavDashboard_Click(object s, RoutedEventArgs e)  => OpenTab("📊 仪表盘", () => new DashboardView());
    void NavChart_Click(object s, RoutedEventArgs e)      => OpenTab("📈 行情",    () => new ChartView());
    void NavStrategies_Click(object s, RoutedEventArgs e) => OpenTab("📋 策略",    () => new PlaceholderView("📋","策略管理","Phase 2")) ;
    void NavOrders_Click(object s, RoutedEventArgs e)     => OpenTab("📜 订单",    () => new PlaceholderView("📜","订单监控","Phase 2"));
    void NavLogs_Click(object s, RoutedEventArgs e)       => OpenTab("⚙ 日志",    () => new PlaceholderView("⚙","系统日志","Phase 2"));
    void NavAdmin_Click(object s, RoutedEventArgs e)      => OpenTab("⚡ 管理",    () => new PlaceholderView("⚡","管理","Phase 2"));
}
