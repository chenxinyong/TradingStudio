#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""第5层 新能源发电 19只个股 周线对比分析"""
import subprocess, json
from datetime import datetime as dt
try: import sys; sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

LAYER5 = [
    ("sz002202","金风科技"),("sz001289","龙源电力"),("sz300772","运达股份"),
    ("sh601985","中国核电"),("sz002733","雄韬股份"),("sz002015","协鑫能科"),
    ("sh688411","海博思创"),("sh600905","三峡能源"),("sh688599","天合光能"),
    ("sz301358","湖南裕能"),("sz300769","德方纳米"),("sz300073","当升科技"),
    ("sh603659","璞泰来"),("sz001301","尚太科技"),("sz002709","天赐材料"),
    ("sz002812","恩捷股份"),("sh688116","天奈科技"),("sh688772","珠海冠宇"),
    ("sz300850","新强联"),
]

def secid(code):
    'convert sz/sh code to eastmoney secid'
    if code.startswith('sh'): return '1.'+code[2:]
    return '0.'+code[2:]

def fetch_weekly(code, name):
    sid=secid(code)
    url=f'https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={sid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57&klt=102&fqt=0&end=20500101&lmt=60'
    try:
        r=subprocess.run(['curl','-s','--max-time','15','-H','Referer: https://quote.eastmoney.com','-H','User-Agent: Mozilla/5.0',url],capture_output=True,timeout=20)
        data=json.loads(r.stdout)['data']
        if not data: return None
        kls=data['klines']
        weekly=[]
        for line in kls:
            d,o,c,h,l,v,t=line.split(',')
            weekly.append({'d':d,'o':float(o),'h':float(h),'l':float(l),'c':float(c),'v':float(v)})
        return weekly
    except: return None

print('第5层 新能源发电 — 周线对比')
print('数据截止: 2026-07-14 (本周)')
print('='*70)

results=[]
for code,name in LAYER5:
    wk=fetch_weekly(code,name)
    if not wk or len(wk)<5:
        results.append((name,code,None,None,None,None,None,None,None))
        continue
    cur=wk[-1]; first=wk[0]
    cur_c=cur['c']
    all_h=max(w['h'] for w in wk)
    all_l=min(w['l'] for w in wk)
    ph_date=max(wk,key=lambda w:w['h'])['d']
    drawdown=(cur_c-all_h)/all_h*100
    # 4wk MA = simple 4-week close avg
    if len(wk)>=4:
        ma4=sum(w['c'] for w in wk[-4:])/4
    else:
        ma4=cur_c
    ma4_pos=(cur_c-ma4)/ma4*100
    # weekly trend: last 4 weeks slope
    if len(wk)>=4:
        w4c=wk[-4]['c']; trend_4w=(cur_c-w4c)/w4c*100
    else:
        trend_4w=0
    # 本周涨跌
    if len(wk)>=2:
        prev_c=wk[-2]['c']; wk_chg=(cur_c-prev_c)/prev_c*100
    else:
        wk_chg=0
    # YTD
    # find closest to 2026-01-02
    ytd_start=None
    for w in wk:
        if w['d']>='2025-12-29':
            ytd_start=w; break
    ytd_chg=(cur_c-ytd_start['c'])/ytd_start['c']*100 if ytd_start else 0
    results.append((name,code,cur_c,wk_chg,trend_4w,ma4_pos,drawdown,all_h,ph_date[-5:],ytd_chg))

# --- 排序输出 ---
print(f'\n{"名称":<8}{"代码":<10}{"收盘":>7}{"本周":>8}{"近4周":>8}{"距4wMA":>8}{"距高点回撤":>10}{"高点日":>7}{"YTD":>8}')
print('-'*70)
for r in sorted(results, key=lambda x: x[3] if x[3] is not None else -999, reverse=True):
    if r[2] is None:
        print(f'{r[0]:<8}{r[1]:<10} 无数据')
        continue
    print(f'{r[0]:<8}{r[1]:<10}{r[2]:>7.2f}{r[3]:>+7.2f}%{r[4]:>+7.2f}%{r[5]:>+7.2f}%{r[6]:>+7.1f}%{"("+r[8]+")":>7}{r[9]:>+7.2f}%')

# --- 688599 专项 ---
target=None
for r in results:
    if r[1]=='sh688599': target=r; break
if target:
    print('\n' + '='*70)
    print(f'688599 天合光能 在第5层中的位置:')
    valid=[r for r in results if r[2] is not None]
    n=len(valid)
    rank_wk=sorted(valid,key=lambda x:x[3],reverse=True)
    rank_dd=sorted(valid,key=lambda x:x[6])
    rank_4w=sorted(valid,key=lambda x:x[4],reverse=True)
    idx_wk=next(i for i,r in enumerate(rank_wk) if r[1]=='sh688599')+1
    idx_dd=next(i for i,r in enumerate(rank_dd) if r[1]=='sh688599')+1
    idx_4w=next(i for i,r in enumerate(rank_4w) if r[1]=='sh688599')+1
    print(f'  本周涨跌排名: {idx_wk}/{n}  ({target[3]:+.2f}%)')
    print(f'  近4周涨跌排名: {idx_4w}/{n} ({target[4]:+.2f}%)')
    print(f'  距高点回撤排名: {idx_dd}/{n} ({target[6]:.1f}%, 高点{target[7]:.2f}于{target[8]})')
    # 板块均值
    avg_wk=sum(r[3] for r in valid)/n
    avg_dd=sum(r[6] for r in valid)/n
    avg_4w=sum(r[4] for r in valid)/n
    avg_ytd=sum(r[9] for r in valid)/n
    print(f'\n  板块均值(第5层 {n}只): 本周{avg_wk:+.2f}%  近4周{avg_4w:+.2f}%  回撤{avg_dd:+.1f}%  YTD{avg_ytd:+.2f}%')
    print(f'  天合光能偏离:    本周{target[3]-avg_wk:+.2f}%  近4周{target[4]-avg_4w:+.2f}%  回撤{target[6]-avg_dd:+.1f}%')

# --- 板块强弱全景 ---
print('\n' + '='*70)
print('板块内分层:')
valid_sorted=sorted(valid,key=lambda x:x[3],reverse=True)
top3=valid_sorted[:3]; mid=valid_sorted[n//2-2:n//2+2]; bot3=valid_sorted[-3:]
def show(label, group):
    print(f'  [{label}] ', end='')
    for r in group:
        flag='' if r[3]>=0 else ''
        print(f'{r[0]}({r[3]:+.1f}%)',end='  ')
    print()
show('本周强', top3)
show('本周中', mid)
show('本周弱', bot3)

# --- 本周无一下跌？检查 ---
up=sum(1 for r in valid if r[3]>0)
down=sum(1 for r in valid if r[3]<0)
print(f'\n板块广度: {up}涨/{down}跌/{n}总')
if down==0:
    print('  → 第5层本周全红，板块性反弹，688599 的弱势是相对排名(垫底)，不是逆势下跌')
