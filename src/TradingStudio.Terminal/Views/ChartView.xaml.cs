using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal.Views;

public partial class ChartView : UserControl
{
    public ChartView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ChartViewModel>();
    }
}
