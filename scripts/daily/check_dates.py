#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Check what dates futures API returns"""
import subprocess, json

def fetch(sym):
    url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{}=/InnerFuturesNewService.getDailyKLine?symbol={}".format(sym, sym)
    try:
        r = subprocess.run(['curl', '-s', '--max-time', '15', url], capture_output=True, timeout=20)
        t = r.stdout.decode('gbk', errors='replace')
        s = t[t.index('['):t.rindex(']')+1]
        return json.loads(s)
    except:
        return None

for sym in ["AG2609", "CU2609", "MA2609", "JM2609", "IF2609"]:
    k = fetch(sym)
    if k:
        dates = [x['d'] for x in k[-5:]]
        print("{} last 5 dates: {}".format(sym, dates))
    else:
        print("{}: no data".format(sym))
