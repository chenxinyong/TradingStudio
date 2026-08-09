using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Wpf;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal.Views;

/// <summary>
/// 缠论分析视图 — WebView2 嵌入 Plotly.js 图表 + 工具栏 + 摘要栏。
/// </summary>
public partial class ChanLunView : UserControl
{
    private readonly ChanLunViewModel _vm;

    public ChanLunView()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<ChanLunViewModel>();
        DataContext = _vm;

        // 激活 ViewModel（Tab 模式不会自动调用 OnNavigatedTo）
        _vm.OnNavigatedTo();

        // WebView2 异步初始化
        _ = InitializeWebViewAsync();
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // 构造后自动触发首次分析（因为 SelectedProduct 在 OnNavigatedTo 之前设置，不会自动触发）
        _ = _vm.AnalyzeCommand.ExecuteAsync(null);

        // 卸载时停用 ViewModel
        Unloaded += (_, _) => _vm.OnNavigatedFrom();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await ChanLunWebView.EnsureCoreWebView2Async(null);
            ChanLunWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            // 设置暗色背景避免加载闪烁
            ChanLunWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);

            if (!string.IsNullOrEmpty(_vm.HtmlContent))
                ChanLunWebView.NavigateToString(_vm.HtmlContent);
        }
        catch (Exception ex)
        {
            // WebView2 不可用时，视图仍可正常显示（图表区域空白）
            System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChanLunViewModel.HtmlContent)
            && !string.IsNullOrEmpty(_vm.HtmlContent)
            && ChanLunWebView.CoreWebView2 != null)
        {
            Dispatcher.Invoke(() => ChanLunWebView.NavigateToString(_vm.HtmlContent));
        }
    }
}
