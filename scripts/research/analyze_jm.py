#!/usr/bin/env python3
import urllib.request, re, json

def get_day_kline(symbol):
    url = f"https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{symbol}=/InnerFuturesNewService.getDailyKLine?symbol={symbol}"
    req = urllib.request.Request(url)
    data = urllib.request.urlopen(req, timeout=10).read().decode()
    m = re.search(r'\((\[.*\])\)', data)
    if m:
        return json.loads(m.group(1))
    return []

days = get_day_kline('JM2609')
print(f"Total days: {len(days)}")
for d in days[-5:]:
    print(f"{d['d']}: O={d['o']} H={d['h']} L={d['l']} C={d['c']} S={d['s']} Vol={d['v']} OI={d['p']}")
