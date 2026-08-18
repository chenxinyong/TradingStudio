#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""全光伏链 周线对比 —— 688599 天合光能在光伏板块中的位置"""
import subprocess, json
try: import sys; sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

# 全光伏链（你的池子 + 行业标杆）
PV = [
    # --- 组件/一体化（与688599直接可比）---
    ("sh688599","天合光能","组件"),
    ("sh688223","晶科能源","组件"),
    ("sh601012","隆基绿能","组件/硅片"),
    ("sz002459","晶澳科技","组件"),
    ("sz300118","东方日升","组件"),
    # --- 上游硅料/硅片 ---
    ("sh600438","通威股份","硅料/电池"),
    ("sz002129","TCL中环","硅片"),
    ("sh688303","大全能源","硅料"),
    # --- 逆变器（你池子内）---
    ("sz300274","阳光电源","逆变器"),
    ("sh688032","禾迈股份","微逆"),
    ("sz300763","锦浪科技","逆变器"),
    ("sh605117","德业股份","逆变器/储能"),
    # --- 设备（你池子内）---
    ("sh688516","奥特维","组件设备"),
    ("sz300724","捷佳伟创","电池设备"),
    ("sz300751","迈为股份","HJT设备"),
    # --- 辅材 ---
    ("sh688680","海优新材","胶膜"),
]

def fetch_weekly(code):
    sid='1.'+code[2:] if code.startswith('sh') else '0.'+code[2:]
    url=f'https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={sid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57&klt=102&fqt=0&end=20500101&lmt=60'
    try:
        r=subprocess.run(['curl','-s','--max-time','15','-H','Referer: https://quote.eastmoney.com','-H','User-Agent: Mozilla/5.0',url],capture_output=True,timeout=20)
        data=json.loads(r.stdout)['data']
        if not data: return None
        return [(l.split(',')[0],float(l.split(',')[2]),float(l.split(',')[3]),float(l.split(',')[4]),float(l.split(',')[5])) for l in data['klines']]
    except: return None

print('光伏板块 周线对比  数据截止: 2026-07-14')
print('='*78)

results=[]
for code,name,seg in PV:
    wk=fetch_weekly(code)
    if not wk or len(wk)<5: results.append((seg,name,code,None,None,None,None,None,None)); continue
    cur_c=wk[-1][1]; all_h=max(w[2] for w in wk)
    dd=(cur_c-all_h)/all_h*100
    prev_c=wk[-2][1]; wk_chg=(cur_c-prev_c)/prev_c*100
    c4w=wk[-4][1]; tr4=(cur_c-c4w)/c4w*100
    # ytd
    ytd=None
    for d,o,h,l,c in wk:
        if d>='2025-12-29': ytd=c; break
    ytd_c=(cur_c-ytd)/ytd*100 if ytd else 0
    results.append((seg,name,code,cur_c,wk_chg,tr4,dd,all_h,ytd_c))

# sort by本周
valid=[r for r in results if r[3] is not None]
valid.sort(key=lambda x: x[4], reverse=True)

print(f'{"细分":<10}{"名称":<8}{"代码":<10}{"收盘":>7}{"本周":>8}{"近4周":>8}{"距高点回撤":>10}{"YTD":>8}')
print('-'*78)
for seg,name,code,cur,wk,tr,dd,ah,ytd in valid:
    # mark 天合光能
    prefix='>>' if code=='sh688599' else '  '
    print(f'{prefix}{seg:<8}{name:<8}{code:<10}{cur:>7.2f}{wk:>+7.2f}%{tr:>+7.2f}%{dd:>+7.1f}%{ytd:>+7.2f}%')

# --- 分环节统计 ---
seg_avg={}
for seg,name,code,cur,wk,tr,dd,ah,ytd in valid:
    seg_avg.setdefault(seg,{'wk':[],'tr':[],'dd':[],'count':0})
    seg_avg[seg]['wk'].append(wk); seg_avg[seg]['tr'].append(tr); seg_avg[seg]['dd'].append(dd); seg_avg[seg]['count']+=1

print('\n--- 各环节均值 ---')
print(f'{"环节":<10}{"只数":>4}{"本周均值":>10}{"近4周均值":>10}{"回撤均值":>10}')
order=['硅料/电池','硅料','硅片','组件/硅片','组件','逆变器','微逆','逆变器/储能','电池设备','HJT设备','组件设备','胶膜']
seen=set()
for seg,_,_,_,_,_,_,_,_ in valid:
    if seg in seen: continue; seen.add(seg)
    s=seg_avg[seg]; a_wk=sum(s['wk'])/s['count']; a_tr=sum(s['tr'])/s['count']; a_dd=sum(s['dd'])/s['count']
    print(f'{seg:<10}{s["count"]:>4}{a_wk:>+9.2f}%{a_tr:>+9.2f}%{a_dd:>+9.1f}%')

# --- 688599专项 ---
target=[r for r in valid if r[1]=='天合光能'][0]
n=len(valid)
rank_wk=sorted(valid,key=lambda x:x[4],reverse=True)
idx_wk=next(i+1 for i,r in enumerate(rank_wk) if r[1]=='天合光能')
rank_dd=sorted(valid,key=lambda x:x[6])
idx_dd=next(i+1 for i,r in enumerate(rank_dd) if r[1]=='天合光能')

print(f'\n--- 天合光能(688599)光伏板块内定位 ---')
print(f'  本周涨跌排名: {idx_wk}/{n}  ({target[4]:+.2f}%)')
print(f'  距高点回撤排名: {idx_dd}/{n} ({target[6]:.1f}%)')
avg_wk=sum(r[4] for r in valid)/n; avg_dd=sum(r[6] for r in valid)/n
print(f'  光伏板块均值: 本周{avg_wk:+.2f}%  回撤{avg_dd:.1f}%')
print(f'  天合偏离: 本周{target[4]-avg_wk:+.2f}%  回撤{target[6]-avg_dd:+.1f}%')

# 组件环节对比
comp=[r for r in valid if '组件' in r[0] and '设备' not in r[0]]
print(f'\n  组件环节内对比({len(comp)}家):')
for r in sorted(comp,key=lambda x:x[4],reverse=True):
    mark=' <--' if r[1]=='天合光能' else ''
    print(f'    {r[1]:<8} 本周{r[4]:>+6.2f}%  近4周{r[5]:>+7.2f}%  回撤{r[6]:>+6.1f}%{mark}')
