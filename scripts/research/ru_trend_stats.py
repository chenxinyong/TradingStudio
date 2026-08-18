#!/usr/bin/env python3
"""RU 历史趋势持续性统计 — 基于周线笔数据"""
import json, subprocess
from datetime import datetime

url = 'https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_RU0=/InnerFuturesNewService.getDailyKLine?symbol=RU0'
r = subprocess.run(['curl', '-s', '--max-time', '20',
    '-H', 'Referer: https://finance.sina.com.cn', '-H', 'User-Agent: Mozilla/5.0', url],
    capture_output=True, timeout=25)
text = r.stdout.decode('gbk', errors='replace')
daily = json.loads(text[text.index('['):text.rindex(']')+1])

daily_bars = []
for d in daily:
    dt = datetime.strptime(d['d'], '%Y-%m-%d')
    daily_bars.append({'date': dt, 'o': float(d['o']), 'h': float(d['h']), 'l': float(d['l']), 'c': float(d['c'])})

# Weekly synthesis (same as before)
weeks = {}
for bar in daily_bars:
    iso = bar['date'].isocalendar()
    wk_key = f"{iso[0]}-W{iso[1]:02d}"
    if wk_key not in weeks: weeks[wk_key] = []
    weeks[wk_key].append(bar)

weekly_bars = []
for wk_key in sorted(weeks.keys()):
    bars = weeks[wk_key]; bars.sort(key=lambda x: x['date'])
    weekly_bars.append({'week': wk_key, 'end_date': bars[-1]['date'].strftime('%Y-%m-%d'),
        'o': round(bars[0]['o'],1), 'h': round(max(b['h'] for b in bars),1),
        'l': round(min(b['l'] for b in bars),1), 'c': round(bars[-1]['c'],1)})

# Inclusion processing
processed = []
i = 0
while i < len(weekly_bars):
    if len(processed) < 2:
        processed.append(dict(weekly_bars[i])); i += 1; continue
    prev, prev_prev, curr = processed[-1], processed[-2], weekly_bars[i]
    trend_up = prev['h'] > prev_prev['h'] and prev['l'] > prev_prev['l']
    trend_down = prev['h'] < prev_prev['h'] and prev['l'] < prev_prev['l']
    included = (prev['h'] >= curr['h'] and prev['l'] <= curr['l']) or (curr['h'] >= prev['h'] and curr['l'] <= prev['l'])
    if included:
        if trend_up:
            processed[-1]['h'] = max(prev['h'], curr['h']); processed[-1]['l'] = max(prev['l'], curr['l'])
        elif trend_down:
            processed[-1]['h'] = min(prev['h'], curr['h']); processed[-1]['l'] = min(prev['l'], curr['l'])
        else:
            processed[-1]['h'] = max(prev['h'], curr['h']); processed[-1]['l'] = max(prev['l'], curr['l'])
        processed[-1]['c'] = curr['c']; processed[-1]['week'] = curr['week']; i += 1
    else:
        processed.append(dict(weekly_bars[i])); i += 1

# Fractals
fractals = []
for i in range(1, len(processed)-1):
    l, m, r = processed[i-1], processed[i], processed[i+1]
    if m['h'] > l['h'] and m['h'] > r['h'] and (m['l'] > l['l'] or m['l'] > r['l']):
        fractals.append((i, 'T', m))
    elif m['l'] < l['l'] and m['l'] < r['l'] and (m['h'] < l['h'] or m['h'] < r['h']):
        fractals.append((i, 'B', m))

# Bis
bis = []
if len(fractals) >= 2:
    i = 0
    while i < len(fractals)-1:
        fi = fractals[i]; fj = None
        for j in range(i+1, len(fractals)):
            if fractals[j][1] != fi[1]: fj = fractals[j]; break
        if fj is None: break
        if abs(fj[0]-fi[0]) >= 2:
            s, e = processed[fi[0]], processed[fj[0]]
            if fi[1] == 'T':
                bis.append({'type':'DOWN','from':s['h'],'to':e['l'],'amp':(e['l']-s['h'])/s['h']*100,
                    's_week':s['week'],'e_week':e['week'],'bars':fj[0]-fi[0]})
            else:
                bis.append({'type':'UP','from':s['l'],'to':e['h'],'amp':(e['h']-s['l'])/s['l']*100,
                    's_week':s['week'],'e_week':e['week'],'bars':fj[0]-fi[0]})
        i = fractals.index(fj)

# ============ STATISTICS ============
print("="*70)
print("RU 周线历史趋势持续性统计")
print("="*70)

# 1. Define "trends" as consecutive same-direction bis that move price significantly
# Better: group consecutive UP bis into uptrends
up_runs = []
current_run = []
for bi in bis:
    if bi['type'] == 'UP':
        current_run.append(bi)
    else:
        if len(current_run) >= 1:
            total_amp = (current_run[-1]['to'] - current_run[0]['from']) / current_run[0]['from'] * 100
            total_bars = sum(b['bars'] for b in current_run)
            start_wk = current_run[0]['s_week']
            end_wk = current_run[-1]['e_week']
            up_runs.append({'bis': len(current_run), 'amp': total_amp, 'bars': total_bars,
                'start': start_wk, 'end': end_wk, 'from': current_run[0]['from'], 'to': current_run[-1]['to']})
        current_run = []
if current_run:
    total_amp = (current_run[-1]['to'] - current_run[0]['from']) / current_run[0]['from'] * 100
    total_bars = sum(b['bars'] for b in current_run)
    up_runs.append({'bis': len(current_run), 'amp': total_amp, 'bars': total_bars,
        'start': current_run[0]['s_week'], 'end': current_run[-1]['e_week'],
        'from': current_run[0]['from'], 'to': current_run[-1]['to']})

down_runs = []
current_run = []
for bi in bis:
    if bi['type'] == 'DOWN':
        current_run.append(bi)
    else:
        if len(current_run) >= 1:
            total_amp = (current_run[-1]['to'] - current_run[0]['from']) / current_run[0]['from'] * 100
            total_bars = sum(b['bars'] for b in current_run)
            down_runs.append({'bis': len(current_run), 'amp': total_amp, 'bars': total_bars,
                'start': current_run[0]['s_week'], 'end': current_run[-1]['e_week'],
                'from': current_run[0]['from'], 'to': current_run[-1]['to']})
        current_run = []
if current_run:
    total_amp = (current_run[-1]['to'] - current_run[0]['from']) / current_run[0]['from'] * 100
    down_runs.append({'bis': len(current_run), 'amp': total_amp, 'bars': total_bars,
        'start': current_run[0]['s_week'], 'end': current_run[-1]['e_week'],
        'from': current_run[0]['from'], 'to': current_run[-1]['to']})

# Print all major up_runs (>10% amplitude)
major_ups = [r for r in up_runs if r['amp'] > 10]
major_ups.sort(key=lambda x: x['amp'], reverse=True)

print(f"\n主要上涨趋势 (幅度>10%, 共{len(major_ups)}次):")
print(f"{'排名':<5} {'起始':<12} {'结束':<12} {'起价':<8} {'终价':<8} {'涨幅':<10} {'笔数':<6} {'周K数':<8}")
print("-"*80)
for rank, r in enumerate(major_ups[:30], 1):
    print(f"  {rank:<3} {r['start']:<12} {r['end']:<12} {r['from']:<8.0f} {r['to']:<8.0f} {r['amp']:<+9.1f}% {r['bis']:<6} {r['bars']:<8}")

# Statistics
if major_ups:
    amps = [r['amp'] for r in major_ups]
    bars_list = [r['bars'] for r in major_ups]
    print(f"\n上涨趋势统计 (幅度>10%):")
    print(f"  总次数: {len(major_ups)}")
    print(f"  平均涨幅: {sum(amps)/len(amps):.1f}%")
    print(f"  中位涨幅: {sorted(amps)[len(amps)//2]:.1f}%")
    print(f"  最大涨幅: {max(amps):.1f}%")
    print(f"  最小涨幅: {min(amps):.1f}%")
    print(f"  平均周K数: {sum(bars_list)/len(bars_list):.0f}根")
    print(f"  最长趋势: {max(bars_list)}根周K")

# Current trend
print(f"\n当前状态:")
print(f"  当前价格: {weekly_bars[-1]['c']:.0f}")
# Find the start of current up-run
if bis and bis[-1]['type'] == 'UP':
    current_from = bis[-1]['from']
    current_to = weekly_bars[-1]['c']
    current_amp = (current_to - current_from) / current_from * 100
    print(f"  正在进行中的上涨笔: {bis[-1]['s_week']} 底={current_from:.0f} -> NOW={current_to:.0f} ({current_amp:+.1f}%)")

# Find the current up-run
print(f"\n当前上涨趋势详情:")
current_up = up_runs[-1] if up_runs else None
if current_up:
    print(f"  起始: {current_up['start']} 低点={current_up['from']:.0f}")
    print(f"  当前: {weekly_bars[-1]['c']:.0f}")
    ongoing_amp = (weekly_bars[-1]['c'] - current_up['from']) / current_up['from'] * 100
    print(f"  持续涨幅: {ongoing_amp:+.1f}%")
    # Compare to historical
    avg_amp = sum(r['amp'] for r in major_ups) / len(major_ups) if major_ups else 0
    print(f"  历史平均上涨趋势涨幅: {avg_amp:.1f}%")
    pct_of_avg = ongoing_amp / avg_amp * 100 if avg_amp else 0
    print(f"  当前完成度: {pct_of_avg:.0f}% of 历史平均")
    if ongoing_amp < avg_amp * 0.5:
        print(f"  >>> 处于趋势早期, 仍有较大空间")
    elif ongoing_amp < avg_amp:
        print(f"  >>> 处于趋势中期")
    else:
        print(f"  >>> 已接近历史平均, 注意风险")

# Distribution
print(f"\n涨幅分布:")
ranges = [(0,15),(15,30),(30,50),(50,80),(80,150),(150,500)]
for lo, hi in ranges:
    cnt = sum(1 for a in amps if lo <= a < hi)
    bar = '#' * cnt
    print(f"  {lo:>3}-{hi:>3}%: {cnt:>2}次 {bar}")

# Max drawdown during uptrends
print(f"\n上涨趋势中的最大回撤:")
for r in major_ups[-10:]:
    print(f"  {r['start']}~{r['end']}: +{r['amp']:.1f}% ({r['bars']}周)")

# Time between up-trends
print(f"\n趋势间隔分析:")
if len(major_ups) >= 2:
    intervals = []
    for i in range(1, len(major_ups)):
        # Count weeks between end of prev and start of current
        prev_end = major_ups[i-1]['end']
        curr_start = major_ups[i]['start']
        intervals.append((prev_end, curr_start))
    print(f"  最近5次间隔:")
    for prev, curr in intervals[-5:]:
        print(f"    {prev} -> {curr}")
