#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
将 20 行业 ETF 板块涨幅追加到每日股票日志
用法: python append_sectors.py [YYYY-MM-DD]
在 full_generator.py 之后运行
"""
import subprocess, os, sys, csv
from datetime import date

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"
NOTES = r"c:\Works\ClaudeCode\TradingStudio\docs\trading\Notes"
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
RUN_DATE = sys.argv[1] if len(sys.argv) > 1 else date.today().isoformat()
LOG_PATH = os.path.join(NOTES, "股票", f"{RUN_DATE}.md")
CSV_PATH = os.path.join(SCRIPT_DIR, "sectors_watch.csv")

# 检查日志是否存在
if not os.path.exists(LOG_PATH):
    print(f"日志 {LOG_PATH} 不存在，请先运行 full_generator.py")
    sys.exit(1)

# 读取当日板块数据
if not os.path.exists(CSV_PATH):
    print(f"CSV {CSV_PATH} 不存在，请先运行 watch_sectors.py")
    sys.exit(1)

sectors_today = []
with open(CSV_PATH, 'r', encoding='utf-8-sig', newline='') as f:
    for r in csv.reader(f):
        if r and r[0] == RUN_DATE:
            sectors_today.append((float(r[4]), float(r[6]), r[2], r[3]))
sectors_today.sort(reverse=True)

if not sectors_today:
    print(f"CSV 中无 {RUN_DATE} 数据，请先运行 watch_sectors.py {RUN_DATE}")
    sys.exit(1)

# 读取日志，检查是否已追加
with open(LOG_PATH, 'r', encoding='utf-8') as f:
    content = f.read()

if '## 板块涨幅（20行业ETF）' in content:
    print(f"{RUN_DATE} 日志已含板块涨幅段，跳过。")
    sys.exit(0)

# 找到插入位置：在「七、链接」之前
insert_pos = content.find('## 七、链接')
if insert_pos == -1:
    # fallback: find last ---
    insert_pos = content.rfind('\n---\n')

if insert_pos == -1:
    print("无法定位插入位置")
    sys.exit(1)

# 构建板块涨幅 Markdown
lines = []
lines.append('')
lines.append('---')
lines.append('')
lines.append('## 板块涨幅（20行业ETF）')
lines.append('')
lines.append(f'> 数据：scripts/watch_sectors.py | 基准：国证A股')
lines.append('')

cats_order = ["科技", "新能源", "制造", "周期", "消费", "医药", "金融", "防御"]
for cat in cats_order:
    items = [(c, rs, name) for c, rs, name, cc in sectors_today if cc == cat]
    if not items:
        continue
    items.sort(reverse=True)
    lines.append(f'### {cat}')
    lines.append(f'| 排名 | 行业 | 涨跌 | 相对强弱 |')
    lines.append(f'|------|------|------|------|')
    for rank, (chg, rs, name) in enumerate(items, 1):
        flag = '+' if chg > 0 else ('~' if rs > 0 else '-')
        lines.append(f'| {rank} | {flag} {name} | {chg:+.2f}% | {rs:+.2f}% |')
    lines.append('')

new_content = content[:insert_pos] + '\n'.join(lines) + '\n' + content[insert_pos:]

with open(LOG_PATH, 'w', encoding='utf-8') as f:
    f.write(new_content)

print(f"{RUN_DATE} 板块涨幅段已追加到日志。")
