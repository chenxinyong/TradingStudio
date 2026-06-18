"""
缠论学习与实践 — 03: TradingStudio 真实期货 K 线 → czsc 分析
将 SQLite 中的 Bar 数据转换为 czsc 的 RawBar 格式，运行缠论分析。
运行: cd /tmp && PYTHONIOENCODING=utf-8 python scripts/chanlun/03_bridge_tradingstudio.py
"""
from __future__ import annotations

import sys
sys.path = [p for p in sys.path if 'ClaudeCode/czsc' not in p]

import sqlite3
from datetime import datetime
from typing import List

from czsc import CZSC, Freq, format_standard_kline, ZS, Direction
import pandas as pd

# ── Database path ──────────────────────────────────────────────
DB_PATH = r"C:\Works\ClaudeCode\TradingStudio\data\archive\bars_2024.db"
PRICE_SCALE = 10_000_000  # 价格以 ×10^7 整数存储


def load_bars_1min(instrument: str, start_date: str = "2024-01-01") -> pd.DataFrame:
    """从 SQLite 加载指定品种的 1 分钟 K 线"""
    conn = sqlite3.connect(DB_PATH)
    query = """
        SELECT instrument_id, bar_time, open, high, low, close, volume, open_interest
        FROM bars_1min
        WHERE instrument_id = ? AND bar_time >= ?
        ORDER BY bar_time ASC
    """
    df = pd.read_sql_query(query, conn, params=(instrument, start_date))
    conn.close()

    # 价格从 ×10^7 整数 → float
    for col in ["open", "high", "low", "close"]:
        df[col] = df[col].astype(float) / PRICE_SCALE

    print(f"[load] {instrument}: {len(df)} bars from {df['bar_time'].min()} to {df['bar_time'].max()}")
    return df


def convert_to_rawbars(df: pd.DataFrame) -> list:
    """
    将 TradingStudio Bar DataFrame 转为 czsc RawBar 列表。
    czsc RawBar 需要: symbol, dt, freq, id, open, close, high, low, vol, amount
    """
    from czsc import RawBar

    bars = []
    for i, row in df.iterrows():
        bar_time = pd.Timestamp(row["bar_time"]).to_pydatetime()
        bar = RawBar(
            symbol=row["instrument_id"],
            dt=bar_time,
            freq=Freq.F1,
            id=i,
            open=float(row["open"]),
            close=float(row["close"]),
            high=float(row["high"]),
            low=float(row["low"]),
            vol=int(row.get("volume", 0)),
            amount=0.0,  # SQLite 中无 amount 字段，用 0
        )
        bars.append(bar)
    return bars


def analyze_with_czsc(bars: list, symbol: str) -> CZSC:
    """用 czsc 分析 K 线序列"""
    print(f"\n[czsc] Analyzing {len(bars)} bars for {symbol}...")
    c = CZSC(bars, max_bi_num=100)  # 增大 max_bi_num 以适应长序列
    print(f"  bars_raw={len(c.bars_raw)}, bars_ubi={len(c.bars_ubi)}")
    print(f"  fx_list={len(c.fx_list)}, bi_list={len(c.bi_list)}")
    print(f"  finished_bis={len(c.finished_bis)}, last_bi_extend={c.last_bi_extend}")
    return c


def show_bi_statistics(c: CZSC) -> None:
    """统计笔的力度分布"""
    print(f"\n=== 笔力度统计 ({len(c.bi_list)}笔) ===")

    up_bis = [bi for bi in c.bi_list if bi.direction == Direction.Up]
    down_bis = [bi for bi in c.bi_list if bi.direction == Direction.Down]

    for label, bis in [("向上笔", up_bis), ("向下笔", down_bis)]:
        if not bis:
            continue
        powers = [bi.power for bi in bis]
        changes = [bi.change * 100 for bi in bis]  # to %
        lengths = [bi.length for bi in bis]
        snrs = [bi.SNR for bi in bis]

        print(f"\n  [{label}] 共 {len(bis)} 笔:")
        print(f"    力度(power):   min={min(powers):.2f}  avg={sum(powers)/len(powers):.2f}  max={max(powers):.2f}")
        print(f"    涨跌幅(%):      min={min(changes):.2f}  avg={sum(changes)/len(changes):.2f}  max={max(changes):.2f}")
        print(f"    K线数(length): min={min(lengths):.0f}  avg={sum(lengths)/len(lengths):.1f}  max={max(lengths):.0f}")
        print(f"    信噪比(SNR):   min={min(snrs):.3f}  avg={sum(snrs)/len(snrs):.3f}  max={max(snrs):.3f}")


def show_zs_analysis(c: CZSC) -> None:
    """中枢分析：从笔列表中找到有效中枢"""
    print(f"\n=== 中枢 (ZS) 扫描 ===")
    valid_zs_count = 0
    zs_list = []

    # 滑动窗口搜索有效中枢
    for window in range(5, min(20, len(c.bi_list) + 1)):
        for start in range(len(c.bi_list) - window + 1):
            bis = c.bi_list[start:start + window]
            try:
                zs = ZS(bis)
                if zs.is_valid():
                    valid_zs_count += 1
                    zs_list.append({
                        "start_dt": zs.sdt,
                        "end_dt": zs.edt,
                        "zg": zs.zg,
                        "zd": zs.zd,
                        "zz": zs.zz,
                        "bis": window,
                    })
            except Exception:
                pass

    print(f"  找到 {valid_zs_count} 个有效中枢 (滑动窗口扫描)")

    if zs_list:
        # 只保留不重叠的 (简化处理：按时间排序取第一个，跳过重叠的)
        zs_list.sort(key=lambda z: z["start_dt"])
        unique_zs = []
        last_end = None
        for z in zs_list:
            if last_end is None or z["start_dt"] >= last_end:
                unique_zs.append(z)
                last_end = z["end_dt"]

        print(f"  去重后: {len(unique_zs)} 个")
        print(f"\n  前5个中枢:")
        for i, z in enumerate(unique_zs[:5], 1):
            print(f"    ZS#{i}: [{z['zd']:.2f}, {z['zg']:.2f}] 中轴={z['zz']:.2f}")
            print(f"           {z['start_dt']} → {z['end_dt']}  (含{z['bis']}笔)")


def main() -> None:
    # 选择分析品种
    # ag2412 = 白银2412 (上期所) — 波动性好的贵金属
    # SA501 = 纯碱2501 (郑商所) — 高活跃度化工品
    instrument = "ag2412"

    print("=" * 60)
    print(f"TradingStudio → czsc 桥接分析: {instrument}")
    print("=" * 60)

    # 1. Load data
    df = load_bars_1min(instrument, start_date="2024-01-01")

    # 2. Convert to RawBar (use 1min bars directly)
    bars = convert_to_rawbars(df)

    # 3. Analyze with czsc
    c = analyze_with_czsc(bars, instrument)

    # 4. Statistics
    show_bi_statistics(c)
    show_zs_analysis(c)

    # 5. Show last few bis with their metrics
    print(f"\n=== 最近5笔 ===")
    for bi in c.bi_list[-5:]:
        arrow = "UP" if bi.direction == Direction.Up else "DN"
        print(f"  [{arrow}] {str(bi.sdt)[:19]} -> {str(bi.edt)[:19]}")
        print(f"        power={bi.power:.2f}  len={bi.length}  SNR={bi.SNR:.3f}  change={bi.change:.3%}")

    print("\n✅ Bridge analysis complete!")


if __name__ == "__main__":
    main()
