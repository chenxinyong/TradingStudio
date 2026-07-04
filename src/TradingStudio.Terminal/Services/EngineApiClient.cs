using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace TradingStudio.Terminal.Services;

/// <summary>
/// 引擎 REST API 客户端 — 从引擎获取 Bar/品种列表/快照数据。
/// API 不可用时自动 fallback，WPF 不直接碰 DuckDB。
/// </summary>
public class EngineApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<EngineApiClient> _log;
    private string _baseUrl;

    public string BaseUrl => _baseUrl;
    public bool IsAvailable { get; private set; }

    public EngineApiClient(string baseUrl = "http://localhost:5199",
                           ILogger<EngineApiClient>? log = null)
    {
        _baseUrl = baseUrl;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineApiClient>.Instance;
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>探测引擎是否在线</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("/api/health", ct);
            IsAvailable = resp.IsSuccessStatusCode;
            return IsAvailable;
        }
        catch
        {
            IsAvailable = false;
            return false;
        }
    }

    /// <summary>获取品种的 Bar 数据</summary>
    public async Task<List<BarDto>?> GetBarsAsync(string instrumentId, string freq = "15min",
        CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/bars/{instrumentId}?freq={freq}";
            var bars = await _http.GetFromJsonAsync<List<BarDto>>(url, ct);
            if (bars != null && bars.Count > 0)
            {
                IsAvailable = true;
                _log.LogDebug("API returned {Count} bars for {Id} {Freq}", bars.Count, instrumentId, freq);
                return bars;
            }
            return null;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            _log.LogWarning(ex, "Engine API unavailable, will use DuckDB fallback");
            return null;
        }
    }

    /// <summary>获取可用品种列表</summary>
    public async Task<List<ProductInfo>?> GetProductsAsync(CancellationToken ct = default)
    {
        try
        {
            var url = "/api/products";
            var products = await _http.GetFromJsonAsync<List<ProductInfo>>(url, ct);
            IsAvailable = products != null;
            return products;
        }
        catch
        {
            IsAvailable = false;
            return null;
        }
    }
}

/// <summary>API 返回的 Bar 数据（与引擎 BarDto 字段对齐）</summary>
public record BarDto(DateTime Dt, double Open, double High, double Low, double Close, long Volume);

/// <summary>API 返回的品种信息</summary>
public record ProductInfo(string Code, string InstrumentId, long BarCount,
    string FirstBar, string LastBar);
