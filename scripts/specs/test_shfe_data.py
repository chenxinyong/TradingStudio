"""Test fetching SHFE futures data via AKShare"""
import akshare as ak
import pandas as pd

# Test 1: Get the exchange symbol mapping table
print("=== 交易所品种对照表 ===")
try:
    df_map = ak.futures_hist_table_em()
    print(df_map.head(20).to_string())
except Exception as e:
    print(f"Error: {e}")

# Test 2: Try getting SHFE daily data with Chinese names
print("\n=== 测试获取铜主连数据 ===")
test_symbols = ['铜主连', '沪铜主连', 'CU主连', '铜2508']
for sym in test_symbols:
    try:
        df = ak.futures_hist_em(symbol=sym, period='daily', start_date='20260601', end_date='20260610')
        print(f"  {sym}: OK, {len(df)} rows, cols={list(df.columns)[:5]}")
    except Exception as e:
        print(f"  {sym}: {type(e).__name__}: {str(e)[:100]}")
