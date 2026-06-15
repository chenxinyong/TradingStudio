# 缠论量化规格书

> 对着这份文档直接写代码。Python 和 C# 双语言参考实现。
>
> C# 实现目标命名空间：`TradingStudio.Strategy.ChanLun`

---

## 0. 输入规范与参数

### 0.1 K线数据格式

```
输入：按时间升序排列的等周期K线数组
周期：1min / 5min / 15min / 30min / 60min / Day

每条K线的必选字段：
  dt:      DateTime     -- K线时间
  open:    decimal
  high:    decimal
  low:     decimal
  close:   decimal
  vol:     long         -- 成交量（仅用于聚合，核心算法不消费）
```

### 0.2 参数

```python
# Python
MIN_BI_LEN = 7
MAX_BI_NUM = 50
ZS_MIN_OVERLAP = 1
DIVERGENCE_RATIO = 0.5
```

```csharp
// C#
public static class ChanLunConfig
{
    public const int MinBiLen = 7;
    public const int MaxBiNum = 50;
    public const decimal ZsMinOverlap = 1m;
    public const double DivergenceRatio = 0.5;
}
```

---

## 1. 包含处理

### 1.1 定义

```
两根K线K_a, K_b（K_a在前），存在包含关系当：
  (K_a.high >= K_b.high AND K_a.low <= K_b.low)   -- K_a包含K_b
  OR
  (K_b.high >= K_a.high AND K_b.low <= K_a.low)   -- K_b包含K_a
```

### 1.2 方向判定

```
取最近两根已确认的非包含K线 prev, curr（prev在前）：
  direction = UP     if curr.high > prev.high
  direction = DOWN   if curr.high < prev.high
  direction = PREV   if curr.high == prev.high（沿用上一次的方向）
```

### 1.3 合并规则

```
给定K_a（前）、K_b（后），K_a包含K_b：

  UP方向:
      新高 = max(K_a.high, K_b.high)
      新低 = max(K_a.low,  K_b.low)

  DOWN方向:
      新高 = min(K_a.high, K_b.high)
      新低 = min(K_a.low,  K_b.low)

  新K线 = (dt=K_b.dt, open=K_a.open, close=K_b.close,
           high=新高, low=新低, vol=K_a.vol + K_b.vol)
```

### 1.4 全序列处理

```python
# Python
def has_inclusion(a, b):
    return (a.high >= b.high and a.low <= b.low) or \
           (b.high >= a.high and b.low <= a.low)

def process_inclusions(raw_bars):
    direction = None
    result = []
    pending = None

    for bar in raw_bars:
        if pending is None:
            pending = bar
            continue

        if not has_inclusion(pending, bar):
            if len(result) >= 1:
                prev = result[-1]
                if bar.high > prev.high:
                    direction = "UP"
                elif bar.high < prev.high:
                    direction = "DOWN"
            result.append(pending)
            pending = bar
        else:
            if len(result) >= 1:
                prev = result[-1]
                if pending.high > prev.high:
                    direction = "UP"
                elif pending.high < prev.high:
                    direction = "DOWN"
            d = direction or "UP"
            if d == "UP":
                pending = Bar(dt=bar.dt, open=pending.open, close=bar.close,
                              high=max(pending.high, bar.high),
                              low=max(pending.low, bar.low),
                              vol=pending.vol + bar.vol)
            else:
                pending = Bar(dt=bar.dt, open=pending.open, close=bar.close,
                              high=min(pending.high, bar.high),
                              low=min(pending.low, bar.low),
                              vol=pending.vol + bar.vol)

    if pending is not None:
        result.append(pending)
    return result
```

```csharp
// C#
public enum Direction { Up, Down }

public static bool HasInclusion(Bar a, Bar b) =>
    (a.High >= b.High && a.Low <= b.Low) ||
    (b.High >= a.High && b.Low <= a.Low);

public static List<Bar> ProcessInclusions(List<Bar> rawBars)
{
    Direction? direction = null;
    var result = new List<Bar>();
    Bar pending = null;

    foreach (var bar in rawBars)
    {
        if (pending == null)
        {
            pending = bar;
            continue;
        }

        if (!HasInclusion(pending, bar))
        {
            if (result.Count >= 1)
            {
                var prev = result[^1];
                direction = bar.High > prev.High ? Direction.Up
                           : bar.High < prev.High ? Direction.Down
                           : direction;
            }
            result.Add(pending);
            pending = bar;
        }
        else
        {
            if (result.Count >= 1)
            {
                var prev = result[^1];
                direction = pending.High > prev.High ? Direction.Up
                           : pending.High < prev.High ? Direction.Down
                           : direction;
            }
            var d = direction ?? Direction.Up;
            pending = d == Direction.Up
                ? pending with { Dt = bar.Dt, Close = bar.Close,
                      High = Math.Max(pending.High, bar.High),
                      Low  = Math.Max(pending.Low,  bar.Low),
                      Vol  = pending.Vol + bar.Vol }
                : pending with { Dt = bar.Dt, Close = bar.Close,
                      High = Math.Min(pending.High, bar.High),
                      Low  = Math.Min(pending.Low,  bar.Low),
                      Vol  = pending.Vol + bar.Vol };
        }
    }

    if (pending != null) result.Add(pending);
    return result;
}
```

### 1.5 验证断言

```
输出断言：
  1. result.Count <= rawBars.Count
  2. 任意相邻两根标准K线 !HasInclusion(result[i], result[i+1])
  3. 时间单调递增
```

---

## 2. 分型识别

### 2.1 数据结构

```python
# Python
@dataclass
class Fractal:
    type: str          # "TOP" | "BOTTOM"
    index: int         # 在标准K线数组中的位置
    price: Decimal     # TOP=K线.high, BOTTOM=K线.low
    dt: datetime
```

```csharp
// C#
public enum FractalType { Top, Bottom }

public record Fractal(
    FractalType Type,
    int Index,          // 在标准K线数组中的位置
    decimal Price,      // Top=K线.High, Bottom=K线.Low
    DateTime Dt
);
```

### 2.2 识别规则

```python
# Python
def find_fractals(std_bars):
    fractals = []
    for i in range(1, len(std_bars) - 1):
        a, b, c = std_bars[i-1], std_bars[i], std_bars[i+1]

        # 顶分型
        if (b.high > a.high and b.high > c.high and
            b.low  > a.low  and b.low  > c.low):
            fractals.append(Fractal("TOP", i, b.high, b.dt))

        # 底分型
        elif (b.low  < a.low  and b.low  < c.low and
              b.high < a.high and b.high < c.high):
            fractals.append(Fractal("BOTTOM", i, b.low, b.dt))

    return fractals
```

```csharp
// C#
public static List<Fractal> FindFractals(List<Bar> stdBars)
{
    var fractals = new List<Fractal>();
    for (int i = 1; i < stdBars.Count - 1; i++)
    {
        var a = stdBars[i - 1]; var b = stdBars[i]; var c = stdBars[i + 1];

        // 顶分型
        if (b.High > a.High && b.High > c.High &&
            b.Low  > a.Low  && b.Low  > c.Low)
            fractals.Add(new Fractal(FractalType.Top, i, b.High, b.Dt));

        // 底分型
        else if (b.Low  < a.Low  && b.Low  < c.Low &&
                 b.High < a.High && b.High < c.High)
            fractals.Add(new Fractal(FractalType.Bottom, i, b.Low, b.Dt));
    }
    return fractals;
}
```

### 2.3 分型去重

```
相邻同类型分型 → 保留极值更极端者：
  连续两个TOP → 保留 Price 更高的，删除另一个
  连续两个BOTTOM → 保留 Price 更低的，删除另一个
```

```python
# Python
def deduplicate_fractals(fractals):
    if len(fractals) < 2:
        return fractals

    result = [fractals[0]]
    for f in fractals[1:]:
        last = result[-1]
        if f.type == last.type:
            if f.type == "TOP" and f.price > last.price:
                result[-1] = f
            elif f.type == "BOTTOM" and f.price < last.price:
                result[-1] = f
        else:
            result.append(f)
    return result
```

```csharp
// C#
public static List<Fractal> DeduplicateFractals(List<Fractal> fractals)
{
    if (fractals.Count < 2) return fractals;

    var result = new List<Fractal> { fractals[0] };
    foreach (var f in fractals.Skip(1))
    {
        var last = result[^1];
        if (f.Type == last.Type)
        {
            if (f.Type == FractalType.Top && f.Price > last.Price)
                result[^1] = f;
            else if (f.Type == FractalType.Bottom && f.Price < last.Price)
                result[^1] = f;
        }
        else result.Add(f);
    }
    return result;
}
```

### 2.4 验证断言

```
1. 序列中 Top 和 Bottom 严格交替
2. Top.Price == 对应K线.High
3. Bottom.Price == 对应K线.Low
```

---

## 3. 笔

### 3.1 数据结构

```python
# Python
@dataclass
class Bi:
    type: str          # "UP" | "DOWN"
    start_fx: Fractal
    end_fx: Fractal
    start_idx: int
    end_idx: int
    bar_count: int     # 包含的标准K线数
    low: Decimal
    high: Decimal
    dt_start: datetime
    dt_end: datetime
```

```csharp
// C#
public enum BiType { Up, Down }

public record Bi(
    BiType Type,
    Fractal StartFx,
    Fractal EndFx,
    int StartIdx,
    int EndIdx,
    int BarCount,
    decimal Low,
    decimal High,
    DateTime DtStart,
    DateTime DtEnd
);
```

### 3.2 划分规则

```
一笔成立：
  1. start_fx 和 end_fx 类型不同
  2. end_idx - start_idx + 1 >= MIN_BI_LEN（默认7）
  3. Up笔: end_fx.Price > start_fx.Price
     Down笔: start_fx.Price > end_fx.Price
```

```python
# Python
def build_bis(fractals, std_bars):
    if len(fractals) < 2:
        return []

    bis = []
    i = 0
    while i < len(fractals) - 1:
        start, end = fractals[i], fractals[i+1]

        if start.type == end.type:
            i += 1; continue

        kline_count = end.index - start.index + 1
        if kline_count < MIN_BI_LEN:
            i += 1; continue

        bi_type = "UP" if (start.type == "BOTTOM" and end.type == "TOP") else "DOWN"

        seg = std_bars[start.index : end.index + 1]
        bi_high = max(b.high for b in seg)
        bi_low  = min(b.low  for b in seg)

        bis.append(Bi(bi_type, start, end, start.index, end.index,
                      kline_count, bi_low, bi_high, start.dt, end.dt))
        i += 1

    return bis
```

```csharp
// C#
public static List<Bi> BuildBis(List<Fractal> fractals, List<Bar> stdBars)
{
    if (fractals.Count < 2) return [];

    var bis = new List<Bi>();
    int i = 0;
    while (i < fractals.Count - 1)
    {
        var start = fractals[i];
        var end = fractals[i + 1];

        if (start.Type == end.Type) { i++; continue; }

        int klineCount = end.Index - start.Index + 1;
        if (klineCount < ChanLunConfig.MinBiLen) { i++; continue; }

        var biType = (start.Type == FractalType.Bottom && end.Type == FractalType.Top)
            ? BiType.Up : BiType.Down;

        var seg = stdBars.GetRange(start.Index, klineCount);
        var biHigh = seg.Max(b => b.High);
        var biLow  = seg.Min(b => b.Low);

        bis.Add(new Bi(biType, start, end, start.Index, end.Index,
                       klineCount, biLow, biHigh, start.Dt, end.Dt));
        i++;
    }
    return bis;
}
```

### 3.3 笔的力度指标

```python
# Python
def bi_power(bi):
    """涨跌幅度（万分比）"""
    if bi.type == "UP":
        return float(bi.high - bi.low) / float(bi.low) * 10000
    else:
        return float(bi.high - bi.low) / float(bi.high) * 10000
```

```csharp
// C#
public static double BiPower(Bi bi) => bi.Type == BiType.Up
    ? (double)(bi.High - bi.Low) / (double)bi.Low * 10000
    : (double)(bi.High - bi.Low) / (double)bi.High * 10000;
```

### 3.4 验证断言

```
1. 相邻笔方向相反 (Up→Down→Up→Down...)
2. 每笔 BarCount >= MinBiLen
3. Up笔: High>=Low 且 EndFx.Price > StartFx.Price
4. Down笔: High>=Low 且 StartFx.Price > EndFx.Price
```

---

## 4. 中枢

### 4.1 数据结构

```python
# Python
@dataclass
class Zhongshu:
    zg: Decimal            # 中枢上沿 = min(三笔高点中的最大值)
    zd: Decimal            # 中枢下沿 = max(三笔低点中的最小值)
    zz: Decimal            # 中枢中轨 = (zg+zd)/2
    start_bi_idx: int      # 第一笔在bi_list中的索引
    end_bi_idx: int        # 第三笔在bi_list中的索引
    bi_count: int          # 构成笔数（默认3，延伸后更大）
    level: int             # 中枢级别（1=笔中枢）
    dt_start: datetime
    dt_end: datetime
```

```csharp
// C#
public record Zhongshu(
    decimal Zg,            // 中枢上沿 = min(三笔高点中的最大值)
    decimal Zd,            // 中枢下沿 = max(三笔低点中的最小值)
    decimal Zz,            // 中枢中轨 = (Zg+Zd)/2
    int StartBiIdx,
    int EndBiIdx,
    int BiCount,
    int Level,             // 1=笔中枢
    DateTime DtStart,
    DateTime DtEnd
);
```

### 4.2 构建规则

```
连续三笔 bi[i], bi[i+1], bi[i+2]：

  ZG = min(bi[i].high, bi[i+1].high, bi[i+2].high)
  ZD = max(bi[i].low,  bi[i+1].low,  bi[i+2].low)

  if ZG > ZD + ZS_MIN_OVERLAP → 中枢成立
  else → 不构成中枢
```

```python
# Python
def build_zhongshus(bis):
    if len(bis) < 3: return []

    zs_list = []
    i = 0
    while i < len(bis) - 2:
        b1, b2, b3 = bis[i], bis[i+1], bis[i+2]
        zg = min(b1.high, b2.high, b3.high)
        zd = max(b1.low,  b2.low,  b3.low)

        if zg > zd + ZS_MIN_OVERLAP:
            zs = Zhongshu(zg=zg, zd=zd, zz=(zg+zd)/2,
                          start_bi_idx=i, end_bi_idx=i+2,
                          bi_count=3, level=1,
                          dt_start=b1.dt_start, dt_end=b3.dt_end)

            if zs_list and zs_overlap(zs_list[-1], zs):
                zs_list[-1] = merge_zhongshus(zs_list[-1], zs)
            else:
                zs_list.append(zs)
            i += 1
        else:
            i += 1
    return zs_list

def zs_overlap(zs1, zs2):
    return zs1.zg > zs2.zd and zs2.zg > zs1.zd

def merge_zhongshus(zs1, zs2):
    return Zhongshu(
        zg=max(zs1.zg, zs2.zg), zd=min(zs1.zd, zs2.zd),
        zz=(max(zs1.zg, zs2.zg) + min(zs1.zd, zs2.zd)) / 2,
        start_bi_idx=zs1.start_bi_idx, end_bi_idx=zs2.end_bi_idx,
        bi_count=zs1.bi_count + zs2.bi_count - 2,
        level=zs1.level, dt_start=zs1.dt_start, dt_end=zs2.dt_end)
```

```csharp
// C#
public static List<Zhongshu> BuildZhongshus(List<Bi> bis)
{
    if (bis.Count < 3) return [];

    var zsList = new List<Zhongshu>();
    int i = 0;
    while (i < bis.Count - 2)
    {
        var b1 = bis[i]; var b2 = bis[i+1]; var b3 = bis[i+2];
        var zg = Min(b1.High, b2.High, b3.High);
        var zd = Max(b1.Low,  b2.Low,  b3.Low);

        if (zg > zd + ChanLunConfig.ZsMinOverlap)
        {
            var zs = new Zhongshu(zg, zd, (zg+zd)/2, i, i+2, 3, 1,
                                  b1.DtStart, b3.DtEnd);

            if (zsList.Count > 0 && ZsOverlap(zsList[^1], zs))
                zsList[^1] = MergeZhongshus(zsList[^1], zs);
            else
                zsList.Add(zs);
            i++;
        }
        else i++;
    }
    return zsList;
}

private static bool ZsOverlap(Zhongshu a, Zhongshu b) =>
    a.Zg > b.Zd && b.Zg > a.Zd;

private static Zhongshu MergeZhongshus(Zhongshu a, Zhongshu b) =>
    new(
        Zg: Math.Max(a.Zg, b.Zg), Zd: Math.Min(a.Zd, b.Zd),
        Zz: (Math.Max(a.Zg, b.Zg) + Math.Min(a.Zd, b.Zd)) / 2,
        StartBiIdx: a.StartBiIdx, EndBiIdx: b.EndBiIdx,
        BiCount: a.BiCount + b.BiCount - 2,
        Level: a.Level, DtStart: a.DtStart, DtEnd: b.DtEnd
    );

// Helpers — C# 没有三个参数的 Math.Min/Max
private static decimal Min(decimal a, decimal b, decimal c) =>
    Math.Min(a, Math.Min(b, c));
private static decimal Max(decimal a, decimal b, decimal c) =>
    Math.Max(a, Math.Max(b, c));
```

### 4.3 走势分类

```python
# Python
def classify_trend(zs_list):
    if len(zs_list) == 0:  return "UNCLASSIFIED"
    if len(zs_list) == 1:  return "CONSOLIDATION"

    direction = None
    for i in range(len(zs_list) - 1):
        zs1, zs2 = zs_list[i], zs_list[i+1]
        if zs2.zd > zs1.zg:
            if direction == "DOWN": return "CONSOLIDATION"
            direction = "UP"
        elif zs2.zg < zs1.zd:
            if direction == "UP": return "CONSOLIDATION"
            direction = "DOWN"
        else:
            return "CONSOLIDATION"

    if direction == "UP":   return "UPTREND"
    if direction == "DOWN": return "DOWNTREND"
    return "CONSOLIDATION"
```

```csharp
// C#
public enum TrendType { Unclassified, Consolidation, UpTrend, DownTrend }

public static TrendType ClassifyTrend(List<Zhongshu> zsList)
{
    if (zsList.Count == 0) return TrendType.Unclassified;
    if (zsList.Count == 1) return TrendType.Consolidation;

    TrendType? direction = null;
    for (int i = 0; i < zsList.Count - 1; i++)
    {
        var (zs1, zs2) = (zsList[i], zsList[i+1]);
        if (zs2.Zd > zs1.Zg)
        {
            if (direction == TrendType.DownTrend) return TrendType.Consolidation;
            direction = TrendType.UpTrend;
        }
        else if (zs2.Zg < zs1.Zd)
        {
            if (direction == TrendType.UpTrend) return TrendType.Consolidation;
            direction = TrendType.DownTrend;
        }
        else return TrendType.Consolidation;
    }
    return direction ?? TrendType.Consolidation;
}
```

---

## 5. 背驰

### 5.1 前提

```
背驰判断仅在趋势中进行（classify_trend ∈ {UPTREND, DOWNTREND}）
且至少存在 2 个中枢。
```

### 5.2 MACD 计算

```python
# Python
def ema(data, period):
    """指数移动平均"""
    k = 2 / (period + 1)
    result = [data[0]]
    for val in data[1:]:
        result.append(val * k + result[-1] * (1 - k))
    return result

def macd_area(bars, start_idx, end_idx):
    """计算指定区间的MACD红柱面积和绿柱面积"""
    closes = [float(b.close) for b in bars[start_idx:end_idx+1]]
    ema12 = ema(closes, 12)
    ema26 = ema(closes, 26)
    dif = [e12 - e26 for e12, e26 in zip(ema12, ema26)]
    dea = ema(dif, 9)
    macd_bar = [2 * (d - e) for d, e in zip(dif, dea)]  # (DIF-DEA)*2

    red_area   = sum(max(0, m) for m in macd_bar)
    green_area = sum(abs(min(0, m)) for m in macd_bar)
    return red_area, green_area
```

```csharp
// C#
public static (double RedArea, double GreenArea) MacdArea(
    List<Bar> bars, int startIdx, int endIdx)
{
    int count = endIdx - startIdx + 1;
    var closes = new double[count];
    for (int i = 0; i < count; i++)
        closes[i] = (double)bars[startIdx + i].Close;

    var ema12 = Ema(closes, 12);
    var ema26 = Ema(closes, 26);
    var dif  = ema12.Zip(ema26, (a, b) => a - b).ToArray();
    var dea  = Ema(dif, 9);

    double redArea = 0, greenArea = 0;
    for (int i = 0; i < dif.Length; i++)
    {
        var bar = 2 * (dif[i] - dea[i]);
        if (bar > 0) redArea   += bar;
        else         greenArea += Math.Abs(bar);
    }
    return (redArea, greenArea);
}

private static double[] Ema(double[] data, int period)
{
    double k = 2.0 / (period + 1);
    var result = new double[data.Length];
    result[0] = data[0];
    for (int i = 1; i < data.Length; i++)
        result[i] = data[i] * k + result[i - 1] * (1 - k);
    return result;
}
```

### 5.3 趋势背驰检测

```python
# Python
def check_divergence(bis, zs_list, bars):
    """
    上涨趋势顶背驰: C段MACD红柱面积 < A段MACD红柱面积 * DIVERGENCE_RATIO
    下跌趋势底背驰: C段MACD绿柱面积 < A段MACD绿柱面积 * DIVERGENCE_RATIO
    """
    trend = classify_trend(zs_list)
    if trend not in ("UPTREND", "DOWNTREND"):
        return []

    signals = []
    for i in range(len(zs_list) - 1):
        zs1, zs2 = zs_list[i], zs_list[i+1]
        a_bi = bis[zs1.start_bi_idx - 1] if zs1.start_bi_idx > 0 else None
        c_dir = "DOWN" if trend == "DOWNTREND" else "UP"
        c_bi = find_exit_bi(bis, zs2, c_dir)

        if a_bi is None or c_bi is None: continue

        red_a, green_a = macd_area(bars, a_bi.start_idx, a_bi.end_idx)
        red_c, green_c = macd_area(bars, c_bi.start_idx, c_bi.end_idx)

        if trend == "DOWNTREND" and green_c > 0 and green_c < green_a * DIVERGENCE_RATIO:
            signals.append({
                "type": "BOTTOM_DIVERGENCE",
                "zs_index": i+1, "a_area": green_a, "c_area": green_c,
                "ratio": green_c / green_a if green_a > 0 else 1.0,
                "a_bi": a_bi, "c_bi": c_bi, "dt": c_bi.dt_end
            })
        elif trend == "UPTREND" and red_c > 0 and red_c < red_a * DIVERGENCE_RATIO:
            signals.append({
                "type": "TOP_DIVERGENCE",
                "zs_index": i+1, "a_area": red_a, "c_area": red_c,
                "ratio": red_c / red_a if red_a > 0 else 1.0,
                "a_bi": a_bi, "c_bi": c_bi, "dt": c_bi.dt_end
            })

    return signals

def find_exit_bi(bis, zs, direction):
    for i in range(zs.end_bi_idx + 1, len(bis)):
        if bis[i].type == direction:
            return bis[i]
    return None
```

```csharp
// C#
public record DivergenceSignal(
    string Type,      // "BOTTOM_DIVERGENCE" | "TOP_DIVERGENCE"
    int ZsIndex,
    double AArea,
    double CArea,
    double Ratio,
    Bi ABi,
    Bi CBi,
    DateTime Dt
);

public static List<DivergenceSignal> CheckDivergence(
    List<Bi> bis, List<Zhongshu> zsList, List<Bar> bars)
{
    var trend = ClassifyTrend(zsList);
    if (trend is not (TrendType.UpTrend or TrendType.DownTrend))
        return [];

    var signals = new List<DivergenceSignal>();

    for (int i = 0; i < zsList.Count - 1; i++)
    {
        var (zs1, zs2) = (zsList[i], zsList[i+1]);
        var aBi = zs1.StartBiIdx > 0 ? bis[zs1.StartBiIdx - 1] : null;
        var cDir = trend == TrendType.DownTrend ? BiType.Down : BiType.Up;
        var cBi = FindExitBi(bis, zs2, cDir);
        if (aBi == null || cBi == null) continue;

        var (redA, greenA) = MacdArea(bars, aBi.StartIdx, aBi.EndIdx);
        var (redC, greenC) = MacdArea(bars, cBi.StartIdx, cBi.EndIdx);

        if (trend == TrendType.DownTrend
            && greenC > 0 && greenC < greenA * ChanLunConfig.DivergenceRatio)
        {
            signals.Add(new DivergenceSignal("BOTTOM_DIVERGENCE", i+1,
                greenA, greenC, greenA > 0 ? greenC/greenA : 1.0,
                aBi, cBi, cBi.DtEnd));
        }
        else if (trend == TrendType.UpTrend
            && redC > 0 && redC < redA * ChanLunConfig.DivergenceRatio)
        {
            signals.Add(new DivergenceSignal("TOP_DIVERGENCE", i+1,
                redA, redC, redA > 0 ? redC/redA : 1.0,
                aBi, cBi, cBi.DtEnd));
        }
    }
    return signals;
}

private static Bi? FindExitBi(List<Bi> bis, Zhongshu zs, BiType direction)
{
    for (int i = zs.EndBiIdx + 1; i < bis.Count; i++)
        if (bis[i].Type == direction) return bis[i];
    return null;
}
```

---

## 6. 买卖点信号

### 6.1 信号数据结构

```python
# Python
@dataclass
class ChanSignal:
    type: str              # "B1"|"B2"|"B3"|"S1"|"S2"|"S3"
    dt: datetime
    price: Decimal
    stop_loss: Decimal
    confidence: int        # 1-3
    zs: Zhongshu
    description: str
```

```csharp
// C#
public enum SignalType { B1, B2, B3, S1, S2, S3 }

public record ChanSignal(
    SignalType Type,
    DateTime Dt,
    decimal Price,
    decimal StopLoss,
    int Confidence,       // 1-3
    Zhongshu Zs,
    string Description
);
```

### 6.2 一买 B1（底背驰买点）

```
条件：
  1. 趋势底背驰成立
  2. C段之后出现反向笔（确认C段完成）

入场价：确认笔起点（底分型价格）
止损价：C段最低点 × 0.995
```

```python
# Python
def signal_b1(divergence, bis, zs_list):
    c_bi = divergence["c_bi"]
    c_idx = bis.index(c_bi)
    if c_idx + 1 >= len(bis): return None

    confirm_bi = bis[c_idx + 1]
    if confirm_bi.type != "UP": return None

    return ChanSignal(
        type="B1", dt=confirm_bi.dt_start,
        price=confirm_bi.start_fx.price,
        stop_loss=Decimal(str(c_bi.low)) * Decimal("0.995"),
        confidence=2 if len(zs_list) >= 2 else 1,
        zs=zs_list[divergence["zs_index"]],
        description=f"一买: {divergence['zs_index']+1}中枢底背驰"
    )
```

```csharp
// C#
public static ChanSignal? SignalB1(DivergenceSignal div,
    List<Bi> bis, List<Zhongshu> zsList)
{
    var cIdx = bis.IndexOf(div.CBi);
    if (cIdx < 0 || cIdx + 1 >= bis.Count) return null;

    var confirm = bis[cIdx + 1];
    if (confirm.Type != BiType.Up) return null;

    return new ChanSignal(SignalType.B1, confirm.DtStart,
        confirm.StartFx.Price,
        div.CBi.Low * 0.995m,
        zsList.Count >= 2 ? 2 : 1,
        zsList[div.ZsIndex],
        $"一买: {div.ZsIndex+1}中枢底背驰, C/A={div.Ratio:F2}");
}
```

### 6.3 二买 B2（回抽确认）

```
条件：
  1. B1 已出现
  2. B1 之后向下笔回抽
  3. 回抽低点 > B1低点（不创新低）
  4. 回抽后的向上笔确认

入场价：确认笔起点
止损价：B1低点 × 0.995
```

```python
# Python
def signal_b2(bis, zs_list, b1_signal):
    if b1_signal is None: return None
    b1_bi = b1_signal.get("c_bi")
    if b1_bi is None: return None

    b1_idx = bis.index(b1_bi) if b1_bi in bis else -1
    if b1_idx < 0 or b1_idx + 3 >= len(bis): return None

    up_bi   = bis[b1_idx + 1]    # B1确认笔
    pullback = bis[b1_idx + 2]    # 回抽笔
    if up_bi.type != "UP" or pullback.type != "DOWN":
        return None

    if pullback.low <= b1_bi.low:
        return None              # 创新低→不是二买

    if b1_idx + 3 >= len(bis): return None
    confirm = bis[b1_idx + 3]
    if confirm.type != "UP": return None

    zs = zs_list[b1_signal["zs_index"]]
    if pullback.low > zs.zg:      strength = "强"
    elif pullback.low >= zs.zd:   strength = "标准"
    else:                         strength = "弱"

    return ChanSignal(
        type="B2", dt=confirm.dt_start,
        price=confirm.start_fx.price,
        stop_loss=b1_bi.low * Decimal("0.995"),
        confidence=3 if strength == "强" else 2,
        zs=zs, description=f"二买({strength}): 回抽不破B1低点")
```

```csharp
// C#
public static ChanSignal? SignalB2(List<Bi> bis, List<Zhongshu> zsList,
    DivergenceSignal b1Div)
{
    if (b1Div == null) return null;
    var b1Idx = bis.IndexOf(b1Div.CBi);
    if (b1Idx < 0 || b1Idx + 3 >= bis.Count) return null;

    var upBi     = bis[b1Idx + 1];
    var pullback = bis[b1Idx + 2];
    if (upBi.Type != BiType.Up || pullback.Type != BiType.Down)
        return null;
    if (pullback.Low <= b1Div.CBi.Low)
        return null;  // 创新低

    var confirm = bis[b1Idx + 3];
    if (confirm.Type != BiType.Up) return null;

    var zs = zsList[b1Div.ZsIndex];
    string strength = pullback.Low > zs.Zg ? "强"
                    : pullback.Low >= zs.Zd ? "标准" : "弱";

    return new ChanSignal(SignalType.B2, confirm.DtStart,
        confirm.StartFx.Price,
        b1Div.CBi.Low * 0.995m,
        strength == "强" ? 3 : 2, zs,
        $"二买({strength}): 回抽不破B1低点");
}
```

### 6.4 三买 B3（中枢突破确认）

```
条件：
  1. 一笔向上离开中枢（笔内所有K线 > ZG）
  2. 随后一笔向下回抽
  3. 回抽低点 > ZG（不进入中枢）
  4. 回抽后向上笔确认

入场价：确认笔起点
止损价：ZG × 0.998
```

```python
# Python
def signal_b3(bis, zs_list):
    if not zs_list: return []
    signals = []

    for zs in zs_list:
        exit_bi = find_exit_bi(bis, zs, "UP")
        if exit_bi is None or exit_bi.low <= zs.zg: continue

        exit_idx = bis.index(exit_bi)
        if exit_idx + 2 >= len(bis): continue

        pullback = bis[exit_idx + 1]
        if pullback.type != "DOWN" or pullback.low <= zs.zg: continue

        confirm = bis[exit_idx + 2]
        if confirm.type != "UP": continue

        dist = float(pullback.low - zs.zg) / float(zs.zg) * 10000
        signals.append(ChanSignal(
            type="B3", dt=confirm.dt_start,
            price=confirm.start_fx.price,
            stop_loss=zs.zg * Decimal("0.998"),
            confidence=3 if dist > 50 else 2,
            zs=zs, description=f"三买: 回抽距ZG {dist:.0f}bp"))

    return signals
```

```csharp
// C#
public static List<ChanSignal> SignalB3(List<Bi> bis, List<Zhongshu> zsList)
{
    var signals = new List<ChanSignal>();
    foreach (var zs in zsList)
    {
        var exitBi = FindExitBi(bis, zs, BiType.Up);
        if (exitBi == null || exitBi.Low <= zs.Zg) continue;

        int exitIdx = bis.IndexOf(exitBi);
        if (exitIdx < 0 || exitIdx + 2 >= bis.Count) continue;

        var pullback = bis[exitIdx + 1];
        if (pullback.Type != BiType.Down || pullback.Low <= zs.Zg) continue;

        var confirm = bis[exitIdx + 2];
        if (confirm.Type != BiType.Up) continue;

        double dist = (double)(pullback.Low - zs.Zg) / (double)zs.Zg * 10000;
        signals.Add(new ChanSignal(SignalType.B3, confirm.DtStart,
            confirm.StartFx.Price, zs.Zg * 0.998m,
            dist > 50 ? 3 : 2, zs,
            $"三买: 回抽距ZG {dist:F0}bp"));
    }
    return signals;
}
```

### 6.5 卖点 S1/S2/S3

S1/S2/S3 是 B1/B2/B3 的镜像（方向取反）：

```
S1: 上涨趋势顶背驰 → 做空。入场=确认向下跌破，止损=C段高点×1.005
S2: S1后反弹不创新高 → 做空
S3: 向下离开中枢后反弹不入中枢 → 做空
```

---

## 7. 双级别联立

### 7.1 配置

```
大级别（方向级）：30min K线
小级别（执行级）：5min K线

信号触发 = 大级别方向明确 AND 小级别出现买卖点
```

### 7.2 核心管道工厂

两个级别的分析管道完全一样，只是输入数据不同。抽取为一个工厂函数：

```python
# Python
def analyze(bars):
    """完整分析管道：原始K线 → (bis, zs_list, trend)"""
    std  = process_inclusions(bars)
    fx   = deduplicate_fractals(find_fractals(std))
    bis  = build_bis(fx, std)
    zs   = build_zhongshus(bis)
    trend = classify_trend(zs)
    return bis, zs, trend, std
```

```csharp
// C#
public record AnalysisResult(
    List<Bi> Bis, List<Zhongshu> ZsList, TrendType Trend, List<Bar> Std);

public static AnalysisResult Analyze(List<Bar> bars)
{
    var std  = ProcessInclusions(bars);
    var fx   = DeduplicateFractals(FindFractals(std));
    var bis  = BuildBis(fx, std);
    var zs   = BuildZhongshus(bis);
    var trend = ClassifyTrend(zs);
    return new AnalysisResult(bis, zs, trend, std);
}
```

### 7.3 双级别信号

```python
# Python
def dual_level_signal(bars_big, bars_small):
    bi_big, zs_big, trend_big, std_big = analyze(bars_big)

    if trend_big not in ("UPTREND", "DOWNTREND"):
        return []  # 大级别不明确→不交易

    bi_sm, zs_sm, trend_sm, std_sm = analyze(bars_small)
    div_sm = check_divergence(bi_sm, zs_sm, std_sm)

    signals = []
    for div in div_sm:
        if trend_big == "DOWNTREND" and div["type"] == "BOTTOM_DIVERGENCE":
            s = signal_b1(div, bi_sm, zs_sm)
        elif trend_big == "UPTREND" and div["type"] == "TOP_DIVERGENCE":
            s = signal_s1(div, bi_sm, zs_sm)  # 镜像实现
        else:
            continue
        if s:
            s.confidence = 3
            signals.append(s)

    return signals
```

```csharp
// C#
public static List<ChanSignal> DualLevelSignal(
    List<Bar> barsBig, List<Bar> barsSmall)
{
    var big = Analyze(barsBig);
    if (big.Trend is not (TrendType.UpTrend or TrendType.DownTrend))
        return [];

    var sm  = Analyze(barsSmall);
    var div = CheckDivergence(sm.Bis, sm.ZsList, sm.Std);

    var signals = new List<ChanSignal>();
    foreach (var d in div)
    {
        ChanSignal? s = null;
        if (big.Trend == TrendType.DownTrend && d.Type == "BOTTOM_DIVERGENCE")
            s = SignalB1(d, sm.Bis, sm.ZsList);
        else if (big.Trend == TrendType.UpTrend && d.Type == "TOP_DIVERGENCE")
            s = SignalS1(d, sm.Bis, sm.ZsList);  // 镜像实现

        if (s != null) signals.Add(s with { Confidence = 3 });
    }
    return signals;
}
```

---

## 8. 回测接口

### 8.1 策略骨架（C# 版 — 直接对接 TradingStudio）

```csharp
// C# — TradingStudio.Strategy.ChanLun.ChanLunStrategy
using TradingStudio.Core.Engine;
using TradingStudio.Core.Strategy;

public class ChanLunStrategy : IStrategy
{
    private readonly string _freqBig;
    private readonly string _freqSmall;
    private readonly List<Bar> _barsBig = [];
    private readonly List<Bar> _barsSmall = [];
    private int _position;

    public ChanLunStrategy(string freqBig = "30min", string freqSmall = "5min")
    {
        _freqBig = freqBig;
        _freqSmall = freqSmall;
    }

    public StrategyContext Context { get; set; }

    public void OnBar(Bar bar)
    {
        _barsSmall.Add(bar);

        if (_barsSmall.Count % SmallPerBig() == 0)
            _barsBig.Add(Resample(_barsSmall.TakeLast(SmallPerBig())));

        if (_barsSmall.Count < 30 || _barsBig.Count < 10) return;

        var signals = DualLevelSignal(_barsBig, _barsSmall);
        if (signals.Count == 0) return;

        var sig = signals[^1];

        if (sig.Type.ToString().StartsWith("B") && _position <= 0)
            Context.EnterLong(sig.Price, sig.StopLoss, sig.Description);
        else if (sig.Type.ToString().StartsWith("S") && _position >= 0)
            Context.EnterShort(sig.Price, sig.StopLoss, sig.Description);
    }

    public void OnTick(TickRecord tick) { } // 缠论策略用 Bar 入口

    private int SmallPerBig() => _freqBig switch
    {
        "30min" when _freqSmall == "5min"  => 6,
        "60min" when _freqSmall == "15min" => 4,
        "60min" when _freqSmall == "5min"  => 12,
        _ => 1
    };

    private static Bar Resample(IEnumerable<Bar> bars) => new(
        Dt:    bars.Last().Dt,
        Open:  bars.First().Open,
        High:  bars.Max(b => b.High),
        Low:   bars.Min(b => b.Low),
        Close: bars.Last().Close,
        Vol:   bars.Sum(b => b.Vol)
    );
}
```

### 8.2 参数网格

```python
# Python
PARAM_GRID = {
    "min_bi_len": [5, 6, 7, 8],
    "divergence_ratio": [0.3, 0.5, 0.7],
    "freq_big": ["30min", "60min"],
    "freq_small": ["5min", "15min"],
    "use_dual_level": [True, False],
}
```

```csharp
// C#
public record ChanLunParams(
    int MinBiLen = 7,
    double DivergenceRatio = 0.5,
    string FreqBig = "30min",
    string FreqSmall = "5min",
    bool UseDualLevel = true
);

public static IEnumerable<ChanLunParams> ParamGrid() =>
    from minBiLen       in new[] { 5, 6, 7, 8 }
    from divergenceRatio in new[] { 0.3, 0.5, 0.7 }
    from freqBig         in new[] { "30min", "60min" }
    from freqSmall       in new[] { "5min", "15min" }
    from useDual         in new[] { true, false }
    select new ChanLunParams(minBiLen, divergenceRatio, freqBig, freqSmall, useDual);
```

---

## 9. 验证清单

### 9.1 组件测试

```python
# Python
def test_inclusion():
    merged = process_inclusions(make_test_bars())
    assert all(not has_inclusion(merged[i], merged[i+1]) for i in range(len(merged)-1))
    assert len(merged) <= len(raw)

def test_fractal_alternation():
    fx = deduplicate_fractals(find_fractals(std_bars))
    assert all(fx[i].type != fx[i+1].type for i in range(len(fx)-1))

def test_bi_length():
    bis = build_bis(fractals, std_bars)
    assert all(bi.bar_count >= MIN_BI_LEN for bi in bis)

def test_zs_overlap():
    for zs in build_zhongshus(bis):
        assert zs.zg > zs.zd + ZS_MIN_OVERLAP
```

```csharp
// C# — xUnit
[Fact]
public void Inclusion_NoAdjacentInclusions()
{
    var merged = ChanLun.ProcessInclusions(TestData.Bars);
    for (int i = 0; i < merged.Count - 1; i++)
        Assert.False(ChanLun.HasInclusion(merged[i], merged[i+1]));
}

[Fact]
public void Fractal_StrictAlternation()
{
    var fx = ChanLun.DeduplicateFractals(
        ChanLun.FindFractals(TestData.StdBars));
    for (int i = 0; i < fx.Count - 1; i++)
        Assert.NotEqual(fx[i].Type, fx[i+1].Type);
}

[Fact]
public void Bi_MinBarCount()
{
    var bis = ChanLun.BuildBis(TestData.Fractals, TestData.StdBars);
    Assert.All(bis, bi => Assert.True(bi.BarCount >= ChanLunConfig.MinBiLen));
}

[Fact]
public void Zs_ValidOverlap()
{
    foreach (var zs in ChanLun.BuildZhongshus(TestData.Bis))
        Assert.True(zs.Zg > zs.Zd + ChanLunConfig.ZsMinOverlap);
}
```

### 9.2 信号级验证

```
□ 一买：取10个B1信号 → 人工判断是否"像底"
□ 二买：B2后价格继续上涨的比例
□ 三买：B3后价格回到中枢的比例（回中枢=B3失败）
□ 背驰：背驰后5/10/20根K线方向正确率
□ 双级别：对比单/双级别的胜率和盈亏比
```

### 9.3 完整回测

```
□ 单品种+单级别 → 夏普 > 0？
□ 5品种同参数 → 至少3个夏普 > 0？
□ 样本外衰减 < 50%？
□ 任一通过 → 值得继续。都没过 → 放弃或仅保留笔+中枢做辅助。
```

---

## 10. C# 项目结构建议

```
TradingStudio.Strategy.ChanLun/
├── ChanLunConfig.cs          — 全局参数常量
├── Models/
│   ├── Bar.cs                — K线（复用 Core 的，或 record 别名）
│   ├── Fractal.cs            — 分型 record
│   ├── Bi.cs                 — 笔 record
│   ├── Zhongshu.cs           — 中枢 record
│   ├── DivergenceSignal.cs   — 背驰信号 record
│   └── ChanSignal.cs         — 买卖点信号 record
├── Pipeline/
│   ├── InclusionProcessor.cs — 包含处理
│   ├── FractalDetector.cs    — 分型识别+去重
│   ├── BiBuilder.cs          — 笔划分
│   ├── ZhongshuBuilder.cs    — 中枢识别+合并+走势分类
│   ├── DivergenceDetector.cs — MACD面积+背驰检测
│   └── SignalGenerator.cs    — B1/B2/B3/S1/S2/S3 信号生成
├── Analysis/
│   ├── ChanLunAnalyzer.cs    — 单级别分析管道（Analyze工厂）
│   └── DualLevelAnalyzer.cs  — 双级别联立
├── ChanLunStrategy.cs        — IStrategy 适配（回测/实盘通用）
└── ChanLunStrategyTests/     — xUnit 单元测试 + 集成回测
```

---

## 附录：关键公式速查

```
┌─────────────────────────────────────────────────────┐
│ 包含合并（向上）                                      │
│   merged.High = max(a.High, b.High)                   │
│   merged.Low  = max(a.Low,  b.Low)                    │
│                                                       │
│ 顶分型                                                 │
│   b.High > a.High && b.High > c.High                  │
│   b.Low  > a.Low  && b.Low  > c.Low                   │
│                                                       │
│ 笔（向上笔）                                           │
│   start=Bottom, end=Top, BarCount ≥ MinBiLen          │
│                                                       │
│ 中枢                                                  │
│   ZG = Min(b1.High, b2.High, b3.High)                 │
│   ZD = Max(b1.Low,  b2.Low,  b3.Low)                  │
│   成立: ZG > ZD                                        │
│                                                       │
│ 盘整 vs 趋势                                           │
│   zsList.Count == 1 → Consolidation                   │
│   zsList.Count ≥ 2 && 同向不重叠 → UpTrend/DownTrend  │
│                                                       │
│ 趋势背驰                                               │
│   ratio = C段MACD面积 / A段MACD面积                   │
│   信号: ratio < DivergenceRatio                        │
│                                                       │
│ B1: 趋势底背驰 + C段反向笔确认                         │
│ B2: B1后回抽不创新低 + 反向笔确认                      │
│ B3: 离开中枢后回抽不入中枢 + 反向笔确认                 │
└─────────────────────────────────────────────────────┘
```
