using System.Collections.Concurrent;

namespace TradingStudio.Live;

/// <summary>
/// 合约活跃度追踪器 — 在行情观察期内积累成交量、持仓量、沉淀资金，
/// 用于筛选高活跃度合约，减少 CTP 订阅量。
/// </summary>
public class ContractActivityTracker
{
    private readonly ConcurrentDictionary<string, ContractStats> _stats = new();

    /// <summary>观察期（秒），到期后可调用 GetActiveContracts 获取排名结果</summary>
    public int ObservationSeconds { get; }

    private DateTime? _startTime;
    private volatile bool _observationComplete;

    public bool IsComplete => _observationComplete;

    public ContractActivityTracker(int observationSeconds = 60)
    {
        ObservationSeconds = observationSeconds;
    }

    public void Start()
    {
        _startTime = DateTime.UtcNow;
        _observationComplete = false;
    }

    /// <summary>从 CTP Quote 提取活跃度数据并累积</summary>
    public void Feed(string instId, CTP.Quote quote)
    {
        if (_observationComplete) return;

        var stats = _stats.GetOrAdd(instId, _ => new ContractStats { InstrumentId = instId });

        // 成交量（累计最大，取最新快照值）
        if (quote.Volume > stats.MaxVolume)
            stats.MaxVolume = quote.Volume;

        // 持仓量（累计最大）
        if (quote.OpenInterest > stats.MaxOpenInterest)
            stats.MaxOpenInterest = quote.OpenInterest;

        // 最新价（持续更新）
        stats.LastPrice = quote.LastPrice;

        // Tick 到达次数（反映行情推送频率）
        Interlocked.Increment(ref stats.TickCount);

        // 检查观察期是否结束
        if (_startTime.HasValue && !_observationComplete)
        {
            var elapsed = (DateTime.UtcNow - _startTime.Value).TotalSeconds;
            if (elapsed >= ObservationSeconds)
                _observationComplete = true;
        }
    }

    /// <summary>
    /// 获取活跃合约排名，返回前 targetCount 个合约代码。
    /// 综合得分 = log(成交量+1) × log(持仓量+1) × log(沉淀资金+1) × sqrt(Tick到达次数)
    /// 即使观察期未结束也可调用，返回当前快照排名。
    /// </summary>
    public IReadOnlyList<string> GetActiveContracts(int? targetCount = null)
    {
        var ranked = _stats.Values
            .Where(s => s.MaxVolume > 0 || s.MaxOpenInterest > 0) // 排除完全无活动的合约
            .Select(s =>
            {
                var settledFunds = s.MaxOpenInterest * (double)s.LastPrice;
                var score = Math.Log(s.MaxVolume + 1)
                          * Math.Log(s.MaxOpenInterest + 1)
                          * Math.Log(settledFunds + 1)
                          * Math.Sqrt(s.TickCount + 1);
                return (s.InstrumentId, Score: score, s.MaxVolume, s.MaxOpenInterest, SettledFunds: settledFunds, s.TickCount);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var take = targetCount ?? Math.Max(100, ranked.Count / 2); // 默认取前 50%，最少 100
        return ranked.Take(take).Select(x => x.InstrumentId).ToList();
    }

    /// <summary>获取完整排名（用于日志输出 Top N）</summary>
    public IReadOnlyList<(string InstrumentId, double Score, int Volume, double OpenInterest, double SettledFunds, long TickCount)>
        GetRanked(int topN = 20)
    {
        return _stats.Values
            .Where(s => s.MaxVolume > 0 || s.MaxOpenInterest > 0)
            .Select(s =>
            {
                var settledFunds = s.MaxOpenInterest * (double)s.LastPrice;
                var score = Math.Log(s.MaxVolume + 1)
                          * Math.Log(s.MaxOpenInterest + 1)
                          * Math.Log(settledFunds + 1)
                          * Math.Sqrt(s.TickCount + 1);
                return (s.InstrumentId, Score: score, s.MaxVolume, s.MaxOpenInterest, SettledFunds: settledFunds, s.TickCount);
            })
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// 品种级聚合：将合约活跃度按品种汇总，返回 Top N 品种代码。
    /// 品种得分 = Σ(合约成交量) × Σ(合约持仓量)，体现品种的整体市场热度。
    /// </summary>
    public IReadOnlyList<string> GetTopProducts(int topN = 30)
    {
        // 合约 → 品种（ag2608 → ag, TA608 → TA）
        static string productOf(string instId)
        {
            if (string.IsNullOrEmpty(instId) || instId.Length < 2) return instId;
            // CZCE 短码: 2字母 + 1数字年代 + 2数字月份 → 前2字母
            // 其他: 字母前缀 + 4数字 → 取字母前缀
            var span = instId.AsSpan();
            int i = 0;
            while (i < span.Length && !char.IsDigit(span[i])) i++;
            return i > 0 ? span[..i].ToString() : instId;
        }

        var productScores = _stats.Values
            .Where(s => s.MaxVolume > 0 || s.MaxOpenInterest > 0)
            .GroupBy(s => productOf(s.InstrumentId))
            .Select(g =>
            {
                var totalVol = g.Sum(s => (long)s.MaxVolume);
                var totalOI = g.Sum(s => s.MaxOpenInterest);
                var maxPrice = g.Max(s => (double)s.LastPrice);
                // 品种得分: log(总成交量+1) × log(总持仓量+1) × log(沉淀资金+1)
                var settledFunds = totalOI * maxPrice;
                var score = Math.Log(totalVol + 1)
                          * Math.Log(totalOI + 1)
                          * Math.Log(settledFunds + 1);
                return (Product: g.Key, Score: score, TotalVol: totalVol, TotalOI: totalOI,
                        ContractCount: g.Count());
            })
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .ToList();

        return productScores.Select(x => x.Product).ToList();
    }

    /// <summary>获取品种级排名详情（用于日志）</summary>
    public IReadOnlyList<(string Product, double Score, long TotalVol, double TotalOI, int Contracts)>
        GetTopProductRanking(int topN = 30)
    {
        static string productOf(string instId)
        {
            var span = instId.AsSpan();
            int i = 0;
            while (i < span.Length && !char.IsDigit(span[i])) i++;
            return i > 0 ? span[..i].ToString() : instId;
        }

        return _stats.Values
            .Where(s => s.MaxVolume > 0 || s.MaxOpenInterest > 0)
            .GroupBy(s => productOf(s.InstrumentId))
            .Select(g =>
            {
                var totalVol = g.Sum(s => (long)s.MaxVolume);
                var totalOI = g.Sum(s => s.MaxOpenInterest);
                var maxPrice = g.Max(s => (double)s.LastPrice);
                var settledFunds = totalOI * maxPrice;
                var score = Math.Log(totalVol + 1) * Math.Log(totalOI + 1) * Math.Log(settledFunds + 1);
                return (Product: g.Key, Score: score, TotalVol: totalVol, TotalOI: totalOI, Contracts: g.Count());
            })
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .ToList();
    }
}

internal class ContractStats
{
    public string InstrumentId { get; set; } = "";
    public int MaxVolume;
    public double MaxOpenInterest;
    public double LastPrice;
    public long TickCount;
}
