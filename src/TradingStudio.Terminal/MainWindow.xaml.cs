using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.UI.ViewModels;

namespace TradingStudio.UI;

/// <summary>
/// K线图表 Demo 主窗口 — 四窗格布局 (K线/成交量/MACD/RSI)
/// OxyPlot + MVVM + 完整技术指标 + 实时数据模拟
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 从 DI 容器获取 ViewModel（自动注入 ILogger<ChartViewModel>）
        DataContext = App.Services.GetRequiredService<ChartViewModel>();
    }
}
