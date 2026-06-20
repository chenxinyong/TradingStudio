namespace TradingStudio.Terminal.Core.Input;

/// <summary>
/// Platform-independent key gesture: a key combined with modifier keys.
/// Used by TradingCommand and KeybindingRegistry.
/// Maps to WPF KeyGesture in the UI layer via InputAdapter.
/// </summary>
public class KeyGesture : IEquatable<KeyGesture>
{
    public Key Key { get; }
    public ModifierKeys Modifiers { get; }

    public KeyGesture(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        Key = key;
        Modifiers = modifiers;
    }

    /// <summary>
    /// Parse a gesture string like "Ctrl+S" or "Ctrl+Shift+K".
    /// </summary>
    public static KeyGesture? Parse(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return null;

        var parts = gesture.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = ModifierKeys.None;
        Key key = Key.None;

        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Alt;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Windows;
            else
                key = ParseKey(part);
        }

        return key != Key.None ? new KeyGesture(key, modifiers) : null;
    }

    private static Key ParseKey(string keyName)
    {
        if (keyName.Length == 1)
        {
            var c = keyName.ToUpperInvariant()[0];
            if (c >= 'A' && c <= 'Z') return Key.A + (c - 'A');
            if (c >= '0' && c <= '9') return Key.D0 + (c - '0');
        }
        if (keyName.StartsWith('F') && int.TryParse(keyName[1..], out var fNum) && fNum is >= 1 and <= 24)
            return Key.F1 + (fNum - 1);
        return keyName.ToLowerInvariant() switch
        {
            "escape" or "esc" => Key.Escape,
            "enter" or "return" => Key.Enter,
            "space" => Key.Space,
            "tab" => Key.Tab,
            "backspace" or "back" => Key.Back,
            "delete" or "del" => Key.Delete,
            "insert" or "ins" => Key.Insert,
            "home" => Key.Home, "end" => Key.End,
            "pageup" or "prior" => Key.Prior,
            "pagedown" or "next" => Key.Next,
            "up" => Key.Up, "down" => Key.Down, "left" => Key.Left, "right" => Key.Right,
            _ => Key.None
        };
    }

    /// <summary>
    /// Human-readable string like "Ctrl+S".
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(),
            >= Key.A and <= Key.Z => ((char)('A' + (Key - Key.A))).ToString(),
            >= Key.F1 and <= Key.F24 => $"F{(int)(Key - Key.F1) + 1}",
            _ => Key.ToString()
        });
        return string.Join("+", parts);
    }

    public bool Equals(KeyGesture? other)
    {
        if (other is null) return false;
        return Key == other.Key && Modifiers == other.Modifiers;
    }

    public override bool Equals(object? obj) => Equals(obj as KeyGesture);
    public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
}
