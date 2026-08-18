#!/usr/bin/env python
"""第6层 储能与调节 周线扫描"""
import subprocess, json
from datetime import datetime as dt
try: import sys; sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

stocks = [
    ('sz300750','宁德时代','电池龙头'),('sz300014','亿纬锂能','电池'),
    ('sz300274','阳光电源','逆变器'),('sh600406','国电南瑞','电网'),
    ('sz002121','科陆电子','储能'),('sh605117','德业股份','逆变器/储能'),
    ('sh688032','禾迈股份','微逆'),('sh603063','禾望电气','变流器'),
    ('sz300763','锦浪科技','逆变器'),('sz002245','蔚蓝锂芯','电池'),
    ('sz000400','许继电气','电网'),('sh600089','特变电工','变压器'),
    ('sh601179','中国西电','电网'),('sh600312','平高电气','开关'),
    ('sh688100','威胜信息','电表'),('sz300882','万胜智能','电表'),
    ('sh688248','南网科技','电网AI'),
]

results = []
for code, name, seg in stocks:
    sid = '0.' + code[2:] if code.startswith('sz') else '1.' + code[2:]
    url = f'https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={sid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57&klt=101&fqt=0&end=20500101&lmt=60'
    r = subprocess.run(['curl','-s','--max-time','10','-H','Referer: https://quote.eastmoney.com','-H','User-Agent: Mozilla/5.0',url], capture_output=True, timeout=15)
    data = json.loads(r.stdout)['data']['klines']
    # 聚合周线
    weekly = []
    cur_wk = None
    wk = {}
    for line in data:
        parts = line.split(',')
        d, o, c, h, l = parts[0], float(parts[1]), float(parts[2]), float(parts[3]), float(parts[4])
        dd = dt.fromisoformat(d)
        wk_key = dd.strftime('%G-W%V')
        if wk_key != cur_wk:
            if wk:
                weekly.append(wk)
            cur_wk = wk_key
            wk = {'d': d, 'o': o, 'h': h, 'l': l, 'c': c}
        else:
            if h > wk['h']:
                wk['h'] = h
            if l < wk['l']:
                wk['l'] = l
            wk['c'] = c
            wk['d'] = d
    if wk:
        weekly.append(wk)
    if len(weekly) < 3:
        continue
    cur = weekly[-1]
    prev = weekly[-2]
    w4 = weekly[-4] if len(weekly) >= 4 else prev
    wk_chg = (cur['c'] - prev['c']) / prev['c'] * 100
    tr4 = (cur['c'] - w4['c']) / w4['c'] * 100
    all_h = max(w['h'] for w in weekly)
    dd_pct = (cur['c'] - all_h) / all_h * 100
    results.append((wk_chg, name, seg, cur['c'], tr4, dd_pct))

results.sort(reverse=True)
print('第6层 储能与调节  周线表现 (截至07-24本周收盘)')
print('=' * 70)
print(f'{"名称":<8} {"细分":<12} {"收盘":>8} {"本周":>8} {"近4周":>8} {"回撤":>8}')
print('-' * 70)
for chg, name, seg, c, tr4, dd in results:
    print(f'{name:<8} {seg:<12} {c:>8.2f} {chg:>+7.2f}% {tr4:>+7.2f}% {dd:>+7.1f}%')

avg_wk = sum(r[0] for r in results) / len(results)
avg_dd = sum(r[5] for r in results) / len(results)
up = sum(1 for r in results if r[0] > 0)
print('-' * 70)
print(f'板块均值: 本周{avg_wk:+.2f}%  回撤{avg_dd:.1f}%  {up}/{len(results)}涨')

# 对比 07-23 日数据
print()
print('07-23 vs 07-24 对比:')
print('07-23: 储能 +5.03% (17/17全涨) — 政策催化暴涨')
print('07-24: 储能 -2.92% (1/17涨)   — 一日游回吐')
print(f'本周累计: {avg_wk:+.2f}%')
