#!/usr/bin/env python
"""创新药/医药 全板块周线扫描"""
import subprocess, json
from datetime import datetime as dt
try: import sys; sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

stocks = [
    # CXO
    ("sh603259","药明康德","CXO龙头"),("sz300759","康龙化成","CXO龙二"),
    ("sz300347","泰格医药","CRO"),("sh688202","美迪西","CRO"),
    ("sz002821","凯莱英","CDMO"),("sh688131","皓元医药","CXO"),
    # 创新药
    ("sh688331","荣昌生物","ADC"),("sh688180","君实生物","PD-1"),
    ("sh688266","泽璟制药","创新药"),("sh688192","迪哲医药","创新药"),
    ("sz000963","华东医药","GLP-1"),("sh688428","诺诚健华","BTK"),
    # 医药龙头
    ("sh600276","恒瑞医药","仿创龙头"),("sh600196","复星医药","综合"),
    ("sz002001","新和成","维生素"),("sh600085","同仁堂","中药"),
    ("sh300760","迈瑞医疗","器械龙头"),("sh688617","惠泰医疗","器械"),
    # ETF
    ("sz159992","创新药ETF","ETF"),("sh512010","医药ETF","ETF"),
    ("sh560080","中药ETF","ETF"),("sh513120","港股创新药ETF","ETF"),
]

results = []
for code, name, seg in stocks:
    sid = '0.' + code[2:] if code.startswith('sz') else '1.' + code[2:]
    url = f'https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={sid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57&klt=101&fqt=0&end=20500101&lmt=60'
    r = subprocess.run(['curl','-s','--max-time','10','-H','Referer: https://quote.eastmoney.com','-H','User-Agent: Mozilla/5.0',url], capture_output=True, timeout=15)
    try:
        data = json.loads(r.stdout)['data']
        if not data or not data.get('klines'): raise ValueError('nodata')
        klines = data['klines']
    except: continue
    weekly = []
    cur_wk = None; wk = {}
    for line in klines:
        parts = line.split(',')
        d, o, c, h, l = parts[0], float(parts[1]), float(parts[2]), float(parts[3]), float(parts[4])
        dd = dt.fromisoformat(d); wk_key = dd.strftime('%G-W%V')
        if wk_key != cur_wk:
            if wk: weekly.append(wk)
            cur_wk = wk_key; wk = {'d': d, 'o': o, 'h': h, 'l': l, 'c': c}
        else:
            if h > wk['h']: wk['h'] = h
            if l < wk['l']: wk['l'] = l
            wk['c'] = c; wk['d'] = d
    if wk: weekly.append(wk)
    if len(weekly) < 3: continue
    cur = weekly[-1]; prev = weekly[-2]; w4 = weekly[-4] if len(weekly) >= 4 else prev
    wk_chg = (cur['c'] - prev['c']) / prev['c'] * 100
    tr4 = (cur['c'] - w4['c']) / w4['c'] * 100
    all_h = max(w['h'] for w in weekly); dd_pct = (cur['c'] - all_h) / all_h * 100
    results.append((wk_chg, name, seg, cur['c'], tr4, dd_pct))

results.sort(reverse=True)
print('创新药/医药 板块 周线表现 (截至07-24本周收盘)')
print('=' * 72)
print(f'{"名称":<10} {"细分":<12} {"收盘":>8} {"本周":>8} {"近4周":>8} {"回撤":>8}')
print('-' * 72)
for chg, name, seg, c, tr4, dd in results:
    marker = '  <--' if name in ['康龙化成','药明康德','荣昌生物','华东医药','恒瑞医药','泰格医药','同仁堂'] else ''
    print(f'{name:<10} {seg:<12} {c:>8.2f} {chg:>+7.2f}% {tr4:>+7.2f}% {dd:>+7.1f}%{marker}')

avg_wk = sum(r[0] for r in results) / len(results)
avg_dd = sum(r[5] for r in results) / len(results)

# 分类汇总
cxo = [r for r in results if 'CXO' in r[2] or r[2] in ['CDMO','CRO']]
innov = [r for r in results if r[2] in ['ADC','PD-1','创新药','GLP-1','BTK']]
pharma = [r for r in results if r[2] in ['仿创龙头','综合','维生素','中药','器械龙头','器械']]
etfs = [r for r in results if r[2] == 'ETF']

print()
for label, grp in [('CXO',cxo),('创新药',innov),('医药综合',pharma),('ETF',etfs)]:
    if grp:
        a=sum(r[0] for r in grp)/len(grp); d=sum(r[5] for r in grp)/len(grp)
        print(f'  {label}: 本周均值{a:+.2f}%  回撤{d:.1f}%  ({len(grp)}只)')

print(f'\n板块总均值: 本周{avg_wk:+.2f}%  回撤{avg_dd:.1f}%')

# 对比07-23 vs 07-24
print()
print('07-23 → 07-24 日级别对比（关键转折）:')
print('  07-23: 创新药ETF +0.11%  (涨)  |  医药ETF +0.27%')
print('  07-24: 创新药ETF -4.33% (暴跌) |  医药ETF -3.18%')
print('  一天之内从涨转暴跌——资金出逃还是获利了结？')
