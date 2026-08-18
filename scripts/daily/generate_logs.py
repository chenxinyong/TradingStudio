#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Generate stock and futures trading logs from fetched data"""

import json
import os
import pickle
import glob
from datetime import datetime

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"
NOTES = r"c:\Works\ClaudeCode\TradingStudio\docs\trading\Notes"
DATE = "2026-06-08"
PREV_DATE = "2026-06-05"  # Previous trading day (Friday)

# ============ Read fetched data ============
print("正在读取抓取的数据...")

# Read stock batch files
stock_data_dir = os.path.join(VAULT, "scripts", "stock_data")
all_prices = {}
stock_map = {}

# Define stocks and indices (same as before)
stocks_data = [
    ("688256", 1, "寒武纪", 1, "AI芯片", "B"),("688041", 1, "海光信息", 1, "AI芯片", "A"),
    ("688047", 1, "龙芯中科", 1, "AI芯片", "B"),("688008", 1, "澜起科技", 1, "AI芯片", "B"),
    ("300474", 0, "景嘉微", 1, "AI芯片", "C"),("601138", 1, "工业富联", 1, "AI服务器", "A"),
    ("000977", 0, "浪潮信息", 1, "AI服务器", "B"),("000938", 0, "紫光股份", 1, "AI服务器", "B"),
    ("603019", 1, "中科曙光", 1, "AI服务器", "B"),("300308", 0, "中际旭创", 1, "光模块", "A"),
    ("300502", 0, "新易盛", 1, "光模块", "B"),("300394", 0, "天孚通信", 1, "光器件", "B"),
    ("002281", 0, "光迅科技", 1, "光模块", "B"),("000988", 0, "华工科技", 1, "光模块", "B"),
    ("300476", 0, "胜宏科技", 1, "PCB", "A"),("002463", 0, "沪电股份", 1, "PCB", "A"),
    ("002916", 0, "深南电路", 1, "PCB", "B"),("002938", 0, "鹏鼎控股", 1, "PCB", "C"),
    ("688183", 1, "生益电子", 1, "PCB", "B"),("603228", 1, "景旺电子", 1, "PCB", "B"),
    ("002837", 0, "英维克", 1, "液冷", "B"),("300499", 0, "高澜股份", 1, "液冷", "B"),
    ("301018", 0, "申菱环境", 1, "液冷", "C"),("002126", 0, "银轮股份", 1, "液冷", "B"),
    ("688498", 1, "源杰科技", 1, "光芯片", "B"),("601869", 1, "长飞光纤", 1, "光纤光缆", "B"),
    ("600487", 1, "亨通光电", 1, "光纤光缆", "B"),("600522", 1, "中天科技", 1, "光纤光缆", "B"),
    ("002851", 0, "麦格米特", 1, "服务器电源", "C"),("002518", 0, "科士达", 1, "电源", "C"),
    ("300857", 0, "协创数据", 1, "算力租赁", "C"),("002384", 0, "东山精密", 1, "PCB", "C"),
    ("300990", 0, "同飞股份", 1, "液冷", "C"),("603893", 1, "瑞芯微", 1, "端侧AI芯片", "A"),
    ("603296", 1, "华勤技术", 1, "消费电子ODM", "B"),
    ("688981", 1, "中芯国际", 2, "晶圆代工", "A"),("688347", 1, "华虹公司", 2, "晶圆代工", "B"),
    ("688396", 1, "华润微", 2, "晶圆代工", "C"),("002371", 0, "北方华创", 2, "半导体设备", "A"),
    ("688012", 1, "中微公司", 2, "半导体设备", "A"),("688072", 1, "拓荆科技", 2, "半导体设备", "A"),
    ("688082", 1, "盛美上海", 2, "半导体设备", "B"),("688120", 1, "华海清科", 2, "半导体设备", "B"),
    ("688019", 1, "安集科技", 2, "半导体材料", "B"),("002409", 0, "雅克科技", 2, "半导体材料", "C"),
    ("300655", 0, "晶瑞电材", 2, "半导体材料", "C"),("600584", 1, "长电科技", 2, "封测", "A"),
    ("002156", 0, "通富微电", 2, "封测", "B"),("002185", 0, "华天科技", 2, "封测", "B"),
    ("300604", 0, "长川科技", 2, "封测", "B"),("688234", 1, "天岳先进", 2, "碳化硅", "C"),
    ("600703", 1, "三安光电", 2, "碳化硅", "C"),("603290", 1, "斯达半导", 2, "功率半导体", "B"),
    ("605358", 1, "立昂微", 2, "硅片+功率", "B"),("688126", 1, "沪硅产业", 2, "硅片", "C"),
    ("603501", 1, "韦尔股份", 2, "CIS传感器", "A"),("300458", 0, "全志科技", 2, "端侧AI SoC", "B"),
    ("688608", 1, "恒玄科技", 2, "AIoT SoC", "B"),("603986", 1, "兆易创新", 2, "存储芯片", "A"),
    ("300782", 0, "卓胜微", 2, "射频前端", "A"),
    ("002230", 0, "科大讯飞", 3, "AI应用", "C"),("688111", 1, "金山办公", 3, "AI应用", "B"),
    ("600588", 1, "用友网络", 3, "AI应用", "C"),("300170", 0, "汉得信息", 3, "AI应用", "C"),
    ("300058", 0, "蓝色光标", 3, "AI应用", "C"),("600570", 1, "恒生电子", 3, "AI应用", "B"),
    ("300496", 0, "中科创达", 3, "AI OS+Agent", "B"),("688561", 1, "奇安信", 3, "AI安全", "A"),
    ("688023", 1, "安恒信息", 3, "AI安全", "B"),
    ("688676", 1, "金盘科技", 4, "变压器", "A"),("002922", 0, "伊戈尔", 4, "变压器", "B"),
    ("301291", 0, "明阳电气", 4, "变压器", "C"),("300001", 0, "特锐德", 4, "配电", "C"),
    ("002706", 0, "良信股份", 4, "配电", "C"),("002364", 0, "中恒电气", 4, "HVDC", "B"),
    ("002335", 0, "科华数据", 4, "UPS", "B"),("601126", 1, "四方股份", 4, "电力设备", "B"),
    ("301120", 0, "新特电气", 4, "变压器", "C"),("002484", 0, "江海股份", 4, "超级电容", "C"),
    ("000338", 0, "潍柴动力", 4, "柴油发电机", "A"),("300153", 0, "科泰电源", 4, "发电机组", "C"),
    ("600841", 1, "动力新科", 4, "发电设备", "C"),("600482", 1, "中国动力", 4, "燃气内燃机", "C"),
    ("600885", 1, "宏发股份", 4, "继电器", "A"),("301031", 0, "中熔电气", 4, "熔断器", "C"),
    ("300693", 0, "盛弘股份", 4, "储能变流器", "B"),("600160", 1, "巨化股份", 4, "AI液冷", "B"),
    ("002202", 0, "金风科技", 5, "风电", "C"),("001289", 0, "龙源电力", 5, "风电", "C"),
    ("300772", 0, "运达股份", 5, "风电", "C"),("601985", 1, "中国核电", 5, "核电", "A"),
    ("002733", 0, "雄韬股份", 5, "氢能", "C"),("002015", 0, "协鑫能科", 5, "绿电", "C"),
    ("688411", 1, "海博思创", 5, "储能", "C"),("600905", 1, "三峡能源", 5, "绿电", "B"),
    ("688599", 1, "天合光能", 5, "光伏组件", "C"),("301358", 0, "湖南裕能", 5, "磷酸铁锂", "C"),
    ("300769", 0, "德方纳米", 5, "磷酸铁锂", "C"),("300073", 0, "当升科技", 5, "三元正极", "C"),
    ("603659", 1, "璞泰来", 5, "负极材料", "B"),("001301", 0, "尚太科技", 5, "负极材料", "B"),
    ("002709", 0, "天赐材料", 5, "电解液", "A"),("002812", 0, "恩捷股份", 5, "隔膜", "B"),
    ("688116", 1, "天奈科技", 5, "导电剂", "C"),("688772", 1, "珠海冠宇", 5, "消费电池", "B"),
    ("300850", 0, "新强联", 5, "风电轴承", "B"),
    ("300750", 0, "宁德时代", 6, "储能电池", "A"),("300014", 0, "亿纬锂能", 6, "储能电池", "B"),
    ("300274", 0, "阳光电源", 6, "逆变器", "B"),("600406", 1, "国电南瑞", 6, "电网储能", "A"),
    ("002121", 0, "科陆电子", 6, "储能系统", "C"),("605117", 1, "德业股份", 6, "逆变器", "B"),
    ("688032", 1, "禾迈股份", 6, "逆变器", "C"),("603063", 1, "禾望电气", 6, "储能PCS", "C"),
    ("300763", 0, "锦浪科技", 6, "逆变器", "C"),("002245", 0, "蔚蓝锂芯", 6, "锂电池", "C"),
    ("000400", 0, "许继电气", 6, "特高压", "B"),("600089", 1, "特变电工", 6, "变压器", "B"),
    ("601179", 1, "中国西电", 6, "特高压", "B"),("600312", 1, "平高电气", 6, "特高压", "C"),
    ("688100", 1, "威胜信息", 6, "能源物联网", "B"),("300882", 0, "万胜智能", 6, "智慧计量", "C"),
    ("688248", 1, "南网科技", 6, "数智化", "B"),
    ("300450", 0, "先导智能", 7, "锂电设备", "B"),("300724", 0, "捷佳伟创", 7, "光伏设备", "B"),
    ("300751", 0, "迈为股份", 7, "光伏设备", "C"),("688516", 1, "奥特维", 7, "光伏设备", "C"),
    ("300124", 0, "汇川技术", 7, "工业自动化", "A"),("603416", 1, "信捷电气", 7, "PLC", "C"),
    ("002979", 0, "雷赛智能", 7, "运动控制", "C"),("002747", 0, "埃斯顿", 7, "工业机器人", "B"),
    ("002008", 0, "大族激光", 7, "激光设备", "A"),("300747", 0, "锐科激光", 7, "激光器", "C"),
    ("300776", 0, "帝尔激光", 7, "激光设备", "C"),("688518", 1, "联赢激光", 7, "激光焊接", "C"),
    ("688305", 1, "科德数控", 7, "数控机床", "A"),("601882", 1, "海天精工", 7, "数控机床", "B"),
    ("688697", 1, "纽威数控", 7, "数控机床", "B"),("300161", 0, "华中数控", 7, "数控系统", "C"),
    ("603699", 1, "纽威股份", 7, "工业阀门", "B"),("002438", 0, "江苏神通", 7, "特种阀门", "C"),
    ("688698", 1, "伟创电气", 7, "变频器", "C"),("688017", 1, "绿的谐波", 7, "减速器", "B"),
    ("002472", 0, "双环传动", 7, "减速器", "B"),("003021", 0, "兆威机电", 7, "执行器", "C"),
    ("601689", 1, "拓普集团", 7, "执行器", "B"),("603728", 1, "鸣志电器", 7, "电机", "C"),
    ("300007", 0, "汉威科技", 7, "传感器", "C"),("300115", 0, "长盈精密", 7, "结构件", "C"),
    ("000636", 0, "风华高科", 8, "MLCC", "A"),("300408", 0, "三环集团", 8, "MLCC", "B"),
    ("002138", 0, "顺络电子", 8, "电感", "B"),("600563", 1, "法拉电子", 8, "薄膜电容", "B"),
    ("002199", 0, "东晶电子", 8, "石英晶振", "C"),("605376", 1, "博迁新材", 8, "MLCC材料", "B"),
    ("002475", 0, "立讯精密", 8, "连接器", "A"),("688800", 1, "瑞可达", 8, "连接器", "C"),
    ("300913", 0, "兆龙互连", 8, "铜缆连接", "C"),("002130", 0, "沃尔核材", 8, "铜缆连接", "B"),
    ("300285", 0, "国瓷材料", 8, "MLCC材料", "B"),("603890", 1, "春秋电子", 8, "PC结构件", "C"),
    ("002241", 0, "歌尔股份", 8, "声学/传感器", "B"),
    ("601899", 1, "紫金矿业", 9, "铜", "A"),("601600", 1, "中国铝业", 9, "铝", "B"),
    ("603993", 1, "洛阳钼业", 9, "铜钴", "B"),("000657", 0, "中钨高新", 9, "钨", "B"),
    ("600549", 1, "厦门钨业", 9, "钨", "B"),("002378", 0, "章源钨业", 9, "钨", "C"),
    ("000960", 0, "锡业股份", 9, "锡", "B"),("600301", 1, "华锡有色", 9, "锡锑", "C"),
    ("002155", 0, "湖南黄金", 9, "锑黄金", "B"),("601020", 1, "华钰矿业", 9, "锑", "C"),
    ("002428", 0, "云南锗业", 9, "锗", "B"),("600111", 1, "北方稀土", 9, "稀土", "B"),
    ("300748", 0, "金力永磁", 9, "稀土永磁", "B"),("000603", 0, "盛达资源", 9, "白银", "B"),
]

indices = [
    ("000001", 1, "上证指数"),("399001", 0, "深证成指"),("399006", 0, "创业板指"),
    ("000688", 1, "科创50"),("399317", 0, "国证A股"),("000016", 1, "上证50"),
    ("000300", 1, "沪深300"),("000905", 1, "中证500"),("000852", 1, "中证1000"),
]

# Build stock map
for stock in stocks_data:
    code, market, name, layer, sub, level = stock
    secid = f"{market}.{code}"
    stock_map[secid] = {"name": name, "layer": layer, "sub": sub, "level": level, "code": code}

for code, market, name in indices:
    secid = f"{market}.{code}"
    stock_map[secid] = {"name": name, "layer": 0, "sub": "指数", "level": "index", "code": code}

# Read all batch files
batch_files = sorted(glob.glob(os.path.join(stock_data_dir, "*.json")))
for bf in batch_files:
    with open(bf, 'r', encoding='utf-8') as f:
        data = json.load(f)
    if data and data.get("data") and data["data"].get("diff"):
        for item in data["data"]["diff"]:
            code = str(item.get("f12", ""))
            if code:
                # Find the matching secid
                for secid in stock_map:
                    if secid.endswith(code):
                        all_prices[secid] = {
                            "price": item.get("f2"),
                            "change_pct": item.get("f3"),
                            "change_amt": item.get("f4"),
                            "name": item.get("f14", ""),
                        }
                        break

print(f"读取到 {len(all_prices)} 只股票/指数数据")

# Print indices
print("\n大盘指数:")
for code, market, name in indices:
    secid = f"{market}.{code}"
    if secid in all_prices:
        d = all_prices[secid]
        print(f"  {name}: {d.get('price','?')} ({d.get('change_pct',0):+.2f}%)")

# ============ Read futures data from pickle (saved earlier) ============
with open(os.path.join(VAULT, "scripts", "raw_data.pkl"), "rb") as f:
    saved_data = pickle.load(f)
futures_data = saved_data["futures"]

print(f"\n读取到 {len(futures_data)} 个期货合约数据")

# ============ Generate stock log ============
print("\n正在生成股票日志...")

stock_log = f"""---
date: {DATE}
tags:
  - 交易日志/股票
---

# 📊 股票交易日志 - {DATE}

> 🕐 上班不能盯盘？→ 盘前挂好单，午休看一眼，收盘再复盘。
> 策略参考：[[04-Research/04-交易系统/非盯盘交易策略]]

---

## 盘前计划（出门前填好）

### 📋 今日挂单

| 标的 | 方向 | 价格 | 数量 | 逻辑 |
|------|------|------|------|------|
| | 买入/卖出 | | | |
| | 买入/卖出 | | | |

### ⚠️ 止损单检查

| 标的 | 止损价 | 是否已设置 | 有效期 |
|------|--------|-----------|--------|
| | | ✅ / ❌ | |

### 📌 今天最重要的3件事

1.
2.
3.

---

## 📡 自选股扫描（5分钟速览）

> 扫描方法：[[04-Research/04-交易系统/自选股分级扫描系统]]

"""

# Calculate layer performance
layer_perf = {}
for secid, info in stock_map.items():
    if info["level"] == "index":
        continue
    layer = info["layer"]
    if layer not in layer_perf:
        layer_perf[layer] = {"names": [], "changes": [], "up": 0, "down": 0, "total": 0}
    if secid in all_prices and all_prices[secid].get("change_pct") is not None:
        chg = all_prices[secid]["change_pct"]
        layer_perf[layer]["names"].append(info["name"])
        layer_perf[layer]["changes"].append(chg)
        layer_perf[layer]["total"] += 1
        if chg > 0:
            layer_perf[layer]["up"] += 1
        elif chg < 0:
            layer_perf[layer]["down"] += 1

# Layer names
layer_names = {
    1: "AI算力基座", 2: "半导体产业链", 3: "大模型与AI应用",
    4: "AI电力设备", 5: "新能源发电", 6: "储能与调节",
    7: "高端装备制造", 8: "电子元器件", 9: "石化与基础金属"
}

# Find active layers
layer_avg = {}
for l, perf in layer_perf.items():
    if perf["changes"]:
        avg = sum(perf["changes"]) / len(perf["changes"])
        layer_avg[l] = avg

# Sort by average change
sorted_layers = sorted(layer_avg.items(), key=lambda x: x[1], reverse=True)

# Mark active layers
for l, avg in sorted_layers[:3]:
    lname = layer_names.get(l, f"第{l}层")
    print(f"  活跃层级: {lname} (均涨跌 {avg:+.2f}%)")

# Active layers section
if sorted_layers:
    top_l = sorted_layers[0]
    mid_l = sorted_layers[len(sorted_layers)//2] if len(sorted_layers) > 2 else sorted_layers[-1]
    bot_l = sorted_layers[-1]

    top_emoji = "🟢" if top_l[1] > 0 else "🔴"
    mid_emoji = "🟡"
    bot_emoji = "🔴" if bot_l[1] < 0 else "🟢"

    stock_log += f"### 今日活跃层级\n"
    stock_log += f"- {top_emoji} 第{top_l[0]}层（{layer_names.get(top_l[0], '')}）：均涨跌 **{top_l[1]:+.2f}%**\n"
    stock_log += f"- {mid_emoji} 第{mid_l[0]}层（{layer_names.get(mid_l[0], '')}）：均涨跌 **{mid_l[1]:+.2f}%**\n"
    stock_log += f"- {bot_emoji} 第{bot_l[0]}层（{layer_names.get(bot_l[0], '')}）：均涨跌 **{bot_l[1]:+.2f}%**\n\n"

# Find abnormal stocks (change > 5%)
abnormal_stocks = []
for secid, info in stock_map.items():
    if info["level"] == "index":
        continue
    if secid in all_prices and all_prices[secid].get("change_pct") is not None:
        chg = all_prices[secid]["change_pct"]
        if abs(chg) >= 5:
            abnormal_stocks.append((secid, info, chg))

abnormal_stocks.sort(key=lambda x: abs(x[2]), reverse=True)

stock_log += "### A类个股异动\n"
stock_log += "| 标的 | 涨跌幅 | 信号 | 操作 |\n"
stock_log += "|------|--------|------|------|\n"
if abnormal_stocks:
    for secid, info, chg in abnormal_stocks[:10]:
        signal = "🔴 大涨" if chg > 0 else "🔴 大跌"
        stock_log += f"| **{info['name']}** | **{chg:+.2f}%** | {signal} | 观察 |\n"
else:
    stock_log += "| 今日无显著异动（涨跌幅<5%） | | | |\n"

stock_log += "\n### B/C类值得注意的\n"
stock_log += "| 标的 | 所属层 | 异动原因 | 是否升级A类 |\n"
stock_log += "|------|--------|---------|------------|\n"
# Show top 5 with 3-5% change
near_abnormal = []
for secid, info in stock_map.items():
    if info["level"] == "index":
        continue
    if secid in all_prices and all_prices[secid].get("change_pct") is not None:
        chg = all_prices[secid]["change_pct"]
        if 3 <= abs(chg) < 5:
            near_abnormal.append((secid, info, chg))
near_abnormal.sort(key=lambda x: abs(x[2]), reverse=True)
for secid, info, chg in near_abnormal[:5]:
    stock_log += f"| {info['name']} | 第{info['layer']}层 | 涨跌幅 **{chg:+.2f}%** | ❌ |\n"
if not near_abnormal:
    stock_log += "| 无接近异动阈值的个股 | | | |\n"

# Index data
index_data = {}
for code, market, name in indices:
    secid = f"{market}.{code}"
    if secid in all_prices:
        index_data[name] = all_prices[secid]

stock_log += f"""
---

## 一、大盘指数

| 指数 | 收盘价 | 涨跌幅 | 成交额 | 备注 |
|------|--------|--------|-------|------|
"""

# Market indices table
index_order = ["国证A股", "创业板指", "科创50", "上证指数", "深证成指", "上证50", "沪深300", "中证500", "中证1000"]
for iname in index_order:
    if iname in index_data:
        d = index_data[iname]
        price = d.get("price", "—")
        chg = d.get("change_pct", 0)
        if chg is not None:
            chg_str = f"**{chg:+.2f}%**"
        else:
            chg_str = "—"
        stock_log += f"| {iname} | {price} | {chg_str} | 数据获取失败 | |\n"
    else:
        stock_log += f"| {iname} | 数据获取失败 | — | — | |\n"

# Calculate advance/decline ratio
up_count = sum(1 for s in all_prices.values() if s.get("change_pct") is not None and s["change_pct"] > 0)
down_count = sum(1 for s in all_prices.values() if s.get("change_pct") is not None and s["change_pct"] < 0)
flat_count = sum(1 for s in all_prices.values() if s.get("change_pct") is not None and s["change_pct"] == 0)
total_tracked = up_count + down_count + flat_count

# Market sentiment
if up_count > down_count * 1.5:
    sentiment = "活跃"
elif up_count > down_count:
    sentiment = "正常（偏多）"
elif down_count > up_count * 1.5:
    sentiment = "冷清（普跌）"
elif down_count > up_count:
    sentiment = "正常（偏空）"
else:
    sentiment = "正常"

stock_log += f"""
**盘面感觉**：
- 情绪（冷清/正常/活跃/亢奋/恐慌）：**{sentiment}**
- 涨跌家数比（自选股范围）：**{up_count}涨 / {down_count}跌 / {flat_count}平**
- 北向资金：数据获取失败
"""

# Layer summary table
stock_log += f"""
---

## 二、热点板块

### 14层产业链各层级表现
| 层级 | 名称 | 平均涨跌幅 | 上涨/总家数 | 活跃度 |
|------|------|-----------|-----------|-------|
"""
for l, avg in sorted_layers:
    lname = layer_names.get(l, f"第{l}层")
    perf = layer_perf[l]
    act = "🟢" if avg > 0 else "🔴"
    stock_log += f"| 第{l}层 | {lname} | **{avg:+.2f}%** | {perf['up']}/{perf['total']} | {act} |\n"

stock_log += f"""
### 涨幅居前个股（自选股TOP10）
| 个股 | 所属层 | 涨跌幅 | 关注 |
|------|--------|--------|------|
"""
# Find top gainers and losers
all_with_chg = [(secid, info, all_prices[secid]["change_pct"])
                for secid, info in stock_map.items()
                if info["level"] != "index" and secid in all_prices and all_prices[secid].get("change_pct") is not None]
all_with_chg.sort(key=lambda x: x[2], reverse=True)

for secid, info, chg in all_with_chg[:10]:
    attention = "🔴" if abs(chg) >= 5 else ("🟡" if abs(chg) >= 3 else "🟢")
    stock_log += f"| {info['name']} | 第{info['layer']}层 | **{chg:+.2f}%** | {attention} |\n"

stock_log += f"""
### 跌幅居前个股（自选股TOP10）
| 个股 | 所属层 | 涨跌幅 | 是否影响持仓 |
|------|--------|--------|------------|
"""
for secid, info, chg in all_with_chg[-10:]:
    stock_log += f"| {info['name']} | 第{info['layer']}层 | **{chg:+.2f}%** | 观察 |\n"

stock_log += f"""
> 💡 链接到研究框架：哪些层级今天在动？
"""
for l, avg in sorted_layers[:3]:
    lname = layer_names.get(l, f"第{l}层")
    stock_log += f"> - 第{l}层（{lname}）：均涨跌 **{avg:+.2f}%**\n"

stock_log += f"""
---

## 三、异动个股

| 个股 | 代码 | 异动方向 | 涨跌幅 | 可能原因 | 是否值得关注 |
|------|------|---------|--------|---------|------------|
"""
if abnormal_stocks:
    for secid, info, chg in abnormal_stocks[:8]:
        direction = "↑" if chg > 0 else "↓"
        attention = "🔴"
        stock_log += f"| **{info['name']}** | {info['code']} | {direction} | **{chg:+.2f}%** | 数据获取失败 | {attention} |\n"
else:
    stock_log += "| 今日无涨跌幅>5%的异动个股 | | | | | |\n"

stock_log += f"""
**说明**：🔴 重点关注 / 🟡 观察 / 🟢 忽略

---

## 四、今日交易记录

### 🟢 买入
| 标的 | 代码 | 数量/金额 | 买入理由 | 对应研究框架层级 | 是否符合计划 |
|------|------|----------|---------|---------------|------------|
| | | | | 第__层： | ✅ / ❌ |

### 🔴 卖出
| 标的 | 代码 | 数量/金额 | 卖出理由 | 盈亏 | 反思 |
|------|------|----------|---------|------|------|
| | | | | ±% | |

> 今日无交易

---

## 五、持仓跟踪

| 标的 | 代码 | 成本价 | 现价 | 盈亏% | 仓位占比 | 当前判断 | 操作计划 |
|------|------|-------|------|-------|---------|---------|---------|
| | | | | | % | 持有/加仓/减仓/止损 | |
| | | | | | % | 持有/加仓/减仓/止损 | |
| | | | | | % | 持有/加仓/减仓/止损 | |

**总仓位**：__%
**持仓数量**：__支
**单支最大仓位**：__%（标的：）

---

## 六、收盘复盘

### 今日做得好的
-

### 今日做得不好的 / 违反纪律的地方
- [ ]

### 🧠 理性检查（来自[[04-Research/04-交易系统/交易纪律与理性检查]]）
- [ ] 今天所有操作都有明确理由，没有情绪化交易
- [ ] 所有持仓的买入逻辑今天仍然成立
- [ ] 没有违反仓位/止损纪律
- [ ] 如果今天不是我的持仓，我愿意在当前价格买入吗？
- [ ] 有没有掉进某个认知偏差？（确认偏误/锚定/损失厌恶/近期偏误等）

### 明日关注
- [ ]
- [ ]

### 自由记录（想法/疑虑/观察到的东西）
>

---

## 七、链接

- 研究框架：[[AI产业链深度分析]]
- 期货日志：[[01-Daily/期货/{DATE}]]
- 本周复盘：待创建
"""

# Write stock log
stock_path = os.path.join(NOTES, "股票", f"{DATE}.md")
with open(stock_path, 'w', encoding='utf-8') as f:
    f.write(stock_log)
print(f"✅ 股票日志已写入: {stock_path}")

# ============ Generate futures log ============
print("正在生成期货日志...")

# Futures contract definitions
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

futures_log = f"""---
date: {DATE}
tags:
  - 交易日志/期货
---

# 📊 期货交易日志 - {DATE}

> **夜盘归属说明**：本日志记录 {DATE} 白天日盘 + {PREV_DATE} 夜盘

---

## 一、市场总览（规则一：文华商品定方向）

### 商品指数
| 指数 | 收盘价 | 涨跌幅 | 趋势判断 | 备注 |
|------|--------|--------|---------|------|
| 文华商品 | — | — | ➡（数据获取失败） | 免费API无商品指数数据 |
| 工业品 | — | — | ➡ | 同上 |
| 农产品 | — | — | ➡ | 同上 |

**文华商品方向**：➡ 震荡 → 决定今日交易基调

### 股指期货
| 合约 | 收盘价 | 涨跌幅 | 基差 | 持仓量变化 |
|------|--------|--------|------|-----------|
| IF | — | — | — | — |
| IC | — | — | — | — |
| IM | — | — | — | — |
| IH | — | — | — | — |

**基差说明**：数据获取失败（免费API无股指期货连续数据）

---

## 二、各板块跟踪（规则二：七大类板块）

> 只关注规则三中 Top 30 流动性品种。周线选方向，日线看位置。

"""

def get_futures_row(symbol, futures_data):
    """Generate a table row for a futures contract"""
    if symbol not in futures_data or not futures_data[symbol].get("klines"):
        return f"| {symbol} | 数据获取失败 | — | — | ➡ | — | 无 |"

    fd = futures_data[symbol]
    klines = fd["klines"]
    today = klines[-1]
    close = today.get("c", "—")
    settle = today.get("s", "—")
    chg = fd.get("chg_pct", 0)

    # Open interest change
    if len(klines) >= 2:
        prev_oi = int(klines[-2].get("p", 0))
        curr_oi = int(klines[-1].get("p", 0))
        oi_change = curr_oi - prev_oi
        oi_str = f"{oi_change:+,}"
    else:
        oi_str = "—"

    chg_str = f"**{chg:+.2f}%**"
    if abs(chg) >= 2:
        chg_str = f"**{chg:+.2f}%** 🔴"

    # Trend guess based on recent data
    if len(klines) >= 3:
        c1 = float(klines[-3].get("c", 0))
        c3 = float(klines[-1].get("c", 0))
        if c3 > c1 * 1.03:
            trend = "⬆"
        elif c3 < c1 * 0.97:
            trend = "⬇"
        else:
            trend = "➡"
    else:
        trend = "➡"

    return f"| {fd['name']} | {close} | {chg_str} | {oi_str} | {trend} | — | 无 |"

# Group by category
categories = {"贵金属": [], "有色金属": [], "能源": [], "化工": [], "黑色系": [], "农产品": [], "新能源": []}
for symbol, name, cat in futures_contracts:
    categories[cat].append(symbol)

# Category display names
cat_names = {
    "贵金属": "① 贵金属",
    "有色金属": "② 有色金属",
    "能源": "③ 能源",
    "化工": "④ 化工",
    "黑色系": "⑤ 黑色系",
    "农产品": "⑥ 农产品",
    "新能源": "⑦ 新能源"
}

# Headers
table_header = "| 合约 | 收盘价 | 涨跌幅 | 持仓量变化 | 周线趋势 | 日线位置 | 15分钟信号 |\n|------|--------|--------|-----------|---------|---------|-----------|\n"

for cat, syms in categories.items():
    futures_log += f"### {cat_names[cat]}\n"
    futures_log += table_header
    for sym in syms:
        futures_log += get_futures_row(sym, futures_data) + "\n"
    futures_log += "\n"

# Collect abnormal futures
abnormal_futures = []
for symbol, name, cat in futures_contracts:
    if symbol in futures_data:
        fd = futures_data[symbol]
        chg = fd.get("chg_pct", 0)
        if abs(chg) >= 2:
            # Check open interest change
            klines = fd.get("klines", [])
            oi_change = "—"
            if len(klines) >= 2:
                prev_oi = int(klines[-2].get("p", 0))
                curr_oi = int(klines[-1].get("p", 0))
                oi_change = curr_oi - prev_oi
            abnormal_futures.append((symbol, name, cat, chg, oi_change))
abnormal_futures.sort(key=lambda x: abs(x[3]), reverse=True)

futures_log += """---

## 三、异动合约

| 合约 | 方向 | 涨跌幅 | 持仓量异动 | 可能原因 | 是否关注 |
|------|------|--------|-----------|---------|---------|
"""
if abnormal_futures:
    for symbol, name, cat, chg, oi_change in abnormal_futures:
        direction = "↑" if chg > 0 else "↓"
        oi_type = "增仓" if oi_change != "—" and oi_change > 0 else ("减仓" if oi_change != "—" and oi_change < 0 else "—")
        attention = "🔴" if abs(chg) >= 3 else "🟡"
        futures_log += f"| {name} | {direction} | **{chg:+.2f}%** | {oi_type} {oi_change if oi_change != '—' else '—'} | 数据获取失败 | {attention} |\n"
else:
    futures_log += "| 无显著异动合约 | | | | | |\n"

futures_log += """
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
- 最强板块："""

# Find strongest and weakest sectors
sector_avg = {}
for cat in categories:
    changes = []
    for sym in categories[cat]:
        if sym in futures_data and futures_data[sym].get("chg_pct") is not None:
            changes.append(futures_data[sym]["chg_pct"])
    if changes:
        sector_avg[cat] = sum(changes) / len(changes)

if sector_avg:
    strongest = max(sector_avg, key=sector_avg.get)
    weakest = min(sector_avg, key=sector_avg.get)
    futures_log += f"\n  - **{strongest}**（均涨跌 **{sector_avg[strongest]:+.2f}%**）"
    futures_log += f"\n- 最弱板块：**{weakest}**（均涨跌 **{sector_avg[weakest]:+.2f}%**）"
else:
    futures_log += "\n  - 数据不足"
    futures_log += "\n- 最弱板块：数据不足"

futures_log += """

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

- 股票日志：[[01-Daily/股票/{DATE}]]
- 交易系统：[[04-Research/04-交易系统/期货交易系统]]
- 品种研究：待创建
"""

# Write futures log
futures_path = os.path.join(NOTES, "期货", f"{DATE}.md")
with open(futures_path, 'w', encoding='utf-8') as f:
    f.write(futures_log)
print(f"✅ 期货日志已写入: {futures_path}")

print(f"\n{'='*50}")
print("✅✅✅ 全部完成！")
print(f"  📄 股票日志: 01-Daily/股票/{DATE}.md")
print(f"  📄 期货日志: 01-Daily/期货/{DATE}.md")
print(f"{'='*50}")
