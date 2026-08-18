"""Test fetching SHFE futures data via AKShare - v2"""
import akshare as ak
import pandas as pd
import sys
sys.stdout.reconfigure(encoding='utf-8')

# Test 1: Get the exchange symbol mapping, filter SHFE only
print("=== SHFE (上期所) 品种列表 ===")
try:
    df_map = ak.futures_hist_table_em()
    # Filter for 上期所
    shfe = df_map[df_map['市场简称'] == '上期所']
    print(f"共 {len(shfe)} 个合约")
    # Show unique product names
    names = shfe['合约中文代码'].unique()
    print(f"品种简称: {sorted(names)[:30]}")

    # Test with the exact names from the table
    print("\n=== 测试几个关键品种 ===")
    test_codes = ['沪铜主连', '沪铝主连', '沪锌主连', '沪银主连', '沪金主连', '沪镍主连']
    for code in test_codes:
        try:
            df = ak.futures_hist_em(symbol=code, period='daily', start_date='20260601', end_date='20260610')
            print(f"  {code}: OK, {len(df)} rows")
        except Exception as e:
            print(f"  {code}: {type(e).__name__}: {str(e)[:80]}")
except Exception as e:
    print(f"Error: {type(e).__name__}: {e}")
