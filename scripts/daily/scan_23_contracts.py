#!/usr/bin/env python3
"""
23个重点期货品种 — 全量扫描
输出：当前价格/涨跌幅/持仓变化/3日趋势/关键价位
"""
import subprocess, json, sys
from datetime import datetime

CONTRACTS = [
    # (symbol, name, category, fallback_symbols)
    ("AG2612", "白银 AG", "贵金属", ["AG2609","AG2610","AG2608"]),
    ("AU2612", "黄金 AU", "贵金属", ["AU2609","AU2610","AU2608"]),
    ("LC2609", "碳酸锂 LC", "新能源", ["LC2610","LC2611"]),
    ("CU2609", "沪铜 CU", "有色金属", ["CU2610","CU2611"]),
    ("SN2609", "沪锡 SN", "有色金属", ["SN2610","SN2611"]),
    ("NI2609", "沪镍 NI", "有色金属", ["NI2610","NI2611"]),
    ("I2609", "铁矿石 I", "黑色系", ["I2610","I2601"]),
    ("JM2609", "焦煤 JM", "黑色系", ["JM2610","JM2601"]),
    ("SA2701", "纯碱 SA", "化工", ["SA2609","SA2610"]),
    ("FG2609", "玻璃 FG", "化工", ["FG2610","FG2701"]),
    ("SR2609", "白糖 SR", "农产品", ["SR2611","SR2701"]),
    ("RU2701", "天然橡胶 RU", "化工", ["RU2609","RU2611"]),
    ("AO2609", "氧化铝 AO", "有色金属", ["AO2610","AO2611"]),
    ("SH2609", "烧碱 SH", "化工", ["SH2610","SH2611"]),
    ("V2609", "PVC V", "化工", ["V2610","V2601"]),
    ("P2609", "棕榈油 P", "农产品", ["P2610","P2601"]),
    ("FU2609", "燃料油 FU", "能源", ["FU2610","FU2611"]),
    ("UR2609", "尿素 UR", "化工", ["UR2610","UR2601"]),
    ("CJ2609", "红枣 CJ", "农产品", ["CJ2610","CJ2601"]),
    ("CF2609", "棉花 CF", "农产品", ["CF2611","CF2701"]),
    ("TA2609", "PTA TA", "化工", ["TA2610","TA2601"]),
    ("M2609", "豆粕 M", "农产品", ["M2611","M2701"]),
    ("RM2609", "菜粕 RM", "农产品", ["RM2611","RM2701"]),
]

def fetch_contract(symbol):
    url = f'https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{symbol}=/InnerFuturesNewService.getDailyKLine?symbol={symbol}'
    try:
        r = subprocess.run(['curl','-s','--max-time','12',
            '-H','Referer: https://finance.sina.com.cn',
            '-H','User-Agent: Mozilla/5.0', url], capture_output=True, timeout=18)
        text = r.stdout.decode('gbk', errors='replace')
        if 'null' in text[:200] or '[' not in text:
            return None
        j = json.loads(text[text.index('['):text.rindex(']')+1])
        if len(j) < 2:
            return None
        return j
    except:
        return None

def calc_ma(klines, period):
    closes = [float(k['c']) for k in klines[-period:]]
    return sum(closes) / len(closes) if closes else 0

print("=" * 90)
print(f"23个重点品种全量扫描 — {datetime.now().strftime('%Y-%m-%d %H:%M')}")
print("=" * 90)

results = []
for sym, name, cat, fallbacks in CONTRACTS:
    klines = fetch_contract(sym)
    actual_sym = sym
    if klines is None:
        for fb in fallbacks:
            klines = fetch_contract(fb)
            if klines:
                actual_sym = fb
                break
    if klines is None or len(klines) < 3:
        results.append({'name': name, 'sym': sym, 'cat': cat, 'error': True})
        continue

    today = klines[-1]
    yesterday = klines[-2]
    day3 = klines[-3] if len(klines) >= 3 else yesterday

    close = float(today['c'])
    prev_close = float(yesterday['c'])
    chg_pct = (close - prev_close) / prev_close * 100
    oi = int(today.get('p', 0))
    prev_oi = int(yesterday.get('p', 0))
    oi_chg = oi - prev_oi

    # 3-day and 5-day change
    c3 = float(day3['c'])
    chg_3d = (close - c3) / c3 * 100

    # 5-day change
    if len(klines) >= 6:
        c5 = float(klines[-6]['c'])
        chg_5d = (close - c5) / c5 * 100
    else:
        chg_5d = None

    # MA positions
    ma20 = calc_ma(klines, 20)
    ma60 = calc_ma(klines, min(60, len(klines)))
    ma_pct = (close - ma20) / ma20 * 100 if ma20 else 0

    # Range (high-low spread today)
    high = float(today['h'])
    low = float(today['l'])
    range_pct = (high - low) / prev_close * 100
    vol = int(today.get('v', 0))
    prev_vol = int(yesterday.get('v', 0))
    vol_ratio = vol / prev_vol if prev_vol else 1

    # 20-day high/low position
    highs_20 = [float(k['h']) for k in klines[-20:]]
    lows_20 = [float(k['l']) for k in klines[-20:]]
    h20, l20 = max(highs_20), min(lows_20)
    pos_20d = (close - l20) / (h20 - l20) * 100 if h20 != l20 else 50

    results.append({
        'name': name, 'sym': actual_sym, 'cat': cat,
        'close': close, 'chg': chg_pct, 'oi': oi, 'oi_chg': oi_chg,
        'chg_3d': chg_3d, 'chg_5d': chg_5d,
        'ma20_pct': ma_pct, 'range': range_pct,
        'vol_ratio': vol_ratio, 'pos_20d': pos_20d,
        'high': high, 'low': low,
        'date': today['d']
    })

# ============ OUTPUT ============

# Sort by category then absolute change
cats_order = ["贵金属","有色金属","黑色系","能源","化工","新能源","农产品"]

print(f"\n{'品种':<14} {'合约':<8} {'收盘':<10} {'日涨跌':<8} {'3日':<8} {'5日':<8} {'持仓变化':<12} {'仓变方向':<10} {'MA20位':<8} {'20日位置':<10} {'放量':<8}")
print("-" * 115)

for cat in cats_order:
    cat_results = [r for r in results if r['cat'] == cat]
    if not cat_results:
        continue
    print(f"\n--- {cat} ---")
    for r in cat_results:
        if r.get('error'):
            print(f"  {r['name']:<14} {'ERROR':<8} 数据获取失败")
            continue

        # OI direction
        if r['oi_chg'] > 5000:
            oi_dir = "大幅增仓 ↑"
        elif r['oi_chg'] > 0:
            oi_dir = "增仓 ↑"
        elif r['oi_chg'] < -5000:
            oi_dir = "大幅减仓 ↓"
        elif r['oi_chg'] < 0:
            oi_dir = "减仓 ↓"
        else:
            oi_dir = "—"

        # MA20 position
        if r['ma20_pct'] > 3:
            ma_pos = f"上方 {r['ma20_pct']:.0f}%"
        elif r['ma20_pct'] > 0:
            ma_pos = f"上方 {r['ma20_pct']:.0f}%"
        elif r['ma20_pct'] > -3:
            ma_pos = f"附近 {r['ma20_pct']:.0f}%"
        else:
            ma_pos = f"下方 {abs(r['ma20_pct']):.0f}%"

        # Volume
        if r['vol_ratio'] > 1.5:
            vol_str = f"放量 {r['vol_ratio']:.1f}x"
        elif r['vol_ratio'] > 1.2:
            vol_str = f"放量 {r['vol_ratio']:.1f}x"
        elif r['vol_ratio'] < 0.7:
            vol_str = f"缩量 {r['vol_ratio']:.1f}x"
        else:
            vol_str = "—"

        # 5-day
        chg5_str = f"{r['chg_5d']:+.1f}%" if r['chg_5d'] is not None else "—"

        print(f"  {r['name']:<14} {r['sym']:<8} {r['close']:<10.0f} {r['chg']:+7.2f}% {r['chg_3d']:+6.1f}% {chg5_str:<8} {r['oi_chg']:>+10,}  {oi_dir:<10} {ma_pos:<8} {r['pos_20d']:>5.0f}% {'':>2} {vol_str}")

# ============ SIGNALS SUMMARY ============
print("\n" + "=" * 90)
print("SIGNALS SUMMARY")
print("=" * 90)

# Strong gainers (>2%)
gainers = [r for r in results if not r.get('error') and r['chg'] > 2]
gainers.sort(key=lambda x: x['chg'], reverse=True)
print(f"\n[UP] Strong Gainers (>2%):")
for r in gainers:
    oi_signal = "NewLong" if r['oi_chg'] > 0 else "ShortCover"
    print(f"  {r['name']}: {r['chg']:+.2f}% | {oi_signal} ({r['oi_chg']:+,}) | 20dPos={r['pos_20d']:.0f}%")

# Decliners (<-1%)
losers = [r for r in results if not r.get('error') and r['chg'] < -1]
losers.sort(key=lambda x: x['chg'])
print(f"\n[DOWN] Notable Decliners (<-1%):")
for r in losers:
    oi_signal = "NewShort" if r['oi_chg'] < 0 else "LongCover"
    print(f"  {r['name']}: {r['chg']:+.2f}% | {oi_signal} ({r['oi_chg']:+,}) | 20dPos={r['pos_20d']:.0f}%")

# Heavy OI increase
oi_up = [r for r in results if not r.get('error') and r['oi_chg'] > 5000]
oi_up.sort(key=lambda x: x['oi_chg'], reverse=True)
print(f"\n[OI] Heavy OI Increase (>5000):")
for r in oi_up:
    dir_str = "OIUp+Up" if r['chg'] > 0 else "OIUp+Dn"
    print(f"  {r['name']}: OI {r['oi_chg']:+,} | {dir_str} | {r['chg']:+.2f}%")

# Extreme 20-day position
high_pos = [r for r in results if not r.get('error') and r['pos_20d'] > 80]
low_pos = [r for r in results if not r.get('error') and r['pos_20d'] < 20]
if high_pos:
    print(f"\n[HIGH] 20d High (>80%): ", end="")
    print(", ".join(f"{r['name']}({r['pos_20d']:.0f}%)" for r in high_pos))
if low_pos:
    print(f"\n[LOW] 20d Low (<20%): ", end="")
    print(", ".join(f"{r['name']}({r['pos_20d']:.0f}%)" for r in low_pos))

# Continuous trend (3d and 5d same direction)
print(f"\n[TREND] Continuous (3d+5d same direction):")
trending = [r for r in results if not r.get('error') and r['chg_5d'] is not None
            and ((r['chg_3d'] > 1 and r['chg_5d'] > 1) or (r['chg_3d'] < -1 and r['chg_5d'] < -1))]
trending.sort(key=lambda x: x['chg_5d'] or 0, reverse=True)
for r in trending:
    arrow = "UP" if (r['chg_5d'] or 0) > 0 else "DN"
    print(f"  {r['name']}: 3d {r['chg_3d']:+.1f}% | 5d {r['chg_5d']:+.1f}% {arrow}")

print(f"\nData Date: {results[0]['date'] if results and not results[0].get('error') else 'N/A'}")
