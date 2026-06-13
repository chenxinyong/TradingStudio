using System.Diagnostics;
using System.Text;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Storage;

namespace TradingStudio.Data.Import;

/// <summary>
/// 金数源 RAR 历史数据导入编排器。
/// 通过 UnRAR.exe 子进程实现 RAR 列表+流式提取，不依赖第三方 .NET 库。
/// </summary>
public class JinshuyuanImportService
{
    private static readonly Encoding Gbk;

    static JinshuyuanImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    private readonly JinshuyuanOptions _opts;
    private readonly string _unrarPath;

    public JinshuyuanImportService(JinshuyuanOptions opts)
    {
        _opts = opts;
        _unrarPath = FindUnrar();
    }

    public async Task ImportAsync(CancellationToken ct = default)
    {
        var rarFiles = DiscoverRarFiles();
        if (rarFiles.Count == 0)
        {
            Console.WriteLine($"No RAR files found in {_opts.DataDir}");
            return;
        }

        Console.WriteLine($"Found {rarFiles.Count} RAR files  Layer: {_opts.Layer}");
        Console.WriteLine($"UnRAR: {_unrarPath}");
        if (_opts.ExchangeCode is not null) Console.WriteLine($"Exchange: {_opts.ExchangeCode}");
        if (_opts.Symbols.Count > 0) Console.WriteLine($"Symbols: {string.Join(", ", _opts.Symbols)}");
        Console.WriteLine();

        if (_opts.DryRun) { await DryRunAsync(rarFiles, ct); return; }

        using var barStore = new BarStore(_opts.DbPath);
        long totalTicks = 0, totalBars = 0;
        int ok = 0, fail = 0;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < rarFiles.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var path = rarFiles[i];
            Console.WriteLine($"[{i + 1}/{rarFiles.Count}] {Path.GetFileName(path)}");

            try
            {
                var (t, b) = ProcessRar(path, barStore, ct);
                totalTicks += t; totalBars += b; ok++;
                Console.WriteLine($"  OK: {t:N0} ticks, {b:N0} bars");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { fail++; Console.WriteLine($"  FAIL: {ex.Message}"); }
            Console.WriteLine();
        }

        sw.Stop();
        Console.WriteLine("═══════════════════════════════════════");
        Console.WriteLine($"  Done: {ok}/{rarFiles.Count} RARs, {totalTicks:N0} ticks, {totalBars:N0} bars");
        Console.WriteLine($"  DB: {_opts.DbPath}  Time: {sw.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine("═══════════════════════════════════════");
    }

    /// <summary>处理单个 RAR: 列出条目 → 逐个提取到 stdout → 解析 → 聚合 → 写库</summary>
    private (long Ticks, long Bars) ProcessRar(string rarPath, BarStore barStore, CancellationToken ct)
    {
        using var barAgg = new BarAggregator();
        using var dayAgg = new DailyBarAggregator();
        var bars = new List<Bar>(8192);
        barAgg.OnBar += b => { lock (bars) bars.Add(b); };
        dayAgg.OnBar += b => { lock (bars) bars.Add(b); };

        long ticks = 0;
        int ok = 0, skip = 0;

        // 1. 列出 RAR 内所有文件
        var entries = ListRarEntries(rarPath);
        var matching = entries
            .Where(e => JinshuyuanEntryFilter.Matches(e, _opts))
            .ToList();

        Console.WriteLine($"  {matching.Count} matching of {entries.Count} entries");

        // 2. 逐个提取到 stdout → 流式解析
        foreach (var entry in matching)
        {
            if (ct.IsCancellationRequested) break;
            if (!TryParse(entry, out var meta)) { skip++; continue; }

            try
            {
                int n = 0;
                using var process = RunUnrarP(rarPath, entry);

                // 流式读取 UnRAR stdout (GBK CSV)，直接喂给解析器
                using var tr = new System.IO.StreamReader(process.StandardOutput.BaseStream, Gbk);
                foreach (var r in CsvTickImporter.Parse(tr, meta.ContractCode, meta.TradingDay))
                {
                    barAgg.Feed(r.Tick, r.InstrumentId, r.TradingDay);
                    dayAgg.Feed(r.Tick, r.InstrumentId, r.TradingDay);
                    ticks++; n++;
                }

                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    var err = process.StandardError.ReadToEnd();
                    throw new Exception($"UnRAR exit {process.ExitCode}: {err.Trim()}");
                }

                ok++;
                Console.Write($"\r  [{ok}/{matching.Count}] {entry}  {n:N0} ticks   ");
            }
            catch (Exception ex) { Console.WriteLine($"\n  SKIP {entry}: {ex.Message}"); }
        }

        barAgg.Flush(); dayAgg.FlushAll();
        Console.WriteLine();
        if (skip > 0) Console.WriteLine($"  {ok} imported, {skip} skipped");

        long bc = bars.Count;
        if (bc > 0) barStore.WriteBatchAsync(bars, ct).AsTask().Wait(ct);
        return (ticks, bc);
    }

    private async Task DryRunAsync(List<string> rarFiles, CancellationToken ct)
    {
        long tm = 0, ts = 0;

        for (int i = 0; i < rarFiles.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var path = rarFiles[i];
            Console.WriteLine($"[{i + 1}/{rarFiles.Count}] {Path.GetFileName(path)}");

            var entries = ListRarEntries(path);
            int m = 0, s = 0;
            foreach (var entry in entries)
            {
                if (JinshuyuanEntryFilter.Matches(entry, _opts))
                {
                    if (TryParse(entry, out var meta))
                        Console.WriteLine($"  [{meta.ExchangeCode}/{meta.ProductCode}] {entry}  {meta.TradingDay:yyyy-MM-dd}");
                    m++;
                }
                else s++;
            }
            Console.WriteLine($"  -> {m} match, {s} skip");
            tm += m; ts += s;
        }
        Console.WriteLine();
        Console.WriteLine($"Matching: {tm:N0}  Skipped: {ts:N0}");
        await Task.CompletedTask;
    }

    // ═══════════ UnRAR 子进程 ═══════════

    /// <summary>列出 RAR 内所有文件路径</summary>
    private List<string> ListRarEntries(string rarPath)
    {
        var result = new List<string>();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _unrarPath,
                Arguments = $"lb \"{rarPath}\" -p\"{_opts.Password}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                result.Add(line.Trim());
        }
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new Exception($"UnRAR list failed (exit {process.ExitCode}): {err.Trim()}");
        }

        return result;
    }

    /// <summary>启动 UnRAR p 命令，将指定文件输出到 stdout</summary>
    private Process RunUnrarP(string rarPath, string entryPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _unrarPath,
                // -inul 抑制 stderr 消息（文件内容走 stdout，干净分离）
                Arguments = $"p \"{rarPath}\" -p\"{_opts.Password}\" -inul \"{entryPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        return process;
    }

    // ═══════════ 辅助 ═══════════

    /// <summary>查找 UnRAR.exe</summary>
    private static string FindUnrar()
    {
        // 1. 项目 tools 目录
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "UnRAR.exe"),
            Path.Combine(AppContext.BaseDirectory, "UnRAR.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "UnRAR.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "UnRAR.exe"),
        };

        foreach (var p in paths)
            if (File.Exists(p)) return p;

        // 2. PATH 中搜索
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, "UnRAR.exe");
            if (File.Exists(p)) return p;
        }

        throw new FileNotFoundException(
            "UnRAR.exe not found. Place it in the tools/ directory or add to PATH.");
    }

    private static bool TryParse(string entry, out JinshuyuanEntryFilter.EntryMeta meta)
    {
        try
        {
            // UnRAR lb 输出用反斜杠，JinshuyuanEntryFilter 用正斜杠
            meta = JinshuyuanEntryFilter.ParseEntryPath(entry.Replace('\\', '/'));
            return true;
        }
        catch { meta = default; return false; }
    }

    private List<string> DiscoverRarFiles()
    {
        var files = new List<string>();
        if (!Directory.Exists(_opts.DataDir)) { Console.WriteLine($"Not found: {_opts.DataDir}"); return files; }

        int fy = int.Parse(_opts.FromMonth[..4]), ty = int.Parse(_opts.ToMonth[..4]);
        for (int y = fy; y <= ty; y++)
        {
            var dir = Path.Combine(_opts.DataDir, $"FutAC_TickKZ_CTP_Daily_{y}");
            if (!Directory.Exists(dir)) continue;
            foreach (var rar in Directory.GetFiles(dir, "*.rar"))
            {
                var name = Path.GetFileNameWithoutExtension(rar);
                var idx = name.LastIndexOf('_');
                if (idx < 0) continue;
                var ym = name[(idx + 1)..];
                if (ym.Length != 6) continue;
                if (string.Compare(ym, _opts.FromMonth, StringComparison.Ordinal) < 0) continue;
                if (string.Compare(ym, _opts.ToMonth, StringComparison.Ordinal) > 0) continue;
                files.Add(rar);
            }
        }
        files.Sort();
        return files;
    }
}
