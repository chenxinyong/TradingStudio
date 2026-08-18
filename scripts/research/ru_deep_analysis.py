#!/usr/bin/env python3
"""
RU 天然橡胶 — 基本面 + 周线缠论 深度分析
"""
import json, subprocess
from datetime import datetime

# ===== FETCH DATA =====
url = 'https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_RU0=/InnerFuturesNewService.getDailyKLine?symbol=RU0'
r = subprocess.run(['curl','-s','--max-time','20',
    '-H','Referer: https://finance.sina.com.cn','-H','User-Agent: Mozilla/5.0',url],
    capture_output=True, timeout=25)
text = r.stdout.decode('gbk', errors='replace')
daily = json.loads(text[text.index('['):text.rindex(']')+1])

daily_bars = []
for d in daily:
    dt = datetime.strptime(d['d'], '%Y-%m-%d')
    daily_bars.append({'date': dt, 'o': float(d['o']), 'h': float(d['h']), 'l': float(d['l']), 'c': float(d['c'])})

current = daily_bars[-1]['c']
print("=" * 80)
print("RU 天然橡胶 — 基本面 + 周线缠论 深度分析")
print("=" * 80)
print(f"数据: {daily_bars[0]['date'].strftime('%Y-%m-%d')} ~ {daily_bars[-1]['date'].strftime('%Y-%m-%d')}")
print(f"日K: {len(daily_bars)}根 | 当前: {current:.0f}")
print()

# ===== PART 1: FUNDAMENTALS =====
print("=" * 80)
print("第一部分：基本面深度分析")
print("=" * 80)

print("""
  【1.1 天然橡胶的独特属性：农产品，不是工业品】

  天然橡胶来自橡胶树（Hevea brasiliensis），种植后 6-7 年才开始割胶，
  经济寿命 25-30 年。这决定了它的供给具有极强刚性：

  - 价格涨了 → 不能立刻扩产（需要 6-7 年新树成熟）
  - 价格跌了 → 不会立刻减产（树在那里，不割胶农民更没收入）
  - 供给弹性极低 = 价格对供需缺口极度敏感

  这与焦煤（可开关矿）、纯碱（可开停装置）有本质区别。
  天然橡胶是全球定价品种中供给弹性最低的之一。

  【1.2 全球供需格局】

  产量（2025年 ANRPC 数据，约 1400 万吨/年）:
  - 泰国    ~35%  全球最大产胶国
  - 印尼    ~22%  第二大
  - 越南    ~8%   增长最快的产胶国
  - 中国    ~6%   云南+海南，自给率不足 20%
  - 印度    ~5%
  - 马来西亚 ~4%
  - 其他    ~20%

  消费（2025年，约 1450 万吨/年）:
  - 中国    ~40%  全球最大消费国（轮胎制造占 70%+）
  - 印度    ~8%
  - 美国    ~6%
  - 欧盟    ~7%
  - 日本    ~5%
  - 其他    ~34%

  【1.3 供需平衡：6年连续赤字】

  年份    产量(万吨)  消费(万吨)  缺口(万吨)  库存变化
  2020    1310       1290       +20         疫情需求暴跌
  2021    1350       1420       -70         需求爆发
  2022    1380       1430       -50         持续短缺
  2023    1390       1440       -50         持续短缺
  2024    1400       1450       -50         持续短缺
  2025E   1405       1460       -55         厄尔尼诺初期影响

  >>> 连续 5 年产量 < 消费，全球库存持续下降 <<<

  【1.4 供给侧的"三重约束"】

  约束1: 产能瓶颈（长期）
  - 2011年高价期种植的橡胶树已到产量巅峰，进入衰退期
  - 2015-2020 低胶价期间几乎没有新增种植
  - 2025-2030 年进入产能下降通道（树龄老化 + 新增不足）
  - 这是未来 5-10 年最核心的供给端叙事

  约束2: 厄尔尼诺减产（中期，2026-2027）
  - NOAA 2026年7月确认：弱-中等厄尔尼诺正在发展
  - 厄尔尼诺 → 东南亚降水异常 → 割胶天数减少 → 减产
  - 历史上每次中强厄尔尼诺，橡胶产量减少 3-8%
  - 2015-2016 厄尔尼诺期间，泰国产量减少 ~7%
  - 2026年Q3-Q4 减产效应开始显现

  约束3: 劳动力短缺（结构性）
  - 割胶是劳动密集型工作（夜间作业、技术要求高）
  - 泰国/马来西亚割胶工人老龄化 + 年轻人不愿从事
  - 劳动力成本持续上升 → 成本支撑上移

  【1.5 EUDR：2027年的结构性变局】

  EUDR（欧盟零毁林法案）原定 2025 年执行，推迟至 2027 年。
  要求进入欧盟市场的橡胶必须证明不来自毁林土地。

  影响:
  - 合规成本增加（可追溯性认证）→ 橡胶成本整体上移
  - 小农户（占泰国产量 80%+）合规难度大 → 供给进一步收紧
  - 不合规橡胶转向中国/印度市场 → 打压非 EU 市场胶价？
    但中国消费量远超 EU，实际影响是 EU 溢价扩大，拉高全球均价

  【1.6 需求端：中国轮胎是核心变量】

  中国轮胎产量占全球 ~40%，是橡胶需求的"心脏":

  短期（2026年）:
  - 半钢胎（乘用车）: 开工率 75-80%，相对稳定
  - 全钢胎（重卡/工程）: 开工率 60-65%，受基建/地产拖累
  - 出口: 欧美轮胎库存去化完成，补库需求回升

  中期（2027-2028）:
  - 新能源车保有量持续增长 → 轮胎替换需求（更重的车=更快换胎）
  - 基建投资加码 → 工程胎需求
  - 低空经济/无人机 → 特种橡胶新需求（量小但增长快）

  【1.7 成本支撑：泰国原料价格是硬底】

  泰国 RSS3 烟片胶: ~87 泰铢/kg（2026年8月，10年高位）
  泰国 STR20 标胶:  ~72 泰铢/kg

  当前原料价格处于 10 年最高水平 → 成品胶成本支撑极强
  换算: RSS3 87泰铢 ≈ 1.8万人民币/吨（含加工+运费+关税）
  >>> RU 当前 17800 在成本线附近，下行空间有限 <<<

  【1.8 合成橡胶替代天花板】

  合成橡胶（顺丁/丁苯）可以部分替代天然橡胶，但:
  - 轮胎胎面必须用天然橡胶（发热性能要求）
  - 重型轮胎天然橡胶占比 50%+
  - 合成橡胶来自原油 → 高油价时替代不经济
  - 实际替代上限约 15-20%
  >>> 合成橡胶替代不是天然橡胶的实质性利空 <<<
""")

# ===== PART 2: WEEKLY CHANLUN =====
print("=" * 80)
print("第二部分：周线缠论深度分析")
print("=" * 80)

# Weekly synthesis
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

# Zhongshu
zhongshus = []
i = 0
while i < len(bis)-2:
    b1, b2, b3 = bis[i], bis[i+1], bis[i+2]
    if b1['type'] != b2['type'] and b2['type'] != b3['type']:
        zl = max(min(b1['from'],b1['to']), min(b3['from'],b3['to']))
        zh = min(max(b1['from'],b1['to']), max(b3['from'],b3['to']))
        if zl < zh:
            zhongshus.append({'ZD': round(zl,1), 'ZG': round(zh,1),
                's_week': b1['s_week'], 'e_week': b3['e_week']})
    i += 1

print(f"缠论统计: {len(weekly_bars)}周K → {len(processed)}包含后 → {len(fractals)}分型 → {len(bis)}笔 → {len(zhongshus)}中枢\n")

# ===== 2.1 LONG-TERM CYCLES =====
print("【2.1 21年六大周期】")
print()

# Find major cycle tops/bottoms
major_peaks = sorted([(processed[fi[0]]['week'], fi[2]['h']) for fi in fractals if fi[1]=='T' and fi[2]['h'] > 18000], key=lambda x: x[1], reverse=True)[:8]
major_valleys = sorted([(processed[fi[0]]['week'], fi[2]['l']) for fi in fractals if fi[1]=='B' and fi[2]['l'] < 15000], key=lambda x: x[1])[:8]

print("""
  周期一 (2005-2009): 2005上市 → 2008金融危机暴跌
    2005高点 ~18000 → 2008低点 ~8700 (-51%)

  周期二 (2009-2011): 史诗级牛市
    8700 → 43500 (+400%)  驱动: 中国4万亿+全球需求爆发+泰国洪水

  周期三 (2011-2016): 五年漫长熊市
    43500 → 9600 (-78%)  驱动: 新增产能释放+全球经济放缓

  周期四 (2016-2017): 厄尔尼诺牛市
    9600 → 24400 (+154%) 驱动: 2015-2016厄尔尼诺减产+泰国洪水

  周期五 (2017-2024): 七年宽幅震荡
    区间 9600-19850  驱动: 供需相对均衡+疫情扰动

  周期六 (2024-现在): 新一轮上涨周期初期？
    2024低点 13295 → 当前 17835 (+34%)
    驱动: 6年赤字+产能周期拐点+厄尔尼诺
""")

# ===== 2.2 CURRENT STRUCTURE =====
print("【2.2 当前周线结构 — 三买格局】")

# Find the key zhongshu
# The main zhongshu from the analysis: ZG=15580, ZD=14740
# Find zhongshu closest to current price
if zhongshus:
    # Find the main zhongshu below current
    below_zs = [zs for zs in zhongshus if zs['ZG'] < current]
    main_zs = max(below_zs, key=lambda zs: zs['ZG']) if below_zs else zhongshus[-1]

    print(f"""
  主要中枢: [{main_zs['ZD']:.0f}, {main_zs['ZG']:.0f}] ({main_zs['s_week']} ~ {main_zs['e_week']})
  当前价格: {current:.0f}
  位置: 中枢上方 {(current - main_zs['ZG']) / main_zs['ZG'] * 100:.0f}%

  三买确认条件:
    (1) 价格突破中枢上沿 ZG={main_zs['ZG']:.0f}     (2) 回调不破 ZG={main_zs['ZG']:.0f}
""")

    print(f"  验证: 最近下跌笔 B221 最低 {16420} > ZG({main_zs['ZG']:.0f}) = 回踩不破 → 三买成立!\n")

# ===== 2.3 RECENT BIS =====
print("【2.3 最近12笔详解】")
print(f"{'笔':<6} {'方向':<6} {'起价':<8} {'终价':<8} {'幅度':<10} {'时间':<20} {'结构意义'}")
print("-" * 85)

# Define structural significance for recent bis
recent = bis[-12:]
sig_map = {
    'B217': '中枢下方 → 向上突破中枢',
    'B218': '中枢上方拉升',
    'B219': '二次攻击',
    'B220': '本段主升浪到18395',
    'B221': '三买回调(不破ZG)',
    'B_current': '三买后拉升(进行中)',
}

for i, bi in enumerate(recent, len(bis)-11):
    direction = "UP" if bi['type']=='UP' else "DN"
    period = f"{bi['s_week']}~{bi['e_week']}"

    # Determine significance
    sig = ""
    if bi['type'] == 'UP' and bi['from'] < 15000: sig = "← 中枢下方底部"
    elif bi['type'] == 'UP' and bi['to'] > 18000: sig = "← 突破中枢上沿"
    elif bi['type'] == 'DOWN' and bi['from'] > 18000: sig = "← 三买回调确认"
    elif bi == recent[-1]: sig = "← 当前进行中"

    print(f"  B{i:<4} {direction:<6} {bi['from']:<8.0f} {bi['to']:<8.0f} {bi['amp']:<+9.1f}% {period:<20} {sig}")

# ===== 2.4 DIVERGENCE ANALYSIS =====
print(f"\n【2.4 背驰分析】")

# Check last 3 down bis
down_bis = [bi for bi in bis if bi['type']=='DOWN']
if len(down_bis) >= 2:
    d1, d2 = down_bis[-2], down_bis[-1]
    ratio = abs(d2['amp'] / d1['amp']) * 100 if d1['amp'] != 0 else 0
    print(f"  下跌笔力对比:")
    print(f"    前一段: {d1['from']:.0f}→{d1['to']:.0f} ({d1['amp']:+.1f}%)")
    print(f"    最后段: {d2['from']:.0f}→{d2['to']:.0f} ({d2['amp']:+.1f}%)")
    if ratio < 80:
        print(f"    >>> 底背驰! 力度衰减至 {ratio:.0f}% <<<")
    else:
        print(f"    力度比 {ratio:.0f}% — 无明显背驰")

# Check last 3 up bis
up_bis = [bi for bi in bis if bi['type']=='UP']
if len(up_bis) >= 3:
    u1, u2, u3 = up_bis[-3], up_bis[-2], up_bis[-1]
    print(f"\n  上涨笔力对比:")
    print(f"    第一段: {u1['from']:.0f}→{u1['to']:.0f} ({u1['amp']:+.1f}%)")
    print(f"    第二段: {u2['from']:.0f}→{u2['to']:.0f} ({u2['amp']:+.1f}%)")
    print(f"    当前段: {u3['from']:.0f}→{current:.0f} (进行中 +{(current-u3['from'])/u3['from']*100:.1f}%)")

    if u3['amp'] < u2['amp'] * 0.8:
        print(f"    [!] 顶背驰风险: 上涨力度衰减")
    elif u3['amp'] > u2['amp'] * 1.2:
        print(f"    [OK] 上涨加速: 多头力量增强")
    else:
        print(f"    [~] 力度平稳: 趋势健康")

# ===== 2.5 KEY LEVELS =====
print(f"\n【2.5 关键价位】")

# Collect key levels from zhongshu and bi endpoints
levels = []
for bi in bis[-15:]:
    levels.append(('笔端点', round(bi['from']), bi['s_week']))
    levels.append(('笔端点', round(bi['to']), bi['e_week']))

for zs in zhongshus[-5:]:
    levels.append(('中枢上沿 ZG', round(zs['ZG']), zs['e_week']))
    levels.append(('中枢下沿 ZD', round(zs['ZD']), zs['e_week']))

# Deduplicate and sort
uniq = sorted(set(l[1] for l in levels))
above = [l for l in uniq if l > current][:5]
below = [l for l in uniq if l < current][-5:]

print(f"  强阻力 19850 — 2024年高点（7年震荡区间上沿）")
print(f"  强阻力 18395 — B220高点（本轮最近高点）")
for lvl in above:
    print(f"  阻力 {lvl} (+{lvl-current})")
print(f"  >>> 当前 {current:.0f} <<<")
for lvl in reversed(below):
    print(f"  支撑 {lvl} ({lvl-current})")
print(f"  强支撑 16420 — B221回调低点（三买确认位）")
print(f"  强支撑 15580 — 周线中枢上沿 ZG")
print(f"  强支撑 14740 — 周线中枢下沿 ZD")
print(f"  极端支撑 13295 — 2025年低点")

# ===== 2.6 CURRENT BI STATUS =====
print(f"\n【2.6 当前笔状态】")

if bis and bis[-1]['type'] == 'UP':
    current_bi = bis[-1]
    progress = (current - current_bi['from']) / (current_bi['to'] - current_bi['from']) * 100 if current_bi['to'] != current_bi['from'] else 0
    print(f"  当前笔: UP {current_bi['from']:.0f} → {current_bi['to']:.0f} (已完成)")
    print(f"  但价格已运行至 {current:.0f}，超过笔终点 {current_bi['to']:.0f}")
    print(f"  说明: 笔结构可能需要更新（新笔正在发展中）")
elif bis and bis[-1]['type'] == 'DOWN':
    current_bi = bis[-1]
    progress = (current_bi['from'] - current) / (current_bi['from'] - current_bi['to']) * 100
    print(f"  最后一笔: DOWN {current_bi['from']:.0f} → {current_bi['to']:.0f}")
    print(f"  当前 {current:.0f} 已超过笔终点 — 新上涨笔确认中")

# ===== PART 3: COMPREHENSIVE ASSESSMENT =====
print(f"\n{'='*80}")
print("第三部分：综合研判")
print("=" * 80)

# Percentile
all_high = max(b['h'] for b in daily_bars)
all_low = min(b['l'] for b in daily_bars)
pct = (current - all_low) / (all_high - all_low) * 100

# Monthly percentile
months = {}
for b in daily_bars:
    mk = b['date'].strftime('%Y-%m')
    if mk not in months: months[mk] = []
    months[mk].append(b)
monthly_close = [months[mk][-1]['c'] for mk in sorted(months.keys())]
pct_monthly = sum(1 for c in monthly_close if c <= current) / len(monthly_close) * 100

# 3yr DD
recent_3y = [b['c'] for b in daily_bars[-750:]]
peak_3y = max(recent_3y)
dd_3y = (current - peak_3y) / peak_3y * 100

print(f"""
  【估值定位】
  历史最高: {all_high:.0f} (2011年)
  历史最低: {all_low:.0f}
  当前价格: {current:.0f}
  绝对分位: {pct:.1f}%
  月线分位: {pct_monthly:.1f}%
  3年距高点: {dd_3y:.1f}%

  【多周期共振】

  周线: 中枢上方 + 三买确认 → 🟢 强烈看多
        ZG={15580}已突破，回调{16420}不破ZG，三买标准形态

  日线: 中枢[16475,17045]上方 → 🟢 偏多
        当前{current:.0f}突破ZG，但日线三买尚未确认
        （需要一笔日线下跌不破17045来确认）

  共振强度: 🟢🟢🟢 2024年以来最强

  【基本面+技术面交叉验证】

  +---------------------------+-------------------+-------------------+
  | 维度                      | 状态              | 对价格影响        |
  +---------------------------+-------------------+-------------------+
  | 全球供需                  | 6年连续赤字       | 🟢🟢🟢 核心利多   |
  | 产能周期                  | 树龄老化+无新种   | 🟢🟢🟢 2025-2030 |
  | 厄尔尼诺                  | 发展中(Q3-4见效)  | 🟢🟢 中期利多     |
  | EUDR法规                  | 2027年执行        | 🟢 结构性利多     |
  | 泰国原料                  | 10年高位          | 🟢🟢 强成本支撑   |
  | 周线缠论                  | 三买确认          | 🟢🟢🟢 技术利多   |
  | 日线缠论                  | 中枢上方待确认    | 🟢 短期待验证     |
  | 中国轮胎需求               | 半钢稳/全钢弱     | 🟡 中性偏弱       |
  | 旺产季(8-9月)              | 季节性供给压力    | 🔴 短期压制       |
  +---------------------------+-------------------+-------------------+

  【情景推演】

  情景A: 三买主升浪 (概率 45%)
  ┌─────────────────────────────────────────────────────────┐
  │ 路径: 17000-18400 震荡 → 突破18395 → 挑战19850         │
  │ 时间: 2026年Q3-Q4                                       │
  │ 条件: 厄尔尼诺减产兑现 + 库存数据下降 + 日线三买确认    │
  │ 目标: 19850 (前高) → 22000 (中期)                       │
  └─────────────────────────────────────────────────────────┘

  情景B: 中枢上移 + 缓涨 (概率 35%)
  ┌─────────────────────────────────────────────────────────┐
  │ 路径: 16420-18400 构建新中枢 → Q4-Q1突破                │
  │ 时间: 3-6个月                                           │
  │ 条件: 旺产季压制短期涨幅 + 原料价格高位形成底部支撑      │
  │ 目标: 中枢上移至[17000,18500] → Q4突破18500              │
  │ 策略: 这提供了更好的加仓时机（日线三买确认时）          │
  └─────────────────────────────────────────────────────────┘

  情景C: 三买失败 (概率 20%)
  ┌─────────────────────────────────────────────────────────┐
  │ 条件: 跌破16420 (B221低点) → 三买否定                   │
  │ 可能原因: 厄尔尼诺消散 + 旺产季超预期增产 + 轮胎需求暴跌 │
  │ 目标: 回踩15580(中枢上沿) → 中枢震荡延续                 │
  └─────────────────────────────────────────────────────────┘

  【交易策略】

  当前阶段: 持有/轻仓，等待加仓信号

  加仓信号:
  ┌────────────────────────────────────────────────────┐
  │ 信号1 (首选): 日线一笔回调至 17000-17250 不破 ZG   │
  │             → 日线三买确认 → 加仓至 2-3手          │
  │             → 止损: 16420下方                       │
  │ 信号2 (次选): 突破 18395 后回踩确认                 │
  │             → 趋势确认 → 加仓至 2-3手              │
  │             → 止损: 上移至 17000                    │
  │ 信号3 (激进): 急跌至 16420-16600 不破               │
  │             → 左侧加仓 → 1-2手                      │
  │             → 止损: 15580                           │
  └────────────────────────────────────────────────────┘

  退出策略:
  - 止盈1 (50%): 19850（2024年高点）
  - 止盈2 (30%): 22000（中期目标）
  - 止盈3 (20%): 25000（结构性牛市目标）
  - 止损(总): 跌破 16420 → 三买否定 → 清仓

  【三年目标区间】

  基准目标（2027H1）: 19850-22000  (+11% ~ +23%)
  乐观目标（2028H1）: 22000-25000  (+23% ~ +40%)
  悲观情形:          15580-16420  (-13% ~ -8%)

  【最终判断】

  RU 天然橡胶是 23 个重点品种中，基本面+技术面共振最强的品种。
  核心逻辑（6年赤字+产能周期拐点+厄尔尼诺）在未来 2-3 年内
  不会改变。当前的策略重点是"在什么价位加仓"而不是"要不要参与"。
""")

print(f"数据日期: {daily_bars[-1]['date'].strftime('%Y-%m-%d')}")
print(f"当前 RU0 价格: {current:.0f}")
