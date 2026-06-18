"""
缠论学习与实践 — 01: czsc 核心 API 探索
运行: python scripts/chanlun/01_czsc_explore.py
"""
from __future__ import annotations

import sys
# Ensure installed czsc is used, not local clone
sys.path = [p for p in sys.path if 'ClaudeCode/czsc' not in p]

import czsc
from czsc import CZSC, Freq, format_standard_kline, ZS, Direction
from czsc.mock import generate_symbol_kines


def main() -> None:
    print("=" * 60)
    print(f"czsc {czsc.__version__} @ {czsc.__date__}")
    print(f"min_bi_len={czsc.envs.get_min_bi_len()}, max_bi_num={czsc.envs.get_max_bi_num()}")
    print("=" * 60)

    # Generate mock data
    df = generate_symbol_kines("000001", "30分钟", "20210101", "20240101", seed=42)
    bars = format_standard_kline(df, freq=Freq.F30)
    c = CZSC(bars)

    print(f"\n[CZSC] {c.symbol} {c.freq}")
    print(f"  bars_raw={len(c.bars_raw)}, bars_ubi={len(c.bars_ubi)}")
    print(f"  fx_list={len(c.fx_list)}, bi_list={len(c.bi_list)}")
    print(f"  finished_bis={len(c.finished_bis)}, last_bi_extend={c.last_bi_extend}")

    # Show FX (顶底分型) — fx.mark 包含 "顶分型"/"底分型" 文本
    print(f"\n=== 前5个分型 (FX) ===")
    for i, fx in enumerate(c.fx_list[:5], 1):
        print(f"  FX#{i} [{fx.mark}] dt={fx.dt} price={fx.fx:.3f} power={fx.power_str}")

    # Show BI (笔)
    print(f"\n=== 前5笔 (BI) ===")
    for i, bi in enumerate(c.bi_list[:5], 1):
        arrow = "UP" if bi.direction == Direction.Up else "DOWN"
        print(f"  BI#{i} [{arrow}] {str(bi.sdt)[:19]} -> {str(bi.edt)[:19]}")
        print(f"        power={bi.power:.3f}  change={bi.change:.3%}  len={bi.length}")
        print(f"        SNR={bi.SNR:.3f}  rsq={bi.rsq:.3f}  angle={bi.angle:.1f}deg")

    # Try constructing 中枢 from recent bis
    print(f"\n=== 中枢分析 ===")
    for window in [len(c.bi_list), 12, 8, 5]:
        bis = c.bi_list[-window:]
        try:
            zs = ZS(bis)
            print(f"  ZS(last {window} bis): valid={zs.is_valid()}", end="")
            if zs.is_valid():
                print(f" 区间=[{zs.zd:.3f}, {zs.zg:.3f}] 中轴={zs.zz:.3f} 极值=[{zs.dd:.3f}, {zs.gg:.3f}]")
            else:
                print()
        except Exception as e:
            print(f"  ZS(last {window} bis): Error={e}")

    # 未完成笔
    ubi = c.ubi
    print(f"\n=== 未完成笔 (UBI) ===")
    if ubi:
        print(f"  direction={ubi.get('direction')}")
        print(f"  high={ubi.get('high'):.3f}, low={ubi.get('low'):.3f}")

    print("\n=== 关键发现 ===")
    print(f"  1. CZSC 自动完成包含处理 -> 分型识别 -> 笔识别")
    print(f"  2. {len(c.bars_raw)}根K线 -> {len(c.fx_list)}个分型 -> {len(c.bi_list)}笔")
    print(f"  3. 每笔含力度(power/SNR/angle)可做信号量化")
    print(f"  4. 中枢从笔列表构造，需3+笔重叠区间")
    print(f"  5. update()方法支持增量推进，适合实盘场景")

    print("\n✅ CZSC core API exploration complete!")


if __name__ == "__main__":
    main()
