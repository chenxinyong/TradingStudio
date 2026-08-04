"""
Factor Mining Phase 1: 因子计算 (Feature Engineering)
基于6.5年 bars_1min + bars_day 数据，计算15个因子，构建面板数据。

用法: cd TradingStudio && python scripts/factor_research/factor_compute.py

输出: scripts/factor_research/output/factors_panel_v1.parquet
      同时写入 DuckDB 表 factor_panel_v1
"""

import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import duckdb
import numpy as np
import pandas as pd
from datetime import datetime
import warnings
warnings.filterwarnings('ignore')

# ── 配置 ──────────────────────────────────────────

DB_PATH = r"C:\Works\Datas\bars_history.duckdb"
OUTPUT_DIR = "scripts/factor_research/output"
MIN_DAY_BARS = 252          # 最少1年日线
PRICE_SCALE = 10_000_000    # Bar OHLC 价格缩放因子
IS_START = "2020-01-01"
IS_END   = "2023-12-31"
OOS_START = "2024-01-01"
OOS_END   = "2026-06-30"

os.makedirs(OUTPUT_DIR, exist_ok=True)

if not os.path.exists(DB_PATH):
    print(f"[ERROR] Database not found: {DB_PATH}")
    sys.exit(1)

conn = duckdb.connect(DB_PATH, read_only=True)
print(f"DuckDB 已连接: {DB_PATH}")

# ── 1. 发现连续合约 ───────────────────────────────

instruments = conn.execute("""
    SELECT instrument_id, COUNT(*) AS bar_count,
           MIN(bar_time) AS start_date, MAX(bar_time) AS end_date
    FROM bars_day
    WHERE instrument_id LIKE '%000'
    GROUP BY instrument_id
    HAVING COUNT(*) >= ?
    ORDER BY bar_count DESC
""", [MIN_DAY_BARS]).fetchall()

print(f"\n找到 {len(instruments)} 个连续合约 (日线 >={MIN_DAY_BARS} 根):")
for row in instruments[:10]:
    print(f"  {row[0]:10s}  {row[1]:6d} bars  {str(row[2])[:10]} ~ {str(row[3])[:10]}")
if len(instruments) > 10:
    print(f"  ... 共 {len(instruments)} 个")

INST_IDS = [r[0] for r in instruments]

# ── 2. 加载全品种日线数据 ──────────────────────────

print("\n[1/3] 加载 bars_day 全品种数据...")
day_sql = f"""
    SELECT instrument_id, trading_day, bar_time,
           open/{PRICE_SCALE}.0 AS open, high/{PRICE_SCALE}.0 AS high,
           low/{PRICE_SCALE}.0 AS low, close/{PRICE_SCALE}.0 AS close,
           volume, turnover, open_interest, tick_count
    FROM bars_day
    WHERE instrument_id IN ({','.join(['?']*len(INST_IDS))})
      AND bar_time >= '2019-06-01'  -- 额外6个月用于滚动窗口预热
    ORDER BY instrument_id, bar_time
"""
day_raw = conn.execute(day_sql, INST_IDS).fetchdf()
day_raw['bar_time'] = pd.to_datetime(day_raw['bar_time'])
day_raw['trading_day'] = pd.to_datetime(day_raw['trading_day']).dt.date
print(f"  加载 {len(day_raw):,} 行, {day_raw['instrument_id'].nunique()} 品种")
print(f"  日期范围: {day_raw['bar_time'].min()} ~ {day_raw['bar_time'].max()}")

# ── 3. 计算日频因子 ────────────────────────────────

print("\n[2/3] 计算日频因子...")

def compute_day_factors(df: pd.DataFrame) -> pd.DataFrame:
    """对单个品种的日线数据计算因子"""
    df = df.sort_values('bar_time').copy()

    # 基础序列
    close = df['close'].values.astype(np.float64)
    high  = df['high'].values.astype(np.float64)
    low   = df['low'].values.astype(np.float64)
    open_ = df['open'].values.astype(np.float64)
    vol   = df['volume'].values.astype(np.float64)
    turnover = df['turnover'].values.astype(np.float64)
    oi    = df['open_interest'].values.astype(np.float64)

    n = len(df)
    factors = {}

    # ─ TSMOM (Time Series Momentum) ─
    for lookback in [20, 60]:
        ret = np.full(n, np.nan)
        ret[lookback:] = close[lookback:] / close[:-lookback] - 1.0
        factors[f'TSMOM_{lookback}d'] = ret

    # ─ MA Deviation ─
    for period in [20, 50]:
        sma = pd.Series(close).rolling(period, min_periods=period).mean().values
        factors[f'MA_Dev_{period}d'] = close / sma - 1.0

    # ─ Historical Volatility (20d, annualized) ─
    log_ret = np.full(n, np.nan)
    log_ret[1:] = np.log(close[1:] / close[:-1])
    hv = pd.Series(log_ret).rolling(20, min_periods=20).std().values * np.sqrt(252)
    factors['HV_20d'] = hv

    # ─ ATR Ratio (14d) ─
    tr = np.maximum(high - low,
                    np.maximum(np.abs(high - np.roll(close, 1)),
                               np.abs(low - np.roll(close, 1))))
    tr[0] = high[0] - low[0]
    atr = pd.Series(tr).rolling(14, min_periods=14).mean().values
    factors['AtrRatio_14d'] = atr / close

    # ─ Amihud Illiquidity ─
    abs_ret = np.abs(np.full(n, np.nan))
    abs_ret[1:] = np.abs(close[1:] / close[:-1] - 1.0)
    ami = abs_ret / (turnover + 1e-12)
    factors['Amihud'] = ami

    # ─ Overnight Gap ─
    gap = np.full(n, np.nan)
    gap[1:] = (open_[1:] - close[:-1]) / close[:-1]
    factors['OvernightGap'] = gap

    # ─ Volume Ratio (20d) ─
    vol_sma = pd.Series(vol).rolling(20, min_periods=20).mean().values
    factors['VolumeRatio_20d'] = vol / (vol_sma + 1e-12)

    # ─ OI Change Rate ─
    oi_chg = np.full(n, np.nan)
    oi_chg[1:] = (oi[1:] - oi[:-1]) / (np.abs(oi[:-1]) + 1e-12)
    factors['OI_Change'] = oi_chg

    # ─ N-day High/Low (20d) ─
    hh = pd.Series(high).rolling(20, min_periods=20).max().values
    ll = pd.Series(low).rolling(20, min_periods=20).min().values
    rng = hh - ll
    factors['NdayHigh_20d'] = (close - hh) / (rng + 1e-12)

    # ─ Vol Regime: HV_20d / HV_60d_20pct ─
    hv_60_roll = pd.Series(hv).rolling(60, min_periods=60)
    hv_p20 = hv_60_roll.quantile(0.20).values
    factors['VolRegime'] = hv / (hv_p20 + 1e-12)

    # ─ Daily Return (for forward return / IC target) ─
    fwd_ret = np.full(n, np.nan)
    fwd_ret[:-1] = close[1:] / close[:-1] - 1.0
    factors['FwdRet_1d'] = fwd_ret

    # Build output
    out = df[['instrument_id', 'trading_day', 'bar_time']].copy()
    for name, values in factors.items():
        out[name] = values
    return out

# 按品种分组计算
day_factors_list = []
for i, inst in enumerate(INST_IDS):
    df_inst = day_raw[day_raw['instrument_id'] == inst]
    if len(df_inst) < MIN_DAY_BARS:
        continue
    try:
        fac = compute_day_factors(df_inst)
        day_factors_list.append(fac)
    except Exception as e:
        print(f"  [WARN] {inst}: {e}")

    if (i + 1) % 20 == 0:
        print(f"  进度: {i+1}/{len(INST_IDS)}")

day_panel = pd.concat(day_factors_list, ignore_index=True)
print(f"  日频因子完成: {len(day_panel):,} 行, {day_panel['instrument_id'].nunique()} 品种")

# ── 4. 计算 1min 微观结构因子 ──────────────────────

print("\n[3/3] 从 bars_1min 计算微观结构因子 (逐品种, 内存受控)...")

def compute_1min_factors(inst_id: str) -> pd.DataFrame:
    """对单个品种的1min数据做日频聚合, 计算微观结构因子"""
    sql = f"""
        SELECT instrument_id, trading_day, bar_time,
               open/{PRICE_SCALE}.0 AS open, high/{PRICE_SCALE}.0 AS high,
               low/{PRICE_SCALE}.0 AS low, close/{PRICE_SCALE}.0 AS close,
               volume, turnover, open_interest, tick_count
        FROM bars_1min
        WHERE instrument_id = ?
          AND bar_time >= '2019-06-01'
        ORDER BY bar_time
    """
    df = conn.execute(sql, [inst_id]).fetchdf()
    if len(df) == 0:
        return pd.DataFrame()

    df['bar_time'] = pd.to_datetime(df['bar_time'])
    df['trading_day'] = pd.to_datetime(df['trading_day']).dt.date
    df['date'] = df['bar_time'].dt.date

    # 1min log returns (within same day, no overnight)
    df['log_ret'] = np.log(df['close'] / df['close'].shift(1))
    # Mask overnight gaps (first bar of each day)
    df.loc[df['date'] != df['date'].shift(1), 'log_ret'] = np.nan

    # Daily aggregation
    daily = df.groupby(['instrument_id', 'trading_day', 'date'], sort=False).agg(
        open_daily   = ('open', 'first'),
        high_daily   = ('high', 'max'),
        low_daily    = ('low', 'min'),
        close_daily  = ('close', 'last'),
        volume_daily = ('volume', 'sum'),
        turnover_daily = ('turnover', 'sum'),
        oi_daily     = ('open_interest', 'last'),
        tick_count_daily = ('tick_count', 'sum'),
        bar_count    = ('close', 'count'),
        # RV: sum of squared 1min log returns
        sum_sq_ret   = ('log_ret', lambda x: np.nansum(x**2)),
        # VWAP components
        vwap_num     = ('close', lambda x: np.nansum(x * df.loc[x.index, 'volume'])),
        # intraday momentum: close of first ~30min (bar #30) vs open
        close_30m    = ('close', lambda x: x.iloc[min(29, len(x)-1)] if len(x) > 0 else np.nan),
    ).reset_index()

    # 价格: 除以PRICE_SCALE已经在SQL中完成

    # ── RV (Realized Volatility, 5d) ──
    daily['RV_daily'] = np.sqrt(daily['sum_sq_ret'] * 252)
    daily['RV_5d'] = daily.groupby('instrument_id')['RV_daily'].transform(
        lambda x: x.rolling(5, min_periods=5).mean()
    )

    # ── Intraday Momentum ──
    daily['IntradayMom'] = (daily['close_30m'] - daily['open_daily']) / (daily['open_daily'] + 1e-12)

    # ── VWAP Deviation ──
    daily['VWAP'] = daily['vwap_num'] / (daily['volume_daily'] + 1e-12)
    daily['VWAP_Dev'] = (daily['close_daily'] - daily['VWAP']) / (daily['VWAP'] + 1e-12)

    # ── Amplitude ──
    daily['Amplitude'] = (daily['high_daily'] - daily['low_daily']) / (daily['open_daily'] + 1e-12)

    # Select and rename
    out = daily[['instrument_id', 'trading_day', 'date',
                 'RV_5d', 'IntradayMom', 'VWAP_Dev', 'Amplitude']].copy()
    out.rename(columns={'date': 'bar_date'}, inplace=True)
    return out

# 逐品种处理
micro_list = []
for i, inst in enumerate(INST_IDS):
    try:
        mf = compute_1min_factors(inst)
        if len(mf) > 0:
            micro_list.append(mf)
    except Exception as e:
        print(f"  [WARN] {inst} 1min: {e}")

    if (i + 1) % 5 == 0:
        print(f"  进度: {i+1}/{len(INST_IDS)}  ({len(micro_list)} 成功)")

micro_panel = pd.concat(micro_list, ignore_index=True)
print(f"  微观结构因子完成: {len(micro_panel):,} 行, {micro_panel['instrument_id'].nunique()} 品种")

# ── 5. 合并日频 + 微观因子 ─────────────────────────

print("\n合并因子面板...")
# 统一 key: (instrument_id, trading_day)
day_panel['trading_day'] = pd.to_datetime(day_panel['trading_day']).dt.date
micro_panel['trading_day'] = pd.to_datetime(micro_panel['trading_day']).dt.date

panel = day_panel.merge(
    micro_panel[['instrument_id', 'trading_day', 'RV_5d', 'IntradayMom', 'VWAP_Dev', 'Amplitude']],
    on=['instrument_id', 'trading_day'], how='left'
)

# ── 6. 标注 IS/OOS ──────────────────────────────────

panel['period'] = 'OOS'
panel.loc[pd.to_datetime(panel['trading_day']) <= pd.to_datetime(IS_END), 'period'] = 'IS'

# 转为 pandas Timestamp 方便后续处理
panel['trading_day'] = pd.to_datetime(panel['trading_day'])

# ── 7. 检查与统计 ──────────────────────────────────

factor_cols = [c for c in panel.columns if c not in
    ['instrument_id', 'trading_day', 'bar_time', 'period', 'FwdRet_1d']]

print(f"\n面板统计:")
print(f"  总行数:     {len(panel):,}")
print(f"  品种数:     {panel['instrument_id'].nunique()}")
print(f"  日期范围:   {panel['trading_day'].min().date()} ~ {panel['trading_day'].max().date()}")
print(f"  IS 行数:    {(panel['period']=='IS').sum():,}")
print(f"  OOS 行数:   {(panel['period']=='OOS').sum():,}")
print(f"  因子列数:   {len(factor_cols)}")
print(f"  因子清单:   {factor_cols}")

# NaN 覆盖率
print(f"\n因子 NaN 占比 (全样本):")
for c in factor_cols:
    nan_pct = panel[c].isna().mean() * 100
    bar = '█' * int(nan_pct / 5) + '░' * (20 - int(nan_pct / 5))
    print(f"  {c:20s}  {nan_pct:5.1f}%  {bar}")

# ── 8. 保存 ────────────────────────────────────────

# 优先 Parquet, 失败则 CSV
parquet_path = os.path.join(OUTPUT_DIR, "factors_panel_v1.parquet")
csv_path = os.path.join(OUTPUT_DIR, "factors_panel_v1.csv")
try:
    panel.to_parquet(parquet_path, index=False)
    print(f"\n[SAVED] {parquet_path}")
    print(f"  文件大小: {os.path.getsize(parquet_path)/1024/1024:.1f} MB")
except Exception as e:
    print(f"\n[WARN] Parquet 写入失败 ({e}), 改用 CSV...")
    panel.to_csv(csv_path, index=False, encoding='utf-8-sig')
    print(f"[SAVED] {csv_path}")
    print(f"  文件大小: {os.path.getsize(csv_path)/1024/1024:.1f} MB")

# 也写入 DuckDB (创建副本)
try:
    db_out = os.path.join(OUTPUT_DIR, "factors.duckdb")
    wconn = duckdb.connect(db_out)
    wconn.execute("DROP TABLE IF EXISTS factor_panel_v1")
    wconn.execute("CREATE TABLE factor_panel_v1 AS SELECT * FROM panel")
    wconn.close()
    print(f"[SAVED] DuckDB: {db_out}")
except Exception as e:
    print(f"[WARN] DuckDB 写入失败: {e}")

# ── 9. 基础摘要 ────────────────────────────────────

print(f"\n{'='*70}")
print(f"因子计算完成!")
print(f"{'='*70}")
print(f"\n因子列表 ({len(factor_cols)} 个):")
print(f"  T1 (趋势/波动率): TSMOM_20d, MA_Dev_50d, HV_20d")
print(f"  T2 (微观结构+风险): RV_5d, IntradayMom, Amihud, VWAP_Dev, AtrRatio_14d")
print(f"  T3 (辅助): TSMOM_60d, MA_Dev_20d, Amplitude, OvernightGap, VolumeRatio_20d, OI_Change, NdayHigh_20d, VolRegime")
print(f"\n下一步: python scripts/factor_research/factor_preprocess.py")
conn.close()
