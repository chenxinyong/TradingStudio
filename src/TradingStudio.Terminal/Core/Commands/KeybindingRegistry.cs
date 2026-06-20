using System.Collections.Concurrent;
using TradingStudio.Terminal.Core.Input;

namespace TradingStudio.Terminal.Core.Commands;

/// <summary>
/// Maps keyboard gestures to command IDs.
/// Supports multi-layer keybindings (defaults + user overrides).
/// </summary>
public class KeybindingRegistry
{
    private readonly ConcurrentDictionary<KeyGesture, string> _bindings = new();
    private readonly Dictionary<string, List<KeyGesture>> _commandToGestures = new();

    /// <summary>Bind a key gesture to a command.</summary>
    public void Bind(KeyGesture gesture, string commandId)
    {
        _bindings[gesture] = commandId;
        if (!_commandToGestures.TryGetValue(commandId, out var list))
        {
            list = new List<KeyGesture>();
            _commandToGestures[commandId] = list;
        }
        if (!list.Contains(gesture))
            list.Add(gesture);
    }

    public void Bind(Key key, ModifierKeys modifiers, string commandId)
        => Bind(new KeyGesture(key, modifiers), commandId);

    /// <summary>Get the command ID bound to a gesture, or null if unbound.</summary>
    public string? GetCommandId(KeyGesture gesture)
    {
        _bindings.TryGetValue(gesture, out var commandId);
        return commandId;
    }

    /// <summary>Get all gestures that trigger a given command.</summary>
    public IReadOnlyList<KeyGesture> GetGestures(string commandId)
    {
        if (_commandToGestures.TryGetValue(commandId, out var list))
            return list;
        return Array.Empty<KeyGesture>();
    }

    public bool Unbind(KeyGesture gesture)
    {
        if (!_bindings.TryRemove(gesture, out var commandId)) return false;
        if (_commandToGestures.TryGetValue(commandId, out var list))
            list.Remove(gesture);
        return true;
    }

    public IReadOnlyDictionary<KeyGesture, string> All => _bindings;
    public void Clear()
    {
        _bindings.Clear();
        _commandToGestures.Clear();
    }
}
