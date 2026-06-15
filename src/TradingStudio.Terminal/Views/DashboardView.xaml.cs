using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DashboardViewModel>();
    }
}
