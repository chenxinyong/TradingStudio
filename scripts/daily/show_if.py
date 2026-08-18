#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Parse and display stock index futures data"""
import json, subprocess

def fetch_futures(symbol):
    url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{}=/InnerFuturesNewService.getDailyKLine?symbol={}".format(symbol, symbol)
    try:
        r = subprocess.run(['curl', '-s', '--max-time', '15', url], capture_output=True, timeout=20)
        text = r.stdout.decode('gbk', errors='replace')
        json_str = text[text.index('['):text.rindex(']')+1]
        return json.loads(json_str)
    except Exception as e:
        print("Error {}: {}".format(symbol, e))
        return None

indices = {
    "IF": {"name": "沪深300", "spot": 4713.64, "spot_chg": -2.14},
    "IC": {"name": "中证500", "spot": 7963.45, "spot_chg": -3.49},
    "IM": {"name": "中证1000", "spot": 8081.26, "spot_chg": -3.11},
    "IH": {"name": "上证50", "spot": 2825.01, "spot_chg": -1.43},
}

print("股指期货数据 2026-06-08")
print()
header = "{:<8s} {:<10s} {:>10s} {:>10s} {:>10s} {:>8s} {:>10s}".format(
    "合约", "收盘价", "涨跌幅", "现货指数", "基差", "基差方向", "持仓变化")
print(header)
print("-" * 70)

for prefix in ["IF", "IC", "IM", "IH"]:
    symbol = prefix + "2609"
    klines = fetch_futures(symbol)
    if klines and len(klines) >= 2:
        prev = klines[-2]
        today = klines[-1]
        prev_close = float(prev['c'])
        curr_close = float(today['c'])
        chg_pct = (curr_close - prev_close) / prev_close * 100

        prev_oi = int(prev.get('p', 0))
        curr_oi = int(today.get('p', 0))
        oi_change = curr_oi - prev_oi

        spot = indices[prefix]["spot"]
        basis = curr_close - spot
        basis_dir = "升水" if basis > 0 else "贴水"

        row = "{:<8s} {:<10.2f} {:>+8.2f}% {:>10.2f} {:>+10.2f} {:>8s} {:>+10,d}".format(
            symbol, curr_close, chg_pct, spot, basis, basis_dir, oi_change)
        print(row)
    else:
        print("{:<8s} 数据获取失败".format(symbol))

print("-" * 70)
print()
print("基差说明: 期货价格 - 现货指数价格")
print("  贴水 = 期货低于现货 (市场偏空)")
print("  升水 = 期货高于现货 (市场偏多)")
print()

# Interpretation
print("== 基差解读 ==")
for prefix in ["IF", "IC", "IM", "IH"]:
    symbol = prefix + "2609"
    klines = fetch_futures(symbol)
    if klines and len(klines) >= 2:
        today = klines[-1]
        curr_close = float(today['c'])
        spot = indices[prefix]["spot"]
        basis = curr_close - spot
        basis_pct = basis / spot * 100
        print("  {}: {:.2f}点 ({:+.2f}%) - {}".format(
            indices[prefix]["name"], basis, basis_pct, "市场偏谨慎" if basis < 0 else "市场偏乐观"))
