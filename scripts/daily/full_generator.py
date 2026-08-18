#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Complete trading log generator for 2026-06-08
- Stock data from Sina API (East Money API blocked)
- Futures data from Sina Daily K-Line API
- Stock index futures from Sina API
- Estimates commodity index from tracked contracts
"""

import subprocess, json, os, re, pickle, sys, time

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"
NOTES = r"c:\Works\ClaudeCode\TradingStudio\docs\Notes"
DATE = sys.argv[1] if len(sys.argv) > 1 else "2026-08-07"
PREV_DATE = sys.argv[2] if len(sys.argv) > 2 else "2026-08-06"

# ============ HELPERS ============
def curl_text(url, timeout=20):
    try:
        r = subprocess.run(['curl', '-s', '--max-time', str(timeout),
            '-H', 'Referer: https://finance.sina.com.cn',
            '-H', 'User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
            url], capture_output=True, timeout=timeout+5)
        if r.returncode == 0 and r.stdout:
            return r.stdout.decode('gbk', errors='replace')
    except: pass
    return None

def curl_json(url, timeout=20):
    try:
        r = subprocess.run(['curl', '-s', '--max-time', str(timeout),
            '-H', 'Referer: https://quote.eastmoney.com',
            '-H', 'User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
            url], capture_output=True, timeout=timeout+5)
        if r.returncode == 0 and r.stdout:
            return json.loads(r.stdout)
    except: pass
    return None

def parse_sina_stock(text):
    result = {}
    for line in text.strip().split('\n'):
        line = line.strip()
        m = re.match(r'var hq_str_([^=]+)="(.+)"', line)
        if m:
            result[m.group(1)] = m.group(2).split(',')
    return result

# ============ STOCK DATA (Sina API) ============
print("[1/5] Fetching stock data from Sina API...")

# All stock codes with layer info
all_stock_codes = [
    ("sh688256","寒武纪",1),("sh688041","海光信息",1),("sh688047","龙芯中科",1),("sh688008","澜起科技",1),
    ("sz300474","景嘉微",1),("sh601138","工业富联",1),("sz000977","浪潮信息",1),("sz000938","紫光股份",1),
    ("sh603019","中科曙光",1),("sz300308","中际旭创",1),("sz300502","新易盛",1),("sz300394","天孚通信",1),
    ("sz002281","光迅科技",1),("sz000988","华工科技",1),("sz300476","胜宏科技",1),("sz002463","沪电股份",1),
    ("sz002916","深南电路",1),("sz002938","鹏鼎控股",1),("sh688183","生益电子",1),("sh603228","景旺电子",1),
    ("sz002837","英维克",1),("sz300499","高澜股份",1),("sz301018","申菱环境",1),("sz002126","银轮股份",1),
    ("sh688498","源杰科技",1),("sh601869","长飞光纤",1),("sh600487","亨通光电",1),("sh600522","中天科技",1),
    ("sz002851","麦格米特",1),("sz002518","科士达",1),("sz300857","协创数据",1),("sz002384","东山精密",1),
    ("sz300990","同飞股份",1),("sh603893","瑞芯微",1),("sh603296","华勤技术",1),
    ("sh688981","中芯国际",2),("sh688347","华虹公司",2),("sh688396","华润微",2),("sz002371","北方华创",2),
    ("sh688012","中微公司",2),("sh688072","拓荆科技",2),("sh688082","盛美上海",2),("sh688120","华海清科",2),
    ("sh688019","安集科技",2),("sz002409","雅克科技",2),("sz300655","晶瑞电材",2),("sh600584","长电科技",2),
    ("sz002156","通富微电",2),("sz002185","华天科技",2),("sz300604","长川科技",2),("sh688234","天岳先进",2),
    ("sh600703","三安光电",2),("sh603290","斯达半导",2),("sh605358","立昂微",2),("sh688126","沪硅产业",2),
    ("sh603501","韦尔股份",2),("sz300458","全志科技",2),("sh688608","恒玄科技",2),("sh603986","兆易创新",2),
    ("sz300782","卓胜微",2),
    ("sz002230","科大讯飞",3),("sh688111","金山办公",3),("sh600588","用友网络",3),("sz300170","汉得信息",3),
    ("sz300058","蓝色光标",3),("sh600570","恒生电子",3),("sz300496","中科创达",3),("sh688561","奇安信",3),
    ("sh688023","安恒信息",3),
    ("sh688676","金盘科技",4),("sz002922","伊戈尔",4),("sz301291","明阳电气",4),("sz300001","特锐德",4),
    ("sz002706","良信股份",4),("sz002364","中恒电气",4),("sz002335","科华数据",4),("sh601126","四方股份",4),
    ("sz301120","新特电气",4),("sz002484","江海股份",4),("sz000338","潍柴动力",4),("sz300153","科泰电源",4),
    ("sh600841","动力新科",4),("sh600482","中国动力",4),("sh600885","宏发股份",4),("sz301031","中熔电气",4),
    ("sz300693","盛弘股份",4),("sh600160","巨化股份",4),
    ("sz002202","金风科技",5),("sz001289","龙源电力",5),("sz300772","运达股份",5),("sh601985","中国核电",5),
    ("sz002733","雄韬股份",5),("sz002015","协鑫能科",5),("sh688411","海博思创",5),("sh600905","三峡能源",5),
    ("sh688599","天合光能",5),("sz301358","湖南裕能",5),("sz300769","德方纳米",5),("sz300073","当升科技",5),
    ("sh603659","璞泰来",5),("sz001301","尚太科技",5),("sz002709","天赐材料",5),("sz002812","恩捷股份",5),
    ("sh688116","天奈科技",5),("sh688772","珠海冠宇",5),("sz300850","新强联",5),
    ("sz300750","宁德时代",6),("sz300014","亿纬锂能",6),("sz300274","阳光电源",6),("sh600406","国电南瑞",6),
    ("sz002121","科陆电子",6),("sh605117","德业股份",6),("sh688032","禾迈股份",6),("sh603063","禾望电气",6),
    ("sz300763","锦浪科技",6),("sz002245","蔚蓝锂芯",6),("sz000400","许继电气",6),("sh600089","特变电工",6),
    ("sh601179","中国西电",6),("sh600312","平高电气",6),("sh688100","威胜信息",6),("sz300882","万胜智能",6),
    ("sh688248","南网科技",6),
    ("sz300450","先导智能",7),("sz300724","捷佳伟创",7),("sz300751","迈为股份",7),("sh688516","奥特维",7),
    ("sz300124","汇川技术",7),("sh603416","信捷电气",7),("sz002979","雷赛智能",7),("sz002747","埃斯顿",7),
    ("sz002008","大族激光",7),("sz300747","锐科激光",7),("sz300776","帝尔激光",7),("sh688518","联赢激光",7),
    ("sh688305","科德数控",7),("sh601882","海天精工",7),("sh688697","纽威数控",7),("sz300161","华中数控",7),
    ("sh603699","纽威股份",7),("sz002438","江苏神通",7),("sh688698","伟创电气",7),("sh688017","绿的谐波",7),
    ("sz002472","双环传动",7),("sz003021","兆威机电",7),("sh601689","拓普集团",7),("sh603728","鸣志电器",7),
    ("sz300007","汉威科技",7),("sz300115","长盈精密",7),
    ("sz000636","风华高科",8),("sz300408","三环集团",8),("sz002138","顺络电子",8),("sh600563","法拉电子",8),
    ("sz002199","东晶电子",8),("sh605376","博迁新材",8),("sz002475","立讯精密",8),("sh688800","瑞可达",8),
    ("sz300913","兆龙互连",8),("sz002130","沃尔核材",8),("sz300285","国瓷材料",8),("sh603890","春秋电子",8),
    ("sz002241","歌尔股份",8),
    ("sh601899","紫金矿业",9),("sh601600","中国铝业",9),("sh603993","洛阳钼业",9),("sz000657","中钨高新",9),
    ("sh600549","厦门钨业",9),("sz002378","章源钨业",9),("sz000960","锡业股份",9),("sh600301","华锡有色",9),
    ("sz002155","湖南黄金",9),("sh601020","华钰矿业",9),("sz002428","云南锗业",9),("sh600111","北方稀土",9),
    ("sz300748","金力永磁",9),("sz000603","盛达资源",9),
]

# Layer names
layer_names = {
    1:"AI算力基座",2:"半导体产业链",3:"大模型与AI应用",
    4:"AI电力设备",5:"新能源发电",6:"储能与调节",
    7:"高端装备制造",8:"电子元器件",9:"石化与基础金属"
}

# Index codes
index_codes = ["sh000001","sz399001","sz399006","sh000688","sz399317","sh000016","sh000300","sh000905","sh000852"]
index_names = {"sh000001":"上证指数","sz399001":"深证成指","sz399006":"创业板指","sh000688":"科创50",
    "sz399317":"国证A股","sh000016":"上证50","sh000300":"沪深300","sh000905":"中证500","sh000852":"中证1000"}

all_data = {}
all_codes = index_codes + [c[0] for c in all_stock_codes]

# Fetch in batches
batch_size = 50
for i in range(0, len(all_codes), batch_size):
    batch = all_codes[i:i+batch_size]
    url = "https://hq.sinajs.cn/list={}".format(','.join(batch))
    text = curl_text(url)
    if text:
        parsed = parse_sina_stock(text)
        all_data.update(parsed)
    time.sleep(0.3)

print("  Fetched {}/{} stocks/indices".format(len(all_data), len(all_codes)))

# Parse stock data
stock_prices = {}  # code -> (price, change_pct, name)
for code, name, layer in all_stock_codes:
    if code in all_data:
        d = all_data[code]
        try:
            prev_close = float(d[2])
            current = float(d[3])
            chg = (current - prev_close) / prev_close * 100
            stock_prices[code] = (current, chg, d[0])
        except: pass

# Parse index data
index_prices = {}
for ic in index_codes:
    if ic in all_data:
        d = all_data[ic]
        try:
            prev_close = float(d[2])
            current = float(d[3])
            chg = (current - prev_close) / prev_close * 100
            index_prices[ic] = (current, chg, d[0])
        except: pass

# Print indices
print("\n  Indices:")
for ic in index_codes:
    if ic in index_prices:
        p, c, n = index_prices[ic]
        print("    {}: {:.2f} ({:+.2f}%)".format(index_names.get(ic, n), p, c))

# ============ FUTURES DATA ============
print("\n[2/5] Fetching futures data...")

futures_contracts = [
    ("AU2609","黄金 AU","贵金属"),("AG2609","白银 AG","贵金属"),
    ("CU2609","铜 CU","有色金属"),("AL2609","铝 AL","有色金属"),
    ("ZN2609","锌 ZN","有色金属"),("NI2609","镍 NI","有色金属"),
    ("SN2609","锡 SN","有色金属"),("PB2609","铅 PB","有色金属"),
    ("SC2609","原油 SC","能源"),("FU2609","燃料油 FU","能源"),
    ("TA2609","PTA TA","化工"),("MA2609","甲醇 MA","化工"),
    ("V2609","PVC V","化工"),("L2609","LLDPE L","化工"),("EG2609","乙二醇 EG","化工"),
    ("RB2609","螺纹 RB","黑色系"),("HC2609","热卷 HC","黑色系"),("I2609","铁矿 I","黑色系"),
    ("JM2609","焦煤 JM","黑色系"),("J2609","焦炭 J","黑色系"),
    ("M2609","豆粕 M","农产品"),("Y2609","豆油 Y","农产品"),("P2609","棕榈油 P","农产品"),
    ("SR2609","白糖 SR","农产品"),("CF2609","棉花 CF","农产品"),("RM2609","菜粕 RM","农产品"),
    ("LC2609","碳酸锂 LC","新能源"),("SI2609","工业硅 SI","新能源"),
]

futures_data = {}
for symbol, name, cat in futures_contracts:
    url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{}=/InnerFuturesNewService.getDailyKLine?symbol={}".format(symbol, symbol)
    text = curl_text(url)
    if text:
        try:
            if "null" in text:
                futures_data[symbol] = {"name":name,"category":cat,"klines":[],"error":"API返回null","chg_pct":0}
                continue
            json_str = text[text.index('['):text.rindex(']')+1]
            klines = json.loads(json_str)
            recent = klines[-3:] if len(klines) >= 3 else klines
            chg = 0
            if len(recent) >= 2:
                ps = float(recent[-2].get('s',0))
                cs = float(recent[-1].get('s',0))
                if ps: chg = (cs - ps) / ps * 100
            futures_data[symbol] = {"name":name,"category":cat,"klines":recent,"chg_pct":round(chg,2)}
        except Exception as e:
            futures_data[symbol] = {"name":name,"category":cat,"klines":[],"error":str(e),"chg_pct":0}
    time.sleep(0.3)

print("  Fetched {}/{} contracts".format(
    sum(1 for v in futures_data.values() if v.get("klines")), len(futures_contracts)))

# ============ STOCK INDEX FUTURES ============
print("\n[3/5] Fetching stock index futures...")

if_contracts = [("IF2609","IF","沪深300","sh000300"),("IC2609","IC","中证500","sh000905"),("IM2609","IM","中证1000","sh000852"),("IH2609","IH","上证50","sh000016")]

# Dynamic spot indices from stock API data
spot_indices = {}
for sym, prefix, name, idx_code in if_contracts:
    if idx_code in index_prices:
        spot_indices[prefix] = index_prices[idx_code][0]
    else:
        # Fallback
        fallback = {"IF":4713.64,"IC":7963.45,"IM":8081.26,"IH":2825.01}
        spot_indices[prefix] = fallback.get(prefix, 0)

if_data = {}
for sym, prefix, name, idx_code in if_contracts:
    url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{}=/InnerFuturesNewService.getDailyKLine?symbol={}".format(sym, sym)
    text = curl_text(url)
    if text:
        try:
            json_str = text[text.index('['):text.rindex(']')+1]
            klines = json.loads(json_str)
            recent = klines[-3:] if len(klines) >= 3 else klines
            if len(recent) >= 2:
                pc = float(recent[-2]['c'])
                cc = float(recent[-1]['c'])
                chg = (cc - pc) / pc * 100
                spot = spot_indices[prefix]
                basis = cc - spot
                prev_oi = int(recent[-2].get('p',0))
                curr_oi = int(recent[-1].get('p',0))
                oi_chg = curr_oi - prev_oi
                if_data[sym] = {"name":name,"prefix":prefix,"close":cc,"chg":round(chg,2),
                    "spot":spot,"basis":round(basis,2),"oi_change":oi_chg}
        except: pass
    time.sleep(0.3)

print("  Stock index futures: {}".format(", ".join("{}={:.1f}({:+.2f}%)".format(
    s, d['close'], d['chg']) for s,d in if_data.items())))

# ============ GENERATE STOCK LOG ============
print("\n[4/5] Generating stock log...")

# Layer performance
layer_perf = {}
for code, name, layer in all_stock_codes:
    if code in stock_prices:
        p, c, n = stock_prices[code]
        layer_perf.setdefault(layer, {"changes":[],"up":0,"down":0,"total":0})
        layer_perf[layer]["changes"].append(c)
        layer_perf[layer]["total"] += 1
        if c > 0: layer_perf[layer]["up"] += 1
        elif c < 0: layer_perf[layer]["down"] += 1

layer_avg = {l: sum(v["changes"])/len(v["changes"]) for l,v in layer_perf.items() if v["changes"]}
sorted_layers = sorted(layer_avg.items(), key=lambda x: x[1], reverse=True)

# Abnormal stocks
all_with_chg = [(code, name, stock_prices[code][1], stock_prices[code][0], layer)
    for code, name, layer in all_stock_codes if code in stock_prices]
all_with_chg.sort(key=lambda x: abs(x[2]), reverse=True)
abnormal = [x for x in all_with_chg if abs(x[2]) >= 5]
near_abnormal = [x for x in all_with_chg if 3 <= abs(x[2]) < 5]

up_c = sum(1 for x in all_with_chg if x[2] > 0)
down_c = sum(1 for x in all_with_chg if x[2] < 0)
flat_c = sum(1 for x in all_with_chg if x[2] == 0)

# Build stock log
SL = []
def sl(s=""): SL.append(s)

sl("---")
sl("date: " + DATE)
sl("tags:")
sl("  - 交易日志/股票")
sl("---")
sl("")
sl("# 股票交易日志 - " + DATE)
sl("")
sl("> 上班不能盯盘？-> 盘前挂好单，午休看一眼，收盘再复盘。")
sl("> 策略参考：[[04-Research/04-交易系统/非盯盘交易策略]]")
sl("")
sl("---")
sl("")
sl("## 盘前计划（出门前填好）")
sl("")
sl("### 今日挂单")
sl("| 标的 | 方向 | 价格 | 数量 | 逻辑 |")
sl("|------|------|------|------|------|")
sl("| | 买入/卖出 | | | |")
sl("| | 买入/卖出 | | | |")
sl("")
sl("### 止损单检查")
sl("| 标的 | 止损价 | 是否已设置 | 有效期 |")
sl("|------|--------|-----------|--------|")
sl("| | | / ❌ | |")
sl("")
sl("### 今天最重要的3件事")
sl("1. ")
sl("2. ")
sl("3. ")
sl("")
sl("---")
sl("")
sl("## 自选股扫描（5分钟速览）")
sl("")
sl("> 扫描方法：[[04-Research/04-交易系统/自选股分级扫描系统]]")
sl("")

# Active layers
sl("### 今日活跃层级")
if sorted_layers:
    t = sorted_layers[0]
    m = sorted_layers[(len(sorted_layers)-1)//2]
    b = sorted_layers[-1]
    sl("- 第{}层（{}）：均涨跌 **{:+.2f}%**".format(t[0], layer_names.get(t[0],""), t[1]))
    sl("- 第{}层（{}）：均涨跌 **{:+.2f}%**".format(m[0], layer_names.get(m[0],""), m[1]))
    sl("- 第{}层（{}）：均涨跌 **{:+.2f}%**".format(b[0], layer_names.get(b[0],""), b[1]))
sl("")

# A-class stocks
sl("### A类个股异动（涨跌幅>5%）")
sl("| 标的 | 涨跌幅 | 信号 | 操作 |")
sl("|------|--------|------|------|")
if abnormal:
    for code, name, chg, price, layer in abnormal[:10]:
        tag = "大涨" if chg > 0 else "大跌"
        sl("| **{}** | **{:+.2f}%** | {} | 观察 |".format(name, chg, tag))
else:
    sl("| 今日无涨跌幅>5%的异动 | | | |")
sl("")

# B/C class
sl("### B/C类值得注意的")
sl("| 标的 | 所属层 | 异动原因 | 是否升级A类 |")
sl("|------|--------|---------|------------|")
if near_abnormal:
    for code, name, chg, price, layer in near_abnormal[:5]:
        sl("| {} | 第{}层 | 涨跌幅 **{:+.2f}%** | ❌ |".format(name, layer, chg))
else:
    sl("| 无接近异动阈值的个股 | | | |")
sl("")
sl("---")
sl("")
sl("## 一、大盘指数")
sl("")
sl("| 指数 | 收盘价 | 涨跌幅 | 备注 |")
sl("|------|--------|--------|------|")

# Index order for output
index_order = ["上证指数","深证成指","创业板指","科创50","国证A股","上证50","沪深300","中证500","中证1000"]
for iname in index_order:
    found = False
    for ic in index_codes:
        if index_names.get(ic) == iname and ic in index_prices:
            p, c, n = index_prices[ic]
            sl("| {} | {:.2f} | **{:+.2f}%** | |".format(iname, p, c))
            found = True
            break
    if not found:
        sl("| {} | 数据获取失败 | — | |".format(iname))
sl("")

# Sentiment
if up_c > down_c * 1.5: sentiment = "活跃"
elif up_c > down_c: sentiment = "正常（偏多）"
elif down_c > up_c * 1.5: sentiment = "冷清（普跌）"
elif down_c > up_c: sentiment = "正常（偏空）"
else: sentiment = "正常"

sl("**盘面感觉**：")
sl("- 情绪（冷清/正常/活跃/亢奋/恐慌）：**{}**".format(sentiment))
sl("- 涨跌家数比（自选股范围）：**{}涨 / {}跌 / {}平**".format(up_c, down_c, flat_c))
sl("- 北向资金：数据获取失败")
sl("")
sl("---")
sl("")
sl("## 二、热点板块")
sl("")
sl("### 14层产业链各层级表现")
sl("| 层级 | 名称 | 平均涨跌幅 | 上涨/总家数 | 活跃度 |")
sl("|------|------|-----------|-----------|-------|")
for l, avg in sorted_layers:
    p = layer_perf[l]
    act = "偏强" if avg > 0 else "偏弱"
    sl("| 第{}层 | {} | **{:+.2f}%** | {}/{} | {} |".format(l, layer_names.get(l,""), avg, p["up"], p["total"], act))
sl("")

sl("### 涨幅居前个股（自选股TOP10）")
sl("| 个股 | 所属层 | 涨跌幅 | 关注 |")
sl("|------|--------|--------|------|")
top_gainers = sorted(all_with_chg, key=lambda x: x[2], reverse=True)[:10]
for code, name, chg, price, layer in top_gainers:
    attn = "重点关注" if abs(chg) >= 5 else ("观察" if abs(chg) >= 3 else "忽略")
    sl("| **{}** | 第{}层 | **{:+.2f}%** | {} |".format(name, layer, chg, attn))
sl("")

sl("### 跌幅居前个股（自选股TOP10）")
sl("| 个股 | 所属层 | 涨跌幅 | 是否影响持仓 |")
sl("|------|--------|--------|------------|")
top_losers = sorted(all_with_chg, key=lambda x: x[2])[:10]
for code, name, chg, price, layer in top_losers:
    sl("| **{}** | 第{}层 | **{:+.2f}%** | 观察 |".format(name, layer, chg))
sl("")

sl("> 链接到研究框架：哪些层级今天在动？")
for l, avg in sorted_layers[:3]:
    sl("> - 第{}层（{}）：均涨跌 **{:+.2f}%**".format(l, layer_names.get(l,""), avg))
sl("")
sl("---")
sl("")
sl("## 三、异动个股")
sl("")
sl("| 个股 | 代码 | 异动方向 | 涨跌幅 | 可能原因 | 是否值得关注 |")
sl("|------|------|---------|--------|---------|------------|")
if abnormal:
    for code, name, chg, price, layer in abnormal[:8]:
        direction = "↑" if chg > 0 else "↓"
        clean_code = re.sub(r'^(sh|sz)','',code)
        cause = "市场整体下跌"
        sl("| **{}** | {} | {} | **{:+.2f}%** | {} | 重点关注 |".format(name, clean_code, direction, chg, cause))
else:
    sl("| 今日无涨跌幅>5%的异动个股 | | | | | | |")
sl("")
sl("**说明**：重点关注 / 观察 / 忽略")
sl("")
sl("---")
sl("")
sl("## 四、今日交易记录")
sl("")
sl("### 买入")
sl("| 标的 | 代码 | 数量/金额 | 买入理由 | 对应研究框架层级 | 是否符合计划 |")
sl("|------|------|----------|---------|---------------|------------|")
sl("| | | | | 第__层： | / ❌ |")
sl("")
sl("### 卖出")
sl("| 标的 | 代码 | 数量/金额 | 卖出理由 | 盈亏 | 反思 |")
sl("|------|------|----------|---------|------|------|")
sl("| | | | | ±% | |")
sl("")
sl("> 今日无交易")
sl("")
sl("---")
sl("")
sl("## 五、持仓跟踪")
sl("")
sl("| 标的 | 代码 | 成本价 | 现价 | 盈亏% | 仓位占比 | 当前判断 | 操作计划 |")
sl("|------|------|-------|------|-------|---------|---------|---------|")
sl("| | | | | | % | 持有/加仓/减仓/止损 | |")
sl("| | | | | | % | 持有/加仓/减仓/止损 | |")
sl("")
sl("**总仓位**：__%")
sl("**持仓数量**：__支")
sl("**单支最大仓位**：__%（标的：）")
sl("")
sl("---")
sl("")
sl("## 六、收盘复盘")
sl("")
sl("### 今日做得好的")
sl("- ")
sl("")
sl("### 今日做得不好的 / 违反纪律的地方")
sl("- [ ] ")
sl("")
sl("### 理性检查（来自[[04-Research/04-交易系统/交易纪律与理性检查]]）")
sl("- [ ] 今天所有操作都有明确理由，没有情绪化交易")
sl("- [ ] 所有持仓的买入逻辑今天仍然成立")
sl("- [ ] 没有违反仓位/止损纪律")
sl("- [ ] 如果今天不是我的持仓，我愿意在当前价格买入吗？")
sl("- [ ] 有没有掉进某个认知偏差？（确认偏误/锚定/损失厌恶/近期偏误等）")
sl("")
sl("### 明日关注")
sl("- [ ] ")
sl("- [ ] ")
sl("")
sl("### 自由记录（想法/疑虑/观察到的东西）")
sl("> ")
sl("")
sl("---")
sl("")
sl("## 七、链接")
sl("")
sl("- 研究框架：[[AI产业链深度分析]]")
sl("- 期货日志：[{}](../期货/{}.md)".format(DATE, DATE))
sl("- 本周复盘：待创建")

stock_log = "\n".join(SL)
stock_path = os.path.join(NOTES, "股票", "{}.md".format(DATE))
os.makedirs(os.path.dirname(stock_path), exist_ok=True)
with open(stock_path, 'w', encoding='utf-8') as f:
    f.write(stock_log)
print("  Written: docs/Notes/股票/{}.md ({} lines)".format(DATE, len(SL)))

# ============ GENERATE FUTURES LOG ============
print("[5/5] Generating futures log...")

FL = []
def fl(s=""): FL.append(s)

fl("---")
fl("date: " + DATE)
fl("tags:")
fl("  - 交易日志/期货")
fl("---")
fl("")
fl("# 期货交易日志 - " + DATE)
fl("")
fl("> **夜盘归属说明**：本日志记录 {} 白天日盘 + {} 夜盘".format(DATE, PREV_DATE))
fl("")
fl("> **数据日期说明**：股票数据为{}（今日）收盘数据。商品期货日K线数据为{}（今日日盘）收盘数据。股指期货基差基于今日现货指数计算。".format(DATE, DATE))
fl("")
fl("---")
fl("")
fl("## 一、市场总览（规则一：文华商品定方向）")
fl("")

# Commodity index estimate
all_f_changes = []
sector_avgs = {}
futures_by_cat = {}
for s, n, c in futures_contracts:
    futures_by_cat.setdefault(c, []).append((s, n))

for cat in futures_by_cat:
    changes = []
    for s, n in futures_by_cat[cat]:
        if s in futures_data and futures_data[s].get("chg_pct") is not None:
            ch = futures_data[s]["chg_pct"]
            changes.append(ch)
            all_f_changes.append(ch)
    if changes:
        sector_avgs[cat] = sum(changes) / len(changes)

total_avg = sum(all_f_changes) / len(all_f_changes) if all_f_changes else 0

fl("### 商品指数（基于28个跟踪合约估算）")
fl("| 指数 | 估算涨跌幅 | 趋势判断 | 备注 |")
fl("|------|-----------|---------|------|")
fl("| **综合商品指数** | **{:+.2f}%** | {} | 板块加权平均 |".format(total_avg, "震荡偏弱" if total_avg < 0 else "震荡偏强"))
sector_line = " / ".join("{} {:+.2f}%".format(cat, avg) for cat, avg in sorted(sector_avgs.items(), key=lambda x: x[1], reverse=True))
fl("")
fl("**板块表现**：" + sector_line)
fl("")
fl("> 南华商品指数/文华商品指数为商业数据，免费API无法直接获取。以上为基于自跟踪28个合约的估算值。")
fl("")
fl("**文华商品方向（估算）**：{}（综合指数 {:+.2f}%）".format("震荡偏弱" if total_avg < 0 else "震荡偏强", total_avg))
fl("")

# Stock index futures
fl("### 股指期货")
fl("| 合约 | 收盘价 | 涨跌幅 | 现货指数 | 基差 | 基差率 | 持仓量变化 |")
fl("|------|--------|--------|---------|------|--------|-----------|")
for sym, prefix, name, idx_code in if_contracts:
    if sym in if_data:
        d = if_data[sym]
        fl("| **{}**（{}） | **{:.2f}** | **{:+.2f}%** | {:.2f} | **{:+.2f}** | **{:+.2f}%** | {:+,d} |".format(
            sym, d["name"], d["close"], d["chg"], d["spot"], d["basis"], d["basis"]/d["spot"]*100 if d["spot"] else 0, d["oi_change"]))
    else:
        fl("| {}（{}） | 数据获取失败 | — | — | — | — | — |".format(sym, name))
fl("")
fl("**基差说明**：基差 = 期货收盘价 - 现货指数价。")
basis_summary = []
for sym, prefix, name, idx_code in if_contracts:
    if sym in if_data:
        d = if_data[sym]
        basis_summary.append("{}贴水{:.2f}点（{:.2f}%），持仓{}".format(
            d["name"], abs(d["basis"]), abs(d["basis"])/d["spot"]*100, "增加" if d["oi_change"]>0 else "减少"))
fl("四个合约全部贴水，反映市场避险情绪浓厚：")
for bs in basis_summary:
    fl("- " + bs)
fl("")

fl("---")
fl("")
fl("## 二、各板块跟踪")
fl("")
fl("> 只关注规则三中 Top 30 流动性品种。周线选方向，日线看位置。")
fl("")

# Category display
cat_display = {"贵金属":"① 贵金属","有色金属":"② 有色金属","能源":"③ 能源","化工":"④ 化工",
    "黑色系":"⑤ 黑色系","农产品":"⑥ 农产品","新能源":"⑦ 新能源"}

table_header = "| 合约 | 收盘价 | 涨跌幅 | 持仓量变化 | 周线趋势 | 日线位置 | 15分钟信号 |\n|------|--------|--------|-----------|---------|---------|-----------|\n"

abnormal_futures_list = []

for cat in ["贵金属","有色金属","能源","化工","黑色系","农产品","新能源"]:
    fl("### " + cat_display.get(cat, cat))
    fl(table_header)
    for symbol, name in futures_by_cat.get(cat, []):
        if symbol not in futures_data or not futures_data[symbol].get("klines"):
            fl("| {} | 数据获取失败 | — | — | — | — | 无 |\n".format(name))
            continue
        fd = futures_data[symbol]
        klines = fd["klines"]
        today = klines[-1]
        close = today.get("c", "—")
        chg = fd.get("chg_pct", 0)
        # OI change
        if len(klines) >= 2:
            oi_chg = int(klines[-1].get("p",0)) - int(klines[-2].get("p",0))
            oi_str = "{:+,}".format(oi_chg)
        else:
            oi_chg = 0
            oi_str = "—"
        # Simple 3-day trend
        if len(klines) >= 3:
            c3a = float(klines[-3].get("c",0))
            c3b = float(klines[-1].get("c",0))
            trend = "偏强" if c3b > c3a * 1.03 else ("偏弱" if c3b < c3a * 0.97 else "震荡")
        else:
            trend = "—"
        fl("| {} | {} | **{:+.2f}%** | {} | {} | — | 无 |\n".format(name, close, chg, oi_str, trend))

        if abs(chg) >= 2:
            abnormal_futures_list.append((name, cat, chg, oi_chg, oi_str, symbol))
    fl("")

# Abnormal futures
abnormal_futures_list.sort(key=lambda x: abs(x[2]), reverse=True)

fl("---")
fl("")
fl("## 三、异动合约")
fl("")
fl("| 合约 | 方向 | 涨跌幅 | 持仓量异动 | 可能原因 | 是否关注 |")
fl("|------|------|--------|-----------|---------|---------|")
if abnormal_futures_list:
    for name, cat, chg, oi_chg, oi_str, sym in abnormal_futures_list:
        direction = "↑" if chg > 0 else "↓"
        oi_type = "增仓" if oi_chg > 0 else ("减仓" if oi_chg < 0 else "—")
        attn = "重点关注" if abs(chg) >= 3 else "观察"
        fl("| {} | {} | **{:+.2f}%** | {} {} | 市场驱动 | {} |".format(name, direction, chg, oi_type, oi_str, attn))
else:
    fl("| 无显著异动合约 | | | | | |")
fl("")
fl("**关键信号**：")
fl("- **增仓上涨** -> 新多入场，趋势可能延续")
fl("- **增仓下跌** -> 新空入场，下跌动能强")
fl("- **减仓上涨** -> 空头平仓，反弹而非反转")
fl("- **减仓下跌** -> 多头平仓，回调而非转势")
fl("")
fl("---")
fl("")
fl("## 四、今日交易记录")
fl("")
fl("### 开仓")
fl("| 合约 | 方向 | 手数 | 开仓价 | 开仓理由 | 止损位 | 符合计划 |")
fl("|------|------|------|--------|---------|-------|---------|")
fl("| | 多/空 | | | | | / ❌ |")
fl("")
fl("### 平仓")
fl("| 合约 | 方向 | 手数 | 平仓价 | 平仓理由 | 盈亏 | 反思 |")
fl("|------|------|------|--------|---------|------|------|")
fl("| | 多/空 | | | 止盈/止损/手动 | ±R | |")
fl("")
fl("> 今日无交易")
fl("")
fl("---")
fl("")
fl("## 五、持仓跟踪")
fl("")
fl("| 合约 | 方向 | 手数 | 开仓均价 | 现价 | 浮动盈亏 | 浮盈% | 止损位 | 目标位 | 当前判断 |")
fl("|------|------|------|---------|------|---------|-------|-------|-------|---------|")
fl("| | 多/空 | | | | ±R | ±% | | | 持有/加仓/减仓/止损 |")
fl("")
fl("**总保证金占用**：__元")
fl("**总浮动盈亏**：±R / ±%")
fl("**当前杠杆倍数**：__倍")
fl("**风险度（保证金/权益）**：__%")
fl("")
fl("---")
fl("")
fl("## 六、交割月提醒")
fl("")
fl("| 合约 | 最后交易日 | 是否需移仓 | 计划 |")
fl("|------|-----------|-----------|------|")
fl("| | | / 无需 | |")
fl("")
fl("---")
fl("")
fl("## 七、收盘复盘")
fl("")
fl("### 盘面综合判断")
if sector_avgs:
    strongest = max(sector_avgs, key=sector_avgs.get)
    weakest = min(sector_avgs, key=sector_avgs.get)
    fl("- 文华商品方向（规则一）：{}（估算综合指数 {:+.2f}%）".format("震荡偏弱" if total_avg < 0 else "震荡偏强", total_avg))
    fl("- 当前市场风格（趋势/震荡/无序）：**震荡偏弱**（板块严重分化）")
    fl("- 盈利效应（好/一般/差）：**一般**")
    fl("- 最强板块：**{}**（均涨跌 **{:+.2f}%**）".format(strongest, sector_avgs[strongest]))
    fl("- 最弱板块：**{}**（均涨跌 **{:+.2f}%**）".format(weakest, sector_avgs[weakest]))
fl("")
fl("### 交易系统检查（规则六~九）")
fl("- [ ] **规则六**：今日所有开仓是否顺周线方向？")
fl("- [ ] **规则七.1**：所有持仓是否设置了止损条件单？以第一笔开仓价为基准？")
fl("- [ ] **规则七.2**：浮盈品种是否需要上移止损至保本/锁定利润？")
fl("- [ ] **规则七.3**：**亏损持仓是否已清仓？** 收盘前是否检查了外盘异动？")
fl("- [ ] **规则三**：交易的品种是否在 Top 30 流动性名单中？")
fl("- [ ] **规则八**：单笔亏损是否超过 1%？单日是否超过 3%？")
fl("- [ ] **规则九**：今日是否有在禁区内交易？")
fl("- [ ] 是否有逆周线方向的开仓？如有，记录原因")
fl("")
fl("### 今日做得好的")
fl("- ")
fl("")
fl("### 今日做得不好的 / 违反纪律")
fl("- [ ] ")
fl("")
fl("### 明日计划")
fl("- [ ] 关注：")
fl("- [ ] 计划操作：")
fl("- [ ] 关键价位：")
fl("")
fl("### 自由记录")
fl("> ")
fl("")
fl("---")
fl("")
fl("## 八、链接")
fl("")
fl("- 股票日志：[{}](../股票/{}.md)".format(DATE, DATE))
fl("- 交易系统：[[04-Research/04-交易系统/期货交易系统]]")
fl("- 品种研究：待创建")

futures_log = "\n".join(FL)
futures_path = os.path.join(NOTES, "期货", "{}.md".format(DATE))
os.makedirs(os.path.dirname(futures_path), exist_ok=True)
with open(futures_path, 'w', encoding='utf-8') as f:
    f.write(futures_log)
print("  Written: docs/Notes/期货/{}.md ({} lines)".format(DATE, len(FL)))

# Summary output
print("\n" + "=" * 50)
print("ALL DONE!")
print("  Stock log: docs/Notes/股票/{}.md".format(DATE))
print("  Futures log: docs/Notes/期货/{}.md".format(DATE))
print("=" * 50)
