#!/usr/bin/env python
"""周末股票市场全复盘"""
import subprocess, json
from datetime import date as dt_date
try: import sys; sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

idx={'1.000001':'上证','0.399317':'国证A股','0.399006':'创业板','1.000688':'科创50','1.000300':'沪深300'}

all_data={}
for sid,name in idx.items():
    url=f'https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={sid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57&klt=101&fqt=0&end=20500101&lmt=15'
    r=subprocess.run(['curl','-s','--max-time','10','-H','Referer: https://quote.eastmoney.com','-H','User-Agent: Mozilla/5.0',url],capture_output=True,timeout=15)
    try:
        kls=json.loads(r.stdout)['data']['klines']
        all_data[name]=[(l.split(',')[0],float(l.split(',')[2])) for l in kls]
    except: pass

dates=sorted(set(d for v in all_data.values() for d,_ in v))
dates=[d for d in dates if d>='2026-07-13']
wdays=['一','二','三','四','五','六','日']

print('A股主要指数 逐日收盘')
print('='*100)
header=f'{"日期":<10}{"周":<4}'
for n in ['上证','国证A股','创业板','科创50','沪深300']: header+=f'{n:>14}'
print(header)
print('-'*100)

for d in dates:
    wd=wdays[dt_date.fromisoformat(d).weekday()]
    line=f'{d:<10}{wd:<4}'
    for name in ['上证','国证A股','创业板','科创50','沪深300']:
        if name in all_data:
            vals=dict(all_data[name]); dl=[x[0] for x in all_data[name]]
            if d in vals:
                idx_d=dl.index(d)
                if idx_d>0:
                    pc=all_data[name][idx_d-1][1]; c=vals[d]
                    chg=(c-pc)/pc*100; line+=f'{c:>8.2f} {chg:>+5.2f}%'
                else: line+=f'{vals[d]:>8.2f}   n/a'
            else: line+='    无数据    '
    print(line)

# Weekly summary
print()
print('--- 周度汇总 ---')
weeks={}
for d in dates:
    dd=dt_date.fromisoformat(d); wk=dd.strftime('%G-W%V')
    weeks.setdefault(wk,[]).append(d)
for wk in sorted(weeks):
    wds=weeks[wk]; line=f'{wk} ({wds[0]}~{wds[-1]}): '
    for name in ['上证','国证A股','创业板','科创50']:
        if name in all_data:
            vals=dict(all_data[name])
            if wds[0] in vals and wds[-1] in vals:
                fc=vals[wds[0]]; lc=vals[wds[-1]]; line+=f'{name} {(lc-fc)/fc*100:+.2f}%  '
    print(line)

# Cumul from 07-13
print()
print('--- 07-13起累计 ---')
for name in ['上证','国证A股','创业板','科创50']:
    if name in all_data:
        vals=dict(all_data[name])
        first_d=dates[0]; last_d=dates[-1]
        if first_d in vals and last_d in vals:
            fc=vals[first_d]; lc=vals[last_d]
            print(f'{name}: {fc:.2f} -> {lc:.2f} ({(lc-fc)/fc*100:+.2f}%)')
