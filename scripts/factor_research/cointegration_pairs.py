"""
Ernie Chan Ch7.3: 协整与配对交易
================================
对 Top30 品种做两两协整检验，找到可交易的配对。

Chan 的配对交易流程:
  1. 筛选相关品种对（同产业链/替代品/上下游）
  2. Johansen 检验确认协整关系
  3. 计算对冲比率
  4. 价差 = 品种A - 对冲比率 × 品种B
  5. 对价差做 ADF 检验 + 半衰期

用法:
  cd TradingStudio
  python scripts/factor_research/cointegration_pairs.py
"""

import duckdb
import numpy as np
import os
from datetime import datetime
from itertools import combinations

# ── 配置 ──────────────────────────────────────────

DB_PATH = "data/bars_history.duckdb"
TABLE = "bars_day"
MIN_BARS = 500
MIN_CORRELATION = 0.70    # 最低相关系数（太低的不值得做配对）
TOP_N = 30
OUTPUT_DIR = "scripts/factor_research/output"

# ── 期货产业链分组（领域知识） ─────────────────────

INDUSTRY_GROUPS = {
    "黑色": ["rb", "hc", "i", "j", "jm", "ss"],
    "化工": ["ta", "ma", "eg", "pp", "l", "v", "bu", "sc", "lu", "pg"],
    "油脂": ["y", "p", "oi"],
    "粕类": ["m", "rm"],
    "有色": ["cu", "al", "zn", "pb", "ni", "sn"],
    "贵金属": ["au", "ag"],
    "农产品": ["c", "cs", "a", "b", "jd"],
}

# ── 连接 ────────────────────────────────────────────

conn = duckdb.connect(DB_PATH, read_only=True)

# ── 辅助函数 ────────────────────────────────────────

def calc_half_life(spread: np.ndarray) -> float:
    """半衰期 — 同 adf_mean_reversion.py"""
    y = np.asarray(spread, dtype=np.float64)
    y_lag = y[:-1]
    y_diff = np.diff(y)
    mask = np.isfinite(y_lag) & np.isfinite(y_diff)
    y_lag, y_diff = y_lag[mask], y_diff[mask]
    if len(y_lag) < 100:
        return float('nan')
    X = np.column_stack([np.ones(len(y_lag)), y_lag])
    try:
        beta = np.linalg.lstsq(X, y_diff, rcond=None)[0][1]
    except np.linalg.LinAlgError:
        return float('nan')
    if beta >= 0:
        return float('nan')
    return -np.log(2) / beta


def test_cointegration(y1: np.ndarray, y2: np.ndarray) -> dict:
    """
    测试两个价格序列是否协整。
    用 OLS 估计对冲比率，然后对残差做 ADF 检验（Engle-Granger 两步法）。
    """
    from statsmodels.tsa.stattools import adfuller

    # 对齐长度
    min_len = min(len(y1), len(y2))
    y1 = y1[-min_len:]
    y2 = y2[-min_len:]

    # OLS: y1 = α + β·y2 + ε
    X = np.column_stack([np.ones(len(y2)), y2])
    try:
        coeff = np.linalg.lstsq(X, y1, rcond=None)[0]
    except np.linalg.LinAlgError:
        return {'valid': False}

    alpha, beta = coeff[0], coeff[1]

    if beta <= 0:
        return {'valid': False}  # 负对冲比率没意义

    # 残差 = 价差
    spread = y1 - (alpha + beta * y2)

    # ADF 检验
    try:
        adf = adfuller(spread, maxlag=int(len(spread) ** 0.33), autolag='AIC')
        adf_p = adf[1]
    except Exception:
        return {'valid': False}

    # 相关系数
    corr = np.corrcoef(y1, y2)[0, 1]

    # 半衰期
    hl = calc_half_life(spread)

    # 价差统计
    spread_std = np.std(spread)
    spread_mean = np.mean(spread)

    return {
        'valid': True,
        'alpha': alpha,
        'beta': beta,
        'adf_pvalue': adf_p,
        'correlation': corr,
        'half_life': hl,
        'spread_std': spread_std,
        'spread_mean': spread_mean,
        'cointegrated': adf_p < 0.05,
    }


# ── 1. 拉取所有品种的日线收盘价 ────────────────────

print("=" * 70)
print("Ernie Chan Ch7.3: 协整与配对交易筛选")
print("=" * 70)

instruments = conn.execute(f"""
    SELECT DISTINCT instrument_id FROM {TABLE}
    WHERE instrument_id LIKE '%000'
""").fetchall()

print(f"\n加载 {len(instruments)} 个连续合约品种...")

price_data = {}
for (inst_id,) in instruments:
    product = inst_id[:2] if inst_id[1] >= '0' else inst_id[:1]

    rows = conn.execute(f"""
        SELECT close FROM {TABLE}
        WHERE instrument_id = '{inst_id}'
        ORDER BY bar_time
    """).fetchall()

    closes = np.array([r[0] for r in rows], dtype=np.float64)
    if len(closes) >= MIN_BARS:
        # 对数价格
        price_data[inst_id] = {
            'product': product,
            'log_price': np.log(closes[closes > 0]),
            'raw_price': closes[closes > 0],
        }

print(f"有效品种: {len(price_data)}")

# ── 2. 同产业链内配对测试 ──────────────────────────

print("\n--- 同产业链配对分析 ---\n")

all_pairs = []

for group_name, products in INDUSTRY_GROUPS.items():
    group_insts = [iid for iid, info in price_data.items()
                   if info['product'] in products]

    if len(group_insts) < 2:
        continue

    # 对同组内所有配对做协整检验
    for inst_a, inst_b in combinations(group_insts, 2):
        # 只检验同一产业链的品种
        info_a = price_data[inst_a]
        info_b = price_data[inst_b]

        # 快速过滤：相关性太低跳过
        min_len = min(len(info_a['log_price']), len(info_b['log_price']))
        y1 = info_a['log_price'][-min_len:]
        y2 = info_b['log_price'][-min_len:]
        quick_corr = np.corrcoef(y1, y2)[0, 1]
        if quick_corr < MIN_CORRELATION:
            continue

        result = test_cointegration(y1, y2)

        if result['valid']:
            all_pairs.append({
                'group': group_name,
                'inst_a': inst_a,
                'inst_b': inst_b,
                **result,
            })

# ── 3. 排序输出 ────────────────────────────────────

# 按 ADF p 值排序（最显著的排前面）
all_pairs.sort(key=lambda p: p['adf_pvalue'])

print(f"{'产业链':<6s} {'品种A':<8s} {'品种B':<8s} {'ADF p':>8s} "
      f"{'相关':>6s} {'半衰期':>10s} {'对冲比率':>10s} {'结论':<10s}")
print("-" * 85)

tradeable_pairs = []

for p in all_pairs:
    adf_str = f"{p['adf_pvalue']:.4f}"
    corr_str = f"{p['correlation']:.3f}"
    hl = p['half_life']
    if not np.isnan(hl):
        hl_str = f"{hl:.0f}天" if hl < 252 else f"{hl/252:.1f}年"
    else:
        hl_str = "—"

    beta_str = f"{p['beta']:.3f}"

    # Chan 的配对筛选标准:
    # 1. ADF p < 0.05 (协整)
    # 2. 相关性 > 0.8
    # 3. 半衰期 < 126天（6个月）
    if p['cointegrated'] and p['correlation'] > 0.80 and not np.isnan(hl) and hl < 126:
        conclusion = "可交易 ⭐"
        tradeable_pairs.append(p)
    elif p['cointegrated']:
        conclusion = "协整但待观察"
    else:
        conclusion = "—"

    print(f"{p['group']:<6s} {p['inst_a']:<8s} {p['inst_b']:<8s} {adf_str:>8s} "
          f"{corr_str:>6s} {hl_str:>10s} {beta_str:>10s} {conclusion:<10s}")

# ── 4. 汇总 ────────────────────────────────────────

print(f"\n{'='*70}")
print("Chan Ch7.3 配对交易 — 总结")
print("=" * 70)

print(f"\n🔍 总配对检验数: {len(all_pairs)}")
print(f"⭐ 可交易配对: {len(tradeable_pairs)}")

if tradeable_pairs:
    print(f"\n{'产业链':<6s} {'品种A':<8s} {'品种B':<8s} {'ADF p':>8s} {'半衰期':>10s}")
    print(f"{'-'*50}")
    for p in tradeable_pairs:
        hl_str = f"{p['half_life']:.0f}天" if not np.isnan(p['half_life']) else "—"
        print(f"{p['group']:<6s} {p['inst_a']:<8s} {p['inst_b']:<8s} {p['adf_pvalue']:>8.4f} {hl_str:>10s}")

print(f"""
{'='*70}
配对交易要点 (Chan Ch7.3):

  1. 对冲比率 (β): 买1手A需要卖β手B来对冲
     价差 = log(Price_A) - β × log(Price_B)

  2. 入场: 价差偏离均值超过 2σ 时入场
     做多价差: A被低估, B被高估 → 买A卖B
     做空价差: A被高估, B被低估 → 卖A买B

  3. 出场: 价差回归均值 → 平仓
     止损: 价差继续扩大到 3σ → 止损

  4. 风险:
     - 协整关系可能破裂（结构变化）
     - 对冲比率是动态的，需要定期重新估计
     - 配对交易需要双向保证金，资金占用大
{'='*70}
""")

# ── 5. 保存 ────────────────────────────────────────

os.makedirs(OUTPUT_DIR, exist_ok=True)
timestamp = datetime.now().strftime("%Y%m%d_%H%M")
csv_path = os.path.join(OUTPUT_DIR, f"cointegration_{timestamp}.csv")

with open(csv_path, 'w', encoding='utf-8-sig') as f:
    f.write("产业链,品种A,品种B,ADF_p值,相关系数,半衰期(天),对冲比率,协整\n")
    for p in all_pairs:
        hl_str = f"{p['half_life']:.1f}" if not np.isnan(p['half_life']) else ""
        f.write(f"{p['group']},{p['inst_a']},{p['inst_b']},{p['adf_pvalue']:.4f},"
                f"{p['correlation']:.3f},{hl_str},{p['beta']:.3f},{p['cointegrated']}\n")

print(f"📁 结果已保存: {csv_path}")

conn.close()
print("✅ 完成")
