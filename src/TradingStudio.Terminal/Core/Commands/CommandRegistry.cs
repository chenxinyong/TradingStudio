using System.Collections.Concurrent;

namespace TradingStudio.Terminal.Core.Commands;

/// <summary>
/// Central registry for all trading commands.
/// Supports registration, lookup, fuzzy search (for command palette), and removal.
/// </summary>
public class CommandRegistry
{
    private readonly ConcurrentDictionary<string, TradingCommand> _commands = new();

    public TradingCommand Register(string id, Func<object?, Task> handler, Action<TradingCommand>? configure = null)
    {
        var command = new TradingCommand(id, handler);
        configure?.Invoke(command);
        _commands[id] = command;
        return command;
    }

    public TradingCommand Register(string id, Func<Task> handler, Action<TradingCommand>? configure = null)
    {
        var command = new TradingCommand(id, handler);
        configure?.Invoke(command);
        _commands[id] = command;
        return command;
    }

    public TradingCommand Register(string id, Action<object?> handler, Action<TradingCommand>? configure = null)
    {
        var command = new TradingCommand(id, handler);
        configure?.Invoke(command);
        _commands[id] = command;
        return command;
    }

    public TradingCommand Register(string id, Action handler, Action<TradingCommand>? configure = null)
    {
        var command = new TradingCommand(id, handler);
        configure?.Invoke(command);
        _commands[id] = command;
        return command;
    }

    public TradingCommand Register(TradingCommand command)
    {
        _commands[command.Id] = command;
        return command;
    }

    /// <summary>Get a command by its id.</summary>
    public TradingCommand? Get(string id)
    {
        _commands.TryGetValue(id, out var cmd);
        return cmd;
    }

    /// <summary>Execute a command by id.</summary>
    public async Task<bool> ExecuteAsync(string id, object? parameter = null)
    {
        var cmd = Get(id);
        if (cmd == null || !cmd.CanExecute(parameter))
            return false;
        await cmd.ExecuteAsync(parameter);
        return true;
    }

    /// <summary>All registered commands.</summary>
    public IEnumerable<TradingCommand> All => _commands.Values;

    /// <summary>Fuzzy search for commands matching a query.</summary>
    public IEnumerable<TradingCommand> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return All;
        return All.Where(c =>
            c.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public bool Remove(string id) => _commands.TryRemove(id, out _);
    public int Count => _commands.Count;
    public void Clear() => _commands.Clear();
}
