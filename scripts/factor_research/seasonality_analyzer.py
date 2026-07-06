"""
Ernie Chan Ch7.6: 季节性交易策略
================================
分析期货品种的季节性特征——哪些月份有稳定的方向性偏差。

Chan 的季节性分析方法:
  1. 计算每月的平均收益率
  2. 计算每月收益率的 t 统计量（统计显著性）
  3. 可视化季节性模式

期货市场已知的季节性:
  - 农产品: 播种/生长/收获季 → 供应驱动的价格模式
  - 能源:   取暖季(冬) / 驾驶季(夏) → 需求驱动
  - 化工:   检修季(春秋) → 供应收缩
  - 黑色:   金九银十、冬储 → 季节性需求波动

用法:
  cd TradingStudio
  python scripts/factor_research/seasonality_analyzer.py
"""

import duckdb
import numpy as np
import os
from datetime import datetime
from collections import defaultdict

# ── 配置 ──────────────────────────────────────────

DB_PATH = "data/bars_history.duckdb"
TABLE = "bars_day"
MIN_YEARS = 3        # 最少需要 3 年数据
TOP_N = 30
OUTPUT_DIR = "scripts/factor_research/output"

# 中文月份名
MONTH_NAMES = ['', '1月', '2月', '3月', '4月', '5月', '6月',
               '7月', '8月', '9月', '10月', '11月', '12月']

# ── 连接 ────────────────────────────────────────────

conn = duckdb.connect(DB_PATH, read_only=True)

# ── 辅助函数 ────────────────────────────────────────

def calc_seasonality(monthly_returns: dict) -> list:
    """
    计算每个月的季节性统计量。
    返回 [(月份, 均值, 标准差, t统计量, 胜率), ...]
    """
    results = []
    for month in range(1, 13):
        returns = monthly_returns.get(month, [])
        if len(returns) < MIN_YEARS:
            results.append((month, 0, 0, 0, 0, 0))
            continue

        arr = np.array(returns)
        mean_ret = np.mean(arr)
        std_ret = np.std(arr, ddof=1)
        n = len(arr)

        # t 统计量: mean / (std / sqrt(n))
        t_stat = mean_ret / (std_ret / np.sqrt(n)) if std_ret > 0 else 0
        win_rate = np.sum(arr > 0) / n

        results.append((month, mean_ret, std_ret, t_stat, win_rate, n))

    return results


def seasonality_label(t_stat: float, win_rate: float) -> str:
    """根据 t 统计量和胜率给季节性打分"""
    if abs(t_stat) >= 2.0 and win_rate >= 0.70:
        return "强季节性 ⭐"
    elif abs(t_stat) >= 1.5 and win_rate >= 0.60:
        return "有季节性 📊"
    elif abs(t_stat) >= 1.0:
        return "弱季节性"
    else:
        return "不明显"


# ── 1. 拉取数据并按月分组 ──────────────────────────

print("=" * 70)
print("Ernie Chan Ch7.6: 季节性交易策略分析")
print("=" * 70)

instruments = conn.execute(f"""
    SELECT instrument_id, COUNT(*) AS bar_count
    FROM {TABLE}
    WHERE instrument_id LIKE '%000'
    GROUP BY instrument_id
    HAVING COUNT(*) >= 252 * {MIN_YEARS}
    ORDER BY bar_count DESC
    LIMIT {TOP_N}
""").fetchall()

print(f"\n分析 {len(instruments)} 个品种 (>= {MIN_YEARS} 年数据)\n")

all_results = {}

for inst_id, bar_count in instruments:
    rows = conn.execute(f"""
        SELECT bar_time, close FROM {TABLE}
        WHERE instrument_id = '{inst_id}'
        ORDER BY bar_time
    """).fetchall()

    if len(rows) < 252 * MIN_YEARS:
        continue

    # 按月分组计算收益率
    monthly_returns = defaultdict(list)

    for i in range(1, len(rows)):
        prev_bar_time = rows[i-1][0]
        curr_bar_time = rows[i][0]
        prev_close = rows[i-1][1]
        curr_close = rows[i][1]

        if prev_close <= 0 or curr_close <= 0:
            continue

        month = curr_bar_time.month

        # 日收益率
        daily_ret = (curr_close - prev_close) / prev_close * 100  # 百分比

        # 月份标记基于当前 bar 的月份（这个月已经过去了）
        monthly_returns[month].append(daily_ret)

    seasonality = calc_seasonality(monthly_returns)

    # 找出最强的季节性月份
    best_month = max(seasonality, key=lambda s: abs(s[3]))  # 按 |t_stat| 排序
    worst_month = min(seasonality, key=lambda s: s[3])       # 按 t_stat 排序

    all_results[inst_id] = {
        'seasonality': seasonality,
        'best_month': best_month,
        'worst_month': worst_month,
        'bar_count': bar_count,
    }

# ── 2. 输出每个品种的月度矩阵 ────────────────────────

for inst_id, result in all_results.items():
    s = result['seasonality']

    print(f"\n{'─'*70}")
    print(f"  {inst_id} ({result['bar_count']} 根日线)")
    print(f"{'─'*70}")
    print(f"  {'月份':<6s} {'均值%':>8s} {'标准差%':>8s} {'t值':>8s} {'胜率':>8s} {'样本':>6s} {'季节性':<12s}")
    print(f"  {'-'*70}")

    seasonal_months = []

    for month, mean_r, std_r, t_stat, win_rate, n in s:
        label = seasonality_label(t_stat, win_rate)
        mean_str = f"{mean_r:+.2f}%"
        std_str = f"{std_r:.2f}%"
        t_str = f"{t_stat:+.2f}"
        wr_str = f"{win_rate:.0%}"

        # 高亮显著的月份
        marker = " ←" if abs(t_stat) >= 1.5 else ""
        print(f"  {MONTH_NAMES[month]:<6s} {mean_str:>8s} {std_str:>8s} {t_str:>8s} {wr_str:>8s} {n:>6d} {label:<12s}{marker}")

        if abs(t_stat) >= 1.5:
            seasonal_months.append({
                'month': month,
                'name': MONTH_NAMES[month],
                'mean': mean_r,
                't_stat': t_stat,
                'win_rate': win_rate,
                'direction': '做多' if mean_r > 0 else '做空',
            })

    if seasonal_months:
        print(f"\n  📌 {inst_id} 季节性交易建议:")
        for sm in seasonal_months:
            conf = "高" if abs(sm['t_stat']) >= 2.0 else "中"
            print(f"     {sm['name']}: {sm['direction']} (置信度={conf}, "
                  f"均值={sm['mean']:+.2f}%, 胜率={sm['win_rate']:.0%})")

# ── 3. 全局汇总 ────────────────────────────────────

print(f"\n{'='*70}")
print("Chan Ch7.6 季节性 — 全局汇总")
print("=" * 70)

# 统计哪些品种有显著的季节性
strong = []  # 至少有一个月 t≥2.0
moderate = []  # 至少有一个月 t≥1.5

for inst_id, result in all_results.items():
    has_strong = any(abs(s[3]) >= 2.0 for s in result['seasonality'])
    has_moderate = any(abs(s[3]) >= 1.5 for s in result['seasonality'])
    if has_strong:
        strong.append(inst_id)
    elif has_moderate:
        moderate.append(inst_id)

print(f"\n⭐ 强季节性品种 ({len(strong)} 个):")
if strong:
    # 显示最强月份
    for inst_id in strong:
        r = all_results[inst_id]
        best_m, best_mean, _, best_t, best_wr, _ = r['best_month']
        direction = "多" if best_mean > 0 else "空"
        print(f"  {inst_id:<8s} → {MONTH_NAMES[best_m]}: "
              f"{direction}头 (t={best_t:+.2f}, 均值={best_mean:+.2f}%, 胜率={best_wr:.0%})")

print(f"\n📊 中等季节性品种 ({len(moderate)} 个):")
if moderate:
    print(f"  {', '.join(moderate)}")

print(f"""
{'='*70}
季节性交易要点 (Chan Ch7.6):

  1. 季节性 ≠ 确定性 — 只能作为辅助因子，不能单独决策
  2. 需要和其他信号结合 — 季节性方向 + 技术信号确认
  3. 农产品季节性最明显 — 供应周期驱动
  4. 工业品季节性较弱 — 需求周期 + 宏观因素
  5. 季节性会变化 — 用滚动窗口验证，不要假设过去=未来

  Chan 的建议:
    "季节性策略的风险在于，大家都看到了同样的模式，
     导致价格提前反映，季节性效应反而消失。
     因此，季节性因子应该和动量/均值回归因子混合使用。"
{'='*70}
""")

# ── 4. 保存 ────────────────────────────────────────

os.makedirs(OUTPUT_DIR, exist_ok=True)
timestamp = datetime.now().strftime("%Y%m%d_%H%M")
csv_path = os.path.join(OUTPUT_DIR, f"seasonality_{timestamp}.csv")

with open(csv_path, 'w', encoding='utf-8-sig') as f:
    f.write("品种,月份,均值%,标准差%,t值,胜率,样本数,季节性\n")
    for inst_id, result in all_results.items():
        for month, mean_r, std_r, t_stat, win_rate, n in result['seasonality']:
            label = seasonality_label(t_stat, win_rate)
            f.write(f"{inst_id},{month},{mean_r:.3f},{std_r:.3f},"
                    f"{t_stat:.3f},{win_rate:.3f},{n},{label}\n")

print(f"📁 结果已保存: {csv_path}")

conn.close()
print("✅ 完成")
