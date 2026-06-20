using TradingStudio.Terminal.Core.Input;

namespace TradingStudio.Terminal.Core.Commands;

/// <summary>
/// A trading command — the fundamental unit of user action.
/// Everything from "Buy" to "Show Dashboard" is a TradingCommand.
/// Mapped to menus, keybindings, command palette, and toolbar buttons.
/// </summary>
public class TradingCommand
{
    /// <summary>Unique identifier, e.g. "trade.buy".</summary>
    public string Id { get; }

    /// <summary>Human-readable title for the command palette, e.g. "Buy".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Category for grouping in the command palette, e.g. "Trade".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Optional tooltip shown in toolbar buttons.</summary>
    public string? Tooltip { get; set; }

    /// <summary>Default keyboard shortcut for this command.</summary>
    public KeyGesture? DefaultGesture { get; set; }

    /// <summary>The async handler that executes this command.</summary>
    public Func<object?, Task> Handler { get; }

    /// <summary>Optional condition: returns true if the command can execute right now.</summary>
    public Func<object?, bool>? When { get; init; }

    public TradingCommand(string id, Func<object?, Task> handler)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public TradingCommand(string id, Action<object?> handler)
        : this(id, p => { handler(p); return Task.CompletedTask; }) { }

    public TradingCommand(string id, Func<Task> handler)
        : this(id, _ => handler()) { }

    public TradingCommand(string id, Action handler)
        : this(id, _ => { handler(); return Task.CompletedTask; }) { }

    public bool CanExecute(object? parameter = null) =>
        When?.Invoke(parameter) ?? true;

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (CanExecute(parameter))
            await Handler(parameter);
    }

    /// <summary>Display label for command palette: "Category: Title" or just "Title".</summary>
    public string DisplayLabel =>
        string.IsNullOrEmpty(Category) ? Title : $"{Category}: {Title}";

    public override string ToString() => $"{Id} ({DisplayLabel})";
}
