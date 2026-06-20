namespace TradingStudio.Terminal.Core.Input;

/// <summary>
/// Platform-independent key enumeration.
/// Covers the most commonly used keys. Maps to WPF Key in the UI layer.
/// </summary>
public enum Key
{
    None = 0,
    Cancel, Back, Tab, LineFeed, Clear, Enter, Pause, Capital,
    Escape = 27,
    Space = 32,
    Prior, Next, End, Home, Left, Up, Right, Down, Select, Print, Execute, Snapshot, Insert, Delete, Help,
    D0 = 48, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    LWin = 91, RWin, Apps, Sleep,
    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    Multiply, Add, Separator, Subtract, Decimal, Divide,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    NumLock = 144, Scroll,
    LeftShift = 156, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt,
    BrowserBack, BrowserForward, BrowserRefresh, BrowserStop, BrowserSearch,
    BrowserFavorites, BrowserHome,
    VolumeMute, VolumeDown, VolumeUp,
    MediaNextTrack, MediaPreviousTrack, MediaStop, MediaPlayPause,
    LaunchMail, SelectMedia, LaunchApplication1, LaunchApplication2,
    OemSemicolon = 186, OemPlus = 187, OemComma = 188, OemMinus = 189,
    OemPeriod = 190, OemQuestion = 191, OemTilde = 192,
    OemOpenBrackets = 219, OemPipe = 220, OemCloseBrackets = 221,
    OemQuotes = 222, Oem8, OemBackslash = 226,
}
