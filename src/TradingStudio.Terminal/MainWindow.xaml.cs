using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.Commands;
using TradingStudio.Terminal.Core.Commands;
using TradingStudio.Terminal.Core.Messaging;
using TradingStudio.Terminal.Services;
using TradingStudio.Terminal.ViewModels;
using TradingStudio.Terminal.Views;

namespace TradingStudio.Terminal;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _dashVM;
    private readonly CommandRegistry _commands;
    private readonly KeybindingRegistry _keybindings;
    private readonly EventBus _eventBus;
    private Rect _normalBounds; // saved position for restore

    public MainWindow()
    {
        InitializeComponent();
        _dashVM = App.Services.GetRequiredService<DashboardViewModel>();
        _commands = App.Services.GetRequiredService<CommandRegistry>();
        _keybindings = App.Services.GetRequiredService<KeybindingRegistry>();
        _eventBus = App.Services.GetRequiredService<EventBus>();

        DataContext = _dashVM;

        // Default tabs on startup
        OpenTab("📊 仪表盘", () => new DashboardView());

        // Register keyboard shortcuts from the command system
        RegisterKeyBindings();

        // Listen for connection state changes → update title bar
        _eventBus.Subscribe<EngineConnectionChanged>(change =>
        {
            Dispatcher.Invoke(() =>
            {
                TitleText.Text = change.State switch
                {
                    ConnectionState.Connected => " Terminal ●",
                    ConnectionState.Connecting => " Terminal ◉",
                    _ => " Terminal ○"
                };
            });
        });

        // Global key handler for command shortcuts not covered by WPF InputBindings
        KeyDown += OnGlobalKeyDown;

        // Set placeholder text on command bar
        CommandBarInput.Text = "";
        CommandBarInput.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(CommandBarInput.Text))
                CommandBarInput.Text = "";
        };

        // Maximize to work area (screen minus taskbar) so status bar isn't hidden
        SourceInitialized += (_, _) => MaximizeToWorkArea();
    }

    /// <summary>
    /// Switch to a panel by its ID (for ActivityBar / Command integration).
    /// </summary>
    public void SwitchToPanel(string panelId)
    {
        switch (panelId)
        {
            case "dashboard":  NavDashboard_Click(this, new RoutedEventArgs()); break;
            case "chart":      NavChart_Click(this, new RoutedEventArgs());     break;
            case "strategies": NavStrategies_Click(this, new RoutedEventArgs()); break;
            case "orders":     NavOrders_Click(this, new RoutedEventArgs());    break;
            case "backtest":  NavBacktest_Click(this, new RoutedEventArgs()); break;
            case "replay":    NavReplay_Click(this, new RoutedEventArgs());   break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Keyboard Shortcuts (from KeybindingRegistry → WPF InputBindings)
    // ═══════════════════════════════════════════════════════════

    private void RegisterKeyBindings()
    {
        foreach (var command in _commands.All)
        {
            var wpfBinding = InputAdapter.CreateWpfKeyBinding(command);
            if (wpfBinding != null)
                InputBindings.Add(wpfBinding);
        }
    }

    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+P or Ctrl+P → focus command bar
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            CommandBarInput.Focus();
            CommandBarInput.SelectAll();
            e.Handled = true;
            return;
        }

        // Escape → clear command bar
        if (e.Key == Key.Escape && CommandBarInput.IsFocused)
        {
            CommandBarInput.Text = "";
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Command Bar (Bloomberg-style)
    // ═══════════════════════════════════════════════════════════

    private void CommandBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var query = CommandBarInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            // Search commands
            var matches = _commands.Search(query).ToList();
            if (matches.Count == 1)
            {
                _ = _commands.ExecuteAsync(matches[0].Id);
                CommandBarInput.Text = "";
            }
            else if (matches.Count > 1)
            {
                // Execute first match
                _ = _commands.ExecuteAsync(matches[0].Id);
                CommandBarInput.Text = "";
            }
            e.Handled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Menu Click Handlers → CommandRegistry
    // ═══════════════════════════════════════════════════════════

    private void Menu_Connect(object s, RoutedEventArgs e)
        => _ = _commands.ExecuteAsync("engine.connect");
    private void Menu_Exit(object s, RoutedEventArgs e)
        => Close();
    private void Menu_Dashboard(object s, RoutedEventArgs e)
        => _ = _commands.ExecuteAsync("view.dashboard");
    private void Menu_Chart(object s, RoutedEventArgs e)
        => _ = _commands.ExecuteAsync("view.chart");
    private void Menu_Strategies(object s, RoutedEventArgs e)
        => _ = _commands.ExecuteAsync("view.strategies");
    private void Menu_Orders(object s, RoutedEventArgs e)
        => _ = _commands.ExecuteAsync("view.orders");
    private void Menu_About(object s, RoutedEventArgs e)
        => _ = _commands.ExecuteAsync("help.about");

    // ═══════════════════════════════════════════════════════════
    // Tabs
    // ═══════════════════════════════════════════════════════════

    void OpenTab(string title, Func<UIElement> factory)
    {
        foreach (TabItem tab in MainTabs.Items)
            if (tab.Tag?.ToString() == title) { tab.IsSelected = true; return; }

        var item = new TabItem { Header = title, Tag = title, Content = factory() };
        MainTabs.Items.Add(item);
        item.IsSelected = true;
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string title)
        {
            foreach (TabItem tab in MainTabs.Items)
                if (tab.Tag?.ToString() == title) { MainTabs.Items.Remove(tab); break; }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Window Chrome
    // ═══════════════════════════════════════════════════════════

    void TitleBar_MouseDown(object s, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize_Click(s, e); else if (e.ChangedButton == MouseButton.Left) DragMove(); }
    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object s, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // Restore to saved normal bounds
            WindowState = WindowState.Normal;
            Left = _normalBounds.Left;
            Top = _normalBounds.Top;
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
        }
        else
        {
            // Save current bounds, then maximize to work area (excludes taskbar)
            _normalBounds = new Rect(Left, Top, Width, Height);
            MaximizeToWorkArea();
        }
    }

    private void MaximizeToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        WindowState = WindowState.Normal; // must be Normal to set bounds manually
        Left = workArea.Left;
        Top = workArea.Top;
        Width = workArea.Width;
        Height = workArea.Height;
    }
    void Close_Click(object s, RoutedEventArgs e) => Close();

    // ═══════════════════════════════════════════════════════════
    // Navigation
    // ═══════════════════════════════════════════════════════════

    void NavDashboard_Click(object s, RoutedEventArgs e)  => OpenTab("📊 仪表盘", () => new DashboardView());
    void NavChart_Click(object s, RoutedEventArgs e)      => OpenTab("📈 行情",    () => new ChartView());
    void NavStrategies_Click(object s, RoutedEventArgs e) => OpenTab("📋 策略",    () => new PlaceholderView("📋","策略管理","Phase 2")) ;
    void NavOrders_Click(object s, RoutedEventArgs e)     => OpenTab("📜 订单",    () => new PlaceholderView("📜","订单监控","Phase 2"));
    void NavBacktest_Click(object s, RoutedEventArgs e)   => OpenTab("🔬 回测",   () => new BacktestView());
    void NavReplay_Click(object s, RoutedEventArgs e)     => OpenTab("⏯ 回放",   () => new ReplayView());
    void NavLogs_Click(object s, RoutedEventArgs e)       => OpenTab("⚙ 日志",    () => new PlaceholderView("⚙","系统日志","Phase 2"));
    void NavAdmin_Click(object s, RoutedEventArgs e)      => OpenTab("⚡ 管理",    () => new PlaceholderView("⚡","管理","Phase 2"));
}
