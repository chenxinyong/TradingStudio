using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingStudio.Core.Models;

namespace TradingStudio.Ctp;

/// <summary>
/// Adapts C++/CLI MdApi Tick → internal Channel{TickRecord} pipeline.
/// Routes by InstrumentID — one Channel per contract.
/// Thread-safe: event handlers are called from CTP callback thread.
/// </summary>
public class CtpMdAdapter : IDisposable
{
    private readonly CTP.MdApi _mdApi;
    private readonly ConcurrentDictionary<string, Channel<TickRecord>> _channels = new();
    private readonly Channel<TickRecord> _fallbackChannel;
    private long _tickCount;
    private bool _disposed;

    public event Action? OnConnected;
    public event Action<int>? OnDisconnected;
    public event Action<CTP.LoginInfo>? OnLogin;
    public event Action<CTP.CtpError>? OnError;

    public long TickCount => Interlocked.Read(ref _tickCount);

    public CtpMdAdapter(string flowDir = "./ctp_flow")
    {
        _fallbackChannel = Channel.CreateBounded<TickRecord>(10000);

        _mdApi = new CTP.MdApi();

        _mdApi.OnFrontConnected += () => OnConnected?.Invoke();
        _mdApi.OnFrontDisconnected += r => OnDisconnected?.Invoke(r);
        _mdApi.OnLogin += (err, info) =>
        {
            if (err is { ErrorID: 0 })
                OnLogin?.Invoke(info);
            else
                OnError?.Invoke(err);
        };
        _mdApi.OnTick += OnTick;
        _mdApi.OnError += (err, _) => OnError?.Invoke(err);
    }

    public void Connect(string frontAddr) => _mdApi.Connect(frontAddr);
    public void Login(string broker, string user, string password) => _mdApi.Login(broker, user, password);

    public int Subscribe(params string[] instruments) => _mdApi.Subscribe(instruments);

    public int Unsubscribe(params string[] instruments) => _mdApi.Unsubscribe(instruments);

    /// <summary>
    /// Get a ChannelReader for a specific instrument. Creates one on demand.
    /// </summary>
    public ChannelReader<TickRecord> GetChannel(string instrumentId)
    {
        var ch = _channels.GetOrAdd(instrumentId, _ => Channel.CreateBounded<TickRecord>(10000));
        return ch.Reader;
    }

    /// <summary>
    /// Fallback channel — receives all ticks for instruments without dedicated channels.
    /// </summary>
    public ChannelReader<TickRecord> FallbackReader => _fallbackChannel.Reader;

    /// <summary>
    /// Enumerate all tracked instrument IDs.
    /// </summary>
    public IEnumerable<string> SubscribedInstruments => _channels.Keys;

    private void OnTick(CTP.Tick tick)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _tickCount);

        var record = Convert(tick);

        // Route to instrument-specific channel, fallback to global channel
        if (tick.InstrumentID is not null && _channels.TryGetValue(tick.InstrumentID, out var ch))
        {
            ch.Writer.TryWrite(record);
        }
        _fallbackChannel.Writer.TryWrite(record);
    }

    private static TickRecord Convert(CTP.Tick t)
    {
        return new TickRecord
        {
            ExchangeTimestamp = t.ExchangeTimestamp,
            LocalTimestamp    = t.LocalTimestamp,
            LastPrice  = (long)(t.LastPrice * TickRecord.PriceScale),
            Volume     = t.Volume,
            Turnover   = t.Turnover,
            OpenInterest = t.OpenInterest,
            BidPrice1  = (long)(t.BidPrice1 * TickRecord.PriceScale),
            BidVolume1 = t.BidVolume1,
            AskPrice1  = (long)(t.AskPrice1 * TickRecord.PriceScale),
            AskVolume1 = t.AskVolume1,
            Flags = BuildFlags(t)
        };
    }

    private static int BuildFlags(CTP.Tick t)
    {
        int flags = 0;
        if (t.LastPrice >= t.UpperLimitPrice && t.UpperLimitPrice > 0)
            flags |= TickRecord.FLAG_UPPER_LIMIT;
        if (t.LastPrice <= t.LowerLimitPrice && t.LowerLimitPrice > 0)
            flags |= TickRecord.FLAG_LOWER_LIMIT;
        // Detect auction period: Volume == 0 but prices are updating
        if (t.Volume == 0)
            flags |= TickRecord.FLAG_AUCTION;
        // Open instant: first non-zero volume after auction
        // (detected by caller comparing previous state)
        return flags;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _mdApi.OnTick -= OnTick;
        _mdApi.Dispose();

        _fallbackChannel.Writer.Complete();
        foreach (var ch in _channels.Values)
            ch.Writer.Complete();
    }
}
