#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
创新药/医药 精选观察层
用法: python watch_pharma.py [YYYY-MM-DD]
- 每天收盘跑一次，跟踪7只精选 + 4只ETF参考
- 累积到 pharma_watch.csv
"""
import subprocess, re, os, csv, sys
from datetime import date
try: sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_FILE = os.path.join(SCRIPT_DIR, "pharma_watch.csv")
RUN_DATE = sys.argv[1] if len(sys.argv) > 1 else date.today().isoformat()

# 7精选 + 4ETF参考 + 1基准(国证A股)
WATCH = [
    # 创新药
    ("sh603259","药明康德","CXO龙头"),
    ("sz300759","康龙化成","CXO龙二"),
    ("sh688331","荣昌生物","ADC"),
    ("sz000963","华东医药","GLP-1"),
    # 医药
    ("sh600276","恒瑞医药","仿创龙头"),
    ("sz300347","泰格医药","CRO"),
    ("sh600085","同仁堂","中药"),
    # ETF参考
    ("sz159992","创新药ETF","ETF"),
    ("sh512010","医药ETF","ETF"),
    ("sh560080","中药ETF","ETF"),
    ("sh513120","港股创新药ETF","ETF"),
]
BENCHMARK = ("sh000001","上证指数")  # 用上证作参考基准（新浪有）

HISTORY_DAYS = 5

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

all_codes = [BENCHMARK[0]] + [c[0] for c in WATCH]
chg = fetch(all_codes)
bench = chg.get(BENCHMARK[0], 0)

rows_today = []
for code, name, note in WATCH:
    if code in chg:
        c = chg[code]
        rows_today.append((c, c - bench, name, note, code))

# CSV去重追加
existing = []
if os.path.exists(DATA_FILE):
    with open(DATA_FILE, 'r', encoding='utf-8-sig', newline='') as f:
        existing = [r for r in csv.reader(f)]
header = ["date","code","name","note","chg","bench_chg","rs"]
kept = [r for r in existing if r and r[0]!=header[0] and not (r[0]==RUN_DATE and r[1] in {x[4] for x in rows_today})]
new = [[RUN_DATE, code, name, note, f"{c:.2f}", f"{bench:.2f}", f"{rs:.2f}"] for c, rs, name, note, code in rows_today]
with open(DATA_FILE, 'w', encoding='utf-8-sig', newline='') as f:
    w = csv.writer(f); w.writerow(header)
    for r in kept: w.writerow(r)
    for r in new: w.writerow(r)

# 输出
print("="*60)
print(f"创新药/医药 精选观察  {RUN_DATE}   基准上证 {bench:+.2f}%")
print("="*60)
print(f"{'名称':<10}{'细分':<10}{'涨跌%':>9}{'相对强弱':>11}")
print("-"*60)
for c, rs, name, note, code in sorted(rows_today, reverse=True):
    flag = "[+]" if c > 0 else ("[~]" if rs > 0 else "[-]")
    print(f"{flag} {name:<9}{note:<10}{c:>+8.2f}%{rs:>+9.2f}%")

# 持续性
with open(DATA_FILE, 'r', encoding='utf-8-sig', newline='') as f:
    rows = [r for r in csv.reader(f) if r and r[0] != "date"]
dates = sorted({r[0] for r in rows})[-HISTORY_DAYS:]
if len(dates) >= 2:
    print("\n" + "="*60)
    print(f"持续性统计（近{len(dates)}日：{dates[0]} ~ {dates[-1]}）")
    print("-"*60)
    stat = {}
    for r in rows:
        if r[0] in dates:
            stat.setdefault(r[1], {"name": r[2], "rs": [], "days": 0})
            rs = float(r[6]); stat[r[1]]["rs"].append(rs)
            if rs > 0: stat[r[1]]["days"] += 1
    ranked = sorted(stat.values(), key=lambda s: (s["days"], sum(s["rs"])/len(s["rs"])), reverse=True)
    for s in ranked:
        avg = sum(s["rs"]) / len(s["rs"])
        star = " *" if s["days"] == len(dates) and avg > 0 else ""
        print(f"{s['name']:<10} 跑赢 {s['days']}/{len(dates)} 日   平均RS {avg:>+6.2f}%{star}")
else:
    print("\n(历史仅1日，明日起可看持续性)")

print(f"\n数据: scripts/pharma_watch.csv")
