using System.Windows;
using System.Windows.Input;
using TradingStudio.Terminal.Core.Commands;
using CoreGesture = TradingStudio.Terminal.Core.Input.KeyGesture;
using CoreKey = TradingStudio.Terminal.Core.Input.Key;
using CoreModifiers = TradingStudio.Terminal.Core.Input.ModifierKeys;
using WpfKeyBinding = System.Windows.Input.KeyBinding;

namespace TradingStudio.Terminal.Commands;

/// <summary>
/// Converts between our platform-independent input types and WPF input types.
/// </summary>
public static class InputAdapter
{
    public static System.Windows.Input.KeyGesture ToWpfGesture(CoreGesture gesture)
    {
        var key = ToWpfKey(gesture.Key);
        var modifiers = ToWpfModifierKeys(gesture.Modifiers);
        return new System.Windows.Input.KeyGesture(key, modifiers);
    }

    public static System.Windows.Input.Key ToWpfKey(CoreKey key)
    {
        return key switch
        {
            CoreKey.None => System.Windows.Input.Key.None,
            CoreKey.Back => System.Windows.Input.Key.Back,
            CoreKey.Tab => System.Windows.Input.Key.Tab,
            CoreKey.Enter => System.Windows.Input.Key.Enter,
            CoreKey.Escape => System.Windows.Input.Key.Escape,
            CoreKey.Space => System.Windows.Input.Key.Space,
            CoreKey.End => System.Windows.Input.Key.End,
            CoreKey.Home => System.Windows.Input.Key.Home,
            CoreKey.Left => System.Windows.Input.Key.Left,
            CoreKey.Up => System.Windows.Input.Key.Up,
            CoreKey.Right => System.Windows.Input.Key.Right,
            CoreKey.Down => System.Windows.Input.Key.Down,
            CoreKey.Insert => System.Windows.Input.Key.Insert,
            CoreKey.Delete => System.Windows.Input.Key.Delete,
            >= CoreKey.D0 and <= CoreKey.D9 => System.Windows.Input.Key.D0 + (key - CoreKey.D0),
            >= CoreKey.A and <= CoreKey.Z => System.Windows.Input.Key.A + (key - CoreKey.A),
            >= CoreKey.NumPad0 and <= CoreKey.NumPad9 => System.Windows.Input.Key.NumPad0 + (key - CoreKey.NumPad0),
            >= CoreKey.F1 and <= CoreKey.F24 => System.Windows.Input.Key.F1 + (key - CoreKey.F1),
            CoreKey.OemSemicolon => System.Windows.Input.Key.OemSemicolon,
            CoreKey.OemPlus => System.Windows.Input.Key.OemPlus,
            CoreKey.OemComma => System.Windows.Input.Key.OemComma,
            CoreKey.OemMinus => System.Windows.Input.Key.OemMinus,
            CoreKey.OemPeriod => System.Windows.Input.Key.OemPeriod,
            CoreKey.OemQuestion => System.Windows.Input.Key.OemQuestion,
            CoreKey.OemTilde => System.Windows.Input.Key.OemTilde,
            CoreKey.OemOpenBrackets => System.Windows.Input.Key.OemOpenBrackets,
            CoreKey.OemPipe => System.Windows.Input.Key.OemPipe,
            CoreKey.OemCloseBrackets => System.Windows.Input.Key.OemCloseBrackets,
            CoreKey.OemQuotes => System.Windows.Input.Key.OemQuotes,
            CoreKey.OemBackslash => System.Windows.Input.Key.OemBackslash,
            _ => System.Windows.Input.Key.None
        };
    }

    public static System.Windows.Input.ModifierKeys ToWpfModifierKeys(CoreModifiers modifiers)
    {
        var result = System.Windows.Input.ModifierKeys.None;
        if (modifiers.HasFlag(CoreModifiers.Control)) result |= System.Windows.Input.ModifierKeys.Control;
        if (modifiers.HasFlag(CoreModifiers.Alt)) result |= System.Windows.Input.ModifierKeys.Alt;
        if (modifiers.HasFlag(CoreModifiers.Shift)) result |= System.Windows.Input.ModifierKeys.Shift;
        if (modifiers.HasFlag(CoreModifiers.Windows)) result |= System.Windows.Input.ModifierKeys.Windows;
        return result;
    }

    public static CoreGesture FromWpfKeyEventArgs(System.Windows.Input.KeyEventArgs e)
    {
        var wpfKey = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var key = ToCoreKey(wpfKey);
        var modifiers = CoreModifiers.None;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            modifiers |= CoreModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt))
            modifiers |= CoreModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            modifiers |= CoreModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows))
            modifiers |= CoreModifiers.Windows;
        return new CoreGesture(key, modifiers);
    }

    private static CoreKey ToCoreKey(System.Windows.Input.Key wpfKey)
    {
        return wpfKey switch
        {
            System.Windows.Input.Key.None => CoreKey.None,
            System.Windows.Input.Key.Back => CoreKey.Back,
            System.Windows.Input.Key.Tab => CoreKey.Tab,
            System.Windows.Input.Key.Enter => CoreKey.Enter,
            System.Windows.Input.Key.Escape => CoreKey.Escape,
            System.Windows.Input.Key.Space => CoreKey.Space,
            System.Windows.Input.Key.End => CoreKey.End,
            System.Windows.Input.Key.Home => CoreKey.Home,
            System.Windows.Input.Key.Left => CoreKey.Left,
            System.Windows.Input.Key.Up => CoreKey.Up,
            System.Windows.Input.Key.Right => CoreKey.Right,
            System.Windows.Input.Key.Down => CoreKey.Down,
            System.Windows.Input.Key.Insert => CoreKey.Insert,
            System.Windows.Input.Key.Delete => CoreKey.Delete,
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => CoreKey.D0 + (wpfKey - System.Windows.Input.Key.D0),
            >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z => CoreKey.A + (wpfKey - System.Windows.Input.Key.A),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => CoreKey.NumPad0 + (wpfKey - System.Windows.Input.Key.NumPad0),
            >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24 => CoreKey.F1 + (wpfKey - System.Windows.Input.Key.F1),
            System.Windows.Input.Key.OemSemicolon => CoreKey.OemSemicolon,
            System.Windows.Input.Key.OemPlus => CoreKey.OemPlus,
            System.Windows.Input.Key.OemComma => CoreKey.OemComma,
            System.Windows.Input.Key.OemMinus => CoreKey.OemMinus,
            System.Windows.Input.Key.OemPeriod => CoreKey.OemPeriod,
            System.Windows.Input.Key.OemQuestion => CoreKey.OemQuestion,
            System.Windows.Input.Key.OemTilde => CoreKey.OemTilde,
            System.Windows.Input.Key.OemOpenBrackets => CoreKey.OemOpenBrackets,
            System.Windows.Input.Key.OemPipe => CoreKey.OemPipe,
            System.Windows.Input.Key.OemCloseBrackets => CoreKey.OemCloseBrackets,
            System.Windows.Input.Key.OemQuotes => CoreKey.OemQuotes,
            System.Windows.Input.Key.OemBackslash => CoreKey.OemBackslash,
            _ => CoreKey.None
        };
    }

    /// <summary>
    /// Create a WPF KeyBinding from a TradingCommand's default gesture.
    /// </summary>
    public static WpfKeyBinding? CreateWpfKeyBinding(TradingCommand command)
    {
        if (command.DefaultGesture == null) return null;
        try
        {
            var wpfGesture = ToWpfGesture(command.DefaultGesture);
            return new WpfKeyBinding
            {
                Gesture = wpfGesture,
                Command = new WpfCommandAdapter(command)
            };
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Adapts TradingCommand to WPF's ICommand for use with KeyBinding and Button.
/// </summary>
public class WpfCommandAdapter : ICommand
{
    private readonly TradingCommand _command;

    public WpfCommandAdapter(TradingCommand command) => _command = command;

    public bool CanExecute(object? parameter) => _command.CanExecute(parameter);

    public void Execute(object? parameter) => _ = _command.ExecuteAsync(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
