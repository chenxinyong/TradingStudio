#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Complete trading log generator
- Fetches stock data from Sina API
- Fetches futures data from Sina API
- Generates both stock and futures log files
"""

import subprocess
import json
import os
import re
from datetime import datetime

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"
NOTES = r"c:\Works\ClaudeCode\TradingStudio\docs\Notes"
DATE = "2026-06-08"
PREV_DATE = "2026-06-05"

def curl_fetch_text(url, timeout=20):
    """Fetch text using curl"""
    try:
        result = subprocess.run(
            ['curl', '-s', '--max-time', str(timeout),
             '-H', 'Referer: https://finance.sina.com.cn',
             '-H', 'User-Agent: Mozilla/5.0',
             url],
            capture_output=True, timeout=timeout+5
        )
        if result.returncode == 0 and result.stdout:
            return result.stdout.decode('gbk', errors='replace')
    except Exception as e:
        print(f"  curl error: {e}")
    return None

def parse_hq_str(text):
    """Parse Sina hq_str format into dict"""
    result = {}
    for line in text.strip().split('\n'):
        line = line.strip()
        if not line.startswith('var hq_str_'):
            continue
        # Parse: var hq_str_sh688256="data";
        match = re.match(r'var hq_str_([^=]+)="(.+)"', line)
        if not match:
            continue
        code = match.group(1)
        fields = match.group(2).split(',')
        result[code] = fields
    return result

print("=" * 60)
print("交易日志自动生成 - 综合版")
print(f"日期: {DATE} (周一)")
print("=" * 60)

# ============ PART 1: Fetch Stock Data from Sina ============
print("\n[1/4] 从Sina API抓取股票数据...")

# All stock codes
all_stocks_codes = [
    # Layer 1
    ("sh688256","寒武纪"),("sh688041","海光信息"),("sh688047","龙芯中科"),("sh688008","澜起科技"),
    ("sz300474","景嘉微"),("sh601138","工业富联"),("sz000977","浪潮信息"),("sz000938","紫光股份"),
    ("sh603019","中科曙光"),("sz300308","中际旭创"),("sz300502","新易盛"),("sz300394","天孚通信"),
    ("sz002281","光迅科技"),("sz000988","华工科技"),("sz300476","胜宏科技"),("sz002463","沪电股份"),
    ("sz002916","深南电路"),("sz002938","鹏鼎控股"),("sh688183","生益电子"),("sh603228","景旺电子"),
    ("sz002837","英维克"),("sz300499","高澜股份"),("sz301018","申菱环境"),("sz002126","银轮股份"),
    ("sh688498","源杰科技"),("sh601869","长飞光纤"),("sh600487","亨通光电"),("sh600522","中天科技"),
    ("sz002851","麦格米特"),("sz002518","科士达"),("sz300857","协创数据"),("sz002384","东山精密"),
    ("sz300990","同飞股份"),("sh603893","瑞芯微"),("sh603296","华勤技术"),
    # Layer 2
    ("sh688981","中芯国际"),("sh688347","华虹公司"),("sh688396","华润微"),("sz002371","北方华创"),
    ("sh688012","中微公司"),("sh688072","拓荆科技"),("sh688082","盛美上海"),("sh688120","华海清科"),
    ("sh688019","安集科技"),("sz002409","雅克科技"),("sz300655","晶瑞电材"),("sh600584","长电科技"),
    ("sz002156","通富微电"),("sz002185","华天科技"),("sz300604","长川科技"),("sh688234","天岳先进"),
    ("sh600703","三安光电"),("sh603290","斯达半导"),("sh605358","立昂微"),("sh688126","沪硅产业"),
    ("sh603501","韦尔股份"),("sz300458","全志科技"),("sh688608","恒玄科技"),("sh603986","兆易创新"),
    ("sz300782","卓胜微"),
    # Layer 3
    ("sz002230","科大讯飞"),("sh688111","金山办公"),("sh600588","用友网络"),("sz300170","汉得信息"),
    ("sz300058","蓝色光标"),("sh600570","恒生电子"),("sz300496","中科创达"),("sh688561","奇安信"),
    ("sh688023","安恒信息"),
    # Layer 4
    ("sh688676","金盘科技"),("sz002922","伊戈尔"),("sz301291","明阳电气"),("sz300001","特锐德"),
    ("sz002706","良信股份"),("sz002364","中恒电气"),("sz002335","科华数据"),("sh601126","四方股份"),
    ("sz301120","新特电气"),("sz002484","江海股份"),("sz000338","潍柴动力"),("sz300153","科泰电源"),
    ("sh600841","动力新科"),("sh600482","中国动力"),("sh600885","宏发股份"),("sz301031","中熔电气"),
    ("sz300693","盛弘股份"),("sh600160","巨化股份"),
    # Layer 5
    ("sz002202","金风科技"),("sz001289","龙源电力"),("sz300772","运达股份"),("sh601985","中国核电"),
    ("sz002733","雄韬股份"),("sz002015","协鑫能科"),("sh688411","海博思创"),("sh600905","三峡能源"),
    ("sh688599","天合光能"),("sz301358","湖南裕能"),("sz300769","德方纳米"),("sz300073","当升科技"),
    ("sh603659","璞泰来"),("sz001301","尚太科技"),("sz002709","天赐材料"),("sz002812","恩捷股份"),
    ("sh688116","天奈科技"),("sh688772","珠海冠宇"),("sz300850","新强联"),
    # Layer 6
    ("sz300750","宁德时代"),("sz300014","亿纬锂能"),("sz300274","阳光电源"),("sh600406","国电南瑞"),
    ("sz002121","科陆电子"),("sh605117","德业股份"),("sh688032","禾迈股份"),("sh603063","禾望电气"),
    ("sz300763","锦浪科技"),("sz002245","蔚蓝锂芯"),("sz000400","许继电气"),("sh600089","特变电工"),
    ("sh601179","中国西电"),("sh600312","平高电气"),("sh688100","威胜信息"),("sz300882","万胜智能"),
    ("sh688248","南网科技"),
    # Layer 7
    ("sz300450","先导智能"),("sz300724","捷佳伟创"),("sz300751","迈为股份"),("sh688516","奥特维"),
    ("sz300124","汇川技术"),("sh603416","信捷电气"),("sz002979","雷赛智能"),("sz002747","埃斯顿"),
    ("sz002008","大族激光"),("sz300747","锐科激光"),("sz300776","帝尔激光"),("sh688518","联赢激光"),
    ("sh688305","科德数控"),("sh601882","海天精工"),("sh688697","纽威数控"),("sz300161","华中数控"),
    ("sh603699","纽威股份"),("sz002438","江苏神通"),("sh688698","伟创电气"),("sh688017","绿的谐波"),
    ("sz002472","双环传动"),("sz003021","兆威机电"),("sh601689","拓普集团"),("sh603728","鸣志电器"),
    ("sz300007","汉威科技"),("sz300115","长盈精密"),
    # Layer 8
    ("sz000636","风华高科"),("sz300408","三环集团"),("sz002138","顺络电子"),("sh600563","法拉电子"),
    ("sz002199","东晶电子"),("sh605376","博迁新材"),("sz002475","立讯精密"),("sh688800","瑞可达"),
    ("sz300913","兆龙互连"),("sz002130","沃尔核材"),("sz300285","国瓷材料"),("sh603890","春秋电子"),
    ("sz002241","歌尔股份"),
    # Layer 9
    ("sh601899","紫金矿业"),("sh601600","中国铝业"),("sh603993","洛阳钼业"),("sz000657","中钨高新"),
    ("sh600549","厦门钨业"),("sz002378","章源钨业"),("sz000960","锡业股份"),("sh600301","华锡有色"),
    ("sz002155","湖南黄金"),("sh601020","华钰矿业"),("sz002428","云南锗业"),("sh600111","北方稀土"),
    ("sz300748","金力永磁"),("sz000603","盛达资源"),
]

# Layer info
stock_layer = {}
for code, name in all_stocks_codes:
    for stock in [
        (1,"AI芯片","B","688256","688041","688047","688008","300474"),
        (1,"AI服务器","A","601138"),(1,"AI服务器","B","000977","000938","603019"),
        (1,"光模块","A","300308"),(1,"光模块","B","300502","300394","002281","000988"),
        (1,"PCB","A","300476","002463"),(1,"PCB","B","002916","688183","603228"),
        (1,"PCB","C","002938","002384"),(1,"液冷","B","002837","300499","002126"),
        (1,"液冷","C","301018","300990"),(1,"光芯片","B","688498"),
        (1,"光纤光缆","B","601869","600487","600522"),
        (1,"服务器电源","C","002851"),(1,"电源","C","002518"),
        (1,"算力租赁","C","300857"),(1,"端侧AI芯片","A","603893"),
        (1,"消费电子ODM","B","603296"),
        (2,"晶圆代工","A","688981"),(2,"晶圆代工","B","688347"),(2,"晶圆代工","C","688396"),
        (2,"半导体设备","A","002371","688012","688072"),(2,"半导体设备","B","688082","688120"),
        (2,"半导体材料","B","688019"),(2,"半导体材料","C","002409","300655"),
        (2,"封测","A","600584"),(2,"封测","B","002156","002185","300604"),
        (2,"碳化硅","C","688234","600703"),(2,"功率半导体","B","603290"),
        (2,"硅片+功率","B","605358"),(2,"硅片","C","688126"),
        (2,"CIS传感器","A","603501"),(2,"端侧AI SoC","B","300458"),
        (2,"AIoT SoC","B","688608"),(2,"存储芯片","A","603986"),
        (2,"射频前端","A","300782"),
        (3,"AI应用","C","002230"),(3,"AI应用","B","688111"),(3,"AI应用","C","600588","300170","300058"),
        (3,"AI应用","B","600570"),(3,"AI OS+Agent","B","300496"),
        (3,"AI安全","A","688561"),(3,"AI安全","B","688023"),
        (4,"变压器","A","688676"),(4,"变压器","B","002922"),(4,"变压器","C","301291","301120"),
        (4,"配电","C","300001","002706"),(4,"HVDC","B","002364"),
        (4,"UPS","B","002335"),(4,"电力设备","B","601126"),
        (4,"超级电容","C","002484"),(4,"柴油发电机","A","000338"),
        (4,"发电机组","C","300153"),(4,"发电设备","C","600841"),
        (4,"燃气内燃机","C","600482"),(4,"继电器","A","600885"),
        (4,"熔断器","C","301031"),(4,"储能变流器","B","300693"),
        (4,"AI液冷","B","600160"),
        (5,"风电","C","002202","001289","300772"),(5,"核电","A","601985"),
        (5,"氢能","C","002733"),(5,"绿电","C","002015"),(5,"储能","C","688411"),
        (5,"绿电","B","600905"),(5,"光伏组件","C","688599"),
        (5,"磷酸铁锂","C","301358","300769"),(5,"三元正极","C","300073"),
        (5,"负极材料","B","603659","001301"),(5,"电解液","A","002709"),
        (5,"隔膜","B","002812"),(5,"导电剂","C","688116"),
        (5,"消费电池","B","688772"),(5,"风电轴承","B","300850"),
        (6,"储能电池","A","300750"),(6,"储能电池","B","300014"),
        (6,"逆变器","B","300274"),(6,"电网储能","A","600406"),
        (6,"储能系统","C","002121"),(6,"逆变器","B","605117"),
        (6,"逆变器","C","688032"),(6,"储能PCS","C","603063"),
        (6,"逆变器","C","300763"),(6,"锂电池","C","002245"),
        (6,"特高压","B","000400"),(6,"变压器","B","600089"),
        (6,"特高压","B","601179"),(6,"特高压","C","600312"),
        (6,"能源物联网","B","688100"),(6,"智慧计量","C","300882"),
        (6,"数智化","B","688248"),
        (7,"锂电设备","B","300450"),(7,"光伏设备","B","300724"),
        (7,"光伏设备","C","300751","688516"),(7,"工业自动化","A","300124"),
        (7,"PLC","C","603416"),(7,"运动控制","C","002979"),
        (7,"工业机器人","B","002747"),(7,"激光设备","A","002008"),
        (7,"激光器","C","300747"),(7,"激光设备","C","300776"),
        (7,"激光焊接","C","688518"),(7,"数控机床","A","688305"),
        (7,"数控机床","B","601882","688697"),(7,"数控系统","C","300161"),
        (7,"工业阀门","B","603699"),(7,"特种阀门","C","002438"),
        (7,"变频器","C","688698"),(7,"减速器","B","688017","002472"),
        (7,"执行器","C","003021"),(7,"执行器","B","601689"),
        (7,"电机","C","603728"),(7,"传感器","C","300007"),
        (7,"结构件","C","300115"),
        (8,"MLCC","A","000636"),(8,"MLCC","B","300408"),
        (8,"电感","B","002138"),(8,"薄膜电容","B","600563"),
        (8,"石英晶振","C","002199"),(8,"MLCC材料","B","605376","300285"),
        (8,"连接器","A","002475"),(8,"连接器","C","688800"),
        (8,"铜缆连接","C","300913"),(8,"铜缆连接","B","002130"),
        (8,"PC结构件","C","603890"),(8,"声学/传感器","B","002241"),
        (9,"铜","A","601899"),(9,"铝","B","601600"),(9,"铜钴","B","603993"),
        (9,"钨","B","000657","600549"),(9,"钨","C","002378"),
        (9,"锡","B","000960"),(9,"锡锑","C","600301"),
        (9,"锑黄金","B","002155"),(9,"锑","C","601020"),
        (9,"锗","B","002428"),(9,"稀土","B","600111"),
        (9,"稀土永磁","B","300748"),(9,"白银","B","000603"),
    ]:
        layer = stock[0]
        sub = stock[1]
        level = stock[2]
        codes_in_group = stock[3:]
        c = code.replace('sh','').replace('sz','')
        if c in codes_in_group:
            stock_layer[code] = {"layer": layer, "sub": sub, "level": level}

print(f"  Total stocks configured: {len(all_stocks_codes)}")

# Index codes for sina
index_codes = ["sh000001", "sz399001", "sz399006", "sh000688", "sz399317", "sh000016", "sh000300", "sh000905", "sh000852"]
index_names = {"sh000001":"上证指数","sz399001":"深证成指","sz399006":"创业板指","sh000688":"科创50","sz399317":"国证A股","sh000016":"上证50","sh000300":"沪深300","sh000905":"中证500","sh000852":"中证1000"}

# Fetch all stocks in batches
batch_size = 50
all_data = {}

all_codes_to_fetch = index_codes + [c for c, _ in all_stocks_codes]

for i in range(0, len(all_codes_to_fetch), batch_size):
    batch = all_codes_to_fetch[i:i+batch_size]
    url = f"https://hq.sinajs.cn/list={','.join(batch)}"
    text = curl_fetch_text(url)
    if text:
        parsed = parse_hq_str(text)
        all_data.update(parsed)
        print(f"  Batch {i//batch_size + 1}: {len(parsed)} items")
    else:
        print(f"  Batch {i//batch_size + 1}: FAILED")
    import time
    time.sleep(0.3)

print(f"\n  Total fetched: {len(all_data)}/{len(all_codes_to_fetch)}")

# Print indices
print("\n[Index Data]:")
for ic in index_codes:
    if ic in all_data:
        d = all_data[ic]
        try:
            prev_close = float(d[2])
            current = float(d[3])
            chg_pct = (current - prev_close) / prev_close * 100
            print(f"  {index_names.get(ic, ic)}: {current:.2f} ({chg_pct:+.2f}%)")
        except:
            print(f"  {index_names.get(ic, ic)}: parse error")

# ============ PART 2: Fetch Futures Data ============
print("\n[2/4] 正在抓取期货日K线数据...")

futures_contracts = [
    ("AU2609", "黄金 AU", "贵金属"), ("AG2609", "白银 AG", "贵金属"),
    ("CU2609", "铜 CU", "有色金属"), ("AL2609", "铝 AL", "有色金属"),
    ("ZN2609", "锌 ZN", "有色金属"), ("NI2609", "镍 NI", "有色金属"),
    ("SN2609", "锡 SN", "有色金属"), ("PB2609", "铅 PB", "有色金属"),
    ("SC2609", "原油 SC", "能源"), ("FU2609", "燃料油 FU", "能源"),
    ("TA2609", "PTA TA", "化工"), ("MA2609", "甲醇 MA", "化工"),
    ("V2609", "PVC V", "化工"), ("L2609", "LLDPE L", "化工"),
    ("EG2609", "乙二醇 EG", "化工"),
    ("RB2609", "螺纹 RB", "黑色系"), ("HC2609", "热卷 HC", "黑色系"),
    ("I2609", "铁矿 I", "黑色系"), ("JM2609", "焦煤 JM", "黑色系"),
    ("J2609", "焦炭 J", "黑色系"),
    ("M2609", "豆粕 M", "农产品"), ("Y2609", "豆油 Y", "农产品"),
    ("P2609", "棕榈油 P", "农产品"), ("SR2609", "白糖 SR", "农产品"),
    ("CF2609", "棉花 CF", "农产品"), ("RM2609", "菜粕 RM", "农产品"),
    ("LC2609", "碳酸锂 LC", "新能源"), ("SI2609", "工业硅 SI", "新能源"),
]

futures_data = {}

for symbol, name, category in futures_contracts:
    url = f"https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{symbol}=/InnerFuturesNewService.getDailyKLine?symbol={symbol}"
    text = curl_fetch_text(url)
    if text:
        try:
            json_str = text[text.index('['):text.rindex(']')+1]
            klines = json.loads(json_str)
            recent = klines[-3:] if len(klines) >= 3 else klines
            chg_pct = 0
            if len(recent) >= 2:
                prev_settle = float(recent[-2].get('s', 0))
                curr_settle = float(recent[-1].get('s', 0))
                if prev_settle:
                    chg_pct = (curr_settle - prev_settle) / prev_settle * 100
            futures_data[symbol] = {
                "name": name, "category": category, "klines": recent,
                "all_count": len(klines), "chg_pct": round(chg_pct, 2)
            }
            print(f"  {name}: 收{recent[-1].get('c')} 结{recent[-1].get('s')} ({chg_pct:+.2f}%)")
        except Exception as e:
            print(f"  {name}: parse error - {e}")
            futures_data[symbol] = {"name": name, "category": category, "klines": []}
    else:
        print(f"  {name}: 数据获取失败")
        futures_data[symbol] = {"name": name, "category": category, "klines": []}

# ============ Save raw data ============
print("\n[3/4] 正在生成日志文件...")
raw_data = {"stocks": all_data, "futures": futures_data, "stock_layer": stock_layer,
            "index_codes": index_codes, "index_names": index_names,
            "all_stocks_codes": all_stocks_codes, "futures_contracts": futures_contracts}
os.makedirs(os.path.join(VAULT, "scripts"), exist_ok=True)
import pickle
with open(os.path.join(VAULT, "scripts", "final_data.pkl"), "wb") as f:
    pickle.dump(raw_data, f)

# Helper: get stock price info
def get_stock_info(sina_code):
    if sina_code not in all_data:
        return None, None, None
    d = all_data[sina_code]
    try:
        prev_close = float(d[2])
        current = float(d[3])
        chg_pct = (current - prev_close) / prev_close * 100
        name = d[0]
        return current, chg_pct, name
    except:
        return None, None, None

# ============ GENERATE STOCK LOG ============
layer_names = {
    1: "AI算力基座", 2: "半导体产业链", 3: "大模型与AI应用",
    4: "AI电力设备", 5: "新能源发电", 6: "储能与调节",
    7: "高端装备制造", 8: "电子元器件", 9: "石化与基础金属"
}

# Calculate layer performance
layer_perf = {}
for code, name in all_stocks_codes:
    info = stock_layer.get(code, {})
    layer = info.get("layer")
    if not layer:
        continue
    price, chg, _ = get_stock_info(code)
    if chg is not None:
        if layer not in layer_perf:
            layer_perf[layer] = {"names": [], "changes": [], "up": 0, "down": 0, "total": 0}
        layer_perf[layer]["changes"].append(chg)
        layer_perf[layer]["total"] += 1
        if chg > 0: layer_perf[layer]["up"] += 1
        elif chg < 0: layer_perf[layer]["down"] += 1

layer_avg = {l: sum(p["changes"])/len(p["changes"]) for l, p in layer_perf.items() if p["changes"]}
sorted_layers = sorted(layer_avg.items(), key=lambda x: x[1], reverse=True)

# Up/down counts
up_count = sum(1 for c in all_stocks_codes if get_stock_info(c)[1] is not None and get_stock_info(c)[1] > 0)
down_count = sum(1 for c in all_stocks_codes if get_stock_info(c)[1] is not None and get_stock_info(c)[1] < 0)
flat_count = sum(1 for c in all_stocks_codes if get_stock_info(c)[1] is not None and get_stock_info(c)[1] == 0)

# Abnormal stocks (|chg| >= 5%)
abnormal_stocks = []
near_stocks = []
all_with_chg = []
for code, name in all_stocks_codes:
    price, chg, _ = get_stock_info(code)
    if chg is not None:
        all_with_chg.append((code, name, chg, price))
        if abs(chg) >= 5:
            abnormal_stocks.append((code, name, chg, price))
        elif abs(chg) >= 3:
            near_stocks.append((code, name, chg, price))

all_with_chg.sort(key=lambda x: x[2], reverse=True)
abnormal_stocks.sort(key=lambda x: abs(x[2]), reverse=True)
near_stocks.sort(key=lambda x: abs(x[2]), reverse=True)

# Build log content
sl = []
def L(s=""): sl.append(s)

L(f"---")
L(f"date: {DATE}")
L(f"tags:")
L(f"  - 交易日志/股票")
L(f"---")
L(f"")
L(f"# 📊 股票交易日志 - {DATE}")
L(f"")
L(f"> 🕐 上班不能盯盘？→ 盘前挂好单，午休看一眼，收盘再复盘。")
L(f"> 策略参考：[[04-Research/04-交易系统/非盯盘交易策略]]")
L(f"")
L(f"---")
L(f"")
L(f"## 盘前计划（出门前填好）")
L()
L("### 📋 今日挂单")
L("| 标的 | 方向 | 价格 | 数量 | 逻辑 |")
L("|------|------|------|------|------|")
L("| | 买入/卖出 | | | |")
L("| | 买入/卖出 | | | |")
L()
L("### ⚠️ 止损单检查")
L("| 标的 | 止损价 | 是否已设置 | 有效期 |")
L("|------|--------|-----------|--------|")
L("| | | ✅ / ❌ | |")
L()
L("### 📌 今天最重要的3件事")
L("1. ")
L("2. ")
L("3. ")
L()
L("---")
L()
L("## 📡 自选股扫描（5分钟速览）")
L()
L("> 扫描方法：[[04-Research/04-交易系统/自选股分级扫描系统]]")
L()

# Active layers
L("### 今日活跃层级")
if sorted_layers:
    top_l = sorted_layers[0]
    mid_l = sorted_layers[len(sorted_layers)//2] if len(sorted_layers) > 2 else sorted_layers[-1]
    bot_l = sorted_layers[-1]
    te = "🟢" if top_l[1] > 0 else "🔴"
    me = "🟡"
    be = "🔴" if bot_l[1] < 0 else "🟢"
    L(f"- {te} 第{top_l[0]}层（{layer_names.get(top_l[0],'')}）：均涨跌 **{top_l[1]:+.2f}%**")
    L(f"- {me} 第{mid_l[0]}层（{layer_names.get(mid_l[0],'')}）：均涨跌 **{mid_l[1]:+.2f}%**")
    L(f"- {be} 第{bot_l[0]}层（{layer_names.get(bot_l[0],'')}）：均涨跌 **{bot_l[1]:+.2f}%**")
L()

# A-class
L("### A类个股异动")
L("| 标的 | 涨跌幅 | 信号 | 操作 |")
L("|------|--------|------|------|")
if abnormal_stocks:
    for code, name, chg, price in abnormal_stocks[:10]:
        signal = "🔴 大涨" if chg > 0 else "🔴 大跌"
        L(f"| **{name}** | **{chg:+.2f}%** | {signal} | 观察 |")
else:
    L("| 今日无显著异动（涨跌幅<5%） | | | |")
L()

L("### B/C类值得注意的")
L("| 标的 | 所属层 | 异动原因 | 是否升级A类 |")
L("|------|--------|---------|------------|")
if near_stocks:
    for code, name, chg, price in near_stocks[:5]:
        sinfo = stock_layer.get(code, {})
        L(f"| {name} | 第{sinfo.get('layer','?')}层 | 涨跌幅 **{chg:+.2f}%** | ❌ |")
else:
    L("| 无接近异动阈值的个股 | | | |")
L()
L("---")
L()
L("## 一、大盘指数")
L()
L("| 指数 | 收盘价 | 涨跌幅 | 备注 |")
L("|------|--------|--------|------|")

# Index data
index_order = ["国证A股","创业板指","科创50","上证指数","深证成指","上证50","沪深300","中证500","中证1000"]
for ic in index_codes:
    iname = index_names.get(ic, ic)
    d = all_data.get(ic)
    if d:
        try:
            prev_close = float(d[2])
            current = float(d[3])
            chg_pct = (current - prev_close) / prev_close * 100
            L(f"| {iname} | {current:.2f} | **{chg_pct:+.2f}%** | |")
        except:
            L(f"| {iname} | 数据获取失败 | — | |")
    else:
        L(f"| {iname} | 数据获取失败 | — | |")

L()
# Sentiment
if up_count > down_count * 1.5: sentiment = "活跃"
elif up_count > down_count: sentiment = "正常（偏多）"
elif down_count > up_count * 1.5: sentiment = "冷清（普跌）"
elif down_count > up_count: sentiment = "正常（偏空）"
else: sentiment = "正常"

L("**盘面感觉**：")
L(f"- 情绪（冷清/正常/活跃/亢奋/恐慌）：**{sentiment}**")
L(f"- 涨跌家数比（自选股范围）：**{up_count}涨 / {down_count}跌 / {flat_count}平**")
L("- 北向资金：数据获取失败")
L()
L("---")
L()
L("## 二、热点板块")
L()
L("### 14层产业链各层级表现")
L("| 层级 | 名称 | 平均涨跌幅 | 上涨/总家数 | 活跃度 |")
L("|------|------|-----------|-----------|-------|")
for l, avg in sorted_layers:
    ln = layer_names.get(l, f"第{l}层")
    p = layer_perf[l]
    act = "🟢" if avg > 0 else "🔴"
    L(f"| 第{l}层 | {ln} | **{avg:+.2f}%** | {p['up']}/{p['total']} | {act} |")

L()
L("### 涨幅居前个股（自选股TOP10）")
L("| 个股 | 所属层 | 涨跌幅 | 关注 |")
L("|------|--------|--------|------|")
for code, name, chg, price in all_with_chg[:10]:
    sinfo = stock_layer.get(code, {})
    attn = "🔴" if abs(chg) >= 5 else ("🟡" if abs(chg) >= 3 else "🟢")
    L(f"| **{name}** | 第{sinfo.get('layer','?')}层 | **{chg:+.2f}%** | {attn} |")

L()
L("### 跌幅居前个股（自选股TOP10）")
L("| 个股 | 所属层 | 涨跌幅 | 是否影响持仓 |")
L("|------|--------|--------|------------|")
all_with_chg_rev = sorted(all_with_chg, key=lambda x: x[2])
for code, name, chg, price in all_with_chg_rev[:10]:
    sinfo = stock_layer.get(code, {})
    L(f"| **{name}** | 第{sinfo.get('layer','?')}层 | **{chg:+.2f}%** | 观察 |")

L()
L("> 💡 链接到研究框架：哪些层级今天在动？")
for l, avg in sorted_layers[:3]:
    ln = layer_names.get(l, f"第{l}层")
    L(f"> - 第{l}层（{ln}）：均涨跌 **{avg:+.2f}%**")
L()
L("---")
L()
L("## 三、异动个股")
L()
L("| 个股 | 代码 | 异动方向 | 涨跌幅 | 可能原因 | 是否值得关注 |")
L("|------|------|---------|--------|---------|------------|")
if abnormal_stocks:
    for code, name, chg, price in abnormal_stocks[:8]:
        direction = "↑" if chg > 0 else "↓"
        sina_code_clean = code.replace("sh","").replace("sz","")
        L(f"| **{name}** | {sina_code_clean} | {direction} | **{chg:+.2f}%** | 数据获取失败 | 🔴 |")
else:
    L("| 今日无涨跌幅>5%的异动个股 | | | | | |")
L()
L("**说明**：🔴 重点关注 / 🟡 观察 / 🟢 忽略")
L()
L("---")
L()
L("## 四、今日交易记录")
L()
L("### 🟢 买入")
L("| 标的 | 代码 | 数量/金额 | 买入理由 | 对应研究框架层级 | 是否符合计划 |")
L("|------|------|----------|---------|---------------|------------|")
L("| | | | | 第__层： | ✅ / ❌ |")
L()
L("### 🔴 卖出")
L("| 标的 | 代码 | 数量/金额 | 卖出理由 | 盈亏 | 反思 |")
L("|------|------|----------|---------|------|------|")
L("| | | | | ±% | |")
L()
L("> 今日无交易")
L()
L("---")
L()
L("## 五、持仓跟踪")
L()
L("| 标的 | 代码 | 成本价 | 现价 | 盈亏% | 仓位占比 | 当前判断 | 操作计划 |")
L("|------|------|-------|------|-------|---------|---------|---------|")
L("| | | | | | % | 持有/加仓/减仓/止损 | |")
L("| | | | | | % | 持有/加仓/减仓/止损 | |")
L("| | | | | | % | 持有/加仓/减仓/止损 | |")
L()
L("**总仓位**：__%")
L("**持仓数量**：__支")
L("**单支最大仓位**：__%（标的：）")
L()
L("---")
L()
L("## 六、收盘复盘")
L()
L("### 今日做得好的")
L("- ")
L()
L("### 今日做得不好的 / 违反纪律的地方")
L("- [ ] ")
L()
L("### 🧠 理性检查（来自[[04-Research/04-交易系统/交易纪律与理性检查]]）")
L("- [ ] 今天所有操作都有明确理由，没有情绪化交易")
L("- [ ] 所有持仓的买入逻辑今天仍然成立")
L("- [ ] 没有违反仓位/止损纪律")
L("- [ ] 如果今天不是我的持仓，我愿意在当前价格买入吗？")
L("- [ ] 有没有掉进某个认知偏差？（确认偏误/锚定/损失厌恶/近期偏误等）")
L()
L("### 明日关注")
L("- [ ] ")
L("- [ ] ")
L()
L("### 自由记录（想法/疑虑/观察到的东西）")
L("> ")
L()
L("---")
L()
L("## 七、链接")
L()
L("- 研究框架：[[AI产业链深度分析]]")
L(f"- 期货日志：[[01-Daily/期货/{DATE}]]")
L("- 本周复盘：待创建")

stock_log = "\n".join(sl)

stock_path = os.path.join(NOTES, "股票", f"{DATE}.md")
os.makedirs(os.path.dirname(stock_path), exist_ok=True)
with open(stock_path, 'w', encoding='utf-8') as f:
    f.write(stock_log)
print(f"✅ 股票日志已写入: {stock_path}")

# ============ GENERATE FUTURES LOG ============
fl = []
def FL(s=""): fl.append(s)

FL("---")
FL(f"date: {DATE}")
FL("tags:")
FL("  - 交易日志/期货")
FL("---")
FL("")
FL(f"# 📊 期货交易日志 - {DATE}")
FL("")
FL(f"> **夜盘归属说明**：本日志记录 {DATE} 白天日盘 + {PREV_DATE} 夜盘")
FL("")
FL("---")
FL("")
FL("## 一、市场总览（规则一：文华商品定方向）")
FL("")
FL("### 商品指数")
FL("| 指数 | 收盘价 | 涨跌幅 | 趋势判断 | 备注 |")
FL("|------|--------|--------|---------|------|")
FL("| 文华商品 | — | — | ➡（数据获取失败） | 免费API无商品指数数据 |")
FL("| 工业品 | — | — | ➡ | 同上 |")
FL("| 农产品 | — | — | ➡ | 同上 |")
FL()
FL("**文华商品方向**：➡ 震荡 → 决定今日交易基调")
FL()
FL("### 股指期货")
FL("| 合约 | 收盘价 | 涨跌幅 | 基差 | 持仓量变化 |")
FL("|------|--------|--------|------|-----------|")
FL("| IF | — | — | — | — |")
FL("| IC | — | — | — | — |")
FL("| IM | — | — | — | — |")
FL("| IH | — | — | — | — |")
FL()
FL("**基差说明**：数据获取失败（免费API无股指期货连续数据）")
FL()
FL("---")
FL()
FL("## 二、各板块跟踪（规则二：七大类板块）")
FL()
FL("> 只关注规则三中 Top 30 流动性品种。周线选方向，日线看位置。")
FL()

# Category display
cat_display = {
    "贵金属": "① 贵金属", "有色金属": "② 有色金属", "能源": "③ 能源",
    "化工": "④ 化工", "黑色系": "⑤ 黑色系", "农产品": "⑥ 农产品", "新能源": "⑦ 新能源"
}

# Group futures
futures_by_cat = {}
for symbol, name, cat in futures_contracts:
    if cat not in futures_by_cat:
        futures_by_cat[cat] = []
    futures_by_cat[cat].append((symbol, name))

table_hdr = "| 合约 | 收盘价 | 涨跌幅 | 持仓量变化 | 周线趋势 | 日线位置 | 15分钟信号 |\n|------|--------|--------|-----------|---------|---------|-----------|\n"

for cat in ["贵金属","有色金属","能源","化工","黑色系","农产品","新能源"]:
    FL(f"### {cat_display.get(cat, cat)}")
    FL(table_hdr)
    for symbol, name in futures_by_cat.get(cat, []):
        if symbol not in futures_data:
            FL(f"| {name} | 数据获取失败 | — | — | ➡ | — | 无 |\n")
            continue
        fd = futures_data[symbol]
        klines = fd.get("klines", [])
        if not klines:
            FL(f"| {name} | 数据获取失败 | — | — | ➡ | — | 无 |\n")
            continue
        today = klines[-1]
        close = today.get("c", "—")
        chg = fd.get("chg_pct", 0)
        # Open interest
        if len(klines) >= 2:
            prev_oi = int(klines[-2].get("p", 0))
            curr_oi = int(klines[-1].get("p", 0))
            oi_str = f"{curr_oi - prev_oi:+,}"
        else:
            oi_str = "—"
        chg_str = f"**{chg:+.2f}%**" if abs(chg) < 2 else f"**{chg:+.2f}%** 🔴"
        # Trend (3-day)
        if len(klines) >= 3:
            c1 = float(klines[-3].get("c", 0))
            c3 = float(klines[-1].get("c", 0))
            trend = "⬆" if c3 > c1 * 1.03 else ("⬇" if c3 < c1 * 0.97 else "➡")
        else:
            trend = "➡"
        FL(f"| {name} | {close} | {chg_str} | {oi_str} | {trend} | — | 无 |\n")
    FL("")

# Abnormal futures
abnormal_futures = []
for symbol, name, cat in futures_contracts:
    if symbol in futures_data:
        chg = futures_data[symbol].get("chg_pct", 0)
        if abs(chg) >= 2:
            klines = futures_data[symbol].get("klines", [])
            oi_str = "—"
            if len(klines) >= 2:
                prev_oi = int(klines[-2].get("p", 0))
                curr_oi = int(klines[-1].get("p", 0))
                oi_str = f"{curr_oi - prev_oi:+,}"
            abnormal_futures.append((name, cat, chg, oi_str, symbol))
abnormal_futures.sort(key=lambda x: abs(x[2]), reverse=True)

FL("---\n\n## 三、异动合约\n")
FL("| 合约 | 方向 | 涨跌幅 | 持仓量异动 | 可能原因 | 是否关注 |")
FL("|------|------|--------|-----------|---------|---------|")
if abnormal_futures:
    for name, cat, chg, oi_str, sym in abnormal_futures:
        direction = "↑" if chg > 0 else "↓"
        oi_type = "增仓" if oi_str != "—" and float(oi_str.replace(",","")) > 0 else ("减仓" if oi_str != "—" else "—")
        attn = "🔴" if abs(chg) >= 3 else "🟡"
        FL(f"| {name} | {direction} | **{chg:+.2f}%** | {oi_type} {oi_str} | 数据获取失败 | {attn} |")
else:
    FL("| 无显著异动合约 | | | | | |")

FL("""
**关键信号**：
- **增仓上涨** → 新多入场，趋势可能延续
- **增仓下跌** → 新空入场，下跌动能强
- **减仓上涨** → 空头平仓，反弹而非反转
- **减仓下跌** → 多头平仓，回调而非转势

---

## 四、今日交易记录

### 🟢 开仓
| 合约 | 方向 | 手数 | 开仓价 | 开仓理由 | 止损位 | 符合计划 |
|------|------|------|--------|---------|-------|---------|
| | 多/空 | | | | | ✅ / ❌ |
| | 多/空 | | | | | ✅ / ❌ |

### 🔴 平仓
| 合约 | 方向 | 手数 | 平仓价 | 平仓理由 | 盈亏 | 反思 |
|------|------|------|--------|---------|------|------|
| | 多/空 | | | 止盈/止损/手动 | ±R | |
| | 多/空 | | | 止盈/止损/手动 | ±R | |

> 今日无交易

---

## 五、持仓跟踪

| 合约 | 方向 | 手数 | 开仓均价 | 现价 | 浮动盈亏 | 浮盈% | 止损位 | 目标位 | 当前判断 |
|------|------|------|---------|------|---------|-------|-------|-------|---------|
| | 多/空 | | | | ±R | ±% | | | 持有/加仓/减仓/止损 |
| | 多/空 | | | | ±R | ±% | | | 持有/加仓/减仓/止损 |

**总保证金占用**：__元
**总浮动盈亏**：±R / ±%
**当前杠杆倍数**：__倍
**风险度（保证金/权益）**：__%

---

## 六、交割月提醒

| 合约 | 最后交易日 | 是否需移仓 | 计划 |
|------|-----------|-----------|------|
| | | ✅ 需移仓 / ❌ 无需 | |

---

## 七、收盘复盘

### 盘面综合判断
- 文华商品方向（规则一）：➡ 震荡（数据获取失败）
- 当前市场风格（趋势/震荡/无序）：需结合文华商品指数判断
- 盈利效应（好/一般/差）：—
""")

# Sector analysis
sector_avg = {}
for cat in futures_by_cat:
    changes = []
    for symbol, name in futures_by_cat[cat]:
        if symbol in futures_data and futures_data[symbol].get("chg_pct") is not None:
            changes.append(futures_data[symbol]["chg_pct"])
    if changes:
        sector_avg[cat] = sum(changes) / len(changes)

if sector_avg:
    strongest = max(sector_avg, key=sector_avg.get)
    weakest = min(sector_avg, key=sector_avg.get)
    FL(f"- 最强板块：**{strongest}**（均涨跌 **{sector_avg[strongest]:+.2f}%**）")
    FL(f"- 最弱板块：**{weakest}**（均涨跌 **{sector_avg[weakest]:+.2f}%**）")

FL("""
### 交易系统检查（规则六~九）
- [ ] **规则六**：今日所有开仓是否顺周线方向？
- [ ] **规则七.1**：所有持仓是否设置了止损条件单？以第一笔开仓价为基准？
- [ ] **规则七.2**：浮盈品种是否需要上移止损至保本/锁定利润？
- [ ] **规则七.3**：**亏损持仓是否已清仓？** 收盘前是否检查了外盘异动？
- [ ] **规则三**：交易的品种是否在 Top 30 流动性名单中？
- [ ] **规则八**：单笔亏损是否超过 1%？单日是否超过 3%？
- [ ] **规则九**：今日是否有在禁区内交易？
- [ ] 是否有逆周线方向的开仓？如有，记录原因

### 今日做得好的
-

### 今日做得不好的 / 违反纪律
- [ ]

### 明日计划
- [ ] 关注：
- [ ] 计划操作：
- [ ] 关键价位：

### 自由记录
>

---

## 八、链接

""")
FL(f"- 股票日志：[[01-Daily/股票/{DATE}]]")
FL("- 交易系统：[[04-Research/04-交易系统/期货交易系统]]")
FL("- 品种研究：待创建")

futures_log = "\n".join(fl)

futures_path = os.path.join(NOTES, "期货", f"{DATE}.md")
os.makedirs(os.path.dirname(futures_path), exist_ok=True)
with open(futures_path, 'w', encoding='utf-8') as f:
    f.write(futures_log)
print(f"✅ 期货日志已写入: {futures_path}")

print(f"\n{'='*50}")
print("✅✅✅ 全部完成！")
print(f"  📄 股票日志: 01-Daily/股票/{DATE}.md")
print(f"  📄 期货日志: 01-Daily/期货/{DATE}.md")
print(f"{'='*50}")
