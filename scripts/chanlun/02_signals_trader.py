"""
缠论学习与实践 — 02: 信号生成 + CzscTrader 多级别联立
运行: cd /tmp && PYTHONIOENCODING=utf-8 python scripts/chanlun/02_signals_trader.py
"""
from __future__ import annotations

import sys
sys.path = [p for p in sys.path if 'ClaudeCode/czsc' not in p]

from czsc import (
    CzscTrader, CzscSignals, generate_czsc_signals,
    Freq, format_standard_kline, get_signals_config, get_signals_freqs,
)
from czsc.mock import generate_symbol_kines


def demo_signals_generation() -> None:
    """演示基础信号生成流程"""
    print("=" * 60)
    print("Part A: 单周期信号生成 (generate_czsc_signals)")
    print("=" * 60)

    # Generate 1min bars
    df = generate_symbol_kines("000001", "5分钟", "20240101", "20240201", seed=42)
    bars = format_standard_kline(df, freq=Freq.F5)
    print(f"K线: {len(bars)}根 5分钟")

    # Define signals config - using real signal names from czsc.signals
    signals_config = [
        # 缠论笔状态信号
        {"name": "czsc.signals.cxt_bi_status_V230101"},
        {"name": "czsc.signals.cxt_bi_status_V230102"},
        # 缠论中枢共振
        {"name": "czsc.signals.cxt_zhong_shu_gong_zhen_V221221"},
        # MACD 背驰信号
        {"name": "czsc.signals.tas_macd_bc_V221201"},
        # 笔结束信号
        {"name": "czsc.signals.byi_bi_end_V230106"},
        # 笔力度信号
        {"name": "czsc.signals.cxt_fx_power_V221107"},
    ]

    try:
        sigs = generate_czsc_signals(
            bars,
            signals_config=signals_config,
            symbol="000001",
        )
        print(f"生成信号数: {len(sigs)}")
        if sigs:
            # Show first few signals
            for s in sigs[:5]:
                print(f"  [{s.signal_name}] key={s.key} value={s.value} dt={s.dt}")
    except Exception as e:
        print(f"信号生成出错: {type(e).__name__}: {e}")


def demo_czsc_trader() -> None:
    """演示 CzscTrader 多级别联立分析"""
    print("\n" + "=" * 60)
    print("Part B: 多级别联立 (CzscTrader)")
    print("=" * 60)

    # CzscTrader expects multi-timeframe klines
    # We'll create 3 frequencies
    klines = {}
    for freq_name, freq_enum in [("5分钟", Freq.F5), ("30分钟", Freq.F30), ("日线", Freq.FD)]:
        df = generate_symbol_kines("000001", freq_name, "20240101", "20240301", seed=42)
        klines[freq_name] = format_standard_kline(df, freq=freq_enum)
        print(f"  {freq_name}: {len(klines[freq_name])}根K线")

    # Define signal configs per frequency
    signals_config = [
        {"name": "czsc.signals.cxt_bi_status_V230101", "freq": "5分钟"},
        {"name": "czsc.signals.tas_macd_bc_V221201", "freq": "30分钟"},
        {"name": "czsc.signals.cxt_zhong_shu_gong_zhen_V221221", "freq": "日线"},
    ]

    try:
        trader = CzscTrader(klines, signals_config=signals_config)
        print(f"\nCzscTrader 创建成功")
        print(f"  品种: {trader.symbol}")
        print(f"  周期数: {len(trader.freqs)}")
        print(f"  signals: {len(trader.signals)}")
        if trader.signals:
            for s in trader.signals[:5]:
                print(f"    [{s.signal_name}] value={s.value}")
    except Exception as e:
        print(f"CzscTrader 出错: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()


def demo_signal_config_discovery() -> None:
    """展示如何发现和选择信号函数"""
    print("\n" + "=" * 60)
    print("Part C: 信号函数分类总览")
    print("=" * 60)

    import czsc.signals as sig_mod

    # Categorize by prefix
    categories: dict[str, list[str]] = {}
    for name in dir(sig_mod):
        if name.startswith("_") or not ("_V2" in name):
            continue
        # Extract category prefix (e.g., cxt, tas, bar, byi, etc.)
        parts = name.split("_")
        cat = parts[0] if parts[0] != "update" else "update"
        if cat not in categories:
            categories[cat] = []
        categories[cat].append(name)

    for cat, names in sorted(categories.items()):
        if cat == "update":
            continue
        print(f"  [{cat}] {len(names)} signals", end="")
        # Show 3 examples
        examples = names[:3]
        print(f"  e.g. {', '.join(examples)}")

    # Focus on 缠论-relevant signals
    chanlun_cats = ["cxt", "byi", "zdy", "jcc"]
    print(f"\n  缠论相关信号 (cxt/byi/zdy/jcc):")
    for cat in chanlun_cats:
        if cat in categories:
            print(f"    [{cat}] {len(categories[cat])} 个")
            for name in sorted(categories[cat]):
                print(f"      - {name}")


def main() -> None:
    demo_signals_generation()
    demo_czsc_trader()
    demo_signal_config_discovery()

    print("\n" + "=" * 60)
    print("关键发现:")
    print("  1. generate_czsc_signals: 单周期信号批量生成")
    print("  2. CzscTrader: 多周期联立 + 信号叠加")
    print("  3. 244个信号函数, 按领域分组 (cxt=缠论上下文, tas=技术指标, bar=K线形态)")
    print("  4. 信号配置可指定 freq 字段绑定到特定周期")
    print("=" * 60)


if __name__ == "__main__":
    main()
