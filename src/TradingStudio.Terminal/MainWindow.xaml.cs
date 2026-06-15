using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ChartViewModel>();

        // 键盘快捷键
        KeyDown += (s, e) =>
        {
            if (e.KeyboardDevice.Modifiers != ModifierKeys.Control) return;
            switch (e.Key)
            {
                case Key.D1: NavDashboard_Click(this, e);   break;
                case Key.D2: NavChart_Click(this, e);        break;
                case Key.D3: NavStrategies_Click(this, e);   break;
                case Key.D4: NavOrders_Click(this, e);       break;
                case Key.D5: NavLogs_Click(this, e);         break;
            }
        };
    }

    // ── 标题栏 — 窗口拖拽 ──
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Maximize_Click(sender, e); return; }
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    // ── 窗口控制 ──
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── 左侧导航 — Step 3 实现内容切换 ──
    private void NavDashboard_Click(object sender, RoutedEventArgs e) { /* Step 3 */ }
    private void NavChart_Click(object sender, RoutedEventArgs e)      { /* 当前视图 */ }
    private void NavStrategies_Click(object sender, RoutedEventArgs e) { /* Step 3 */ }
    private void NavOrders_Click(object sender, RoutedEventArgs e)     { /* Step 3 */ }
    private void NavLogs_Click(object sender, RoutedEventArgs e)       { /* Step 3 */ }
}
