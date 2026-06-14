using System.Runtime.CompilerServices;
using System.Text;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Import;

namespace TradingStudio.Data.Engine;

/// <summary>
/// Tick 回放数据源 — K-way merge CSV，按时间顺序回放 TickRecord。
/// 同时通过 BarAggregator 合成 Bar → 产出 BarEvent。
/// 用于精确策略验证。
/// </summary>
public class HistoricalTickFeed : IDataFeed, IDisposable
{
    private readonly string _dataDir;
    private readonly Encoding _gbk;
    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];
    private readonly List<IDisposable> _disposables = new();

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;

    public HistoricalTickFeed(string dataDir)
    {
        _dataDir = dataDir;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _gbk = Encoding.GetEncoding(936);
    }

    public void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments)
    {
        _startTime = startTime;
        _endTime = endTime;
        _instruments = instruments;
    }

    public async IAsyncEnumerable<DataEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_instruments.Count == 0) yield break;

        // 1. 扫描目录，为每个 instrument 收集 CSV 文件
        var csvFiles = Directory.GetFiles(_dataDir, "*.csv", SearchOption.AllDirectories);
        var instrumentFiles = new Dictionary<string, List<string>>();
        foreach (var file in csvFiles)
        {
            try
            {
                var (symbol, day) = CsvTickImporter.ParseRarFileName(file);
                if (_instruments.Contains(symbol) && day >= DateOnly.FromDateTime(_startTime) && day <= DateOnly.FromDateTime(_endTime))
                {
                    if (!instrumentFiles.ContainsKey(symbol))
                        instrumentFiles[symbol] = new();
                    instrumentFiles[symbol].Add(file);
                }
            }
            catch { /* skip unparseable filenames */ }
        }

        // 2. 为每个 instrument 打开第一个 CSV 的枚举器
        var readers = new List<TickReader>();
        foreach (var (inst, files) in instrumentFiles)
        {
            var sorted = files.OrderBy(f => f).ToList();
            var reader = new TickReader(inst, sorted, _gbk, _disposables);
            if (reader.MoveNext())
                readers.Add(reader);
            else
                reader.Dispose();
        }

        if (readers.Count == 0) yield break;

        // 3. 聚合器：Tick → Bar
        using var barAgg = new BarAggregator();
        var barQueue = new Queue<BarEvent>();
        barAgg.OnBar += b =>
        {
            lock (barQueue)
                barQueue.Enqueue(new BarEvent
                {
                    Bar = b,
                    Time = new DateTimeOffset(b.BarTime, TimeSpan.Zero),
                    IsNewBar = true,
                });
        };

        // 4. K-way merge 主循环
        while (readers.Count > 0 && !ct.IsCancellationRequested)
        {
            // 找最早的下一条 Tick
            TickReader? earliest = null;
            for (int i = 0; i < readers.Count; i++)
            {
                if (earliest == null || readers[i].Current!.Value.Tick.ExchangeTimestamp < earliest.Current!.Value.Tick.ExchangeTimestamp)
                    earliest = readers[i];
            }

            if (earliest == null) break;

            var tickResult = earliest.Current!.Value;
            yield return new TickEvent
            {
                Tick = tickResult.Tick,
                InstrumentId = tickResult.InstrumentId,
                TradingDay = tickResult.TradingDay,
                Time = DateTimeOffset.FromUnixTimeMilliseconds(tickResult.Tick.ExchangeTimestamp),
            };

            // 喂 BarAggregator
            barAgg.Feed(tickResult.Tick, tickResult.InstrumentId, tickResult.TradingDay);

            // 输出已闭合的 Bar
            lock (barQueue)
            {
                while (barQueue.Count > 0)
                    yield return barQueue.Dequeue();
            }

            // 推进该 reader
            if (!earliest.MoveNext())
            {
                readers.Remove(earliest);
                earliest.Dispose();
            }
        }

        // 5. Flush 剩余 Bar
        barAgg.Flush();
        lock (barQueue)
        {
            while (barQueue.Count > 0)
                yield return barQueue.Dequeue();
        }
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _disposables.Clear();
    }

    /// <summary>
    /// 单个 instrument 的多文件顺序读取器。
    /// 一个 CSV 读完后自动打开下一个（同 instrument 不同日期）。
    /// </summary>
    private class TickReader : IDisposable
    {
        private readonly string _instrumentId;
        private readonly List<string> _files;
        private readonly Encoding _gbk;
        private readonly List<IDisposable> _disposables;
        private int _fileIndex;
        private StreamReader? _currentStream;
        private IEnumerator<CsvTickImporter.ParseResult>? _currentEnumerator;

        public CsvTickImporter.ParseResult? Current { get; private set; }

        public TickReader(string instrumentId, List<string> files, Encoding gbk, List<IDisposable> disposables)
        {
            _instrumentId = instrumentId;
            _files = files;
            _gbk = gbk;
            _disposables = disposables;
        }

        public bool MoveNext()
        {
            while (true)
            {
                // 尝试当前枚举器
                if (_currentEnumerator != null && _currentEnumerator.MoveNext())
                {
                    Current = _currentEnumerator.Current;
                    return true;
                }

                // 当前文件读完，清理并打开下一个
                _currentEnumerator?.Dispose();
                _currentStream?.Dispose();

                if (_fileIndex >= _files.Count)
                {
                    Current = null;
                    return false;
                }

                var path = _files[_fileIndex++];
                // 跳过空文件
                if (new FileInfo(path).Length == 0) continue;

                try
                {
                    var (symbol, tradingDay) = CsvTickImporter.ParseRarFileName(path);
                    _currentStream = new StreamReader(path, _gbk, detectEncodingFromByteOrderMarks: false);
                    _disposables.Add(_currentStream);
                    _currentEnumerator = CsvTickImporter.Parse(_currentStream, symbol, tradingDay).GetEnumerator();
                }
                catch { /* skip corrupted files */ }
            }
        }

        public void Dispose()
        {
            _currentEnumerator?.Dispose();
            _currentStream?.Dispose();
        }
    }
}
