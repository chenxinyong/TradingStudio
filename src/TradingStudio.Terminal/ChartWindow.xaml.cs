using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal;

/// <summary>
/// 独立 K 线查看器 — 最小化窗口，仅包含 ChartView。
/// 可通过 <c>TradingStudio.Terminal.exe --chart-only</c> 直接启动。
/// </summary>
public partial class ChartWindow : Window
{
    private readonly ChartViewModel _vm;

    public ChartWindow()
    {
        InitializeComponent();

        _vm = App.Services.GetRequiredService<ChartViewModel>();
        DataContext = _vm;

        // 如果有 SignalR 连接可用，订阅实时 Bar
        _vm.SubscribeRealTimeBars();

        // 窗口标题随品种/周期变化
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChartViewModel.SelectedProduct)
                or nameof(ChartViewModel.SelectedFrequency))
            {
                Title = $"K线图 — {_vm.SelectedProduct?.Code ?? "选择品种"} {_vm.SelectedFrequency}";
            }
        };

        // 全局快捷键
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.R &&
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                _vm.ReloadCommand.Execute(null);
                e.Handled = true;
            }
        };

        Title = "TradingStudio — K线图";
    }

    private void OpenReplay_Click(object sender, RoutedEventArgs e)
    {
        var replay = App.Services.GetRequiredService<ReplayWindow>();
        replay.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.UnsubscribeRealTimeBars();
        base.OnClosed(e);
    }
}
