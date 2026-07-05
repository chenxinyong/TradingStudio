using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal.Views;

public partial class BacktestView : UserControl
{
    public BacktestView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<BacktestViewModel>();
    }
}
