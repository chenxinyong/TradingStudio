#!/usr/bin/env python3
"""焦煤JM数据源检查脚本
发现: JM0 (连续合约) 数据损坏，卡在2024年水平
正确数据源:
  1. K-line API: 日线/结算价数据 (可靠)
  2. nf_JM2609: 实时数据 (夜盘也可用)
"""
import urllib.request, re, json, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

def get_kline(symbol):
    url = f"https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20day_{symbol}=/InnerFuturesNewService.getDailyKLine?symbol={symbol}"
    req = urllib.request.Request(url)
    data = urllib.request.urlopen(req, timeout=10).read().decode('utf-8')
    m = re.search(r'\((\[.*\])\)', data)
    return json.loads(m.group(1)) if m else []

print('='*60)
print('1. JM2609 日K线数据（最近5个交易日）')
print('='*60)
days = get_kline('JM2609')
if days:
    for d in days[-5:]:
        s_prev = float(days[days.index(d)-1]['s']) if days.index(d) > 0 else float(d['s'])
        chg = (float(d['s']) - s_prev) / s_prev * 100
        print(f"  {d['d']}: 开={d['o']} 高={d['h']} 低={d['l']} 收={d['c']} 结算={d['s']} 量={d['v']} 仓={d['p']} | 涨幅(对昨结)={chg:+.2f}%")

print()
print('='*60)
print('2. nf_JM2609 实时行情')
print('='*60)
url = 'http://hq.sinajs.cn/list=nf_JM2609'
req = urllib.request.Request(url, headers={'Referer': 'http://finance.sina.com.cn'})
data = urllib.request.urlopen(req, timeout=10).read().decode('gbk')
m = re.search(r'=\"([^\"]+)\"', data)
if m:
    f = m.group(1).split(',')
    print(f"  名称: {f[0]}")
    print(f"  时间: {f[1]}")
    print(f"  开盘: {f[2]}  最高: {f[3]}  最低: {f[4]}")
    print(f"  最新: {f[6]}  昨收: {f[5]}")
    print(f"  结算(日): {f[10]}  结算(动态): {f[27]}")
    print(f"  成交量: {f[13]}  持仓量: {f[14]}")

print()
print('='*60)
print('3. JM0 连续合约（损坏状态确认）')
print('='*60)
url = 'http://hq.sinajs.cn/list=JM0'
req = urllib.request.Request(url, headers={'Referer': 'http://finance.sina.com.cn'})
data = urllib.request.urlopen(req, timeout=10).read().decode('gbk')
m = re.search(r'=\"([^\"]+)\"', data)
if m:
    f = m.group(1).split(',')
    print(f"  名称: {f[0]}")
    print(f"  时间: {f[1]}")
    print(f"  开盘: {f[2]}  最高: {f[3]}  最低: {f[4]}")
    print(f"  最新: {f[8]}  昨收/昨结: {f[5]}/{f[10]}")
    print(f"  持仓量: {f[13]}  成交量: {f[14]}")
    print(f"  => 日期字段={f[17] if len(f) > 17 else 'N/A'}")
    print(f"  => 结论: {'数据损坏(卡在2024年)' if '2024' in (f[17] if len(f) > 17 else '') else '数据正常'}")

print()
print('='*60)
print('✅ 推荐方案: 使用 JM2609 日K线API获取结算价')
print('   最新收盘/结算: %s / %s' % (days[-1]['c'], days[-1]['s']))
print('='*60)
