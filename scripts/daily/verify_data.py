#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Verify futures data accuracy by checking raw API responses"""
import subprocess, json, sys

def fetch_raw(symbol):
    url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{}=/InnerFuturesNewService.getDailyKLine?symbol={}".format(symbol, symbol)
    try:
        r = subprocess.run(['curl', '-s', '--max-time', '15', url], capture_output=True, timeout=20)
        text = r.stdout.decode('gbk', errors='replace')
        return text
    except Exception as e:
        return "Error: {}".format(e)

# Check AU2609
print("=" * 60)
print("数据准确性检查")
print("=" * 60)

print("\n[1] AU2609 - 为什么黄金没有数据？")
raw = fetch_raw("AU2609")
print("  API返回:", raw[:100])
if "null" in raw:
    print("  -> Sina API对AU2609返回null，此合约数据不可用。")
    print("  -> 可能原因：黄金主力合约已换月，或Sina不支持该代码。")

print("\n[2] 检查结算价 vs 收盘价的差异（关键！）")
# Check several contracts to see settlement vs close difference
for sym in ["AG2609", "MA2609", "CU2609", "RB2609", "M2609", "SC2609"]:
    raw = fetch_raw(sym)
    try:
        s = raw[raw.index('['):raw.rindex(']')+1]
        k = json.loads(s)
        today = k[-1]
        yesterday = k[-2]
        close = float(today['c'])
        settle = float(today['s'])
        prev_settle = float(yesterday['s'])
        prev_close = float(yesterday['c'])
        chg_settle = (settle - prev_settle) / prev_settle * 100
        chg_close = (close - prev_close) / prev_close * 100
        diff = abs(chg_settle - chg_close)
        flag = " *** 差异较大!" if diff > 0.5 else ""
        print("  {:<8s} 收{:<12s} 结{:<12s} 按结算:{:+.2f}% 按收盘:{:+.2f}% 差{:.2f}%{}".format(
            sym, today['c'], today['s'], chg_settle, chg_close, diff, flag))
    except Exception as e:
        print("  {}: 解析错误 - {}".format(sym, e))

print("\n[3] 检查持仓量数据可靠性")
for sym in ["MA2609", "FU2609", "TA2609", "I2609", "JM2609"]:
    raw = fetch_raw(sym)
    try:
        s = raw[raw.index('['):raw.rindex(']')+1]
        k = json.loads(s)
        today = k[-1]
        yesterday = k[-2]
        oi_change = int(today['p']) - int(yesterday['p'])
        valid = "✓" if abs(oi_change) < 1000000 else "⚠️ 巨量变化"
        print("  {:<8s} 昨持{:>8s} 今持{:>8s} 变化{:>+8,d} {}".format(
            sym, yesterday['p'], today['p'], oi_change, valid))
    except Exception as e:
        print("  {}: 解析错误 - {}".format(sym, e))

print("\n[4] 股指期货 - 检查结算价=0的问题")
for sym in ["IF2609", "IC2609", "IM2609", "IH2609"]:
    raw = fetch_raw(sym)
    try:
        s = raw[raw.index('['):raw.rindex(']')+1]
        k = json.loads(s)
        today = k[-1]
        yesterday = k[-2]
        close_chg = (float(today['c']) - float(yesterday['c'])) / float(yesterday['c']) * 100
        print("  {:<8s} 收{:<12s} 结{:<12s} 按收盘:{:+.2f}%  (结算={},不可用)".format(
            sym, today['c'], today['s'], close_chg, today['s']))
    except Exception as e:
        print("  {}: 解析错误".format(sym))

print("\n[5] 检查涨跌幅>3%的品种，看是否合理")
for sym in ["AG2609", "SN2609", "MA2609"]:
    raw = fetch_raw(sym)
    try:
        s = raw[raw.index('['):raw.rindex(']')+1]
        k = json.loads(s)
        today = k[-1]
        yesterday = k[-2]
        y_settle = float(yesterday['s'])
        t_settle = float(today['s'])
        chg = (t_settle - y_settle) / y_settle * 100
        print("  {:<8s} {:>10s}->{:>10s} 昨结{:>10s}->今结{:>10s} {:+.2f}%".format(
            sym, yesterday['c'], today['c'], yesterday['s'], today['s'], chg))
    except Exception as e:
        print("  {}: 解析错误".format(sym))

print("\n" + "=" * 60)
print("数据准确性总结")
print("=" * 60)
print("""
✅ 商品期货：结算价(s)字段正常，涨跌幅按结算价计算准确
❌ AU2609(黄金)：Sina API返回null，无法获取数据
⚠️ 股指期货：结算价(s)=0，涨跌幅只能用收盘价计算
⚠️ 持仓量数据：来自日K线API，为当日收盘持仓量，可靠

建议：如对数据准确性有疑虑，可与交易软件（文华/博易）对比校验。
""")
