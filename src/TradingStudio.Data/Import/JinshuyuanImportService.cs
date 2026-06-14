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
    private readonly string _tempRoot;

    public JinshuyuanImportService(JinshuyuanOptions opts)
    {
        _opts = opts;
        _unrarPath = FindUnrar();
        _tempRoot = opts.TempDir ?? Path.Combine(_opts.DataDir, "_import_temp");
        Directory.CreateDirectory(_tempRoot);
        Console.WriteLine($"Temp: {_tempRoot}");
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
        var progressFile = Path.ChangeExtension(_opts.DbPath, ".progress.json");

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

                // 写进度文件（独立于 stdout 缓冲，用于外部监控）
                WriteProgressFile(progressFile, i + 1, rarFiles.Count, totalTicks, totalBars, ok, fail,
                    Path.GetFileName(path), sw.Elapsed);
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

    /// <summary>
    /// 处理单个 RAR: 列出条目 → 批量解压到临时目录 → 遍历 CSV → 聚合 → 写库 → 清理。
    /// 整个 RAR 只需 1 次 UnRAR 调用（vs 旧方案每 CSV 一次，5000× 提速）。
    /// </summary>
    private (long Ticks, long Bars) ProcessRar(string rarPath, BarStore barStore, CancellationToken ct)
    {
        using var barAgg = new BarAggregator();
        using var dayAgg = new DailyBarAggregator();
        var bars = new List<Bar>(16384);
        barAgg.OnBar += b => { lock (bars) bars.Add(b); };
        dayAgg.OnBar += b => { lock (bars) bars.Add(b); };

        long ticks = 0;
        int ok = 0, skip = 0;

        // 1. 列出 RAR 内所有文件并过滤
        var allEntries = ListRarEntries(rarPath);
        var matching = allEntries
            .Where(e => JinshuyuanEntryFilter.Matches(e, _opts))
            .ToList();

        if (matching.Count == 0)
        {
            Console.WriteLine($"  No matching entries (of {allEntries.Count})");
            return (0, 0);
        }

        Console.WriteLine($"  {matching.Count} matching of {allEntries.Count} entries");
        Console.Out.Flush();

        // 2. 批量解压到临时目录
        var rarName = Path.GetFileNameWithoutExtension(rarPath);
        var tempDir = Path.Combine(_tempRoot, rarName);
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, true); }
            catch { Console.WriteLine($"  ⚠ Could not clean old temp dir, using alternative"); }
        }
        Directory.CreateDirectory(tempDir);

        try
        {
            Console.Write($"  Extracting {matching.Count} files... ");
            var sw = Stopwatch.StartNew();
            ExtractFiles(rarPath, matching, tempDir);
            Console.WriteLine($"{sw.Elapsed.TotalSeconds:F1}s");
            Console.Out.Flush();

            // 3. 遍历临时目录处理所有 CSV
            var csvFiles = Directory.GetFiles(tempDir, "*.csv", SearchOption.AllDirectories);
            Console.WriteLine($"  Processing {csvFiles.Length} CSVs...");
            Console.Out.Flush();

            foreach (var csvPath in csvFiles)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var (symbol, tradingDay) = CsvTickImporter.ParseRarFileName(csvPath);
                    int n = 0;

                    // 使用 TextReader 重载（手动传 symbol/tradingDay），
                    // 避免 Parse(string) 内部再调 ParseFileName 格式不匹配
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    var gbk = Encoding.GetEncoding(936);
                    using var reader = new StreamReader(csvPath, gbk, detectEncodingFromByteOrderMarks: false);
                    foreach (var r in CsvTickImporter.Parse(reader, symbol, tradingDay))
                    {
                        barAgg.Feed(r.Tick, r.InstrumentId, r.TradingDay);
                        dayAgg.Feed(r.Tick, r.InstrumentId, r.TradingDay);
                        ticks++; n++;
                    }
                    ok++;
                    if (ok % 500 == 0 || ok == csvFiles.Length)
                    {
                        Console.Write($"\r  [{ok}/{csvFiles.Length}] {Path.GetFileName(csvPath)}  {n:N0} ticks  cumulative: {ticks:N0} ticks, {bars.Count:N0} bars   ");
                        Console.Out.Flush();
                    }
                }
                catch (Exception ex)
                {
                    skip++;
                    if (skip <= 3) Console.WriteLine($"\n  SKIP {Path.GetFileName(csvPath)}: {ex.Message}");
                }
            }

            barAgg.Flush(); dayAgg.FlushAll();
            Console.WriteLine();
            if (skip > 0) Console.WriteLine($"  {ok} imported, {skip} skipped");

            long bc = bars.Count;
            if (bc > 0)
            {
                Console.Write($"  Writing {bc:N0} bars to DB... ");
                var dbSw = Stopwatch.StartNew();
                barStore.WriteBatchAsync(bars, ct).AsTask().Wait(ct);
                Console.WriteLine($"{dbSw.Elapsed.TotalSeconds:F1}s");
                Console.Out.Flush();
            }
            return (ticks, bc);
        }
        finally
        {
            // 4. 清理临时目录
            try { Directory.Delete(tempDir, true); }
            catch (Exception ex) { Console.WriteLine($"  ⚠ Cleanup failed: {ex.Message}"); }
        }
    }

    /// <summary>批量解压指定文件列表到临时目录</summary>
    private void ExtractFiles(string rarPath, List<string> entries, string destDir)
    {
        // 将待解压文件列表写入临时文件，通过 UnRAR @listfile 批量提取
        var listFile = Path.Combine(destDir, "_extract_list.txt");
        File.WriteAllLines(listFile, entries, Gbk);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _unrarPath,
                // x = extract with full path, -o+ = overwrite, @ = read file list
                Arguments = $"x \"{rarPath}\" -p\"{_opts.Password}\" -o+ -inul @\"{listFile}\" \"{destDir}\\\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new Exception($"UnRAR extract failed (exit {process.ExitCode}): {err.Trim()}");
        }
    }

    private async Task DryRunAsync(List<string> rarFiles, CancellationToken ct)
    {
        long tm = 0, ts = 0;

        for (int i = 0; i < rarFiles.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var path = rarFiles[i];
            Console.WriteLine($"[{i + 1}/{rarFiles.Count}] {Path.GetFileName(path)}");
            Console.Out.Flush();

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

    // ═══════════ 进度文件 ═══════════

    private static void WriteProgressFile(string filePath, int completed, int total,
        long ticks, long bars, int ok, int fail, string currentRar, TimeSpan elapsed)
    {
        try
        {
            var eta = completed > 0
                ? TimeSpan.FromTicks(elapsed.Ticks / completed * (total - completed))
                : TimeSpan.Zero;

            var json = $$"""
            {
              "updated": "{{DateTime.Now:yyyy-MM-dd HH:mm:ss}}",
              "progress": "{{completed}}/{{total}}",
              "percent": {{completed * 100.0 / total:F1}},
              "currentRar": "{{currentRar}}",
              "totalTicks": {{ticks}},
              "totalBars": {{bars}},
              "ok": {{ok}},
              "fail": {{fail}},
              "elapsed": "{{elapsed:hh\\:mm\\:ss}}",
              "eta": "{{eta:hh\\:mm\\:ss}}"
            }
            """;

            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { /* 进度文件写入失败不应中断导入 */ }
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
