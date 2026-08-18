"""Test SHFE data download - v3 - try different APIs"""
import akshare as ak
import sys
sys.stdout.reconfigure(encoding='utf-8')

print("=== 方式1: futures_zh_daily_sina ===")
try:
    df = ak.futures_zh_daily_sina(symbol="CU0")
    print(f"CU0: {len(df)} rows")
    print(df.tail(3).to_string())
except Exception as e:
    print(f"Error: {type(e).__name__}: {str(e)[:100]}")

print("\n=== 方式2: get_futures_daily ===")
try:
    df = ak.get_futures_daily(start_date='20260601', end_date='20260610', market='SHFE')
    print(f"SHFE 2026-06: {len(df)} rows")
    print(df.head(3).to_string())
except Exception as e:
    print(f"Error: {type(e).__name__}: {str(e)[:100]}")

print("\n=== 方式3: futures_hist_em with retry ===")
import time
for attempt in range(3):
    try:
        df = ak.futures_hist_em(symbol='沪银主连', period='daily', start_date='20260101', end_date='20260610')
        print(f"沪银主连: {len(df)} rows")
        print(df.head(3).to_string())
        break
    except Exception as e:
        print(f"  Attempt {attempt+1}: {type(e).__name__}")
        if attempt < 2:
            time.sleep(2)
