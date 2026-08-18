#!/usr/bin/env python3
"""
23品种 未来三年大趋势行情概率评估
维度: 估值分位 + 供需周期 + 技术结构 + 催化时间线 + 风险
"""
import json, subprocess
from datetime import datetime

CONTRACTS = [
    ("AG0", "白银", "贵金属", "AG2612"),
    ("AU0", "黄金", "贵金属", "AU2612"),
    ("CU0", "沪铜", "有色金属", "CU2609"),
    ("SN0", "沪锡", "有色金属", "SN2609"),
    ("NI0", "沪镍", "有色金属", "NI2609"),
    ("AO0", "氧化铝", "有色金属", "AO2609"),
    ("I0", "铁矿石", "黑色系", "I2609"),
    ("JM0", "焦煤", "黑色系", "JM2609"),
    ("FU0", "燃料油", "能源", "FU2609"),
    ("SA0", "纯碱", "化工", "SA2701"),
    ("FG0", "玻璃", "化工", "FG2609"),
    ("RU0", "天然橡胶", "化工", "RU2701"),
    ("SH0", "烧碱", "化工", "SH2609"),
    ("V0", "PVC", "化工", "V2609"),
    ("UR0", "尿素", "化工", "UR2609"),
    ("TA0", "PTA", "化工", "TA2609"),
    ("LC0", "碳酸锂", "新能源", "LC2609"),
    ("SR0", "白糖", "农产品", "SR2609"),
    ("P0", "棕榈油", "农产品", "P2609"),
    ("CJ0", "红枣", "农产品", "CJ2609"),
    ("CF0", "棉花", "农产品", "CF2609"),
    ("M0", "豆粕", "农产品", "M2609"),
    ("RM0", "菜粕", "农产品", "RM2609"),
]

def fetch(symbol):
    url = f'https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{symbol}=/InnerFuturesNewService.getDailyKLine?symbol={symbol}'
    try:
        r = subprocess.run(['curl','-s','--max-time','15',
            '-H','Referer: https://finance.sina.com.cn','-H','User-Agent: Mozilla/5.0',url],
            capture_output=True, timeout=20)
        text = r.stdout.decode('gbk', errors='replace')
        if 'null' in text[:200] or '[' not in text: return None
        return json.loads(text[text.index('['):text.rindex(']')+1])
    except: return None

print("=" * 90)
print("23品种 未来三年(2026-2029) 大趋势行情概率评估")
print("=" * 90)

results = []
for sym, name, cat, main_contract in CONTRACTS:
    data = fetch(sym)
    if not data or len(data) < 200:
        results.append({'name': name, 'cat': cat, 'sym': sym, 'error': True})
        continue

    closes = [float(d['c']) for d in data]
    highs = [float(d['h']) for d in data]
    lows = [float(d['l']) for d in data]
    current = closes[-1]
    all_high = max(highs)
    all_low = min(lows)

    # 1. Valuation metrics
    pct_abs = (current - all_low) / (all_high - all_low) * 100 if all_high != all_low else 50

    # Percent of monthly closes below current
    months = {}
    for d in data:
        mk = d['d'][:7]
        if mk not in months: months[mk] = []
        months[mk].append(float(d['c']))
    monthly = [months[mk][-1] for mk in sorted(months.keys())]
    pct_monthly = sum(1 for c in monthly if c <= current) / len(monthly) * 100

    # 3-year drawdown from peak
    recent_3y_closes = [c for c in closes[-750:] if c > 0]
    peak_3y = max(recent_3y_closes) if recent_3y_closes else current
    dd_from_peak = (current - peak_3y) / peak_3y * 100

    # 2. Volatility & trend metrics
    chg_1y = (closes[-1] - closes[-250]) / closes[-250] * 100 if len(closes) >= 250 else 0
    chg_3y = (closes[-1] - closes[-750]) / closes[-750] * 100 if len(closes) >= 750 else 0

    # Annualized volatility
    if len(closes) >= 250:
        daily_returns = [(closes[i]-closes[i-1])/closes[i-1]*100 for i in range(-250,0) if closes[i-1]>0]
        avg_ret = sum(daily_returns)/len(daily_returns)
        std = (sum((r-avg_ret)**2 for r in daily_returns)/len(daily_returns))**0.5
        ann_vol = std * (250**0.5)
    else:
        ann_vol = 0

    # 3. Range-bound detection: time in middle 50% of range
    mid_low = all_low + (all_high - all_low) * 0.25
    mid_high = all_low + (all_high - all_low) * 0.75
    months_mid = sum(1 for c in monthly if mid_low <= c <= mid_high)
    pct_rangebound = months_mid / len(monthly) * 100

    # 4. Trend duration stats (from monthly)
    up_streaks, dn_streaks = [], []
    streak, prev = 0, None
    for c in monthly[-60:]:  # last 5 years
        if prev:
            if c > prev:
                if streak < 0: dn_streaks.append(abs(streak)); streak = 0
                streak += 1
            else:
                if streak > 0: up_streaks.append(streak); streak = 0
                streak -= 1
        prev = c
    avg_up = sum(up_streaks)/len(up_streaks) if up_streaks else 0
    max_up = max(up_streaks) if up_streaks else 0

    # 5. Extreme moves in history
    yearly = {}
    for d in data:
        y = int(d['d'][:4])
        if y not in yearly: yearly[y] = []
        yearly[y].append(float(d['c']))
    yearly_chg = []
    for y in sorted(yearly.keys()):
        bars = yearly[y]
        chg = (bars[-1] - bars[0]) / bars[0] * 100
        yearly_chg.append(chg)
    max_up_year = max(yearly_chg) if yearly_chg else 0
    n_big_up = sum(1 for c in yearly_chg if c > 30)  # years with >30% gain

    results.append({
        'name': name, 'cat': cat, 'sym': sym, 'contract': main_contract,
        'current': current, 'pct_abs': pct_abs, 'pct_monthly': pct_monthly,
        'dd_3y': dd_from_peak, 'chg_1y': chg_1y, 'chg_3y': chg_3y,
        'ann_vol': ann_vol, 'pct_rangebound': pct_rangebound,
        'avg_up': avg_up, 'max_up': max_up, 'max_up_year': max_up_year,
        'n_big_up': n_big_up, 'data_years': len(yearly),
        'all_high': all_high, 'all_low': all_low
    })

# ===== SCORING SYSTEM =====
def score(val, ranges):
    """Linear score 1-10 within ranges"""
    lo, hi = ranges
    if val <= lo: return 10
    if val >= hi: return 1
    return 10 - (val - lo) / (hi - lo) * 9

for r in results:
    if r.get('error'): continue

    # V1: Valuation (lower percentile = more upside = higher score)
    v1 = score(r['pct_abs'], (0, 80))  # 0%=10, 80%=1

    # V2: 3-year drawdown (bigger DD = more recovery potential)
    v2 = score(abs(r['dd_3y']), (0, 60))  # 0% DD=1, 60%=10

    # V3: Historical tendency for big trends (n_big_up / data_years)
    big_up_rate = r['n_big_up'] / r['data_years'] * 100 if r['data_years'] > 0 else 0
    v3 = score(big_up_rate, (0, 30))  # 0%=1, 30%+=10

    # V4: Range-bound avoidance (lower range-bound time = more trend-prone)
    v4 = score(r['pct_rangebound'], (30, 90))  # 30%=10, 90%=1

    # V5: Volatility (higher vol = more trend potential)
    v5 = score(r['ann_vol'], (10, 40))  # 10%=1, 40%=10

    # V6: 3-year trend position (more negative = more recovery upside)
    v6 = score(r['chg_3y'], (-50, 50))  # -50%=10, +50%=1

    weights = {'v1': 0.25, 'v2': 0.15, 'v3': 0.15, 'v4': 0.15, 'v5': 0.15, 'v6': 0.15}
    total = v1*0.25 + v2*0.15 + v3*0.15 + v4*0.15 + v5*0.15 + v6*0.15

    r['scores'] = {'v1_val': v1, 'v2_val': v2, 'v3_val': v3, 'v4_val': v4, 'v5_val': v5, 'v6_val': v6, 'total': total}
    r['big_up_rate'] = big_up_rate

# ===== OUTPUT =====
valid = [r for r in results if not r.get('error')]
valid.sort(key=lambda x: x['scores']['total'], reverse=True)

print(f"\n{'排名':<5} {'品种':<12} {'价格':<10} {'绝对分位':<10} {'月线分位':<10} {'3年回撤':<10} {'1年涨跌':<10} {'3年涨跌':<10} {'大年概率':<10} {'震荡占比':<10} {'年化波动':<10}")
print("-" * 115)
for rank, r in enumerate(valid, 1):
    s = r['scores']
    print(f"  {rank:<3} {r['name']:<12} {r['current']:<10.0f} {r['pct_abs']:>6.1f}%   {r['pct_monthly']:>6.1f}%   {r['dd_3y']:>8.1f}%  {r['chg_1y']:>8.1f}%  {r['chg_3y']:>8.1f}%  {r['big_up_rate']:>5.0f}%     {r['pct_rangebound']:>5.0f}%     {r['ann_vol']:>7.1f}%")

print(f"\n{'='*90}")
print("评分明细 (1-10, 越高=越可能走大趋势)")
print(f"{'='*90}")
print(f"{'排名':<5} {'品种':<12} {'估值分':<8} {'回撤潜力':<10} {'历史趋势':<10} {'趋势倾向':<10} {'波动率':<8} {'3年位置':<10} {'总分':<8} {'板块':<10}")
print("-" * 100)
for rank, r in enumerate(valid, 1):
    s = r['scores']
    print(f"  {rank:<3} {r['name']:<12} {s['v1_val']:<8.1f} {s['v2_val']:<10.1f} {s['v3_val']:<10.1f} {s['v4_val']:<10.1f} {s['v5_val']:<8.1f} {s['v6_val']:<10.1f} {s['total']:<8.2f} {r['cat']:<10}")

# ===== KEY STATS =====
print(f"\n{'='*90}")
print("关键统计")
print(f"{'='*90}")
top5 = valid[:5]
print(f"\n量化排名 Top 5:")
for rank, r in enumerate(top5, 1):
    print(f"  {rank}. {r['name']} ({r['cat']}) — 总分 {r['scores']['total']:.2f}")
    print(f"     价格{r['current']:.0f} | 绝对分位{r['pct_abs']:.0f}% | 月线分位{r['pct_monthly']:.0f}% | 3年回撤{r['dd_3y']:.1f}% | 大年率{r['big_up_rate']:.0f}%")

# Category summary
cats = {}
for r in valid:
    c = r['cat']
    if c not in cats: cats[c] = []
    cats[c].append(r)

print(f"\n板块平均分:")
for c in ["贵金属","有色金属","黑色系","能源","化工","新能源","农产品"]:
    if c in cats:
        avg = sum(r['scores']['total'] for r in cats[c]) / len(cats[c])
        names = ", ".join(r['name'] for r in sorted(cats[c], key=lambda x: x['scores']['total'], reverse=True)[:3])
        print(f"  {c}: 均分 {avg:.2f} | 前3: {names}")

# Lowest and highest valuation
print(f"\n估值极端品种:")
very_low = [r for r in valid if r['pct_abs'] < 20]
very_high = [r for r in valid if r['pct_abs'] > 80]
if very_low:
    print(f"  极低位 (<20%): " + ", ".join(f"{r['name']}({r['pct_abs']:.0f}%)" for r in very_low))
if very_high:
    print(f"  极高位 (>80%): " + ", ".join(f"{r['name']}({r['pct_abs']:.0f}%)" for r in very_high))

print(f"\n数据日期: {datetime.now().strftime('%Y-%m-%d')}")