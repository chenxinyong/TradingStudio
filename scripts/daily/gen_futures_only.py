#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Generate futures log only (from saved data)"""

import pickle
import os
import json
import subprocess

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"
DATE = "2026-06-08"
PREV_DATE = "2026-06-05"

def curl_fetch_text(url, timeout=20):
    try:
        result = subprocess.run(['curl', '-s', '--max-time', str(timeout),
             '-H', 'Referer: https://finance.sina.com.cn',
             '-H', 'User-Agent: Mozilla/5.0', url],
            capture_output=True, timeout=timeout+5)
        if result.returncode == 0 and result.stdout:
            return result.stdout.decode('gbk', errors='replace')
    except:
        pass
    return None

# Also try to load from previously saved data
try:
    with open(os.path.join(VAULT, "scripts", "raw_data.pkl"), "rb") as f:
        saved = pickle.load(f)
    futures_data = saved.get("futures", {})
    print("Loaded from existing pickle: {} contracts".format(len(futures_data)))
except:
    futures_data = {}

print("Generating futures log for {}...".format(DATE))

# Try to fetch any missing data
futures_contracts = [
    ("AU2609","黄金 AU","贵金属"),("AG2609","白银 AG","贵金属"),
    ("CU2609","铜 CU","有色金属"),("AL2609","铝 AL","有色金属"),
    ("ZN2609","锌 ZN","有色金属"),("NI2609","镍 NI","有色金属"),
    ("SN2609","锡 SN","有色金属"),("PB2609","铅 PB","有色金属"),
    ("SC2609","原油 SC","能源"),("FU2609","燃料油 FU","能源"),
    ("TA2609","PTA TA","化工"),("MA2609","甲醇 MA","化工"),
    ("V2609","PVC V","化工"),("L2609","LLDPE L","化工"),
    ("EG2609","乙二醇 EG","化工"),
    ("RB2609","螺纹 RB","黑色系"),("HC2609","热卷 HC","黑色系"),
    ("I2609","铁矿 I","黑色系"),("JM2609","焦煤 JM","黑色系"),("J2609","焦炭 J","黑色系"),
    ("M2609","豆粕 M","农产品"),("Y2609","豆油 Y","农产品"),("P2609","棕榈油 P","农产品"),
    ("SR2609","白糖 SR","农产品"),("CF2609","棉花 CF","农产品"),("RM2609","菜粕 RM","农产品"),
    ("LC2609","碳酸锂 LC","新能源"),("SI2609","工业硅 SI","新能源"),
]

# Re-fetch futures data fresh (since we already had it working)
futures_data = {}
for symbol, name, category in futures_contracts:
    url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{}=/InnerFuturesNewService.getDailyKLine?symbol={}".format(symbol, symbol)
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
            print("  {}: 收{} 结{} ({:+.2f}%)".format(name, recent[-1].get('c'), recent[-1].get('s'), chg_pct))
        except Exception as e:
            print("  {}: error: {}".format(name, e))
            futures_data[symbol] = {"name": name, "category": category, "klines": []}
    else:
        print("  {}: no data".format(name))
        futures_data[symbol] = {"name": name, "category": category, "klines": []}

# Build the log
fl = []

fl.append("---")
fl.append("date: {}".format(DATE))
fl.append("tags:")
fl.append("  - 交易日志/期货")
fl.append("---")
fl.append("")
fl.append("# 期货交易日志 - {}".format(DATE))
fl.append("")
fl.append("> **夜盘归属说明**：本日志记录 {} 白天日盘 + {} 夜盘".format(DATE, PREV_DATE))
fl.append("")
fl.append("---")
fl.append("")
fl.append("## 一、市场总览（规则一：文华商品定方向）")
fl.append("")
fl.append("### 商品指数")
fl.append("| 指数 | 收盘价 | 涨跌幅 | 趋势判断 | 备注 |")
fl.append("|------|--------|--------|---------|------|")
fl.append("| 文华商品 | — | — | ➡（数据获取失败） | 免费API无商品指数数据 |")
fl.append("| 工业品 | — | — | ➡ | 同上 |")
fl.append("| 农产品 | — | — | ➡ | 同上 |")
fl.append("")
fl.append("**文华商品方向**：➡ 震荡")
fl.append("")
fl.append("### 股指期货")
fl.append("| 合约 | 收盘价 | 涨跌幅 | 基差 | 持仓量变化 |")
fl.append("|------|--------|--------|------|-----------|")
fl.append("| IF | — | — | — | — |")
fl.append("| IC | — | — | — | — |")
fl.append("| IM | — | — | — | — |")
fl.append("| IH | — | — | — | — |")
fl.append("")
fl.append("---")
fl.append("")
fl.append("## 二、各板块跟踪")
fl.append("")
fl.append("> 只关注规则三中 Top 30 流动性品种。周线选方向，日线看位置。")
fl.append("")

# Group by category
futures_by_cat = {}
for s, n, c in futures_contracts:
    futures_by_cat.setdefault(c, []).append((s, n))

cat_display = {
    "贵金属": "① 贵金属", "有色金属": "② 有色金属",
    "能源": "③ 能源", "化工": "④ 化工",
    "黑色系": "⑤ 黑色系", "农产品": "⑥ 农产品",
    "新能源": "⑦ 新能源"
}

abnormal_futures = []
table_header = "| 合约 | 收盘价 | 涨跌幅 | 持仓量变化 | 周线趋势 | 日线位置 | 15分钟信号 |\n|------|--------|--------|-----------|---------|---------|-----------|\n"

for cat in ["贵金属","有色金属","能源","化工","黑色系","农产品","新能源"]:
    fl.append("### {}".format(cat_display.get(cat, cat)))
    fl.append(table_header)
    for symbol, name in futures_by_cat.get(cat, []):
        if symbol not in futures_data or not futures_data[symbol].get("klines"):
            fl.append("| {} | 数据获取失败 | — | — | ➡ | — | 无 |".format(name))
            continue
        fd = futures_data[symbol]
        klines = fd["klines"]
        today = klines[-1]
        close = today.get("c", "—")
        chg = fd.get("chg_pct", 0)
        if len(klines) >= 2:
            prev_oi = int(klines[-2].get("p", 0))
            curr_oi = int(klines[-1].get("p", 0))
            oi_str = "{:+,}".format(curr_oi - prev_oi)
        else:
            oi_str = "—"
        chg_str = "**{:+.2f}%**".format(chg)
        if abs(chg) >= 2:
            chg_str = "**{:+.2f}%**".format(chg)
        # Simple 3-day trend
        if len(klines) >= 3:
            c1 = float(klines[-3].get("c", 0))
            c3 = float(klines[-1].get("c", 0))
            if c3 > c1 * 1.03:
                trend = ""
            elif c3 < c1 * 0.97:
                trend = ""
            else:
                trend = ""
        else:
            trend = ""
        fl.append("| {} | {} | {} | {} | {} | — | 无 |".format(name, close, chg_str, oi_str, trend))

        if abs(chg) >= 2:
            abnormal_futures.append((name, cat, chg, oi_str, symbol))
    fl.append("")

abnormal_futures.sort(key=lambda x: abs(x[2]), reverse=True)

fl.append("---")
fl.append("")
fl.append("## 三、异动合约")
fl.append("")
fl.append("| 合约 | 方向 | 涨跌幅 | 持仓量异动 | 可能原因 | 是否关注 |")
fl.append("|------|------|--------|-----------|---------|---------|")
if abnormal_futures:
    for name, cat, chg, oi_str, sym in abnormal_futures:
        direction = "↑" if chg > 0 else "↓"
        try:
            oi_num = int(oi_str.replace(",", ""))
        except:
            oi_num = 0
        if oi_num > 0:
            oi_type = "增仓"
        elif oi_num < 0:
            oi_type = "减仓"
        else:
            oi_type = "—"
        if abs(chg) >= 3:
            attn = ""
        else:
            attn = ""
        fl.append("| {} | {} | **{:+.2f}%** | {} {} | 数据获取失败 | {} |".format(name, direction, chg, oi_type, oi_str, attn))
else:
    fl.append("| 无显著异动合约 | | | | | |")

fl.append("")
fl.append("**关键信号**：")
fl.append("- **增仓上涨** -> 新多入场，趋势可能延续")
fl.append("- **增仓下跌** -> 新空入场，下跌动能强")
fl.append("- **减仓上涨** -> 空头平仓，反弹而非反转")
fl.append("- **减仓下跌** -> 多头平仓，回调而非转势")
fl.append("")
fl.append("---")
fl.append("")
fl.append("## 四、今日交易记录")
fl.append("")
fl.append("### 开仓")
fl.append("| 合约 | 方向 | 手数 | 开仓价 | 开仓理由 | 止损位 | 符合计划 |")
fl.append("|------|------|------|--------|---------|-------|---------|")
fl.append("| | 多/空 | | | | | / ❌ |")
fl.append("")
fl.append("### 平仓")
fl.append("| 合约 | 方向 | 手数 | 平仓价 | 平仓理由 | 盈亏 | 反思 |")
fl.append("|------|------|------|--------|---------|------|------|")
fl.append("| | 多/空 | | | 止盈/止损/手动 | ±R | |")
fl.append("")
fl.append("> 今日无交易")
fl.append("")
fl.append("---")
fl.append("")
fl.append("## 五、持仓跟踪")
fl.append("")
fl.append("| 合约 | 方向 | 手数 | 开仓均价 | 现价 | 浮动盈亏 | 浮盈% | 止损位 | 目标位 | 当前判断 |")
fl.append("|------|------|------|---------|------|---------|-------|-------|-------|---------|")
fl.append("| | 多/空 | | | | ±R | ±% | | | 持有/加仓/减仓/止损 |")
fl.append("")
fl.append("**总保证金占用**：__元")
fl.append("**总浮动盈亏**：±R / ±%")
fl.append("**当前杠杆倍数**：__倍")
fl.append("**风险度（保证金/权益）**：__%")
fl.append("")
fl.append("---")
fl.append("")
fl.append("## 六、交割月提醒")
fl.append("")
fl.append("| 合约 | 最后交易日 | 是否需移仓 | 计划 |")
fl.append("|------|-----------|-----------|------|")
fl.append("| | | / 无需 | |")
fl.append("")
fl.append("---")
fl.append("")
fl.append("## 七、收盘复盘")
fl.append("")
fl.append("### 盘面综合判断")
fl.append("- 文华商品方向（规则一）：➡ 震荡（数据获取失败）")
fl.append("- 当前市场风格（趋势/震荡/无序）：需结合文华商品指数判断")
fl.append("- 盈利效应（好/一般/差）：—")

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
    fl.append("- 最强板块：**{}**（均涨跌 **{:+.2f}%**）".format(strongest, sector_avg[strongest]))
    fl.append("- 最弱板块：**{}**（均涨跌 **{:+.2f}%**）".format(weakest, sector_avg[weakest]))
else:
    fl.append("- 最强板块：数据不足")
    fl.append("- 最弱板块：数据不足")

fl.append("")
fl.append("### 交易系统检查（规则六~九）")
fl.append("- [ ] **规则六**：今日所有开仓是否顺周线方向？")
fl.append("- [ ] **规则七.1**：所有持仓是否设置了止损条件单？以第一笔开仓价为基准？")
fl.append("- [ ] **规则七.2**：浮盈品种是否需要上移止损至保本/锁定利润？")
fl.append("- [ ] **规则七.3**：**亏损持仓是否已清仓？** 收盘前是否检查了外盘异动？")
fl.append("- [ ] **规则三**：交易的品种是否在 Top 30 流动性名单中？")
fl.append("- [ ] **规则八**：单笔亏损是否超过 1%？单日是否超过 3%？")
fl.append("- [ ] **规则九**：今日是否有在禁区内交易？")
fl.append("- [ ] 是否有逆周线方向的开仓？如有，记录原因")
fl.append("")
fl.append("### 今日做得好的")
fl.append("- ")
fl.append("")
fl.append("### 今日做得不好的 / 违反纪律")
fl.append("- [ ] ")
fl.append("")
fl.append("### 明日计划")
fl.append("- [ ] 关注：")
fl.append("- [ ] 计划操作：")
fl.append("- [ ] 关键价位：")
fl.append("")
fl.append("### 自由记录")
fl.append("> ")
fl.append("")
fl.append("---")
fl.append("")
fl.append("## 八、链接")
fl.append("")
fl.append("- 股票日志：[[01-Daily/股票/{}]]".format(DATE))
fl.append("- 交易系统：[[04-Research/04-交易系统/期货交易系统]]")
fl.append("- 品种研究：待创建")

futures_log = "\n".join(fl)

futures_path = os.path.join(VAULT, "01-Daily", "期货", "{}.md".format(DATE))
os.makedirs(os.path.dirname(futures_path), exist_ok=True)
with open(futures_path, 'w', encoding='utf-8') as f:
    f.write(futures_log)

print("Done! Futures log written.")
print("Path: " + futures_path)
