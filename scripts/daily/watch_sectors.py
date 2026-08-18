#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
20 行业 ETF 周度扫描
用法: python watch_sectors.py [YYYY-MM-DD]
累积到 sectors_watch.csv
"""
import subprocess, re, os, csv, sys
from datetime import date
try: sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_FILE = os.path.join(SCRIPT_DIR, "sectors_watch.csv")
RUN_DATE = sys.argv[1] if len(sys.argv) > 1 else date.today().isoformat()

SECTORS = [
    # === 科技 (4) ===
    ("sh512480","半导体","科技"),
    ("sh512720","计算机","科技"),
    ("sh515880","通信","科技"),
    ("sh512980","传媒","科技"),
    # === 新能源 (2) ===
    ("sh516160","新能源","新能源"),
    ("sz159611","电力","新能源"),
    # === 制造 (2) ===
    ("sh512660","军工","制造"),
    ("sh516110","汽车","制造"),
    # === 周期 (4) ===
    ("sh512400","有色","周期"),
    ("sh515220","煤炭","周期"),
    ("sz159870","化工","周期"),
    ("sh515210","钢铁","周期"),
    # === 消费 (2) ===
    ("sh515170","食品饮料","消费"),
    ("sh512690","酒","消费"),
    # === 医药 (3) ===
    ("sz159992","创新药","医药"),
    ("sh512010","医药","医药"),
    ("sh560080","中药","医药"),
    # === 金融 (2) ===
    ("sh512800","银行","金融"),
    ("sh512200","房地产","金融"),
    # === 综合 (1) ===
    ("sh510880","红利","防御"),
]

BENCHMARK = ("sz399317","国证A股")
HISTORY_DAYS = 8

def fetch(codes):
    url = "https://hq.sinajs.cn/list=" + ",".join(codes)
    try:
        r = subprocess.run(['curl','-s','--max-time','20',
            '-H','Referer: https://finance.sina.com.cn',
            '-H','User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
            url], capture_output=True, timeout=25)
        text = r.stdout.decode('gbk', errors='replace')
    except: return {}
    out = {}
    for line in text.strip().split('\n'):
        m = re.match(r'var hq_str_([^=]+)="(.*)"', line)
        if m:
            d = m.group(2).split(',')
            try:
                pc, cur = float(d[2]), float(d[3])
                if pc > 0 and cur > 0:
                    out[m.group(1)] = (cur - pc) / pc * 100
            except: pass
    return out

all_codes = [BENCHMARK[0]] + [c[0] for c in SECTORS]
chg = fetch(all_codes)
bench = chg.get(BENCHMARK[0], 0)

rows_today = []
for code, name, cat in SECTORS:
    if code in chg:
        c = chg[code]
        rows_today.append((c, c - bench, name, cat, code))

# CSV去重追加
existing = []
if os.path.exists(DATA_FILE):
    with open(DATA_FILE, 'r', encoding='utf-8-sig', newline='') as f:
        existing = [r for r in csv.reader(f)]
header = ["date","code","name","cat","chg","bench_chg","rs"]
today_codes = {x[4] for x in rows_today}
kept = [r for r in existing if r and r[0] != header[0] and not (r[0] == RUN_DATE and r[1] in today_codes)]
new = [[RUN_DATE, code, name, cat, f"{c:.2f}", f"{bench:.2f}", f"{rs:.2f}"]
       for c, rs, name, cat, code in rows_today]
with open(DATA_FILE, 'w', encoding='utf-8-sig', newline='') as f:
    w = csv.writer(f); w.writerow(header)
    for r in kept: w.writerow(r)
    for r in new: w.writerow(r)

# === 输出 ===
print("=" * 70)
print(f"20 行业 ETF 周度扫描  {RUN_DATE}   基准 国证A股 {bench:+.2f}%")
print("=" * 70)

# 按大类分组输出
cats_order = ["科技","新能源","制造","周期","消费","医药","金融","防御"]
for cat in cats_order:
    items = [(c, rs, name, code) for c, rs, name, cc, code in rows_today if cc == cat]
    if not items: continue
    items.sort(reverse=True)
    print(f"\n  [{cat}]")
    print(f"  {'名称':<10}{'涨跌':>8}{'相对强弱':>10}{'方向':>6}")
    print(f"  {'-'*36}")
    for chg_pct, rs, name, code in items:
        flag = "+" if chg_pct > 0 else ("~" if rs > 0 else "-")
        print(f"  {flag} {name:<8}{chg_pct:>+7.2f}%{rs:>+9.2f}%")

# 全排名
print(f"\n{'='*70}")
print("全排名 (按当日涨跌)")
print(f"{'='*70}")
all_sorted = sorted(rows_today, reverse=True)
for i, (c, rs, name, cat, code) in enumerate(all_sorted):
    flag = "+" if c > 0 else ("~" if rs > 0 else "-")
    print(f"  {i+1:>2}. {flag} {name:<10} [{cat:<6}] {c:>+7.2f}%  RS{rs:>+7.2f}%")

# 持续性
with open(DATA_FILE, 'r', encoding='utf-8-sig', newline='') as f:
    rows = [r for r in csv.reader(f) if r and r[0] != "date"]
dates = sorted({r[0] for r in rows})[-HISTORY_DAYS:]
if len(dates) >= 2:
    print(f"\n{'='*70}")
    print(f"持续性 (近{len(dates)}日: {dates[0]}~{dates[-1]})  RS>0 = 跑赢国证A股")
    print(f"{'='*70}")
    stat = {}
    for r in rows:
        if r[0] in dates:
            stat.setdefault(r[2], {"cat": r[3], "rs": [], "days": 0})
            rs = float(r[6]); stat[r[2]]["rs"].append(rs)
            if rs > 0: stat[r[2]]["days"] += 1
    ranked = sorted(stat.items(), key=lambda x: (x[1]["days"], sum(x[1]["rs"])/len(x[1]["rs"])), reverse=True)
    for name, s in ranked:
        avg = sum(s["rs"]) / len(s["rs"])
        star = " *" if s["days"] == len(dates) and avg > 0 else ""
        print(f"  {name:<10} [{s['cat']:<6}] 跑赢 {s['days']}/{len(dates)} 日  均RS {avg:>+6.2f}%{star}")

print(f"\n数据: scripts/sectors_watch.csv")
