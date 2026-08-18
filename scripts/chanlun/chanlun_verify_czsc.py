"""
Phase A.3: 验证 czsc 在 SA 日线数据上的分型/笔/中枢识别质量
czsc 0.10.x API: from czsc import CZSC, RawBar, Freq, Direction
"""
import pandas as pd
from czsc import CZSC, RawBar, Freq, Direction

# 加载 SA 日线数据
df = pd.read_parquet("c:/Works/ClaudeCode/TradingStudio/data/sa_chanlun/sa_day.parquet")
df["dt"] = pd.to_datetime(df["dt"])

# 转换为 czsc RawBar 格式 (czsc 0.10.x 签名: symbol, dt, freq, open, close, high, low, vol, amount)
raw_bars = []
for i, (_, row) in enumerate(df.iterrows()):
    raw_bars.append(RawBar(
        symbol="SA",
        dt=row["dt"],
        freq=Freq.D,
        open=row["open"],
        close=row["close"],
        high=row["high"],
        low=row["low"],
        vol=row["vol"],
        amount=row["amount"],
        id=i,
    ))

print(f"SA 日线: {len(raw_bars)} bars")

# CZSC 分析
czsc = CZSC(raw_bars, max_bi_num=500)

print(f"\n=== 分型 (FX) ===")
print(f"总数: {len(czsc.fx_list)}")
tops = [f for f in czsc.fx_list if f.mark == 'G']
bots = [f for f in czsc.fx_list if f.mark == 'D']
print(f"  顶分型(G): {len(tops)}")
print(f"  底分型(D): {len(bots)}")
if czsc.fx_list:
    print(f"  最近5个:")
    for fx in czsc.fx_list[-5:]:
        print(f"    {fx.dt.strftime('%Y-%m-%d')} | {fx.mark} | close={fx.close:.0f} | power={fx.power_str}")

print(f"\n=== 笔 (BI) ===")
print(f"总数: {len(czsc.bi_list)}")
up_bi = [b for b in czsc.bi_list if b.direction == Direction.Up]
dn_bi = [b for b in czsc.bi_list if b.direction == Direction.Down]
print(f"  向上笔: {len(up_bi)}")
print(f"  向下笔: {len(dn_bi)}")
if czsc.bi_list:
    bi_lengths = []
    bi_days = []
    for bi in czsc.bi_list:
        if bi.fx_a.close > 0 and bi.fx_b.close > 0:
            chg = abs(bi.fx_b.close - bi.fx_a.close) / bi.fx_a.close * 100
            bi_lengths.append(chg)
        bi_days.append(len(bi.bars))
    if bi_lengths:
        print(f"  笔幅度: avg={sum(bi_lengths)/len(bi_lengths):.1f}% max={max(bi_lengths):.1f}% min={min(bi_lengths):.1f}%")
    if bi_days:
        print(f"  笔长度: avg={sum(bi_days)/len(bi_days):.1f}天 max={max(bi_days)}天 min={min(bi_days)}天")
    print(f"  最近5笔:")
    for bi in czsc.bi_list[-5:]:
        dname = "Up" if bi.direction == Direction.Up else "Down"
        if bi.fx_a.close > 0:
            chg = abs(bi.fx_b.close - bi.fx_a.close) / bi.fx_a.close * 100
            print(f"    {dname}: {bi.fx_a.dt.strftime('%Y-%m-%d')}({bi.fx_a.close:.0f}) -> {bi.fx_b.dt.strftime('%Y-%m-%d')}({bi.fx_b.close:.0f}) | {chg:.1f}% | {len(bi.bars)}天")

print(f"\n=== 中枢 (ZS) ===")
print(f"总数: {len(czsc.zs_list)}")
if czsc.zs_list:
    print(f"  最近5个:")
    for zs in czsc.zs_list[-5:]:
        width_pct = (zs.zg - zs.zd) / zs.zd * 100 if zs.zd > 0 else 0
        print(f"    {zs.dt_start.strftime('%Y-%m-%d')} ~ {zs.dt_end.strftime('%Y-%m-%d')} | ZG={zs.zg:.0f} ZD={zs.zd:.0f} | width={width_pct:.1f}%")

print(f"\n=== 分段统计 ===")
for yr in range(2020, 2026):
    bi_yr = [b for b in czsc.bi_list if b.fx_a.dt.year == yr and b.fx_b.dt.year == yr]
    zs_yr = [z for z in czsc.zs_list if z.dt_start.year == yr]
    up = len([b for b in bi_yr if b.direction == Direction.Up])
    dn = len([b for b in bi_yr if b.direction == Direction.Down])
    print(f"  {yr}: {len(bi_yr)}笔(up={up}, dn={dn}), {len(zs_yr)}中枢")

print("\nDone - CZSC verified on SA daily!")
