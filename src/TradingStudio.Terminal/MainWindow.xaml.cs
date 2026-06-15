using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ChartViewModel>();
    }

    // ── 左侧导航栏 — Step 3 实现内容切换 ──

    private void NavDashboard_Click(object sender, RoutedEventArgs e) { /* Step 3 */ }
    private void NavChart_Click(object sender, RoutedEventArgs e)      { /* 当前视图 */ }
    private void NavStrategies_Click(object sender, RoutedEventArgs e) { /* Step 3 */ }
    private void NavOrders_Click(object sender, RoutedEventArgs e)     { /* Step 3 */ }
    private void NavLogs_Click(object sender, RoutedEventArgs e)       { /* Step 3 */ }
}
