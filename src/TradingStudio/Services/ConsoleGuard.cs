using System.Runtime.InteropServices;

namespace TradingStudio.Services;

/// <summary>
/// 控制台防护 — 禁用 conhost QuickEdit 模式。
/// QuickEdit 下一次鼠标划选会冻结所有 Console 写入，同步日志 sink 的调用线程
/// 会逐一卡死在 sink 锁上（2026-07-15 曾致 collect 阻塞 3h38m、丢失整个早盘）。
/// </summary>
internal static class ConsoleGuard
{
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>关闭 QuickEdit。任何失败（无控制台/重定向/非 Windows）均静默跳过，不影响启动。</summary>
    public static void DisableQuickEdit()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;

            var handle = GetStdHandle(STD_INPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;   // 无控制台

            if (!GetConsoleMode(handle, out var mode)) return;               // stdin 重定向 / Service 场景

            SetConsoleMode(handle, (mode & ~ENABLE_QUICK_EDIT) | ENABLE_EXTENDED_FLAGS); // 失败可忽略
        }
        catch
        {
            // 防御：控制台防护失败不能影响主流程
        }
    }
}
