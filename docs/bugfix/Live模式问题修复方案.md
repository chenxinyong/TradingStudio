# Live 模式问题诊断与修复方案

## 问题现状

### 问题 1: 没有订阅全品种数据，落盘错误

**根本原因**:
1. **订阅逻辑错误**: `CtpLiveFeed.cs:151` 使用 `_instruments` 订阅，但此列表来自 `EngineOptions.Instruments`
2. **数据源与落盘不一致**: `Program.cs:188` 设置了 `allInstruments`，但 `Initialize()` 时传给 `CtpLiveFeed` 的是这个列表，然而订阅可能未完整执行

**代码位置**:
```csharp
// Program.cs:188-194
var allInstruments = registry.All.Values.Select(f => f.Code).ToList();
// ❌ 问题: allInstruments 是品种代码(如"cu")，不是合约代码(如"cu2506")

var engineOptions = new EngineOptions {
	Instruments = allInstruments,  // ❌ 这里传的是品种代码，无法订阅
	...
};
```

```csharp
// CtpLiveFeed.cs:151-155
for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50) {
	mdApi.Subscribe(_instruments.Skip(i).Take(50).ToArray());
	// ❌ 问题: 如果 _instruments 是 ["cu", "au"]，CTP 无法识别这些品种代码
	await Task.Delay(200, ct);
}
```

### 问题 2: 程序不稳定

**根本原因**:
1. **Channel 满容量丢包**: `TryWrite()` 返回 false 时无处理
2. **资源未清理**: `BarAggregator` 和 `DailyBarAggregator` 未 Dispose
3. **异常捕获不足**: LiveDataCollector 异常时仅日志，未重启
4. **TickCsvWriter 未注入**: `Program.cs:161` 未创建 TickCsvWriter 实例

---

## 修复方案

### 修复 1: 正确生成和订阅合约代码

#### Step 1.1: 修复 Program.cs - 生成完整合约代码

```csharp
// 修改 Program.cs:188-194
// ❌ 旧代码
var allInstruments = registry.All.Values.Select(f => f.Code).ToList();

// ✅ 新代码: 使用 ContractCodeGenerator 生成所有活跃合约
var allInstrumentCodes = ContractCodeGenerator.GenerateAll(registry.All.Values).ToList();
Console.WriteLine($"[Live] Generated {allInstrumentCodes.Count} active contracts from {registry.All.Count} futures");

var engineOptions = new EngineOptions {
	StartTime = DateTime.Today,
	EndTime = DateTime.Today.AddDays(1),
	Instruments = allInstrumentCodes,  // ✅ 传入完整合约代码列表
	StartingCapital = startCapital,
	IsLive = true,
};
```

#### Step 1.2: 添加订阅日志

```csharp
// 修改 CtpLiveFeed.cs:151-155
_log.Information("Subscribing to {Count} instruments in batches of 50", _instruments.Count);

for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50) {
	var batch = _instruments.Skip(i).Take(50).ToArray();
	mdApi.Subscribe(batch);
	_log.Information("Subscribed batch {BatchNum}: {Count} instruments (total: {Total}/{Max})", 
		i / 50 + 1, batch.Length, Math.Min(i + 50, _instruments.Count), _instruments.Count);
	await Task.Delay(200, ct);
}

_log.Information("Subscription completed: {Count} instruments", _instruments.Count);
```

---

### 修复 2: 创建并注入 TickCsvWriter

```csharp
// 修改 Program.cs:150-160
// ✅ 新增: 创建 TickCsvWriter
var tickDataDir = cfg["Live:TickData"] ?? "TickData";
var tickWriter = new TickCsvWriter(tickDataDir);
builder.Services.AddSingleton(tickWriter);

// 修改: 数据持久化
var dbPath = cfg["Live:Database"] ?? "bars_live.db";
var useDuckDB = cfg["Live:UseDuckDB"]?.ToLowerInvariant() == "true";
IBarStore barStore = useDuckDB
	? new DuckDBStore(dbPath, enableTickPurge: true)
	: new SqliteBarStore(dbPath);
builder.Services.AddSingleton(barStore);
```

---

### 修复 3: Channel 背压处理

```csharp
// 修改 CtpLiveFeed.cs:100-101
// ❌ 旧代码
var merged = Channel.CreateBounded<(string InstId, TickRecord Tick, DateOnly TradingDay)>(8192);

// ✅ 新代码: 启用背压 (Wait 模式)
var options = new BoundedChannelOptions(8192) {
	FullMode = BoundedChannelFullMode.Wait,  // 满时阻塞写入，防止丢包
	SingleReader = true,
	SingleWriter = false
};
var merged = Channel.CreateBounded<(string InstId, TickRecord Tick, DateOnly TradingDay)>(options);
```

```csharp
// 修改 CtpLiveFeed.cs:130-132
// ❌ 旧代码
merged.Writer.TryWrite((instId, record, tradingDay));
PersistChannel.Writer.TryWrite((instId, q, tradingDay));

// ✅ 新代码: 异步写入 + 丢包告警
if (!merged.Writer.TryWrite((instId, record, tradingDay))) {
	Interlocked.Increment(ref _droppedTicks);
	_log.Warning("Tick dropped (merged channel full): {Inst}", instId);
}

if (!PersistChannel.Writer.TryWrite((instId, q, tradingDay))) {
	Interlocked.Increment(ref _droppedPersist);
	_log.Warning("Tick dropped (persist channel full): {Inst}", instId);
}
```

```csharp
// 新增字段到 CtpLiveFeed.cs
private long _droppedTicks;
private long _droppedPersist;
public long DroppedTicks => Interlocked.Read(ref _droppedTicks);
public long DroppedPersist => Interlocked.Read(ref _droppedPersist);
```

---

### 修复 4: LiveDataCollector 自动重启

```csharp
// 修改 LiveDataCollector.cs:41-100
protected override async Task ExecuteAsync(CancellationToken ct) {
	while (!ct.IsCancellationRequested) {  // ✅ 外层循环: 崩溃自动重启
		BarAggregator? barAgg = null;
		DailyBarAggregator? dailyAgg = null;

		try {
			barAgg = new BarAggregator();
			dailyAgg = new DailyBarAggregator();
			var barChannel = Channel.CreateBounded<Bar>(4096);

			barAgg.OnBar += bar => { 
				Interlocked.Increment(ref _barCount); 
				barChannel.Writer.TryWrite(bar); 
			};
			dailyAgg.OnBar += bar => barChannel.Writer.TryWrite(bar);

			// 后台写入 BarStore
			var writeTask = WriteLoop(barChannel.Reader, ct);

			var reader = _feed.PersistChannel.Reader;
			await foreach (var item in reader.ReadAllAsync(ct)) {
				var (instId, quote, tradingDay) = item;

				// 去重逻辑 (保持原样)
				var tickKey = $"{quote.UpdateTime}_{quote.UpdateMillisec}";
				if (_lastTickKey.TryGetValue(instId, out var prevKey) && prevKey == tickKey)
					continue;
				_lastTickKey[instId] = tickKey;

				Interlocked.Increment(ref _tickCount);

				// CSV 写入 (保持原样)
				_tickWriter?.Write(...);

				// Bar 聚合 (保持原样)
				var record = QuoteConverter.FromCTPQuote(quote);
				barAgg.Feed(record, instId, tradingDay);
				dailyAgg.Feed(record, instId, tradingDay);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			break;  // 正常退出
		}
		catch (Exception ex) {
			_log.Error(ex, "LiveDataCollector crashed - retrying in 10s...");
			try { await Task.Delay(10_000, ct); } catch { break; }
		}
		finally {
			// ✅ 清理资源
			barAgg?.Dispose();
			dailyAgg?.Dispose();
		}
	}
}
```

---

### 修复 5: 完善 health.json 指标

```csharp
// 修改 HealthMonitor.cs:13-38
public void Update(
	string status,
	long quoteCount,
	long barCount,
	long csvCount,
	long reconnectCount,
	string? session,
	DateTime? lastConnect,
	DateTime? lastQuote,
	DateTime? lastHealth,
	long droppedTicks = 0,      // ✅ 新增
	long droppedPersist = 0)    // ✅ 新增
{
	var h = new {
		timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
		status,
		session,
		quotes = quoteCount,
		bars = barCount,
		csv = csvCount,
		reconnects = reconnectCount,
		droppedTicks,           // ✅ 新增
		droppedPersist,         // ✅ 新增
		lastConnect = lastConnect?.ToString("yyyy-MM-dd HH:mm:ss"),
		lastQuote = lastQuote?.ToString("yyyy-MM-dd HH:mm:ss"),
		lastHealth = lastHealth?.ToString("yyyy-MM-dd HH:mm:ss"),
		uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss")
	};

	try {
		File.WriteAllText(_path, JsonSerializer.Serialize(h, new JsonSerializerOptions {
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		}));
	} catch (Exception ex) {
		// 忽略写入失败，不影响主流程
		Console.Error.WriteLine($"[HealthMonitor] Write failed: {ex.Message}");
	}
}
```

---

## 完整修改清单

### 文件 1: `src/TradingStudio/Program.cs`

**位置 1**: Line 188-194
```diff
- var allInstruments = registry.All.Values.Select(f => f.Code).ToList();
+ var allInstrumentCodes = ContractCodeGenerator.GenerateAll(registry.All.Values).ToList();
+ Console.WriteLine($"[Live] Generated {allInstrumentCodes.Count} active contracts from {registry.All.Count} futures");

  var engineOptions = new EngineOptions
  {
	  StartTime = DateTime.Today,
	  EndTime = DateTime.Today.AddDays(1),
-     Instruments = allInstruments,
+     Instruments = allInstrumentCodes,
	  StartingCapital = startCapital,
	  IsLive = true,
  };
```

**位置 2**: Line 150-160 (新增 TickCsvWriter)
```diff
+ // 数据持久化: TickCsvWriter
+ var tickDataDir = cfg["Live:TickData"] ?? "TickData";
+ var tickWriter = new TickCsvWriter(tickDataDir);
+ builder.Services.AddSingleton(tickWriter);
+
  // 数据持久化: BarStore
  var dbPath = cfg["Live:Database"] ?? "bars_live.db";
  ...
```

**位置 3**: Line 213-221 (更新 Instruments)
```diff
  engineOptions = new EngineOptions
  {
	  StartTime = DateTime.Today,
	  EndTime = DateTime.Today.AddDays(1),
-     Instruments = allInstruments,
+     Instruments = allInstrumentCodes,
	  StrategyConfigs = [strategyConfig],
	  ...
  };
```

---

### 文件 2: `src/TradingStudio/Live/CtpLiveFeed.cs`

**位置 1**: Line 15-20 (新增字段)
```diff
  private readonly Serilog.ILogger _log;
+ private long _droppedTicks;
+ private long _droppedPersist;
+ 
+ public long DroppedTicks => Interlocked.Read(ref _droppedTicks);
+ public long DroppedPersist => Interlocked.Read(ref _droppedPersist);
```

**位置 2**: Line 100 (Channel 背压)
```diff
- var merged = Channel.CreateBounded<(string InstId, TickRecord Tick, DateOnly TradingDay)>(8192);
+ var options = new BoundedChannelOptions(8192) {
+     FullMode = BoundedChannelFullMode.Wait,
+     SingleReader = true,
+     SingleWriter = false
+ };
+ var merged = Channel.CreateBounded<(string InstId, TickRecord Tick, DateOnly TradingDay)>(options);
```

**位置 3**: Line 130-132 (丢包告警)
```diff
- merged.Writer.TryWrite((instId, record, tradingDay));
- PersistChannel.Writer.TryWrite((instId, q, tradingDay));
+ if (!merged.Writer.TryWrite((instId, record, tradingDay))) {
+     Interlocked.Increment(ref _droppedTicks);
+     _log.Warning("Tick dropped (merged channel full): {Inst}", instId);
+ }
+ 
+ if (!PersistChannel.Writer.TryWrite((instId, q, tradingDay))) {
+     Interlocked.Increment(ref _droppedPersist);
+     _log.Warning("Tick dropped (persist channel full): {Inst}", instId);
+ }
```

**位置 4**: Line 151-155 (订阅日志)
```diff
+ _log.Information("Subscribing to {Count} instruments in batches of 50", _instruments.Count);
+ 
  for (int i = 0; i < _instruments.Count && !ct.IsCancellationRequested; i += 50)
  {
-     mdApi.Subscribe(_instruments.Skip(i).Take(50).ToArray());
+     var batch = _instruments.Skip(i).Take(50).ToArray();
+     mdApi.Subscribe(batch);
+     _log.Information("Subscribed batch {BatchNum}: {Count} instruments", i / 50 + 1, batch.Length);
	  await Task.Delay(200, ct);
  }
+ 
+ _log.Information("Subscription completed: {Count} instruments", _instruments.Count);
```

---

### 文件 3: `src/TradingStudio/Services/LiveDataCollector.cs`

**位置 1**: Line 41-100 (自动重启)
```diff
  protected override async Task ExecuteAsync(CancellationToken ct)
  {
+     while (!ct.IsCancellationRequested)
+     {
+         BarAggregator? barAgg = null;
+         DailyBarAggregator? dailyAgg = null;
+         
+         try
+         {
-             var barAgg = new BarAggregator();
-             var dailyAgg = new DailyBarAggregator();
+             barAgg = new BarAggregator();
+             dailyAgg = new DailyBarAggregator();
			  var barChannel = Channel.CreateBounded<Bar>(4096);

			  ... (保持原有逻辑)

+         }
+         catch (OperationCanceledException) when (ct.IsCancellationRequested)
+         {
+             break;
+         }
+         catch (Exception ex)
+         {
+             _log.Error(ex, "LiveDataCollector crashed - retrying in 10s...");
+             try { await Task.Delay(10_000, ct); } catch { break; }
+         }
+         finally
+         {
+             barAgg?.Dispose();
+             dailyAgg?.Dispose();
+         }
+     }
  }
```

---

### 文件 4: `src/TradingStudio/Services/HealthMonitor.cs`

**位置 1**: Line 13-38 (新增字段)
```diff
  public void Update(
	  string status,
	  long quoteCount,
	  long barCount,
	  long csvCount,
	  long reconnectCount,
	  string? session,
	  DateTime? lastConnect,
	  DateTime? lastQuote,
-     DateTime? lastHealth)
+     DateTime? lastHealth,
+     long droppedTicks = 0,
+     long droppedPersist = 0)
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
+         droppedTicks,
+         droppedPersist,
		  ...
	  };

-     File.WriteAllText(_path, JsonSerializer.Serialize(h, ...));
+     try {
+         File.WriteAllText(_path, JsonSerializer.Serialize(h, ...));
+     } catch (Exception ex) {
+         Console.Error.WriteLine($"[HealthMonitor] Write failed: {ex.Message}");
+     }
  }
```

---

## 测试验证步骤

### Step 1: 编译项目
```powershell
cd src/TradingStudio
dotnet build
```

### Step 2: 配置凭证
```powershell
# 编辑 appsettings.local.json
@"
{
  "Live": {
	"MdFront": "tcp://180.168.146.187:10211",
	"BrokerId": "9999",
	"UserId": "YOUR_USER_ID",
	"Password": "YOUR_PASSWORD",
	"SymbolsPath": "symbols.json",
	"Database": "bars_live.db",
	"TickData": "TickData"
  }
}
"@ | Out-File -Encoding utf8 appsettings.local.json
```

### Step 3: 启动测试
```powershell
dotnet run -- live
```

### Step 4: 实时监控
**终端 1: 查看日志**
```powershell
Get-Content log.txt -Wait -Tail 20
```

**终端 2: 监控 health.json**
```powershell
while ($true) {
	Clear-Host
	Get-Content health.json | ConvertFrom-Json | Format-List
	Start-Sleep -Seconds 5
}
```

**终端 3: 检查数据落盘**
```powershell
# 检查 Bar 数据库
sqlite3 bars_live.db "SELECT COUNT(*) FROM bars_1min;"

# 检查 CSV 文件
Get-ChildItem TickData -Recurse -Filter "*.csv" | Measure-Object | Select-Object Count
```

---

## 预期结果

### 正常运行指标
```json
{
  "timestamp": "2025-01-20 14:35:12",
  "status": "Connected",
  "session": "日盘",
  "quotes": 145823,
  "bars": 2847,
  "csv": 145823,
  "reconnects": 0,
  "droppedTicks": 0,        // ✅ 新增: 应为 0
  "droppedPersist": 0,      // ✅ 新增: 应为 0
  "lastConnect": "2025-01-20 09:00:15",
  "lastQuote": "2025-01-20 14:35:11",
  "lastHealth": "2025-01-20 14:35:12",
  "uptime": "0.05:35:00"
}
```

### 日志关键信息
```
09:00:15 [INF] [Live] Generated 487 active contracts from 87 futures
09:00:16 [INF] CTP: Connecting to tcp://180.168.146.187:10211...
09:00:17 [INF] CTP login OK TradingDay=20250120
09:00:18 [INF] Subscribing to 487 instruments in batches of 50
09:00:18 [INF] Subscribed batch 1: 50 instruments
09:00:19 [INF] Subscribed batch 2: 50 instruments
...
09:00:25 [INF] Subscribed batch 10: 37 instruments
09:00:25 [INF] Subscription completed: 487 instruments
09:00:26 [INF] First quote received: cu2506
```

### 数据验证
```sql
-- 检查 Bar 数据
SELECT InstrumentId, COUNT(*) as BarCount, 
	   MIN(BarTime) as FirstBar, MAX(BarTime) as LastBar
FROM bars_1min
GROUP BY InstrumentId
ORDER BY BarCount DESC
LIMIT 10;

-- 预期: 每个合约约 240 条 1min Bar (4小时交易)
```

```powershell
# 检查 CSV 文件
$today = Get-Date -Format "yyyyMMdd"
$csvDir = "TickData\$today"
if (Test-Path $csvDir) {
	$files = Get-ChildItem $csvDir -Filter "*.csv"
	Write-Host "CSV files: $($files.Count)"
	$files | Select-Object Name, @{N='Lines';E={(Get-Content $_.FullName).Count}} | Sort-Object Lines -Descending | Select-Object -First 10
}

# 预期: 100+ 个 CSV 文件，每个文件 1000+ 行 Tick
```

---

## 故障排查

### 问题: 订阅失败
**症状**: `quotes: 0` 持续不变

**排查**:
```powershell
# 1. 检查日志
Get-Content log.txt | Select-String "Subscribed batch"
# 应输出 10 条左右 "Subscribed batch N: 50 instruments"

# 2. 检查网络
Test-NetConnection -ComputerName 180.168.146.187 -Port 10211

# 3. 检查凭证
Get-Content appsettings.local.json | ConvertFrom-Json | Select-Object -ExpandProperty Live
```

---

### 问题: 数据丢包
**症状**: `droppedTicks > 0` 或 `bars << quotes / 60`

**排查**:
```powershell
# 1. 检查丢包告警
Get-Content log.txt | Select-String "Tick dropped"

# 2. 增大 Channel 容量
# 修改 CtpLiveFeed.cs:100
var options = new BoundedChannelOptions(16384) {  // 8192 → 16384
	FullMode = BoundedChannelFullMode.Wait
};

# 3. 检查磁盘 I/O
Get-Counter '\PhysicalDisk(*)\Disk Writes/sec'
```

---

### 问题: 程序崩溃
**症状**: `status: "Crashed"`, 进程退出

**排查**:
```powershell
# 1. 查看崩溃日志
Get-Content crash.log -Tail 50

# 2. 查看最后的异常
Get-Content log.txt | Select-String "ERROR"

# 3. 检查资源占用
Get-Process TradingStudio | Select-Object CPU, WS, PM
```

---

## 下一步优化

1. **实时监控面板** (Grafana + Prometheus)
2. **订单流控** (TokenBucket 限流)
3. **熔断机制** (连续亏损自动停止)
4. **主备热切换** (Keepalived + VIP)
5. **分布式追踪** (OpenTelemetry)

---

**文档版本**: v1.0  
**生成时间**: 2025-01-20  
**审核**: 待测试验证
