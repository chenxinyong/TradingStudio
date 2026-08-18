#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
防御/轮动观察层
- 每个交易日收盘后运行一次：python watch_rotation.py [YYYY-MM-DD]
- 抓取候选板块 ETF，计算相对国证A股(399317)的相对强弱(RS)
- 把每天结果累积到 rotation_watch.csv
- 输出：① 今日板块强弱排序  ② 多日持续性(近N日 RS>0 天数 / 平均RS)
用途：区分「持续相对强 = 真轮动」和「只是今天抗跌」。
"""
import subprocess, re, os, csv, sys
from datetime import date

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_FILE = os.path.join(SCRIPT_DIR, "rotation_watch.csv")
RUN_DATE = sys.argv[1] if len(sys.argv) > 1 else date.today().isoformat()

BENCHMARK = ("sz399317", "国证A股")

# 观察池：防御/轮动候选 + 你的科技池(参考对照)
WATCH = [
    ("sh560080", "中药",       "防御-医药"),
    ("sz159992", "创新药",     "防御-医药"),
    ("sh512010", "医药",       "防御-医药"),
    ("sh513120", "港股创新药", "防御-医药"),
    ("sh512800", "银行",       "防御-价值"),
    ("sh510880", "红利",       "防御-价值"),
    ("sz159928", "消费",       "防御-价值"),
    ("sh518880", "黄金",       "避险"),
    ("sz159611", "电力",       "防御-公用"),
    ("sh512000", "券商",       "情绪风向"),
    ("sh512480", "半导体",     "科技池-参考"),
    ("sh588000", "科创50",     "科技池-参考"),
    ("sz159915", "创业板",     "科技池-参考"),
]

HISTORY_DAYS = 5  # 持续性统计窗口


def fetch(codes):
    url = "https://hq.sinajs.cn/list=" + ",".join(codes)
    try:
        r = subprocess.run(
            ['curl', '-s', '--max-time', '20',
             '-H', 'Referer: https://finance.sina.com.cn',
             '-H', 'User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
             url], capture_output=True, timeout=25)
        text = r.stdout.decode('gbk', errors='replace')
    except Exception as e:
        print("抓取失败:", e); return {}
    out = {}
    for line in text.strip().split('\n'):
        m = re.match(r'var hq_str_([^=]+)="(.*)"', line)
        if m:
            d = m.group(2).split(',')
            try:
                pc, cur = float(d[2]), float(d[3])
                if pc > 0 and cur > 0:
                    out[m.group(1)] = (cur - pc) / pc * 100
            except Exception:
                pass
    return out


# ---- 抓取 ----
all_codes = [BENCHMARK[0]] + [c[0] for c in WATCH]
chg = fetch(all_codes)
bench = chg.get(BENCHMARK[0])
if bench is None:
    print("基准(国证A股)抓取失败，终止。"); sys.exit(1)

# ---- 计算 & 写入历史 ----
rows_today = []  # (chg, rs, name, cat, code)
for code, name, cat in WATCH:
    if code in chg:
        c = chg[code]
        rows_today.append((c, c - bench, name, cat, code))

# 追加到 CSV（去重同日同code）
existing = []
if os.path.exists(DATA_FILE):
    with open(DATA_FILE, 'r', encoding='utf-8-sig', newline='') as f:
        existing = [r for r in csv.reader(f)]
header = ["date", "code", "name", "cat", "chg", "bench_chg", "rs"]
seen_today = {(RUN_DATE, r[4]) for r in rows_today}  # placeholder, refine below
kept = [r for r in existing if r and r[0] != header[0] and not (r[0] == RUN_DATE and r[1] in {x[4] for x in rows_today})]
new_rows = [[RUN_DATE, code, name, cat, f"{c:.2f}", f"{bench:.2f}", f"{rs:.2f}"]
            for c, rs, name, cat, code in rows_today]
with open(DATA_FILE, 'w', encoding='utf-8-sig', newline='') as f:
    w = csv.writer(f)
    w.writerow(header)
    for r in kept:
        w.writerow(r)
    for r in new_rows:
        w.writerow(r)

# ---- 输出 ① 今日排序 ----
print("=" * 60)
print(f"防御/轮动观察层  {RUN_DATE}   基准 国证A股 {bench:+.2f}%")
print("=" * 60)
print(f"{'板块':<10}{'类别':<12}{'涨跌%':>9}{'相对强弱':>11}")
print("-" * 60)
for c, rs, name, cat, code in sorted(rows_today, reverse=True):
    flag = "[+]" if c > 0 else ("[~]" if rs > 0 else "[-]")
    print(f"{flag} {name:<9}{cat:<12}{c:>+8.2f}%{rs:>+9.2f}%")

# ---- 输出 ② 多日持续性 ----
with open(DATA_FILE, 'r', encoding='utf-8-sig', newline='') as f:
    rows = [r for r in csv.reader(f) if r and r[0] != "date"]
dates = sorted({r[0] for r in rows})[-HISTORY_DAYS:]
if len(dates) >= 2:
    print("\n" + "=" * 60)
    print(f"持续性统计（近 {len(dates)} 个记录日：{dates[0]} ~ {dates[-1]}）")
    print("RS>0天数 = 跑赢基准的天数；越多越可能是真轮动")
    print("-" * 60)
    stat = {}
    for r in rows:
        if r[0] in dates:
            stat.setdefault(r[1], {"name": r[2], "rs": [], "days": 0})
            rs = float(r[6]); stat[r[1]]["rs"].append(rs)
            if rs > 0: stat[r[1]]["days"] += 1
    ranked = sorted(stat.values(), key=lambda s: (s["days"], sum(s["rs"]) / len(s["rs"])), reverse=True)
    for s in ranked:
        avg = sum(s["rs"]) / len(s["rs"])
        star = " *" if s["days"] == len(dates) and avg > 0 else ""
        print(f"{s['name']:<10} 跑赢 {s['days']}/{len(dates)} 日   平均RS {avg:>+6.2f}%{star}")
else:
    print("\n（历史仅1日，明日起累积后可看持续性。每交易日收盘后跑一次即可。）")

print("\n数据文件:", os.path.relpath(DATA_FILE, os.path.dirname(SCRIPT_DIR)))
