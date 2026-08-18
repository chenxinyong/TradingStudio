#!/usr/bin/env python3
"""从 stocks.json 生成自选股总表 Markdown 文件"""
import json
import os

script_dir = os.path.dirname(os.path.abspath(__file__))
json_path = os.path.join(script_dir, "stocks.json")
md_path = os.path.join(script_dir, "..", "04-Research", "04-交易系统", "自选股总表.md")

with open(json_path, "r", encoding="utf-8") as f:
    data = json.load(f)

report_name = {
    1: ("L01-算力基座", "算力基座"), 2: ("L02-半导体", "半导体"),
    3: ("L03-大模型与应用", "大模型与应用"), 4: ("L04-电力设备", "电力设备"),
    5: ("L05-新能源发电", "新能源发电"), 6: ("L06-储能与调节", "储能与调节"),
    7: ("L07-高端装备制造", "高端装备制造"), 8: ("L08-机器人", "机器人"),
    9: ("L09-电子元器件", "电子元器件"), 10: ("L10-石化与基础金属", "石化与基础金属"),
    11: ("L11-小金属与贵金属", "小金属与贵金属"), 12: ("L12-大宗化工与电池材料", "大宗化工与电池材料"),
    13: ("L13-化肥农药", "化肥农药"), 14: ("L14-智能电网", "智能电网"),
}

lines = []
lines.append("---")
lines.append('date: 2026-05-25')
lines.append("tags:")
lines.append("  - 自选股")
lines.append("  - 股票池")
lines.append("  - 交易系统")
lines.append("status: draft")
lines.append("---")
lines.append("# 自选股总表")
lines.append("")
lines.append("> {} 标的 | 按9层产业链框架分类 | 更新时间：{}".format(
    "共 {} 支".format(data["meta"]["total"]), data["meta"]["updated"]))
lines.append(">")
lines.append("> 数据源：[[04-Research/00-总纲/AI产业链深度分析]] | 扫描脚本：scripts/scan.sh")
lines.append("")
lines.append("---")

# 按层级组织
layer_stocks = {}
for s in data["stocks"]:
    l = s["layer"]
    layer_stocks.setdefault(l, []).append(s)

for ln in sorted(layer_stocks.keys()):
    li = data["layers"][str(ln)]
    lname = li["name"]
    stars = "★" * li["stars"] + "☆" * (5 - li["stars"])
    slist = layer_stocks[ln]

    a_cnt = len([s for s in slist if s["level"] == "A"])
    b_cnt = len([s for s in slist if s["level"] == "B"])
    c_cnt = len([s for s in slist if s["level"] == "C"])

    lines.append("")
    lines.append("## L{} {}（{}支）{}  A{} B{} C{}".format(
        ln, lname, len(slist), stars, a_cnt, b_cnt, c_cnt))
    lines.append("")
    lines.append("| 级别 | 代码 | 名称 | 子类 | 状态 | 备注 |")
    lines.append("|------|------|------|------|------|------|")

    lv_order = {"A": 0, "B": 1, "C": 2}
    slist.sort(key=lambda x: (lv_order.get(x["level"], 99), x["name"]))

    for s in slist:
        icon = {"A": "A", "B": "B", "C": "C"}.get(s["level"], s["level"])
        status = s["status"] if s["status"] else "-"
        tags = s["tags"] if s["tags"] else ""
        lines.append("| {} | {} | **{}** | {} | {} | {} |".format(
            icon, s["code"], s["name"], s["sub"], status, tags))

    lines.append("")
    if ln in report_name:
        fn, nn = report_name[ln]
        lines.append("> 深度分析：[[04-Research/01-产业链分层/{}|L{} {}]]".format(fn, ln, lname))

lines.append("")
lines.append("---")
lines.append("")

a_all = len([s for s in data["stocks"] if s["level"] == "A"])
b_all = len([s for s in data["stocks"] if s["level"] == "B"])
c_all = len([s for s in data["stocks"] if s["level"] == "C"])
hold = len([s for s in data["stocks"] if s["status"] == "持仓"])

lines.append("**统计**：A类 {} 支 | B类 {} 支 | C类 {} 支 | 持仓 {} 支".format(a_all, b_all, c_all, hold))
lines.append("")
lines.append("### 相关笔记")
lines.append("- [[04-Research/00-总纲/AI产业链深度分析]]")
lines.append("- [[04-Research/04-交易系统/自选股分级扫描系统]]")
lines.append("- [[04-Research/04-交易系统/非盯盘交易策略]]")
lines.append("- [[04-Research/04-交易系统/交易纪律与理性检查]]")
lines.append("- 扫描脚本：scripts/scan.sh")

with open(md_path, "w", encoding="utf-8") as f:
    f.write("\n".join(lines))

print("OK: " + md_path)
print("A={} B={} C={} 持仓={}".format(a_all, b_all, c_all, hold))
