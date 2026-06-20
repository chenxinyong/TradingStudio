namespace TradingStudio.Terminal.Core.Input;

/// <summary>
/// Platform-independent modifier key flags.
/// </summary>
[Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}
