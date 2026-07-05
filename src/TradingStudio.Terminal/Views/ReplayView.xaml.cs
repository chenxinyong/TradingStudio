using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal.Views;

public partial class ReplayView : UserControl
{
    public ReplayView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ReplayViewModel>();
    }
}
