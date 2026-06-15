using System.Windows;

namespace TradingStudio.Terminal.Services;

/// <summary>
/// 窗口导航 — 管理 Chart / StrategyDetail 等独立窗口的生命周期。
/// </summary>
public interface INavigationService
{
    void ShowChart(string? instrumentId = null);
    void ShowStrategyDetail(string strategyId);
    void ShowOrderHistory();
}

public class NavigationService : INavigationService
{
    private ChartWindow? _chartWindow;
    private Window? _strategyWindow;
    private Window? _orderWindow;

    public void ShowChart(string? instrumentId = null)
    {
        if (_chartWindow == null)
        {
            _chartWindow = new ChartWindow();
            _chartWindow.Closed += (_, _) => _chartWindow = null;
        }
        if (instrumentId != null && _chartWindow.DataContext is ViewModels.ChartViewModel vm)
            vm.InstrumentId = instrumentId;
        _chartWindow.Show();
        _chartWindow.Activate();
    }

    public void ShowStrategyDetail(string strategyId)
    {
        // Phase 2 实现
    }

    public void ShowOrderHistory()
    {
        // Phase 2 实现
    }
}

// 占位窗口——后续替换为真实窗口
public class ChartWindow : Window
{
    public ChartWindow()
    {
        Title = "TradingStudio — K 线图";
        Width = 1200; Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}
