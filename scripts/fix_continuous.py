"""
修复单字母品种连续合约数据 — LIKE 'c%' → regex '^c[0-9]' 避免前缀冲突
问题: C(玉米~2400)混入CU(铜~78000) → 加权价格膨胀30倍
用法: python fix_continuous.py [品种...]  默认修复 c, p, l, a, b, j, m
"""
import duckdb, sys, os, time
from pathlib import Path

REPO = Path('c:/Works/ClaudeCode/TradingStudio')
SRC_DB = REPO / 'data' / 'bars_history.duckdb'
CONTINUOUS_DIR = REPO / 'data' / 'continuous'

# 有前缀冲突的单字母品种 (LIKE 'x%' 会匹配到其他品种)
CONFLICT_PRODUCTS = {
    'c': ['cf', 'cs', 'cu'],
    'p': ['pb', 'pd', 'pg', 'pp', 'ps', 'pt'],
    'l': ['lc', 'lg', 'lh', 'lu'],
    'a': ['ad', 'ag', 'al', 'ao', 'au'],
    'b': ['bb', 'bc', 'br', 'bu', 'bz'],
    'j': ['jd', 'jm'],
    'm': ['ma'],
}

# 用户可指定要修的品种
if len(sys.argv) > 1:
    products = [p for p in sys.argv[1:] if p in CONFLICT_PRODUCTS]
else:
    products = ['c', 'p', 'l']  # 默认修最严重的三个

print(f"Fixing: {products}")
print(f"Source: {SRC_DB}")
print(f"Output: {CONTINUOUS_DIR}")

src = duckdb.connect(str(SRC_DB), read_only=True)

for prod in products:
    out_path = CONTINUOUS_DIR / f"{prod}_continuous.duckdb"
    if not out_path.exists():
        print(f"  {prod}: SKIP (no continuous file)")
        continue

    t0 = time.time()

    # 正确的正则：只匹配产品代码后跟数字的合约
    # 例如: ^c[0-9] 匹配 c2405, 不匹配 cf2405, cu2405

    # 重建 continuous_1min
    dst = duckdb.connect(str(out_path))

    try:
        # 用正确正则重建
        for freq, src_table in [('1min', 'bars_1min'), ('5min', 'bars_5min'),
                                 ('15min', 'bars_15min'), ('day', 'bars_day'),
                                 ('week', 'bars_week')]:
            dst_table = f'continuous_{freq}'

            # 检查源表
            tables = [t[0] for t in dst.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='main'").fetchall()]
            if dst_table not in tables:
                print(f"    {dst_table}: SKIP (not in DB)")
                continue

            # 检查源数据
            cnt = src.execute(f"""
                SELECT COUNT(*) FROM {src_table}
                WHERE regexp_matches(instrument_id, '^{prod}[0-9]') AND volume > 0
            """).fetchone()[0]
            if cnt == 0:
                print(f"    {dst_table}: SKIP (no data, regex matched 0)")
                continue

            # 读取正确数据 — 排除已迁移的连续合约({prod}000)和000系列
            df = src.execute(f"""
                SELECT
                    bar_time,
                    bar_time::DATE as trading_day,
                    SUM(open * volume) / NULLIF(SUM(volume), 0) as open,
                    MAX(high) as high,
                    MIN(low) as low,
                    SUM(close * volume) / NULLIF(SUM(volume), 0) as close,
                    SUM(volume) as volume,
                    SUM(CASE WHEN volume > 0 THEN volume ELSE 0 END) as turnover,
                    SUM(CASE WHEN volume > 0 THEN volume ELSE 0 END) as open_interest,
                    COUNT(DISTINCT instrument_id) as active_contracts,
                    FIRST(instrument_id ORDER BY volume DESC) as dominant_contract
                FROM {src_table}
                WHERE regexp_matches(instrument_id, '^{prod}[0-9]')
                  AND instrument_id != '{prod}000'
                  AND volume > 0
                GROUP BY bar_time
                ORDER BY bar_time
            """).df()

            if len(df) == 0:
                print(f"    {dst_table}: SKIP (empty df)")
                continue

            # Transform: DOUBLE → BIGINT (×10⁷), datetime → string
            df['bar_time'] = df['bar_time'].astype(str)
            df['trading_day'] = df['trading_day'].astype(str)
            for col in ['open', 'high', 'low', 'close']:
                df[col] = df[col].round().astype('int64')
            df['volume'] = df['volume'].fillna(0).astype('int64')
            df['turnover'] = df['turnover'].fillna(0.0)
            df['open_interest'] = df['open_interest'].fillna(0.0)
            df['active_contracts'] = df['active_contracts'].fillna(0).astype('int32')
            df['dominant_contract'] = df['dominant_contract'].fillna(f'{prod}000')
            df['tick_count'] = 0

            # Select exactly 12 columns matching table schema
            df = df[['bar_time', 'trading_day', 'open', 'high', 'low', 'close',
                     'volume', 'turnover', 'open_interest', 'active_contracts',
                     'dominant_contract', 'tick_count']]

            # Drop old table and recreate
            dst.execute(f"DROP TABLE IF EXISTS {dst_table}")
            dst.execute(f"""
                CREATE TABLE {dst_table} (
                    bar_time VARCHAR NOT NULL,
                    trading_day VARCHAR NOT NULL,
                    open BIGINT NOT NULL,
                    high BIGINT NOT NULL,
                    low BIGINT NOT NULL,
                    close BIGINT NOT NULL,
                    volume BIGINT NOT NULL,
                    turnover DOUBLE,
                    open_interest DOUBLE,
                    active_contracts INTEGER,
                    dominant_contract VARCHAR,
                    tick_count INTEGER DEFAULT 0,
                    PRIMARY KEY (bar_time)
                )
            """)

            dst.append(dst_table, df)
            print(f"    {dst_table}: {len(df):,} rows")

        dst.close()

        # 验证
        verify = duckdb.connect(str(out_path), read_only=True)
        r = verify.execute(
            "SELECT MIN(close)/1e7, MAX(close)/1e7, AVG(close)/1e7, COUNT(*) FROM continuous_1min"
        ).fetchone()
        print(f"  {prod}: DONE in {time.time()-t0:.0f}s — price {r[0]:.0f}-{r[1]:.0f} avg={r[2]:.0f} bars={r[3]:,}")
        verify.close()

    except Exception as e:
        print(f"  {prod}: ERROR - {e}")
        try: dst.close()
        except: pass

src.close()
print("\nDone. Re-run batch backtest for fixed products to verify.")
