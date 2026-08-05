"""
每日盘前信号生成 — 从前一日15min Bar计算ML复合因子, 生成今日交易信号

用法: python scripts/factor_research/generate_live_signal.py [--date 2026-08-06]
      默认生成"下一个交易日"的信号 (today = 最近一个交易日+1)

依赖: bars_live.duckdb (Live/Collect 模式运行的实时数据库)
      composite_intraday_signals.csv (历史信号, 新信号追加到末尾)
"""

import sys, io, os, argparse
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import pandas as pd, numpy as np
from datetime import date, timedelta

# ── 配置 ──
LIVE_DB = "src/TradingStudio/data/bars_live.duckdb"  # Live模式DB
FACTOR_PANEL = "scripts/factor_research/output/factors_panel_v1.parquet"
SIGNAL_CSV = "scripts/factor_research/output/composite_intraday_signals.csv"

# IC_Weighted 平均权重 (来自 Phase2 ML 分析, OOS窗口平均)
IC_WEIGHTS = {
    'IntradayMom': 0.22,
    'VWAP_Dev': -0.27,   # 负权重: VWAP_Dev有负IC
    'Amihud': 0.33,
    'VolumeRatio_20d': 0.17,
}

TOP_N = 5
MIN_INSTRUMENTS = 10

# ── 参数解析 ──
parser = argparse.ArgumentParser()
parser.add_argument('--date', type=str, help='Target trading day (YYYY-MM-DD), default=next trading day')
args = parser.parse_args()

# ── 连接 ──
import duckdb
try:
    conn = duckdb.connect(LIVE_DB, read_only=True)
    print(f"Live DB: {LIVE_DB}")
except Exception as e:
    print(f"[ERROR] Cannot open live DB: {e}")
    print("  Make sure Live/Collect mode is running.")
    sys.exit(1)

# ── 1. 确定今天和上一个交易日 ──
tables = conn.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='main'").fetchall()
table_names = [t[0] for t in tables]
print(f"Tables: {table_names}")

if 'bars_15min' not in table_names:
    print("[ERROR] bars_15min table not found in live DB")
    sys.exit(1)

# 最新交易日
latest_day = conn.execute("SELECT MAX(trading_day) FROM bars_15min").fetchone()[0]
if latest_day is None:
    print("[ERROR] No bars in live DB")
    sys.exit(1)

if args.date:
    target_day = pd.to_datetime(args.date).date()
else:
    # 下一个交易日 (简化: today + 1, 不做交易日历)
    target_day = latest_day + timedelta(days=1)
    # Skip weekends
    while target_day.weekday() >= 5:
        target_day += timedelta(days=1)

print(f"Latest trading day in DB: {latest_day}")
print(f"Target signal day: {target_day}")

# ── 2. 计算 IntradayMom (从 latest_day 的15min bars) ──
bars = conn.execute(f"""
    SELECT instrument_id, trading_day, bar_time,
           open/1e7 as o, close/1e7 as c
    FROM bars_15min
    WHERE trading_day = '{latest_day}'
    ORDER BY instrument_id, bar_time
""").fetchdf()

if len(bars) == 0:
    print(f"[ERROR] No bars for {latest_day}")
    sys.exit(1)

bars['bar_idx'] = bars.groupby('instrument_id').cumcount()

# IntradayMom: (close of bar #1 (09:15) - open of bar #0 (09:00)) / open
b0 = bars[bars['bar_idx'] == 0][['instrument_id', 'o']].rename(columns={'o': 'open'})
b1 = bars[bars['bar_idx'] == 1][['instrument_id', 'c']].rename(columns={'c': 'c30'})

im = b0.merge(b1, on='instrument_id', how='inner')
im['IntradayMom'] = (im['c30'] - im['open']) / im['open']
im['trading_day'] = latest_day

print(f"IntradayMom computed for {len(im)} instruments")

# ── 3. 加载历史因子值 (VWAP_Dev, Amihud, VolumeRatio) ──
try:
    panel = pd.read_parquet(FACTOR_PANEL)
    panel['trading_day'] = pd.to_datetime(panel['trading_day']).dt.date
    # Get latest_day values for all instruments
    latest_factors = panel[panel['trading_day'] == latest_day][
        ['instrument_id', 'VWAP_Dev', 'Amihud', 'VolumeRatio_20d']
    ].copy()
    print(f"Factors loaded from panel: {len(latest_factors)} instruments for {latest_day}")
except Exception as e:
    print(f"[WARN] Cannot load factor panel: {e}")
    print("  Using zeros for VWAP_Dev/Amihud/VolumeRatio (IntradayMom only)")
    latest_factors = im[['instrument_id']].copy()
    latest_factors['VWAP_Dev'] = 0
    latest_factors['Amihud'] = 0
    latest_factors['VolumeRatio_20d'] = 0

# ── 4. 合并因子 → 计算复合信号 ──
merged = im.merge(latest_factors, on='instrument_id', how='inner')
print(f"Merged: {len(merged)} instruments")

# Z-score normalize each factor (使用因子面板的历史统计)
factor_stats = {}
for fac in ['IntradayMom', 'VWAP_Dev', 'Amihud', 'VolumeRatio_20d']:
    if fac in panel.columns:
        hist = panel[fac].dropna()
        factor_stats[fac] = {'mean': hist.mean(), 'std': hist.std()}
    else:
        factor_stats[fac] = {'mean': 0, 'std': 1}

for fac in ['IntradayMom', 'VWAP_Dev', 'Amihud', 'VolumeRatio_20d']:
    mu, sigma = factor_stats[fac]['mean'], factor_stats[fac]['std']
    merged[f'{fac}_z'] = (merged[fac] - mu) / (sigma if sigma > 0 else 1)

# Composite score
merged['composite'] = sum(
    IC_WEIGHTS.get(fac, 0) * merged[f'{fac}_z']
    for fac in ['IntradayMom', 'VWAP_Dev', 'Amihud', 'VolumeRatio_20d']
)

# ── 5. 横截面排名 → 生成信号 ──
merged = merged.sort_values('composite', ascending=True)  # Ascending: lowest composite = LONG
signals = []
for i, (_, row) in enumerate(merged.iterrows()):
    if i < TOP_N:
        signals.append({'trading_day': target_day, 'instrument_id': row['instrument_id'],
            'direction': 'LONG', 'score': row['composite'],
            'entry': 0, 'exit': 0})  # entry/exit will be determined at runtime
    elif i >= len(merged) - TOP_N:
        signals.append({'trading_day': target_day, 'instrument_id': row['instrument_id'],
            'direction': 'SHORT', 'score': row['composite'],
            'entry': 0, 'exit': 0})

sig_new = pd.DataFrame(signals)
sig_new['trading_day'] = pd.to_datetime(sig_new['trading_day'])
print(f"Generated {len(sig_new)} signals for {target_day}")

# ── 6. 追加到历史信号CSV ──
if os.path.exists(SIGNAL_CSV):
    sig_old = pd.read_csv(SIGNAL_CSV)
    sig_old['trading_day'] = pd.to_datetime(sig_old['trading_day'])
    # Remove existing signals for target_day (allow re-run)
    sig_old = sig_old[sig_old['trading_day'] != pd.to_datetime(target_day)]
    sig_all = pd.concat([sig_old, sig_new], ignore_index=True)
else:
    sig_all = sig_new

sig_all.to_csv(SIGNAL_CSV, index=False, encoding='utf-8-sig')
print(f"Saved {len(sig_all)} total signals to {SIGNAL_CSV}")

# ── 7. 打印今日操作建议 ──
print(f"\n{'='*60}")
print(f"Trading signals for {target_day}")
print(f"{'='*60}")
print(f"{'Direction':8s} {'Instrument':10s} {'Composite':>10s} {'IM':>10s} {'VWAP_Dev':>10s}")
print(f"{'-'*8} {'-'*10} {'-'*10} {'-'*10} {'-'*10}")
for _, s in sig_new.iterrows():
    row = merged[merged['instrument_id'] == s['instrument_id']].iloc[0]
    print(f"{s['direction']:8s} {s['instrument_id']:10s} {s['score']:10.4f} {row['IntradayMom']:10.6f} {row['VWAP_Dev']:10.6f}")

conn.close()
print(f"\nDone. Run 'dotnet run -- live' to start trading.")
