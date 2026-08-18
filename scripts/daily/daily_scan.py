#!/usr/bin/env python3
"""
自选股每日扫描脚本
使用方法: python3 daily_scan.py
输出: 格式化扫描报告，可直接粘贴到Obsidian日志中
"""

import urllib.request
import json
import re
import sys
from datetime import datetime

# ============================================================
# 一、自选股列表（按层级组织，从AI产业链深度分析中提取）
# 格式: (股票名称, 代码, 层级, 子类) — 9层产业框架
# ============================================================

STOCKS = [
    # 第一层：AI算力基座（34支）
    ("寒武纪", "688256", 1, "AI芯片"),
    ("海光信息", "688041", 1, "AI芯片"),
    ("龙芯中科", "688047", 1, "AI芯片"),
    ("澜起科技", "688008", 1, "AI芯片"),
    ("景嘉微", "300474", 1, "AI芯片"),
    ("工业富联", "601138", 1, "AI服务器"),
    ("浪潮信息", "000977", 1, "AI服务器"),
    ("紫光股份", "000938", 1, "AI服务器"),
    ("中科曙光", "603019", 1, "AI服务器"),
    ("中际旭创", "300308", 1, "光模块"),
    ("新易盛", "300502", 1, "光模块"),
    ("天孚通信", "300394", 1, "光模块"),
    ("光迅科技", "002281", 1, "光模块"),
    ("华工科技", "000988", 1, "光模块"),
    ("胜宏科技", "300476", 1, "PCB"),
    ("沪电股份", "002463", 1, "PCB"),
    ("深南电路", "002916", 1, "PCB"),
    ("鹏鼎控股", "002938", 1, "PCB"),
    ("生益电子", "688183", 1, "PCB"),
    ("景旺电子", "603228", 1, "PCB"),
    ("英维克", "002837", 1, "液冷"),
    ("高澜股份", "300499", 1, "液冷"),
    ("申菱环境", "301018", 1, "液冷"),
    ("银轮股份", "002126", 1, "液冷"),
    ("源杰科技", "688498", 1, "光芯片"),
    ("长飞光纤", "601869", 1, "光纤光缆"),
    ("亨通光电", "600487", 1, "光纤光缆"),
    ("中天科技", "600522", 1, "光纤光缆"),
    ("麦格米特", "002851", 1, "服务器电源"),
    ("科士达", "002518", 1, "数据中心电源"),
    ("协创数据", "300857", 1, "算力租赁"),
    ("东山精密", "002384", 1, "PCB"),
    ("同飞股份", "300990", 1, "液冷"),

    # 第二层：半导体产业链（17支）
    ("中芯国际", "688981", 2, "晶圆代工"),
    ("华虹公司", "688347", 2, "晶圆代工"),
    ("华润微", "688396", 2, "晶圆代工"),
    ("北方华创", "002371", 2, "半导体设备"),
    ("中微公司", "688012", 2, "半导体设备"),
    ("拓荆科技", "688072", 2, "半导体设备"),
    ("盛美上海", "688082", 2, "半导体设备"),
    ("华海清科", "688120", 2, "半导体设备"),
    ("安集科技", "688019", 2, "半导体材料"),
    ("雅克科技", "002409", 2, "半导体材料"),
    ("晶瑞电材", "300655", 2, "半导体材料"),
    ("长电科技", "600584", 2, "封测"),
    ("通富微电", "002156", 2, "封测"),
    ("华天科技", "002185", 2, "封测"),
    ("长川科技", "300604", 2, "封测"),
    ("天岳先进", "688234", 2, "碳化硅"),
    ("三安光电", "600703", 2, "碳化硅"),

    # 第三层：大模型与AI应用（10支）
    ("科大讯飞", "002230", 3, "AI应用"),
    ("金山办公", "688111", 3, "AI应用"),
    ("用友网络", "600588", 3, "AI应用"),
    ("汉得信息", "300170", 3, "AI应用"),
    ("蓝色光标", "300058", 3, "AI应用"),
    ("恒生电子", "600570", 3, "AI应用"),
    ("联影医疗", "688271", 3, "AI+医疗"),  # 持仓

    # 第四层：AI电力设备（18支）
    ("金盘科技", "688676", 4, "变压器"),
    ("伊戈尔", "002922", 4, "变压器"),
    ("明阳电气", "301291", 4, "变压器"),
    ("特锐德", "300001", 4, "配电"),
    ("良信股份", "002706", 4, "配电"),
    ("中恒电气", "002364", 4, "HVDC"),
    ("科华数据", "002335", 4, "UPS"),
    ("四方股份", "601126", 4, "电力二次设备"),
    ("新特电气", "301120", 4, "变压器"),
    ("江海股份", "002484", 4, "超级电容"),
    ("潍柴动力", "000338", 4, "柴油发电机"),
    ("科泰电源", "300153", 4, "发电机组"),
    ("动力新科", "600841", 4, "发电设备"),
    ("中国动力", "600482", 4, "燃气内燃机"),
    ("宏发股份", "600885", 4, "继电器"),
    ("中熔电气", "301031", 4, "熔断器"),
    ("盛弘股份", "300693", 4, "储能变流器"),

    # 第五层：新能源发电与电池材料（新能源发电部分）
    ("金风科技", "002202", 5, "风电"),
    ("龙源电力", "001289", 5, "风电"),
    ("运达股份", "300772", 5, "风电"),
    ("中国核电", "601985", 5, "核电"),
    ("雄韬股份", "002733", 5, "氢能"),
    ("协鑫能科", "002015", 5, "绿电运营"),
    ("海博思创", "688411", 5, "储能"),
    ("三峡能源", "600905", 5, "绿电运营"),

    # 第六层：储能与智能电网（储能部分）
    ("宁德时代", "300750", 6, "储能电池"),
    ("亿纬锂能", "300014", 6, "储能电池"),
    ("阳光电源", "300274", 6, "逆变器"),
    ("国电南瑞", "600406", 6, "电网储能"),
    ("科陆电子", "002121", 6, "储能系统"),
    ("德业股份", "605117", 6, "逆变器"),
    ("禾迈股份", "688032", 6, "逆变器"),
    ("禾望电气", "603063", 6, "储能PCS"),
    ("锦浪科技", "300763", 6, "逆变器"),
    ("蔚蓝锂芯", "002245", 6, "锂电池"),

    # 第七层：高端装备与机器人（高端装备部分）
    ("先导智能", "300450", 7, "锂电设备"),
    ("捷佳伟创", "300724", 7, "光伏设备"),
    ("迈为股份", "300751", 7, "光伏设备"),
    ("奥特维", "688516", 7, "光伏设备"),
    ("汇川技术", "300124", 7, "工业自动化"),
    ("信捷电气", "603416", 7, "PLC"),
    ("雷赛智能", "002979", 7, "运动控制"),
    ("埃斯顿", "002747", 7, "工业机器人"),
    ("大族激光", "002008", 7, "激光设备"),
    ("锐科激光", "300747", 7, "激光器"),
    ("帝尔激光", "300776", 7, "激光设备"),
    ("联赢激光", "688518", 7, "激光焊接"),
    ("科德数控", "688305", 7, "数控机床"),
    ("海天精工", "601882", 7, "数控机床"),
    ("纽威数控", "688697", 7, "数控机床"),
    ("华中数控", "300161", 7, "数控系统"),
    ("纽威股份", "603699", 7, "工业阀门"),
    ("江苏神通", "002438", 7, "特种阀门"),
    ("伟创电气", "688698", 7, "变频器"),

    # 第七层：高端装备与机器人 — AI机器人部分（原第八层，7支）
    ("绿的谐波", "688017", 7, "减速器"),
    ("双环传动", "002472", 7, "减速器"),
    ("兆威机电", "003021", 7, "执行器"),
    ("拓普集团", "601689", 7, "执行器"),
    ("鸣志电器", "603728", 7, "电机"),
    ("汉威科技", "300007", 7, "传感器"),
    ("长盈精密", "300115", 7, "结构件"),

    # 第八层：电子零部件（原第九层，11支）
    ("风华高科", "000636", 8, "MLCC"),
    ("三环集团", "300408", 8, "MLCC"),
    ("顺络电子", "002138", 8, "电感"),
    ("法拉电子", "600563", 8, "薄膜电容"),
    ("东晶电子", "002199", 8, "石英晶振"),
    ("博迁新材", "605376", 8, "MLCC材料"),
    ("立讯精密", "002475", 8, "连接器"),
    ("瑞可达", "688800", 8, "连接器"),
    ("兆龙互连", "300913", 8, "铜缆连接"),
    ("沃尔核材", "002130", 8, "铜缆连接"),
    ("国瓷材料", "300285", 8, "MLCC材料"),

    # 第九层：基本金属与小金属 —— 石化+基础金属+小金属+贵金属+化工+化肥（原第十~十三层）
    ("杰瑞股份", "002353", 9, "燃气轮机"),
    ("应流股份", "603308", 9, "燃气轮机"),
    ("中国神华", "601088", 9, "煤电"),
    ("广汇能源", "600256", 9, "综合能源"),
    ("中国石化", "600028", 9, "石化"),
    ("粤桂股份", "000833", 9, "硫铁矿"),
    ("紫金矿业", "601899", 9, "铜"),
    ("洛阳钼业", "603993", 9, "铜钴"),
    ("铜陵有色", "000630", 9, "铜"),
    ("西部矿业", "601168", 9, "多金属"),
    ("中国铝业", "601600", 9, "铝"),
    ("云铝股份", "000807", 9, "铝"),
    # —— 小金属与贵金属 ——
    ("中钨高新", "000657", 9, "钨"),
    ("厦门钨业", "600549", 9, "钨"),
    ("章源钨业", "002378", 9, "钨"),
    ("锡业股份", "000960", 9, "锡"),
    ("华锡有色", "600301", 9, "锡锑"),
    ("湖南黄金", "002155", 9, "锑黄金"),
    ("华钰矿业", "601020", 9, "锑"),
    ("云南锗业", "002428", 9, "锗"),
    ("北方稀土", "600111", 9, "稀土"),
    ("金力永磁", "300748", 9, "稀土永磁"),
    ("盛达资源", "000603", 9, "白银"),
    # —— 大宗化工 ——
    ("万华化学", "600309", 9, "MDI"),
    ("华鲁恒升", "600426", 9, "煤化工"),
    ("巨化股份", "600160", 9, "氟化工"),
    ("三美股份", "603379", 9, "制冷剂"),
    ("金石资源", "603505", 9, "萤石"),
    ("荣盛石化", "002493", 9, "炼化"),
    ("东岳硅材", "300821", 9, "有机硅"),
    ("龙佰集团", "002601", 9, "钛白粉"),
    ("新安股份", "600596", 9, "有机硅"),
    ("黑猫股份", "002068", 9, "炭黑"),
    # —— 化肥农药 ——
    ("云天化", "600096", 9, "磷化工"),
    ("兴发集团", "600141", 9, "磷化工"),
    ("新洋丰", "000902", 9, "复合肥"),
    ("亚钾国际", "000893", 9, "钾肥"),
    ("藏格矿业", "000408", 9, "钾肥"),
    ("湖北宜化", "000422", 9, "化肥"),
    ("扬农化工", "600486", 9, "农药"),
    ("润丰股份", "301035", 9, "农药"),
    ("国光股份", "002749", 9, "植调剂"),
    ("江山股份", "600389", 9, "草甘膦"),

    # 第五层：新能源发电与电池材料 —— 电池材料部分（原第十二层电池）
    ("湖南裕能", "301358", 5, "磷酸铁锂"),
    ("德方纳米", "300769", 5, "磷酸铁锂"),
    ("当升科技", "300073", 5, "三元正极"),
    ("璞泰来", "603659", 5, "负极材料"),
    ("尚太科技", "001301", 5, "负极材料"),
    ("天赐材料", "002709", 5, "电解液"),
    ("恩捷股份", "002812", 5, "隔膜"),
    ("天奈科技", "688116", 5, "导电剂"),

    # 第六层：储能与智能电网 —— 智能电网部分（原第十四层）
    ("许继电气", "000400", 6, "特高压"),
    ("特变电工", "600089", 6, "变压器"),
    ("中国西电", "601179", 6, "特高压"),
    ("平高电气", "600312", 6, "特高压"),
    ("威胜信息", "688100", 6, "能源物联网"),
    ("万胜智能", "300882", 6, "智慧计量"),
    ("南网科技", "688248", 6, "数智化"),
]

# ============================================================
# 二、特殊持仓标记
# ============================================================
HOLDINGS = {"688271"}  # 联影医疗 - 当前持仓

# A类标的（核心关注）
A_LIST = {
    "300308", "601138", "300476", "002463", "002371", "688012", "688072",
    "688981", "000338", "688676", "600885", "300750", "600406", "300124",
    "002008", "688305", "000636", "002475", "300308", "002371", "600406",
    "688271", "601899", "600519",
}

# ============================================================
# 三、数据获取
# ============================================================

# 上证系列指数（以000开头，需要sh前缀，不能走sz逻辑）
_SH_INDEX_CODES = frozenset({"000001","000016","000300","000688","000852","000905"})

def get_exchange_prefix(code):
    """确定股票交易所前缀"""
    if code in _SH_INDEX_CODES: return "sh"
    if code.startswith("6") or code.startswith("9"):
        return "sh"
    elif code.startswith("0") or code.startswith("3") or code.startswith("1"):
        return "sz"
    elif code.startswith("4") or code.startswith("8"):
        return "bj"
    else:
        return "sh"

def fetch_stock_data(codes):
    """从腾讯API批量获取股票数据"""
    results = {}
    # 每次查询最多50支
    batch_size = 50
    for i in range(0, len(codes), batch_size):
        batch = codes[i:i+batch_size]
        qs = ",".join([f"{get_exchange_prefix(c)}{c}" for c in batch])
        url = f"https://qt.gtimg.cn/q={qs}"
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            resp = urllib.request.urlopen(req, timeout=15)
            text = resp.read().decode("gbk")

            for line in text.strip().split("\n"):
                if not line.strip():
                    continue
                # 解析Tencent格式
                match = re.search(r'"(.*)"', line)
                if not match:
                    continue
                fields = match.group(1).split("~")
                if len(fields) < 40:
                    continue
                code = fields[2]
                name = fields[1]
                try:
                    current = float(fields[3]) if fields[3] else 0
                    prev_close = float(fields[4]) if fields[4] else 0
                    change_pct = float(fields[32]) if fields[32] else 0
                    high = float(fields[33]) if fields[33] else 0
                    low = float(fields[34]) if fields[34] else 0
                    volume = float(fields[6]) if fields[6] else 0
                    turnover = float(fields[37]) if fields[37] else 0
                    open_price = float(fields[5]) if fields[5] else 0

                    results[code] = {
                        "name": name,
                        "code": code,
                        "current": current,
                        "change_pct": change_pct,
                        "high": high,
                        "low": low,
                        "volume": volume,
                        "turnover": turnover,
                        "open": open_price,
                        "prev_close": prev_close,
                    }
                except (ValueError, IndexError):
                    continue
        except Exception as e:
            print(f"  获取数据出错: {e}", file=sys.stderr)

    return results

# ============================================================
# 四、报告生成
# ============================================================

def generate_scan_report(data):
    """生成扫描报告"""
    lines = []
    now = datetime.now()
    date_str = now.strftime("%Y-%m-%d")

    lines.append(f"📡 自选股扫描报告 - {date_str}")
    lines.append("")

    # === 大盘概览 ===
    # 从数据中提取大盘指数
    lines.append("## 📊 大盘概览")
    lines.append("")
    for idx_code, idx_name in [("399317", "国证A股"), ("399006", "创业板指"), ("000688", "科创50")]:
        if idx_code in data:
            d = data[idx_code]
            lines.append(f"- **{idx_name}**: {d['current']}  ({d['change_pct']:+.2f}%)")
    lines.append("")

    # === 持仓检查 ===
    lines.append("## 🔴 持仓检查")
    lines.append("")
    has_position = False
    for code in HOLDINGS:
        if code in data:
            has_position = True
            d = data[code]
            lines.append(f"| 标的 | 代码 | 现价 | 涨跌幅 | 最高 | 最低 |")
            lines.append(f"|------|------|------|--------|------|------|")
            lines.append(f"| {d['name']} | {code} | {d['current']} | {d['change_pct']:+.2f}% | {d['high']} | {d['low']} |")
    if not has_position:
        for code in data:
            if code == "688271":
                d = data[code]
                lines.append(f"| 标的 | 代码 | 现价 | 涨跌幅 | 最高 | 最低 |")
                lines.append(f"|------|------|------|--------|------|------|")
                lines.append(f"| {d['name']} | {code} | {d['current']} | {d['change_pct']:+.2f}% | {d['high']} | {d['low']} |")
                has_position = True
                break
    lines.append("")

    # === A类个股异动 ===
    lines.append("## 🟢 A类个股异动")
    lines.append("")
    lines.append("| 标的 | 涨跌幅 | 成交额 | 层位 | 信号 |")
    lines.append("|------|--------|--------|------|------|")

    a_movers = []
    for code in A_LIST:
        if code in data:
            d = data[code]
            # 找层位
            layer = ""
            for s_name, s_code, s_layer, s_sub in STOCKS:
                if s_code == code:
                    layer = f"L{s_layer}"
                    break
            # 判断信号
            signal = "—"
            chg = d['change_pct']
            if chg > 9:
                signal = "🚀 涨停/接近涨停"
            elif chg > 5:
                signal = "📈 大涨"
            elif chg > 3:
                signal = "📈 显著上涨"
            elif chg < -9:
                signal = "💥 跌停/接近跌停"
            elif chg < -5:
                signal = "📉 大跌"
            elif chg < -3:
                signal = "📉 显著下跌"

            # 成交量异常判断
            if d['volume'] > 0 and d.get('avg_volume', 0) > 0:
                vol_ratio = d['volume'] / d['avg_volume']
                if vol_ratio > 2:
                    signal += " 📊放量"

            turnover_str = f"{d['turnover']/10000:.0f}万" if d['turnover'] < 100000000 else f"{d['turnover']/100000000:.2f}亿"
            a_movers.append((abs(chg), d['name'], d['change_pct'], turnover_str, layer, signal))

    # 按涨跌幅绝对值排序，显示前15
    a_movers.sort(key=lambda x: x[0], reverse=True)
    for _, name, chg, turnover, layer, signal in a_movers[:15]:
        chg_str = f"**{chg:+.2f}%**" if abs(chg) > 3 else f"{chg:+.2f}%"
        lines.append(f"| {name} | {chg_str} | {turnover} | {layer} | {signal} |")
    lines.append("")

    # === 全市场异动TOP ===
    lines.append("## ⚡ 今日异动TOP（全部自选股中涨幅/跌幅最大）")
    lines.append("")

    all_data = [(d['change_pct'], d['name'], d['code'], d) for code, d in data.items() if code in {s[1] for s in STOCKS}]
    all_data.sort(key=lambda x: x[0], reverse=True)

    lines.append("**📈 涨幅TOP10:**")
    lines.append("| 排名 | 标的 | 涨跌幅 | 所属层 |")
    lines.append("|------|------|--------|--------|")
    for i, (chg, name, code, d) in enumerate(all_data[:10]):
        layer = ""
        for s_name, s_code, s_layer, s_sub in STOCKS:
            if s_code == code:
                layer = f"L{s_layer}"
                break
        lines.append(f"| {i+1} | {name} | **{chg:+.2f}%** | {layer} |")

    lines.append("")
    lines.append("**📉 跌幅TOP10:**")
    lines.append("| 排名 | 标的 | 涨跌幅 | 所属层 |")
    lines.append("|------|------|--------|--------|")
    for i, (chg, name, code, d) in enumerate(all_data[-10:]):
        layer = ""
        for s_name, s_code, s_layer, s_sub in STOCKS:
            if s_code == code:
                layer = f"L{s_layer}"
                break
        lines.append(f"| {i+1} | {name} | **{chg:+.2f}%** | {layer} |")
    lines.append("")

    # === 活跃层级分析 ===
    lines.append("## 🎯 活跃层级分析")
    lines.append("")

    layer_stats = {}
    for s_name, s_code, s_layer, s_sub in STOCKS:
        if s_code not in data:
            continue
        d = data[s_code]
        if s_layer not in layer_stats:
            layer_stats[s_layer] = {"count": 0, "up": 0, "down": 0, "avg_chg": 0, "big_movers": []}
        layer_stats[s_layer]["count"] += 1
        layer_stats[s_layer]["avg_chg"] += d['change_pct']
        if d['change_pct'] > 0:
            layer_stats[s_layer]["up"] += 1
        else:
            layer_stats[s_layer]["down"] += 1
        if abs(d['change_pct']) > 3:
            layer_stats[s_layer]["big_movers"].append((d['name'], d['change_pct']))

    for layer in sorted(layer_stats.keys()):
        s = layer_stats[layer]
        s["avg_chg"] /= s["count"] if s["count"] > 0 else 1

    # 按平均涨跌幅排序
    sorted_layers = sorted(layer_stats.items(), key=lambda x: x[1]["avg_chg"], reverse=True)

    lines.append("| 层级 | 平均涨跌幅 | 涨/跌家数 | 活跃度 |")
    lines.append("|------|-----------|----------|--------|")
    layer_names = {1:"算力基座", 2:"半导体", 3:"AI应用", 4:"电力设备",
                   5:"新能源发电与电池材料", 6:"储能与智能电网",
                   7:"高端装备与机器人", 8:"电子零部件", 9:"基本金属与小金属"}
    for layer, s in sorted_layers:
        active = "🟢" if s["avg_chg"] > 1 else ("🟡" if s["avg_chg"] > 0 else "🔴")
        name = layer_names.get(layer, f"L{layer}")
        lines.append(f"| L{layer} {name} | {s['avg_chg']:+.2f}% | {s['up']}↑/{s['down']}↓ | {active} |")
    lines.append("")

    # === 数据状态 ===
    lines.append("---")
    lines.append(f"*数据来源: 腾讯财经 | 更新时间: {now.strftime('%H:%M')} | 共扫描 {len([s for s in STOCKS if s[1] in data])} 支*")

    return "\n".join(lines)

# ============================================================
# 五、主程序
# ============================================================

def main():
    print("🔄 正在获取数据...", file=sys.stderr)

    # 获取所有股票代码
    all_codes = list(set([s[1] for s in STOCKS]))

    # 添加指数代码
    index_codes = ["399317", "399006", "000688"]
    all_codes = list(set(all_codes + index_codes))

    print(f"📊 共 {len(all_codes)} 支标的，正在请求数据...", file=sys.stderr)

    data = fetch_stock_data(all_codes)

    print(f"✅ 获取到 {len(data)} 支数据\n", file=sys.stderr)

    report = generate_scan_report(data)
    print(report)

if __name__ == "__main__":
    main()
