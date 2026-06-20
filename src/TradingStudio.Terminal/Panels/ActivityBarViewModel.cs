using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TradingStudio.Terminal.Panels;

/// <summary>
/// ViewModel for the VS Code-style activity bar — the narrow vertical icon strip
/// on the far left that switches which panel is shown in the sidebar.
/// </summary>
public class ActivityBarViewModel : INotifyPropertyChanged
{
    private ActivityBarItem? _selectedItem;

    public ActivityBarViewModel()
    {
        Items = new ObservableCollection<ActivityBarItem>
        {
            new("dashboard",  "仪表盘", 0),
            new("chart",      "K线图",  1),
            new("strategies", "策略",   2),
            new("orders",     "订单",   3),
            new("alerts",     "告警",   4),
        };

        BottomItems = new ObservableCollection<ActivityBarItem>
        {
            new("settings", "设置", 99),
        };

        SelectedItem = Items[0]; // Default: Dashboard
    }

    public ObservableCollection<ActivityBarItem> Items { get; }
    public ObservableCollection<ActivityBarItem> BottomItems { get; }

    public ActivityBarItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != value)
            {
                if (_selectedItem != null) _selectedItem.IsSelected = false;
                _selectedItem = value;
                if (_selectedItem != null) _selectedItem.IsSelected = true;
                OnPropertyChanged();
                SelectedItemChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>The currently active panel ID (e.g., "dashboard", "chart").</summary>
    public string ActivePanelId => _selectedItem?.Id ?? "dashboard";

    public event EventHandler<ActivityBarItem?>? SelectedItemChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// An item in the activity bar — switches sidebar panels when clicked.
/// </summary>
public class ActivityBarItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public ActivityBarItem(string id, string tooltip, int order)
    {
        Id = id; Tooltip = tooltip; Order = order;
    }

    public string Id { get; }
    public string Tooltip { get; }
    public int Order { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
