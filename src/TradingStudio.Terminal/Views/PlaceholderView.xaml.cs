using System.Windows.Controls;

namespace TradingStudio.Terminal.Views;

public partial class PlaceholderView : UserControl
{
    public PlaceholderView(string icon, string title, string desc)
    {
        InitializeComponent();
        IconText.Text = icon;
        TitleText.Text = title;
        DescText.Text = desc;
    }
}
