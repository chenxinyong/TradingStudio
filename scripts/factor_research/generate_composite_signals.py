"""
从 ML 复合信号生成日内交易信号 CSV (供 C# IntradaySignalExecutor 使用)

用法: python scripts/factor_research/generate_composite_signals.py
输出: scripts/factor_research/output/composite_intraday_signals.csv
"""

import pandas as pd, numpy as np, duckdb, os

# Load composite signal (IC_Weighted = best model)
comp = pd.read_csv('scripts/factor_research/output/composite_signal.csv')
comp['trading_day'] = pd.to_datetime(comp['trading_day'])
print(f'Composite signals: {len(comp):,} rows, {comp.instrument_id.nunique()} instruments')

# Load 15-min bar data for entry/exit prices
conn = duckdb.connect(r'C:\Works\Datas\bars_history.duckdb', read_only=True)
instruments = comp['instrument_id'].unique().tolist()

bars_list = []
for inst in instruments[:50]:  # Top 50 most liquid
    try:
        df = conn.execute(f"""
            SELECT instrument_id, trading_day, bar_time,
                   open/1e7 as o, close/1e7 as c
            FROM bars_15min WHERE instrument_id = '{inst}'
            ORDER BY bar_time
        """).fetchdf()
        if len(df) == 0: continue
        df['bar_time'] = pd.to_datetime(df['bar_time'])
        df['trading_day'] = pd.to_datetime(df['trading_day']).dt.date
        df['bar_idx'] = df.groupby('trading_day').cumcount()
        bars_list.append(df)
    except Exception as e:
        print(f'  Skip {inst}: {e}')

bars = pd.concat(bars_list, ignore_index=True)
print(f'15min bars: {len(bars):,}, instruments: {bars.instrument_id.nunique()}')

# Entry: bar #2 (09:30) open, Exit: last bar close
entry = bars[bars['bar_idx'] == 2][['instrument_id','trading_day','o']].rename(columns={'o':'entry_price'})
exit = bars.groupby(['instrument_id','trading_day'])['c'].last().reset_index().rename(columns={'c':'exit_price'})

prices = entry.merge(exit, on=['instrument_id','trading_day'], how='inner')

# Merge composite signal + prices
comp['trading_day'] = pd.to_datetime(comp['trading_day']).dt.date
merged = comp.merge(prices, on=['instrument_id','trading_day'], how='inner')
print(f'Merged: {len(merged):,} rows')

# Cross-sectional ranking per day using IC-weighted composite
TOP_N = 5
signals = []
for d, grp in merged.groupby('trading_day'):
    grp = grp.dropna(subset=['icw_composite'])
    if len(grp) < TOP_N * 2: continue
    # icw_composite predicts next-day return. VWAP_Dev dominates with NEGATIVE IC.
    # High composite → predicted LOW return → SHORT
    # Low composite  → predicted HIGH return → LONG
    grp = grp.sort_values('icw_composite', ascending=True)  # Ascending: lowest first = LONG
    for i, (_, row) in enumerate(grp.iterrows()):
        if i < TOP_N:
            signals.append({'trading_day': d, 'instrument_id': row['instrument_id'],
                'direction': 'LONG', 'score': row['icw_composite'],
                'entry': row['entry_price'], 'exit': row['exit_price']})
        elif i >= len(grp) - TOP_N:
            signals.append({'trading_day': d, 'instrument_id': row['instrument_id'],
                'direction': 'SHORT', 'score': row['icw_composite'],
                'entry': row['entry_price'], 'exit': row['exit_price']})

sig = pd.DataFrame(signals)
sig['trading_day'] = pd.to_datetime(sig['trading_day'])

OUT = 'scripts/factor_research/output'
sig.to_csv(f'{OUT}/composite_intraday_signals.csv', index=False, encoding='utf-8-sig')
print(f'\nSignals: {len(sig)} rows, {sig.trading_day.nunique()} days')
print(f'Date range: {sig.trading_day.min().date()} ~ {sig.trading_day.max().date()}')

# Quick validation
ret = np.where(sig['direction']=='LONG', sig['exit']/sig['entry']-1, sig['entry']/sig['exit']-1)
daily = pd.DataFrame({'trading_day': sig['trading_day'], 'ret': ret}).groupby('trading_day')['ret'].mean().dropna()
sr = daily.mean()/daily.std()*np.sqrt(242) if daily.std()>0 else 0
tr = (1+daily).prod()-1
print(f'Python validation: Sharpe={sr:.2f} TotalRet={tr:.2%} WinRate={(daily>0).mean():.1%} Days={len(daily)}')
print(f'IS (<=2023): Sharpe={(daily[daily.index<="2023-12-31"].mean()/daily[daily.index<="2023-12-31"].std()*np.sqrt(242)):.2f}')
print(f'OOS (>2023):  Sharpe={(daily[daily.index>"2023-12-31"].mean()/daily[daily.index>"2023-12-31"].std()*np.sqrt(242)):.2f}')
