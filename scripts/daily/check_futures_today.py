#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Check if futures API has today's data now"""
import subprocess, json

def fetch_dates(sym):
    url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{}=/InnerFuturesNewService.getDailyKLine?symbol={}".format(sym, sym)
    try:
        r = subprocess.run(['curl', '-s', '--max-time', '15', url], capture_output=True, timeout=20)
        t = r.stdout.decode('gbk', errors='replace')
        if "null" in t:
            return None, "API返回null"
        s = t[t.index('['):t.rindex(']')+1]
        k = json.loads(s)
        last3 = k[-3:]
        return last3, None
    except Exception as e:
        return None, str(e)

# Check key contracts
contracts = ["AG2609", "CU2609", "MA2609", "JM2609", "RB2609", "SC2609", "M2609", "LC2609"]
print("合约      最新日期    收盘    结算    涨跌幅(按结算)")
print("-" * 60)
for sym in contracts:
    klines, err = fetch_dates(sym)
    if err:
        print("{:<10s} {}".format(sym, err))
        continue
    if klines:
        last = klines[-1]
        d = last['d']
        c = last['c']
        s = last['s']
        chg = 0
        if len(klines) >= 2:
            ps = float(klines[-2].get('s', 0))
            cs = float(s)
            if ps: chg = (cs - ps) / ps * 100
        print("{:<10s} {}  收{:<10s} 结{:<10s} {:+.2f}%".format(sym, d, c, s, chg))
