#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
将期货 7 大板块涨跌追加到每日期货日志
用法: python append_futures_sectors.py [YYYY-MM-DD]
"""
import subprocess, os, sys, json, re
from datetime import date

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"
NOTES = r"c:\Works\ClaudeCode\TradingStudio\docs\trading\Notes"
RUN_DATE = sys.argv[1] if len(sys.argv) > 1 else date.today().isoformat()
LOG_PATH = os.path.join(NOTES, "期货", f"{RUN_DATE}.md")

if not os.path.exists(LOG_PATH):
    print(f"日志 {LOG_PATH} 不存在，请先运行 full_generator.py")
    sys.exit(1)

# 期货 7 板块 + 跟踪合约
SECTORS = [
    ("能源", [("SC2609","原油SC"),("FU2609","燃料油FU"),("LU2609","低硫燃油LU")]),
    ("化工", [("TA2609","PTA"),("MA2609","甲醇MA"),("V2609","PVC"),("L2609","塑料L"),("EG2609","乙二醇"),("EB2609","苯乙烯"),("PP2609","聚丙烯")]),
    ("黑色系", [("RB2609","螺纹RB"),("I2609","铁矿I"),("JM2609","焦煤JM"),("J2609","焦炭J"),("HC2609","热卷HC")]),
    ("有色金属", [("CU2609","铜CU"),("AL2609","铝AL"),("ZN2609","锌ZN"),("NI2609","镍NI"),("SN2609","锡SN"),("PB2609","铅PB")]),
    ("贵金属", [("AU2609","黄金AU"),("AG2609","白银AG")]),
    ("农产品", [("M2609","豆粕M"),("Y2609","豆油Y"),("P2609","棕榈油P"),("SR2609","白糖SR"),("CF2609","棉花CF"),("RM2609","菜粕RM")]),
    ("新能源", [("LC2609","碳酸锂LC"),("SI2609","工业硅SI")]),
]

def fetch_kline(sym):
    url = f'https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20d=/InnerFuturesNewService.getDailyKLine?symbol={sym}'
    try:
        r = subprocess.run(['curl','-s','--max-time','15','-H','Referer: https://finance.sina.com.cn','-H','User-Agent: Mozilla/5.0',url], capture_output=True, timeout=20)
        t = r.stdout.decode('gbk', errors='replace')
        if '[' not in t: return None
        kls = json.loads(t[t.index('['):t.rindex(']')+1])
        return kls
    except: return None

# 计算各板块今日涨跌
sector_data = []
for cat_name, contracts in SECTORS:
    chgs = []
    details = []
    for sym, name in contracts:
        kls = fetch_kline(sym)
        if not kls or len(kls) < 2: continue
        try:
            prev_s = float(kls[-2]['s']); curr_s = float(kls[-1]['s'])
            if prev_s > 0:
                chg = (curr_s - prev_s) / prev_s * 100
                oi_chg = int(kls[-1].get('p', 0)) - int(kls[-2].get('p', 0))
                chgs.append(chg)
                details.append((name, chg, oi_chg))
        except: pass
    if chgs:
        avg = sum(chgs) / len(chgs)
        sector_data.append((avg, cat_name, details))
sector_data.sort(reverse=True)

# 计算综合商品指数
all_chgs = []
for _, _, details in sector_data:
    for _, chg, _ in details:
        all_chgs.append(chg)
composite = sum(all_chgs) / len(all_chgs) if all_chgs else 0

# 读取日志
with open(LOG_PATH, 'r', encoding='utf-8') as f:
    content = f.read()

if '## 七、板块表现（详细）' in content:
    print(f"{RUN_DATE} 日志已含板块段，跳过。")
    sys.exit(0)

insert_pos = content.find('## 八、链接')
if insert_pos == -1:
    insert_pos = content.rfind('\n---\n')

if insert_pos == -1:
    print("无法定位插入位置")
    sys.exit(1)

# 构建 Markdown
lines = []
lines.append('')
lines.append('---')
lines.append('')
lines.append('## 七、板块表现（详细）')
lines.append('')
lines.append(f'> 综合商品指数估算: **{composite:+.2f}%** | 按结算价计算')
lines.append('')

for avg, cat_name, details in sector_data:
    flag = '+' if avg > 0 else ('~' if avg > -0.5 else '-')
    lines.append(f'### {flag} {cat_name}（均值 {avg:+.2f}%）')
    lines.append(f'| 品种 | 涨跌 | 持仓变化 | 信号 |')
    lines.append(f'|------|------|------|------|')
    for name, chg, oi in sorted(details, key=lambda x: x[1], reverse=True):
        oi_str = f'增仓 {oi:+}' if oi > 0 else (f'减仓 {oi}' if oi < 0 else '--')
        sig = '增仓上涨' if chg > 0 and oi > 0 else ('减仓上涨' if chg > 0 and oi < 0 else ('增仓下跌' if chg < 0 and oi > 0 else '减仓下跌'))
        print_flag = '+' if chg > 0 else '-'
        lines.append(f'| {print_flag} {name} | {chg:+.2f}% | {oi_str} | {sig} |')
    lines.append('')

new_content = content[:insert_pos] + '\n'.join(lines) + '\n' + content[insert_pos:]

with open(LOG_PATH, 'w', encoding='utf-8') as f:
    f.write(new_content)

print(f"{RUN_DATE} 期货板块段已追加。综合商品 {composite:+.2f}%，{len(sector_data)}个板块。")
