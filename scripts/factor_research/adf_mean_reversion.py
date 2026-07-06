"""
Ernie Chan《Algorithmic Trading》Ch4: Mean Reversion 实践
========================================================
对 Top30 期货品种做 ADF 检验 + 半衰期估算 + Hurst 指数。
回答三个问题：
  1. 哪些品种的价格是平稳的（适合均值回归策略）？
  2. 均值回归的半衰期是多少（决定了持仓周期）？
  3. 价格是趋势主导还是回归主导（Hurst 指数）？

用法：
  cd TradingStudio
  python scripts/factor_research/adf_mean_reversion.py
"""

import duckdb
import numpy as np
from statsmodels.tsa.stattools import adfuller
from collections import defaultdict
from datetime import datetime
import sys
import os

# ── 配置 ──────────────────────────────────────────

DB_PATH = "data/bars_history.duckdb"
TABLE = "bars_day"          # 日线做平稳性检验（Chan 推荐）
MIN_BARS = 500              # 最少需要 500 根日线（约 2 年）
TOP_N = 30                  # 分析品种数量
OUTPUT_DIR = "scripts/factor_research/output"

# ── 连接 DuckDB ────────────────────────────────────

if not os.path.exists(DB_PATH):
    print(f"❌ 数据库不存在: {DB_PATH}")
    print("   请确认路径或先运行: dotnet run -- collect")
    sys.exit(1)

conn = duckdb.connect(DB_PATH, read_only=True)

# ── 1. 发现活跃品种（按总 Bar 数排序，取 Top N）───

print("=" * 70)
print("Ernie Chan Ch4: 均值回归基础 — ADF + 半衰期 + Hurst")
print("=" * 70)

instruments = conn.execute(f"""
    SELECT instrument_id, COUNT(*) AS bar_count,
           MIN(bar_time) AS start_date, MAX(bar_time) AS end_date
    FROM {TABLE}
    WHERE instrument_id LIKE '%000'  -- 只分析连续合约
    GROUP BY instrument_id
    HAVING COUNT(*) >= {MIN_BARS}
    ORDER BY bar_count DESC
    LIMIT {TOP_N}
""").fetchall()

print(f"\n找到 {len(instruments)} 个品种 (连续合约, >= {MIN_BARS} 根日线)\n")

# ── 2. 辅助函数 ────────────────────────────────────

def calc_hurst(ts: np.ndarray, max_lag: int = 20) -> float:
    """
    Hurst 指数 (R/S 方法)。
    H > 0.5: 趋势持续 (trending)
    H ≈ 0.5: 随机游走 (random walk)
    H < 0.5: 均值回归 (mean-reverting)
    """
    ts = np.asarray(ts)
    lags = range(2, min(max_lag, len(ts) // 2))
    rs_values = []

    for lag in lags:
        segments = len(ts) // lag
        if segments < 4:
            continue
        rs = []
        for i in range(segments):
            chunk = ts[i * lag:(i + 1) * lag]
            if len(chunk) < 4:
                continue
            # 离差
            dev = chunk - chunk.mean()
            cumdev = dev.cumsum()
            r = cumdev.max() - cumdev.min()
            s = chunk.std(ddof=1)
            if s > 0:
                rs.append(r / s)
        if rs:
            rs_values.append((np.log(lag), np.log(np.mean(rs))))

    if len(rs_values) < 4:
        return 0.5  # 数据不足，假设随机游走

    x = np.array([v[0] for v in rs_values])
    y = np.array([v[1] for v in rs_values])
    # 线性回归 slope = H
    slope = np.polyfit(x, y, 1)[0]
    return slope


def calc_half_life(spread: np.ndarray) -> float:
    """
    均值回归半衰期 (Chan Ch4.4)。
    用 OLS 估计: Δy_t = α + β·y_{t-1} + ε_t
    半衰期 = -ln(2) / β
    半衰期越短 → 回归速度越快 → 更适合均值回归策略。
    半衰期 > 数据长度的 1/2 → 回归太慢，不考虑。
    """
    y = np.asarray(spread)
    y_lag = y[:-1]
    y_diff = np.diff(y)

    # 去掉 NaN/Inf
    mask = np.isfinite(y_lag) & np.isfinite(y_diff)
    y_lag = y_lag[mask]
    y_diff = y_diff[mask]

    if len(y_lag) < 100:
        return float('nan')

    # OLS: y_diff = α + β * y_lag
    X = np.column_stack([np.ones(len(y_lag)), y_lag])
    try:
        beta = np.linalg.lstsq(X, y_diff, rcond=None)[0][1]
    except np.linalg.LinAlgError:
        return float('nan')

    if beta >= 0:
        return float('nan')  # 不回归（发散）

    half_life = -np.log(2) / beta
    return half_life


def test_spread(close_prices: np.ndarray) -> dict:
    """
    对价格序列做 Chan 的三项检验:
      1. ADF 检验 (平稳性)
      2. Hurst 指数 (趋势 vs 回归特征)
      3. 半衰期 (如果可回归，多快回归)
    """
    result = {}

    # ── ADF 检验 ──
    try:
        adf_result = adfuller(close_prices, maxlag=int(len(close_prices) ** 0.33), autolag='AIC')
        result['adf_stat'] = adf_result[0]
        result['adf_pvalue'] = adf_result[1]
        result['adf_usedlag'] = adf_result[2]
        result['adf_nobs'] = adf_result[3]
        result['adf_1pct'] = adf_result[4]['1%']
        result['adf_5pct'] = adf_result[4]['5%']
        result['adf_10pct'] = adf_result[4]['10%']
    except Exception as e:
        result['adf_stat'] = float('nan')
        result['adf_pvalue'] = float('nan')
        result['adf_error'] = str(e)

    # ── Hurst 指数 ──
    try:
        result['hurst'] = calc_hurst(close_prices)
    except Exception:
        result['hurst'] = float('nan')

    # ── 半衰期（基于对均值的偏离）──
    # Chan: 用 SMA(mid_period) 作为 "均值"，计算价格偏离
    try:
        mid = max(10, min(50, len(close_prices) // 20))
        sma = np.convolve(close_prices, np.ones(mid) / mid, mode='valid')
        aligned_close = close_prices[-len(sma):]
        spread = aligned_close - sma  # 价格偏离 SMA 的距离
        result['half_life'] = calc_half_life(spread)
    except Exception:
        result['half_life'] = float('nan')

    return result


# ── 3. 逐品种分析 ──────────────────────────────────

results = []

for inst_id, bar_count, start, end in instruments:
    # 拉取日线收盘价
    rows = conn.execute(f"""
        SELECT close FROM {TABLE}
        WHERE instrument_id = '{inst_id}'
        ORDER BY bar_time
    """).fetchall()

    closes = np.array([r[0] for r in rows], dtype=np.float64)

    # 过滤异常值（价格 <= 0 或 > 均值 5σ 以上）
    mean_c = closes.mean()
    std_c = closes.std()
    closes = closes[(closes > 0) & (closes < mean_c + 5 * std_c)]

    if len(closes) < MIN_BARS:
        continue

    # 对数价格（ADF 检验通常用 log 价格）
    log_prices = np.log(closes)

    # 运行检验
    stats = test_spread(log_prices)

    # ── Chan 的综合判断 ──
    adf_p = stats.get('adf_pvalue', 1.0)
    hurst = stats.get('hurst', 0.5)
    hl = stats.get('half_life', float('nan'))

    if adf_p < 0.05:
        stationarity = "平稳 ✅"
    elif adf_p < 0.10:
        stationarity = "弱平稳 ⚠️"
    else:
        stationarity = "非平稳"

    if not np.isnan(hurst):
        if hurst < 0.45:
            hurst_label = "均值回归"
        elif hurst > 0.55:
            hurst_label = "趋势主导"
        else:
            hurst_label = "近似随机游走"
    else:
        hurst_label = "—"

    if not np.isnan(hl):
        hl_years = hl / 252
        if hl < 63:   # < 3 个月
            hl_label = f"{hl:.0f}天 ({hl_years*12:.1f}月) — 快速回归 ⚡"
        elif hl < 252:  # < 1 年
            hl_label = f"{hl:.0f}天 ({hl_years*12:.1f}月) — 可交易"
        else:
            hl_label = f"{hl:.0f}天 ({hl_years:.1f}年) — 回归太慢 ❌"
    else:
        hl_label = "不收敛"
        hl = float('nan')

    # 推荐策略类型
    if adf_p < 0.10 and not np.isnan(hl) and hl < 126:
        strategy_rec = "均值回归 ⭐"
    elif hurst > 0.55:
        strategy_rec = "趋势跟踪"
    else:
        strategy_rec = "需进一步分析"

    results.append({
        'instrument': inst_id,
        'bars': bar_count,
        'start': str(start)[:10],
        'end': str(end)[:10],
        'adf_stat': stats.get('adf_stat', float('nan')),
        'adf_pvalue': adf_p,
        'stationarity': stationarity,
        'hurst': hurst,
        'hurst_label': hurst_label,
        'half_life': hl,
        'half_life_label': hl_label,
        'strategy_rec': strategy_rec,
    })

# ── 4. 排序输出 ────────────────────────────────────

# 按 ADF p 值排序（平稳的排前面）
results.sort(key=lambda r: (r['adf_pvalue'] if not np.isnan(r['adf_pvalue']) else 999))

print(f"{'品种':<8s} {'Bar数':>6s} {'ADF p值':>8s} {'平稳性':<10s} "
      f"{'Hurst':>6s} {'特征':<12s} {'半衰期':>20s} {'推荐策略':<14s}")
print("-" * 100)

mean_reversion_candidates = []
trend_candidates = []

for r in results:
    adf_p_str = f"{r['adf_pvalue']:.4f}" if not np.isnan(r['adf_pvalue']) else "N/A"
    hurst_str = f"{r['hurst']:.3f}" if not np.isnan(r['hurst']) else "N/A"

    print(f"{r['instrument']:<8s} {r['bars']:>6d} {adf_p_str:>8s} {r['stationarity']:<10s} "
          f"{hurst_str:>6s} {r['hurst_label']:<12s} {r['half_life_label']:<20s} {r['strategy_rec']:<14s}")

    if r['strategy_rec'] == '均值回归 ⭐':
        mean_reversion_candidates.append(r)
    elif r['strategy_rec'] == '趋势跟踪':
        trend_candidates.append(r)

# ── 5. 汇总 ────────────────────────────────────────

print("\n" + "=" * 70)
print("Chan Ch4 均值回归检验 — 总结")
print("=" * 70)

print(f"\n📊 总品种数: {len(results)}")

print(f"\n🔄 均值回归候选 ({len(mean_reversion_candidates)} 个):")
if mean_reversion_candidates:
    print(f"   {'品种':<8s} {'ADF p':>8s} {'半衰期(天)':>10s} {'Hurst':>6s}")
    print(f"   {'-'*40}")
    for r in mean_reversion_candidates:
        hl_str = f"{r['half_life']:.0f}" if not np.isnan(r['half_life']) else "N/A"
        hurst_str = f"{r['hurst']:.3f}" if not np.isnan(r['hurst']) else "N/A"
        print(f"   {r['instrument']:<8s} {r['adf_pvalue']:>8.4f} {hl_str:>10s} {hurst_str:>6s}")
else:
    print("   (无) — 期货价格通常是趋势主导，均值回归更适合价差/基差策略")

print(f"\n📈 趋势跟踪候选 ({len(trend_candidates)} 个):")
if trend_candidates:
    print(f"   {', '.join(r['instrument'] for r in trend_candidates)}")

# ── 6. ADF 临界值参考 ──
print(f"""
{'='*70}
ADF 检验解读 (Chan Ch4.2):
  p < 0.05 → 拒绝单位根假设 → 价格可能平稳 → 适合均值回归
  p > 0.10 → 不能拒绝单位根 → 价格非平稳 → 适合趋势跟踪

Hurst 指数解读 (Chan Ch4.5):
  H < 0.45 → 均值回归倾向 (偏离会反向修正)
  H ≈ 0.50 → 近似随机游走
  H > 0.55 → 趋势持续倾向 (涨了还会涨)

半衰期解读 (Chan Ch4.4):
  < 21 天 (1个月)  → 高频回归, 适合日内/短线
  21-63 天 (1-3月) → 适合波段交易
  63-126天 (3-6月) → 适合中长线
  > 252天 (1年)    → 回归太慢, 不适合均值回归策略

⚠️  重要提醒 (Chan Ch4.6):
  1. ADF 检验对滞后阶数敏感 — 换一个 lag 可能结论完全不同
  2. 日线平稳 ≠ 分钟线平稳 — 用哪个周期交易就要在哪个周期检验
  3. 期货价格通常非平稳 — 均值回归策略更适合做价差/基差/跨期
  4. 半衰期会随时间变化 — 用滚动窗口重新估计，确认稳定性
{'='*70}
""")

# ── 7. 保存结果 ──

os.makedirs(OUTPUT_DIR, exist_ok=True)
timestamp = datetime.now().strftime("%Y%m%d_%H%M")

# 保存 CSV
csv_path = os.path.join(OUTPUT_DIR, f"adf_results_{timestamp}.csv")
with open(csv_path, 'w', encoding='utf-8-sig') as f:
    headers = ['品种', 'Bar数', '起始', '结束', 'ADF统计量', 'ADF_p值',
               '平稳性', 'Hurst指数', 'Hurst特征', '半衰期(天)', '推荐策略']
    f.write(','.join(headers) + '\n')
    for r in results:
        hl_str = f"{r['half_life']:.1f}" if not np.isnan(r['half_life']) else ""
        hurst_str = f"{r['hurst']:.3f}" if not np.isnan(r['hurst']) else ""
        f.write(f"{r['instrument']},{r['bars']},{r['start']},{r['end']},"
                f"{r['adf_stat']:.4f},{r['adf_pvalue']:.4f},{r['stationarity']},"
                f"{hurst_str},{r['hurst_label']},{hl_str},{r['strategy_rec']}\n")

print(f"📁 结果已保存: {csv_path}")

conn.close()
print("✅ 完成")
