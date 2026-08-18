#!/usr/bin/env python3
"""
收盘日志自动生成器
用法:  python log_generator.py            → 生成今日股票+期货日志
       python log_generator.py 2026-07-24 → 生成指定日期日志

输出: docs/trading/Notes/股票/YYYY-MM-DD.md + docs/trading/Notes/期货/YYYY-MM-DD.md
     自动填充指数/板块/异动数据，分析部分留空手动填写
"""

import sys, os, json, urllib.request, re
from datetime import datetime

# ============= CONFIG =============
BASE_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STOCK_DIR = os.path.join(BASE_DIR, "docs", "trading", "Notes", "股票")
FUT_DIR   = os.path.join(BASE_DIR, "docs", "trading", "Notes", "期货")

# Layer names (9层产业框架，匹配 docs/trading/行业分析/产业链分层/)
LAYER_NAMES = {
    1: "AI算力基座", 2: "半导体产业链", 3: "大模型与AI应用",
    4: "AI电力设备", 5: "新能源发电与电池材料", 6: "储能与智能电网",
    7: "高端装备与机器人", 8: "电子零部件", 9: "基本金属与小金属",
}

# ============= DATA FETCH (reuses daily_scan.py logic) =============

# 上证系列指数（以000开头，需要sh前缀，不能走sz逻辑）
_SH_INDEX_CODES = frozenset({"000001","000016","000300","000688","000852","000905"})

def get_exchange_prefix(code):
    if code in _SH_INDEX_CODES: return "sh"
    if code.startswith("6") or code.startswith("9"): return "sh"
    if code.startswith("0") or code.startswith("3") or code.startswith("1"): return "sz"
    if code.startswith("4") or code.startswith("8"): return "bj"
    return "sh"

def fetch_stock_data(codes):
    """Fetch stock data from Tencent API. Returns {code: {...}}."""
    results = {}
    batch_size = 50
    for i in range(0, len(codes), batch_size):
        batch = codes[i:i+batch_size]
        qs = ",".join(f"{get_exchange_prefix(c)}{c}" for c in batch)
        url = f"https://qt.gtimg.cn/q={qs}"
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            resp = urllib.request.urlopen(req, timeout=15)
            text = resp.read().decode("gbk")
            for line in text.strip().split("\n"):
                if not line.strip(): continue
                match = re.search(r'"(.*)"', line)
                if not match: continue
                fields = match.group(1).split("~")
                if len(fields) < 40: continue
                code = fields[2]
                try:
                    results[code] = {
                        "name": fields[1],
                        "current": float(fields[3]) if fields[3] else 0,
                        "change_pct": float(fields[32]) if fields[32] else 0,
                        "high": float(fields[33]) if fields[33] else 0,
                        "low": float(fields[34]) if fields[34] else 0,
                        "turnover": float(fields[37]) if fields[37] else 0,
                        "open": float(fields[5]) if fields[5] else 0,
                        "prev_close": float(fields[4]) if fields[4] else 0,
                    }
                except (ValueError, IndexError):
                    continue
        except Exception as e:
            print(f"  [WARN] API error: {e}", file=sys.stderr)
    return results

# ============= STOCK LIST =============

STOCKS = [
    # Layer 1: AI Computing Base (34)
    ("寒武纪","688256",1),("海光信息","688041",1),("龙芯中科","688047",1),
    ("澜起科技","688008",1),("景嘉微","300474",1),("工业富联","601138",1),
    ("浪潮信息","000977",1),("紫光股份","000938",1),("中科曙光","603019",1),
    ("中际旭创","300308",1),("新易盛","300502",1),("天孚通信","300394",1),
    ("光迅科技","002281",1),("华工科技","000988",1),("胜宏科技","300476",1),
    ("沪电股份","002463",1),("深南电路","002916",1),("鹏鼎控股","002938",1),
    ("生益电子","688183",1),("景旺电子","603228",1),("英维克","002837",1),
    ("高澜股份","300499",1),("申菱环境","301018",1),("银轮股份","002126",1),
    ("源杰科技","688498",1),("长飞光纤","601869",1),("亨通光电","600487",1),
    ("中天科技","600522",1),("麦格米特","002851",1),("科士达","002518",1),
    ("协创数据","300857",1),("东山精密","002384",1),("同飞股份","300990",1),
    # Layer 2: Semiconductor (17)
    ("中芯国际","688981",2),("华虹公司","688347",2),("华润微","688396",2),
    ("北方华创","002371",2),("中微公司","688012",2),("拓荆科技","688072",2),
    ("盛美上海","688082",2),("华海清科","688120",2),("安集科技","688019",2),
    ("雅克科技","002409",2),("晶瑞电材","300655",2),("长电科技","600584",2),
    ("通富微电","002156",2),("华天科技","002185",2),("长川科技","300604",2),
    ("天岳先进","688234",2),("三安光电","600703",2),
    # Layer 3: AI Applications (7)
    ("科大讯飞","002230",3),("金山办公","688111",3),("用友网络","600588",3),
    ("汉得信息","300170",3),("蓝色光标","300058",3),("恒生电子","600570",3),
    ("联影医疗","688271",3),
    # Layer 4: AI Power Equipment (17)
    ("金盘科技","688676",4),("伊戈尔","002922",4),("明阳电气","301291",4),
    ("特锐德","300001",4),("良信股份","002706",4),("中恒电气","002364",4),
    ("科华数据","002335",4),("四方股份","601126",4),("新特电气","301120",4),
    ("江海股份","002484",4),("潍柴动力","000338",4),("科泰电源","300153",4),
    ("动力新科","600841",4),("中国动力","600482",4),("宏发股份","600885",4),
    ("中熔电气","301031",4),("盛弘股份","300693",4),
    # Layer 5: New Energy + Battery Materials (新能源发电与电池材料)
    ("金风科技","002202",5),("龙源电力","001289",5),("运达股份","300772",5),
    ("中国核电","601985",5),("雄韬股份","002733",5),("协鑫能科","002015",5),
    ("海博思创","688411",5),("三峡能源","600905",5),
    # —— 电池材料（原L12）——
    ("湖南裕能","301358",5),("德方纳米","300769",5),("当升科技","300073",5),
    ("璞泰来","603659",5),("尚太科技","001301",5),("天赐材料","002709",5),
    ("恩捷股份","002812",5),("天奈科技","688116",5),
    # Layer 6: Energy Storage + Smart Grid (储能与智能电网)
    ("宁德时代","300750",6),("亿纬锂能","300014",6),("阳光电源","300274",6),
    ("国电南瑞","600406",6),("科陆电子","002121",6),("德业股份","605117",6),
    ("禾迈股份","688032",6),("禾望电气","603063",6),("锦浪科技","300763",6),
    ("蔚蓝锂芯","002245",6),
    # —— 智能电网（原L14）——
    ("许继电气","000400",6),("特变电工","600089",6),("中国西电","601179",6),
    ("平高电气","600312",6),("威胜信息","688100",6),("万胜智能","300882",6),
    ("南网科技","688248",6),
    # Layer 7: High-End Equipment + Robotics (高端装备与机器人)
    ("先导智能","300450",7),("捷佳伟创","300724",7),("迈为股份","300751",7),
    ("奥特维","688516",7),("汇川技术","300124",7),("信捷电气","603416",7),
    ("雷赛智能","002979",7),("埃斯顿","002747",7),("大族激光","002008",7),
    ("锐科激光","300747",7),("帝尔激光","300776",7),("联赢激光","688518",7),
    ("科德数控","688305",7),("海天精工","601882",7),("纽威数控","688697",7),
    ("华中数控","300161",7),("纽威股份","603699",7),("江苏神通","002438",7),
    ("伟创电气","688698",7),
    # —— 机器人（原L8）——
    ("绿的谐波","688017",7),("双环传动","002472",7),("兆威机电","003021",7),
    ("拓普集团","601689",7),("鸣志电器","603728",7),("汉威科技","300007",7),
    ("长盈精密","300115",7),
    # Layer 8: Electronic Components (电子零部件)
    ("风华高科","000636",8),("三环集团","300408",8),("顺络电子","002138",8),
    ("法拉电子","600563",8),("东晶电子","002199",8),("博迁新材","605376",8),
    ("立讯精密","002475",8),("瑞可达","688800",8),("兆龙互连","300913",8),
    ("沃尔核材","002130",8),("国瓷材料","300285",8),
    # Layer 9: Base Metals, Minor Metals, Chemicals (基本金属与小金属)
    # —— 石化与基础金属（原L10）——
    ("杰瑞股份","002353",9),("应流股份","603308",9),("中国神华","601088",9),
    ("广汇能源","600256",9),("中国石化","600028",9),("粤桂股份","000833",9),
    ("紫金矿业","601899",9),("洛阳钼业","603993",9),("铜陵有色","000630",9),
    ("西部矿业","601168",9),("中国铝业","601600",9),("云铝股份","000807",9),
    # —— 小金属与贵金属（原L11）——
    ("中钨高新","000657",9),("厦门钨业","600549",9),("章源钨业","002378",9),
    ("锡业股份","000960",9),("华锡有色","600301",9),("湖南黄金","002155",9),
    ("华钰矿业","601020",9),("云南锗业","002428",9),("北方稀土","600111",9),
    ("金力永磁","300748",9),("盛达资源","000603",9),
    # —— 大宗化工（原L12化工部分，电池材料已移至L05）——
    ("万华化学","600309",9),("华鲁恒升","600426",9),("巨化股份","600160",9),
    ("三美股份","603379",9),("金石资源","603505",9),("荣盛石化","002493",9),
    ("东岳硅材","300821",9),("龙佰集团","002601",9),("新安股份","600596",9),
    ("黑猫股份","002068",9),
    # —— 化肥农药（原L13）——
    ("云天化","600096",9),("兴发集团","600141",9),("新洋丰","000902",9),
    ("亚钾国际","000893",9),("藏格矿业","000408",9),("湖北宜化","000422",9),
    ("扬农化工","600486",9),("润丰股份","301035",9),("国光股份","002749",9),
    ("江山股份","600389",9),
]

# Build code→(name,layer) lookup
CODE_INFO = {s[1]: (s[0], s[2]) for s in STOCKS}

# A-class core watch
A_LIST = {
    "300308","601138","300476","002463","002371","688012","688072",
    "688981","000338","688676","600885","300750","600406","300124",
    "002008","688305","000636","002475","688271","601899",
}

# ============= REPORT GENERATION =============

def compute_layer_stats(data):
    """Compute per-layer statistics."""
    layers = {}
    for code, (name, layer) in CODE_INFO.items():
        if code not in data: continue
        d = data[code]
        if layer not in layers:
            layers[layer] = {"count":0, "up":0, "down":0, "chg_sum":0.0}
        s = layers[layer]
        s["count"] += 1
        s["chg_sum"] += d["change_pct"]
        if d["change_pct"] > 0: s["up"] += 1
        else: s["down"] += 1
    return layers

def fmt_turnover(t):
    """Format turnover amount."""
    if t >= 10000: return f"{t/10000:.2f}亿"
    if t >= 1: return f"{t:.0f}万"
    return f"{t*10000:.0f}"

def generate_stock_log(date_str, data):
    """Generate stock closing log in markdown."""
    lines = []
    lines.append("---")
    lines.append(f"date: {date_str}")
    lines.append("tags:")
    lines.append("  - 交易日志/股票")
    lines.append("---")
    lines.append("")
    lines.append(f"# 📊 股票交易日志 - {date_str}")
    lines.append("")
    lines.append("> 🕐 上班不能盯盘？→ 盘前挂好单，午休看一眼，收盘再复盘。")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- 盘前计划 ----
    lines.append("## 📋 盘前计划")
    lines.append("")
    lines.append("### 今日挂单")
    lines.append("| 标的 | 方向 | 价格 | 数量 | 逻辑 |")
    lines.append("|------|------|------|------|------|")
    lines.append("| | | | | |")
    lines.append("")
    lines.append("### 止损单检查")
    lines.append("| 标的 | 止损价 | 已设置 |")
    lines.append("|------|--------|--------|")
    lines.append("| | | ❌ |")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- 大盘指数 (auto-filled) ----
    lines.append("## 📊 大盘指数")
    lines.append("")
    lines.append("| 指数 | 收盘价 | 涨跌幅 |")
    lines.append("|------|--------|--------|")
    for code, name in [("399317","国证A股"),("399006","创业板指"),("000688","科创50"),
                       ("000001","上证指数"),("399001","深证成指")]:
        if code in data:
            d = data[code]
            lines.append(f"| {name} | {d['current']:.2f} | **{d['change_pct']:+.2f}%** |")
        else:
            lines.append(f"| {name} | — | — |")
    lines.append("")
    lines.append("**盘面感觉**：")
    lines.append("- 情绪（冷清/正常/活跃/亢奋/恐慌）：")
    lines.append("- 涨跌家数比：")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- 板块表现 (auto-filled) ----
    lines.append("## 🎯 板块表现（9层产业链）")
    lines.append("")
    layers = compute_layer_stats(data)
    sorted_layers = sorted(layers.items(), key=lambda x: x[1]["chg_sum"]/max(x[1]["count"],1), reverse=True)
    lines.append("| 层级 | 名称 | 平均涨跌 | 涨/跌 | 活跃度 |")
    lines.append("|------|------|---------|-------|--------|")
    for lid, s in sorted_layers:
        avg = s["chg_sum"] / max(s["count"], 1)
        icon = "🟢" if avg > 1 else ("🟡" if avg > 0 else "🔴")
        name = LAYER_NAMES.get(lid, f"L{lid}")
        lines.append(f"| L{lid} | {name} | **{avg:+.2f}%** | {s['up']}↑/{s['down']}↓ | {icon} |")
    lines.append("")

    # 手动补充：涨幅/跌幅居前板块
    lines.append("**涨幅居前板块**：")
    lines.append("| 板块 | 驱动因素 | 持续性 |")
    lines.append("|------|---------|--------|")
    lines.append("| | | |")
    lines.append("")
    lines.append("**跌幅居前板块**：")
    lines.append("| 板块 | 原因 | 是否影响持仓 |")
    lines.append("|------|------|------------|")
    lines.append("| | | |")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- A类异动 (auto-filled) ----
    lines.append("## 🟢 A类核心异动")
    lines.append("")
    a_data = []
    for code in A_LIST:
        if code not in data: continue
        d = data[code]
        chg = d["change_pct"]
        sig = "—"
        if chg > 9: sig = "🚀涨停"
        elif chg > 5: sig = "📈大涨"
        elif chg > 3: sig = "显著上涨"
        elif chg < -9: sig = "💥跌停"
        elif chg < -5: sig = "📉大跌"
        elif chg < -3: sig = "显著下跌"
        layer = CODE_INFO.get(code, ("",0))[1]
        a_data.append((abs(chg), d["name"], chg, fmt_turnover(d["turnover"]), layer, sig))

    a_data.sort(key=lambda x: x[0], reverse=True)
    lines.append("| 标的 | 涨跌幅 | 成交额 | 层位 | 信号 |")
    lines.append("|------|--------|--------|------|------|")
    for _, name, chg, tstr, layer, sig in a_data:
        cstr = f"**{chg:+.2f}%**" if abs(chg) > 2 else f"{chg:+.2f}%"
        lines.append(f"| {name} | {cstr} | {tstr} | L{layer} | {sig} |")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- 涨跌 TOP (auto-filled) ----
    all_sorted = []
    for code, (name, layer) in CODE_INFO.items():
        if code not in data: continue
        all_sorted.append((data[code]["change_pct"], name, code, layer))
    all_sorted.sort(key=lambda x: x[0], reverse=True)

    lines.append("## 📈 涨幅 TOP10")
    lines.append("")
    lines.append("| # | 标的 | 涨跌幅 | 层级 |")
    lines.append("|---|------|--------|------|")
    for i, (chg, name, code, layer) in enumerate(all_sorted[:10]):
        lines.append(f"| {i+1} | {name} | **{chg:+.2f}%** | L{layer} |")
    lines.append("")
    lines.append("## 📉 跌幅 TOP10")
    lines.append("")
    lines.append("| # | 标的 | 涨跌幅 | 层级 |")
    lines.append("|---|------|--------|------|")
    bottom = list(reversed(all_sorted[-10:]))
    for i, (chg, name, code, layer) in enumerate(bottom):
        lines.append(f"| {i+1} | {name} | **{chg:+.2f}%** | L{layer} |")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- 交易记录 (manual) ----
    lines.append("## 💰 今日交易记录")
    lines.append("")
    lines.append("### 🟢 买入")
    lines.append("| 标的 | 代码 | 数量 | 买入理由 | 是否符合计划 |")
    lines.append("|------|------|------|---------|------------|")
    lines.append("| | | | | ✅ / ❌ |")
    lines.append("")
    lines.append("### 🔴 卖出")
    lines.append("| 标的 | 代码 | 数量 | 卖出理由 | 盈亏 |")
    lines.append("|------|------|------|---------|------|")
    lines.append("| | | | | ±% |")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- 持仓跟踪 (manual) ----
    lines.append("## 📦 持仓跟踪")
    lines.append("")
    lines.append("| 标的 | 代码 | 成本 | 现价 | 盈亏% | 仓位 | 操作计划 |")
    lines.append("|------|------|------|------|-------|------|---------|")
    lines.append("| | | | | | % | 持有/加仓/减仓/止损 |")
    lines.append("")
    lines.append("**总仓位**：__%　　　**持仓数**：__支")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---- 复盘 (manual) ----
    lines.append("## 🧠 收盘复盘")
    lines.append("")
    lines.append("### 今日做得好的")
    lines.append("- ")
    lines.append("")
    lines.append("### 今日做得不好的")
    lines.append("- [ ] ")
    lines.append("")
    lines.append("### 理性检查")
    lines.append("- [ ] 所有操作都有明确理由，没有情绪化交易")
    lines.append("- [ ] 所有持仓的买入逻辑今天仍然成立")
    lines.append("- [ ] 没有违反仓位/止损纪律")
    lines.append("- [ ] 有没有掉进某个认知偏差？")
    lines.append("")
    lines.append("### 明日关注")
    lines.append("- [ ] ")
    lines.append("- [ ] ")
    lines.append("")
    lines.append("### 自由记录")
    lines.append("> ")
    lines.append("")
    lines.append("---")
    lines.append("")
    scanned = len([c for c in data if c in CODE_INFO])
    lines.append(f"*📡 数据: 腾讯财经 · 自动生成 · 共扫描 {scanned} 支*")
    lines.append("")

    return "\n".join(lines)


def generate_futures_log(date_str):
    """Generate futures closing log template (manual fill)."""
    lines = []
    lines.append("---")
    lines.append(f"date: {date_str}")
    lines.append("tags:")
    lines.append("  - 交易日志/期货")
    lines.append("---")
    lines.append("")
    lines.append(f"# 📊 期货交易日志 - {date_str}")
    lines.append("")
    lines.append("---")
    lines.append("")

    lines.append("## 一、市场总览")
    lines.append("")
    lines.append("| 指数 | 收盘 | 涨跌幅 | 趋势 |")
    lines.append("|------|------|--------|------|")
    for name in ["文华商品","工业品","农产品"]:
        lines.append(f"| {name} | | | ⬆/⬇/➡ |")
    lines.append("")

    lines.append("## 二、板块跟踪")
    lines.append("")
    for sector, contracts in [
        ("贵金属", ["黄金 AU","白银 AG"]),
        ("有色金属", ["铜 CU","铝 AL","锌 ZN"]),
        ("能源", ["原油 SC","燃料油 FU"]),
        ("化工", ["甲醇 MA","PTA","纯碱 SA","PVC V"]),
        ("黑色系", ["螺纹 RB","铁矿 I","焦煤 JM","焦炭 J"]),
        ("农产品", ["豆粕 M","豆油 Y","棕榈油 P","白糖 SR"]),
        ("新能源", ["碳酸锂 LC","工业硅 SI"]),
    ]:
        lines.append(f"### {sector}")
        lines.append("| 合约 | 收盘 | 涨跌幅 | 持仓变化 | 周线趋势 | 信号 |")
        lines.append("|------|------|--------|---------|---------|------|")
        for c in contracts:
            lines.append(f"| {c} | | | ± | ⬆/⬇/➡ | — |")
        lines.append("")

    lines.append("## 三、今日交易")
    lines.append("")
    lines.append("### 开仓")
    lines.append("| 合约 | 方向 | 手数 | 价格 | 理由 | 止损 |")
    lines.append("|------|------|------|------|------|------|")
    lines.append("| | 多/空 | | | | |")
    lines.append("")
    lines.append("### 平仓")
    lines.append("| 合约 | 方向 | 盈亏 | 平仓理由 |")
    lines.append("|------|------|------|---------|")
    lines.append("| | | ±R | |")
    lines.append("")

    lines.append("## 四、持仓")
    lines.append("")
    lines.append("| 合约 | 方向 | 手数 | 开仓价 | 现价 | 浮盈 | 止损 | 判断 |")
    lines.append("|------|------|------|--------|------|------|------|------|")
    lines.append("| | 多/空 | | | | ±R | | 持有/止损 |")
    lines.append("")
    lines.append(f"**总浮盈**：±R　　　**保证金占用**：__%")
    lines.append("")

    lines.append("## 五、复盘")
    lines.append("")
    lines.append("### 盘面判断")
    lines.append("- 文华方向：")
    lines.append("- 市场风格（趋势/震荡/无序）：")
    lines.append("- 最强/最弱板块：")
    lines.append("")
    lines.append("### 纪律检查")
    lines.append("- [ ] 所有开仓顺周线方向")
    lines.append("- [ ] 所有持仓已设止损")
    lines.append("- [ ] 亏损持仓已清仓")
    lines.append("- [ ] 单日亏损未超3%")
    lines.append("")
    lines.append("### 明日计划")
    lines.append("- [ ] ")
    lines.append("")
    lines.append("### 自由记录")
    lines.append("> ")
    lines.append("")

    return "\n".join(lines)


# ============= MAIN =============

def main():
    # Parse date
    if len(sys.argv) > 1:
        date_str = sys.argv[1]
        try:
            datetime.strptime(date_str, "%Y-%m-%d")
        except ValueError:
            print(f"ERROR: Invalid date format. Use YYYY-MM-DD", file=sys.stderr)
            sys.exit(1)
    else:
        date_str = datetime.now().strftime("%Y-%m-%d")

    print(f"📊 生成 {date_str} 收盘日志...", file=sys.stderr)

    # Fetch data
    all_codes = list(set([s[1] for s in STOCKS]))
    index_codes = ["399317","399006","000688","000001","399001"]
    all_codes = list(set(all_codes + index_codes))

    print(f"  获取 {len(all_codes)} 支标的数据...", file=sys.stderr)
    data = fetch_stock_data(all_codes)
    print(f"  获取到 {len(data)} 支", file=sys.stderr)

    # Generate stock log
    stock_content = generate_stock_log(date_str, data)
    stock_path = os.path.join(STOCK_DIR, f"{date_str}.md")
    with open(stock_path, "w", encoding="utf-8") as f:
        f.write(stock_content)
    print(f"  ✅ 股票日志: {stock_path}", file=sys.stderr)

    # Generate futures log
    futures_content = generate_futures_log(date_str)
    futures_path = os.path.join(FUT_DIR, f"{date_str}.md")
    # Don't overwrite existing futures log (user may have filled it)
    if not os.path.exists(futures_path):
        with open(futures_path, "w", encoding="utf-8") as f:
            f.write(futures_content)
        print(f"  ✅ 期货日志: {futures_path}", file=sys.stderr)
    else:
        print(f"  ⏭️  期货日志已存在，跳过: {futures_path}", file=sys.stderr)

    # Summary
    scanned = len([c for c in data if c in CODE_INFO])
    print(f"\n📡 扫描 {scanned} 支自选股 · {date_str}", file=sys.stderr)

    # Quick index summary
    for code, name in [("399317","国证A股"),("399006","创业板"),("000688","科创50")]:
        if code in data:
            d = data[code]
            print(f"  {name}: {d['current']:.1f} ({d['change_pct']:+.2f}%)", file=sys.stderr)

if __name__ == "__main__":
    main()
