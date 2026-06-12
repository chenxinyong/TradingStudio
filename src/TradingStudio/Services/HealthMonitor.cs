using System.Text.Json;

namespace TradingStudio.Services;

/// <summary>
/// 健康监控 — 写 health.json，外部工具可轮询。
/// </summary>
public class HealthMonitor
{
    private readonly string _path;

    public HealthMonitor(string path = "health.json") => _path = path;

    public void Update(
        string status,          // "Connected" | "Disconnected" | "Reconnecting" | "Idle"
        long quoteCount,
        long barCount,
        long csvCount,
        long reconnectCount,
        string? session,        // "日盘" | "夜盘" | "休市"
        DateTime? lastConnect,
        DateTime? lastQuote,
        DateTime? lastHealth)
    {
        var h = new
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            status,
            session,
            quotes = quoteCount,
            bars = barCount,
            csv = csvCount,
            reconnects = reconnectCount,
            lastConnect = lastConnect?.ToString("yyyy-MM-dd HH:mm:ss"),
            lastQuote = lastQuote?.ToString("yyyy-MM-dd HH:mm:ss"),
            lastHealth = lastHealth?.ToString("yyyy-MM-dd HH:mm:ss"),
            uptime = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss")
        };

        File.WriteAllText(_path, JsonSerializer.Serialize(h, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }
}
