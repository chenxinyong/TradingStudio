"""
缠论学习与实践 — 04: 自写算法 vs czsc 参考实现 对比验证
========================================================
将 chanlun_core.py 的结果与 czsc（行业标准）进行对比。

验证目标:
  - 包含处理后K线数量一致性
  - 分型数量/位置一致性
  - 笔数量/方向/长度一致性
  - 中枢区间一致性

运行: cd /tmp && PYTHONIOENCODING=utf-8 python scripts/chanlun/04_validate_vs_czsc.py
"""
from __future__ import annotations

import sys
sys.path = [p for p in sys.path if 'ClaudeCode/czsc' not in p]

import sqlite3
from datetime import datetime

import pandas as pd

from czsc import CZSC, Freq, Direction as CzscDirection, format_standard_kline
from czsc.mock import generate_symbol_kines

# Add our scripts to path
sys.path.insert(0, r"C:\Works\ClaudeCode\TradingStudio\scripts\chanlun")
from chanlun_core import (
    Bar, process_inclusions, find_fractals, deduplicate_fractals,
    build_bis, build_zhongshus, analyze, Direction, FractalType,
    MIN_BI_LEN,
)

DB_PATH = r"C:\Works\ClaudeCode\TradingStudio\data\archive\bars_2024.db"
PRICE_SCALE = 10_000_000


def load_real_data(instrument: str, limit: int = 5000) -> list[Bar]:
    """从 TradingStudio SQLite 加载真实期货数据"""
    conn = sqlite3.connect(DB_PATH)
    query = """
        SELECT bar_time, open, high, low, close, volume
        FROM bars_1min
        WHERE instrument_id = ?
        ORDER BY bar_time ASC
        LIMIT ?
    """
    df = pd.read_sql_query(query, conn, params=(instrument, limit))
    conn.close()

    bars = []
    for _, row in df.iterrows():
        bar_time = pd.Timestamp(row["bar_time"]).to_pydatetime()
        bars.append(Bar(
            dt=bar_time,
            open=float(row["open"]) / PRICE_SCALE,
            high=float(row["high"]) / PRICE_SCALE,
            low=float(row["low"]) / PRICE_SCALE,
            close=float(row["close"]) / PRICE_SCALE,
            vol=int(row["volume"]),
        ))
    return bars


def compare_with_czsc(our_bars: list[Bar], freq_label: str) -> dict:
    """
    用 czsc 分析相同数据，返回对比指标。
    """
    # Convert our bars to czsc RawBar format
    from czsc import RawBar
    freq_map = {"1分钟": Freq.F1, "5分钟": Freq.F5, "30分钟": Freq.F30}
    freq = freq_map.get(freq_label, Freq.F1)

    czsc_bars = []
    for i, b in enumerate(our_bars):
        czsc_bars.append(RawBar(
            symbol="TEST", dt=b.dt, freq=freq, id=i,
            open=b.open, close=b.close, high=b.high, low=b.low,
            vol=b.vol, amount=0.0,
        ))

    c = CZSC(czsc_bars, max_bi_num=100)

    return {
        "bars_raw": len(c.bars_raw),
        "bars_ubi": len(c.bars_ubi),
        "fx_count": len(c.fx_list),
        "bi_count": len(c.bi_list),
        "finished_bis": len(c.finished_bis),
        "last_bi_extend": c.last_bi_extend,
    }


def analyze_our_way(bars: list[Bar]) -> dict:
    """用自写算法分析"""
    result = analyze(bars)
    return {
        "raw_bars": len(result.raw_bars),
        "std_bars": len(result.std_bars),
        "fx_count": len(result.fractals),
        "bi_count": len(result.bis),
        "zs_count": len(result.zhongshus),
        "trend": result.trend,
        "result": result,
    }


def main() -> None:
    print("=" * 70)
    print("缠论算法对比验证: 自写实现 vs czsc (行业参考)")
    print("=" * 70)

    # ── Test 1: Mock data (controlled comparison) ──
    print("\n" + "=" * 70)
    print("Test 1: 模拟数据对比 (可控条件)")
    print("=" * 70)

    df = generate_symbol_kines("000001", "30分钟", "20240101", "20240301", seed=42)
    bars = []
    for _, row in df.iterrows():
        dt = pd.Timestamp(row["dt"]).to_pydatetime()
        bars.append(Bar(
            dt=dt,
            open=float(row["open"]),
            high=float(row["high"]),
            low=float(row["low"]),
            close=float(row["close"]),
            vol=int(row.get("vol", 0)),
        ))
    print(f"  Input: {len(bars)} bars")

    czsc_result = compare_with_czsc(bars, "30分钟")
    our_result = analyze_our_way(bars)

    print(f"\n  {'指标':<25} {'czsc':>8} {'自写':>8} {'差异':>8}")
    print(f"  {'-' * 49}")
    print(f"  {'K线数(处理后)':<25} {czsc_result['bars_raw']:>8} {our_result['std_bars']:>8} {czsc_result['bars_raw'] - our_result['std_bars']:>8}")
    print(f"  {'分型数':<25} {czsc_result['fx_count']:>8} {our_result['fx_count']:>8} {czsc_result['fx_count'] - our_result['fx_count']:>8}")
    print(f"  {'笔数':<25} {czsc_result['bi_count']:>8} {our_result['bi_count']:>8} {czsc_result['bi_count'] - our_result['bi_count']:>8}")
    print(f"  {'中枢数':<25} {'N/A':>8} {our_result['zs_count']:>8} {'N/A':>8}")
    print(f"  {'走势分类':<25} {'N/A':>8} {our_result['trend']:>8} {'N/A':>8}")

    # ── Test 2: Real futures data ──
    print("\n" + "=" * 70)
    print("Test 2: 真实期货数据 (ag2412 白银, 前5000根1分钟K线)")
    print("=" * 70)

    real_bars = load_real_data("ag2412", limit=5000)
    print(f"  Input: {len(real_bars)} bars")
    print(f"  Time range: {real_bars[0].dt} → {real_bars[-1].dt}")

    real_our = analyze_our_way(real_bars)
    print(f"\n  {'指标':<25} {'自写算法':>12}")
    print(f"  {'-' * 37}")
    print(f"  {'原始K线':<25} {real_our['raw_bars']:>12}")
    print(f"  {'标准K线(去包含)':<25} {real_our['std_bars']:>12}")
    print(f"  {'分型(去重前)':<25} {len(find_fractals(real_our['result'].std_bars)):>12}")
    print(f"  {'分型(去重后)':<25} {real_our['fx_count']:>12}")
    print(f"  {'笔':<25} {real_our['bi_count']:>12}")
    print(f"  {'中枢':<25} {real_our['zs_count']:>12}")
    print(f"  {'走势分类':<25} {real_our['trend']:>12}")

    # ── Detailed BI analysis ──
    print(f"\n  === 前10笔详情 ===")
    for i, bi in enumerate(real_our['result'].bis[:10], 1):
        arrow = "UP" if bi.type == Direction.Up else "DN"
        print(f"  BI#{i} [{arrow}] len={bi.bar_count}K "
              f"power={bi.power:.2f} change={bi.change_pct:.2f}% "
              f"lo={bi.low:.2f} hi={bi.high:.2f}")

    # ── Test 3: 不同参数灵敏度 ──
    print("\n" + "=" * 70)
    print("Test 3: 参数灵敏度分析 (MIN_BI_LEN)")
    print("=" * 70)

    for min_len in [5, 7, 9, 12]:
        result = analyze(real_bars, min_bi_len=min_len)
        print(f"  MIN_BI_LEN={min_len:>2}: 分型={len(result.fractals):>4}  笔={len(result.bis):>4}  中枢={len(result.zhongshus):>3}  走势={result.trend}")

    # ── Summary ──
    print("\n" + "=" * 70)
    print("验证总结")
    print("=" * 70)

    # Check assertions
    result = real_our['result']
    checks = []
    # 包含处理
    from chanlun_core import validate_inclusions
    checks.append(("包含处理", validate_inclusions(result.raw_bars, result.std_bars)))
    # 分型
    from chanlun_core import validate_fractals
    checks.append(("分型验证", validate_fractals(result.fractals, result.std_bars)))
    # 笔
    from chanlun_core import validate_bis
    checks.append(("笔验证", validate_bis(result.bis)))

    for name, passed in checks:
        status = "PASS" if passed else "FAIL"
        print(f"  [{status}] {name}")

    print("\n" + "=" * 70)
    print("下一步:")
    print("  1. 分析差异来源（czsc 行业特定处理）")
    print("  2. 选定参数并固化算法")
    print("  3. 对应 C# 版本实现到 TradingStudio.Strategy.ChanLun")
    print("=" * 70)


if __name__ == "__main__":
    main()
