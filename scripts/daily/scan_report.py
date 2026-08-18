#!/usr/bin/env python3
"""
自选股扫描报告生成器
从stdin读取腾讯API返回的原始数据，输出格式化报告
"""
import sys, re, io
from datetime import datetime

# 修复Windows/GBK环境下的Unicode输出
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

raw_data = sys.stdin.read()

# 解析数据
stocks = {}
for line in raw_data.strip().split('\n'):
    if not line.strip():
        continue
    m = re.search(r'\"(.*)\"', line)
    if not m:
        continue
    fields = m.group(1).split('~')
    if len(fields) < 40:
        continue
    try:
        code = fields[2]
        name = fields[1]
        current = float(fields[3]) if fields[3] else 0
        prev_close = float(fields[4]) if fields[4] else 0
        change_pct = float(fields[32]) if fields[32] else 0
        high = float(fields[33]) if fields[33] else 0
        low = float(fields[34]) if fields[34] else 0
        volume = float(fields[6]) if fields[6] else 0
        turnover = float(fields[37]) if fields[37] else 0
        open_price = float(fields[5]) if fields[5] else 0
        stocks[code] = {
            'name': name, 'current': current, 'change_pct': change_pct,
            'high': high, 'low': low, 'volume': volume, 'turnover': turnover
        }
    except (ValueError, IndexError):
        pass

def fmt_turnover(t):
    """格式化成交额，t的单位是万元"""
    if t >= 10000:
        return f'{t/10000:.2f}亿'
    elif t >= 1:
        return f'{t:.0f}万'
    else:
        return f'{t*10000:.0f}'

now = datetime.now()
date_str = now.strftime('%Y-%m-%d')
time_str = now.strftime('%H:%M')

print('📡 自选股扫描报告 - ' + date_str)
print()

# 指数
idx_map = {'399317': '国证A股', '399006': '创业板指', '000688': '科创50'}
print('## 📊 大盘概览')
print()
for ic, iname in idx_map.items():
    if ic in stocks:
        d = stocks[ic]
        print(f'- **{iname}**: {d["current"]}  ({d["change_pct"]:+.2f}%)')
print()

# 持仓检查
print('## 🔴 持仓检查')
print()
if '688271' in stocks:
    d = stocks['688271']
    chg = d['change_pct']
    if chg < -5:
        signal = '🔴 大跌！检查建仓逻辑是否还在'
    elif chg < -3:
        signal = '⚡ 显著下跌，关注'
    elif chg > 5:
        signal = '📈 大涨'
    else:
        signal = '✅ 正常波动'
    print(f'| 标的 | 代码 | 现价 | 涨跌幅 | 最高 | 最低 | 信号 |')
    print(f'|------|------|------|--------|------|------|------|')
    print(f'| {d["name"]} | 688271 | {d["current"]} | {chg:+.2f}% | {d["high"]} | {d["low"]} | {signal} |')
print()

# 层级映射
layer_names = {1:'算力基座',2:'半导体',3:'AI应用',4:'电力设备',
               5:'新能源发电与电池材料',6:'储能与智能电网',
               7:'高端装备与机器人',8:'电子零部件',9:'基本金属与小金属'}

code_layer = {}
# 第一层
for c in ['688256','688041','688047','688008','300474','601138','000977','000938','603019',
          '300308','300502','300394','002281','000988','300476','002463','002916','002938',
          '688183','603228','002837','300499','301018','002126','688498','601869','600487',
          '600522','002851','002518','300857','002384','300990']:
    code_layer[c] = 1
# 第二层
for c in ['688981','688347','688396','002371','688012','688072','688082','688120',
          '688019','002409','300655','600584','002156','002185','300604','688234','600703']:
    code_layer[c] = 2
# 第三层
for c in ['002230','688111','600588','300170','300058','600570','688271']:
    code_layer[c] = 3
# 第四层
for c in ['688676','002922','301291','300001','002706','002364','002335','601126',
          '301120','002484','000338','300153','600841','600482','600885','301031','300693']:
    code_layer[c] = 4
# 第五层
for c in ['002202','001289','300772','601985','002733','002015','688411','600905']:
    code_layer[c] = 5
# 第六层
for c in ['300750','300014','300274','600406','002121','605117','688032','603063','300763','002245']:
    code_layer[c] = 6
# 第七层
for c in ['300450','300724','300751','688516','300124','603416','002979','002747',
          '002008','300747','300776','688518','688305','601882','688697','300161',
          '603699','002438','688698']:
    code_layer[c] = 7
# 第七层：高端装备与机器人 —— 机器人部分（原第八层）
for c in ['688017','002472','003021','601689','603728','300007','300115']:
    code_layer[c] = 7
# 第八层：电子零部件（原第九层）
for c in ['000636','300408','002138','600563','002199','605376','002475','688800','300913','002130','300285']:
    code_layer[c] = 8
# 第九层：基本金属与小金属 —— 石化+基础金属+小金属+贵金属+化工+化肥（原第十~十三层）
for c in ['002353','603308','601088','600256','600028','000833','601899','603993','000630','601168','601600','000807',
          '000657','600549','002378','000960','600301','002155','601020','002428','600111','300748','000603',
          '600309','600426','600160','603379','603505','002493','300821','002601','600596','002068',
          '600096','600141','000902','000893','000408','000422','600486','301035','002749','600389']:
    code_layer[c] = 9
# 第五层：新能源发电与电池材料 —— 电池材料部分（原第十二层电池）
for c in ['301358','300769','300073','603659','001301','002709','002812','688116']:
    code_layer[c] = 5
# 第六层：储能与智能电网 —— 智能电网部分（原第十四层）
for c in ['000400','600089','601179','600312','688100','300882','688248']:
    code_layer[c] = 6

# A类核心关注
A_LIST = {
    '300308':'中际旭创', '601138':'工业富联', '300476':'胜宏科技', '002463':'沪电股份',
    '002371':'北方华创', '688012':'中微公司', '688072':'拓荆科技', '688981':'中芯国际',
    '000338':'潍柴动力', '688676':'金盘科技', '600885':'宏发股份', '300750':'宁德时代',
    '600406':'国电南瑞', '300124':'汇川技术', '002008':'大族激光', '688305':'科德数控',
    '000636':'风华高科', '002475':'立讯精密', '688271':'联影医疗', '601899':'紫金矿业',
}

# ===== 活跃层级分析 =====
layer_stats = {}
for code, d in stocks.items():
    if code in code_layer:
        l = code_layer[code]
        if l not in layer_stats:
            layer_stats[l] = {'count':0, 'up':0, 'down':0, 'chg_sum':0.0, 'movers':[]}
        layer_stats[l]['count'] += 1
        layer_stats[l]['chg_sum'] += d['change_pct']
        if d['change_pct'] > 0:
            layer_stats[l]['up'] += 1
        else:
            layer_stats[l]['down'] += 1
        if abs(d['change_pct']) > 3:
            layer_stats[l]['movers'].append((d['name'], d['change_pct']))

print('## 🎯 活跃层级')
print()
print('| 层级 | 平均涨跌 | 涨/跌 | 活跃度 | 异动标的 |')
print('|------|---------|-------|-------|---------|')
sorted_layers = sorted(layer_stats.items(), key=lambda x: x[1]['chg_sum']/max(x[1]['count'],1), reverse=True)
for l, s in sorted_layers:
    avg = s['chg_sum'] / max(s['count'], 1)
    icon = '🟢' if avg > 1 else ('🟡' if avg > 0 else '🔴')
    movers_str = ''
    if s['movers']:
        movers_str = ', '.join([f'{n}({c:+.1f})' for n,c in s['movers'][:3]])
    print(f'| L{l} {layer_names.get(l,"")} | {avg:+.2f}% | {s["up"]}↑/{s["down"]}↓ | {icon} | {movers_str} |')
print()

# ===== A类异动 =====
print('## 🟢 A类核心异动')
print()
print('| 标的 | 涨跌幅 | 成交额 | 层级 | 信号 |')
print('|------|--------|--------|------|------|')
a_data = []
for code, name in A_LIST.items():
    if code not in stocks:
        continue
    d = stocks[code]
    chg = d['change_pct']
    t = d['turnover']
    t_str = fmt_turnover(t)
    l = code_layer.get(code, '')
    sig = '—'
    if chg > 9: sig = '🚀涨停'
    elif chg > 5: sig = '📈大涨'
    elif chg > 3: sig = '📈↑'
    elif chg < -9: sig = '💥跌停'
    elif chg < -5: sig = '📉大跌'
    elif chg < -3: sig = '📉↓'
    a_data.append((abs(chg), name, chg, t_str, l, sig))
a_data.sort(key=lambda x: x[0], reverse=True)

for _, name, chg, tstr, l, sig in a_data:
    cstr = f'**{chg:+.2f}%**' if abs(chg) > 2 else f'{chg:+.2f}%'
    print(f'| {name} | {cstr} | {tstr} | L{l} | {sig} |')
print()

# ===== B/C类异动（非A类中涨跌幅>5%的）=====
other_movers = []
for code, d in stocks.items():
    if code in code_layer and code not in A_LIST:
        chg = d['change_pct']
        if abs(chg) > 4:
            l = code_layer[code]
            sig = '📈' if chg > 0 else '📉'
            other_movers.append((abs(chg), d['name'], chg, l, sig, d['turnover']))

if other_movers:
    other_movers.sort(key=lambda x: x[0], reverse=True)
    print('## 👀 B/C类异动（涨幅>4%）')
    print()
    print('| 标的 | 涨跌幅 | 成交额 | 层级 |')
    print('|------|--------|--------|------|')
    for _, name, chg, l, sig, t in other_movers[:20]:
        tstr = fmt_turnover(t)
        print(f'| {sig} {name} | **{chg:+.2f}%** | {tstr} | L{l} |')
    print()

# ===== 涨幅TOP & 跌幅TOP =====
all_sorted = [(d['change_pct'], d['name'], code) for code, d in stocks.items() if code in code_layer]
all_sorted.sort(key=lambda x: x[0], reverse=True)

print('## 📈 涨幅TOP10')
print()
print('| # | 标的 | 涨跌幅 | 层级 |')
print('|---|------|--------|------|')
for i, (chg, name, code) in enumerate(all_sorted[:10]):
    l = code_layer.get(code, '')
    print(f'| {i+1} | {name} | **{chg:+.2f}%** | L{l} |')

print()
print('## 📉 跌幅TOP10')
print()
print('| # | 标的 | 涨跌幅 | 层级 |')
print('|---|------|--------|------|')
rev = list(reversed(all_sorted))
for i, (chg, name, code) in enumerate(rev[:10]):
    l = code_layer.get(code, '')
    print(f'| {i+1} | {name} | **{chg:+.2f}%** | L{l} |')

print()
print('---')
scanned = len([c for c in stocks if c in code_layer])
print(f'*数据: 腾讯财经 | {date_str} {time_str} | 共扫描 {scanned} 支*')
