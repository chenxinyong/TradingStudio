"""
缠论学习与实践 — 05: 可视化 (K线 + 分型 + 笔 + 中枢)
=====================================================
用 plotly 输出交互式 HTML 图表，直观展示缠论分析结果。

运行: cd /tmp && PYTHONIOENCODING=utf-8 python scripts/chanlun/05_visualize.py
输出: scripts/chanlun/output/chanlun_chart.html
"""
from __future__ import annotations

import sys
import os
sys.path = [p for p in sys.path if 'ClaudeCode/czsc' not in p]
sys.path.insert(0, r"C:\Works\ClaudeCode\TradingStudio\scripts\chanlun")

import sqlite3
from datetime import datetime
from pathlib import Path

import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots

from chanlun_core import (
    Bar, analyze, Direction, FractalType,
    MIN_BI_LEN,
)

# ── Data loading ──────────────────────────────────────────────

DB_PATH = r"C:\Works\ClaudeCode\TradingStudio\data\archive\bars_2024.db"
PRICE_SCALE = 10_000_000


def load_bars(instrument: str, limit: int = 1000) -> list[Bar]:
    conn = sqlite3.connect(DB_PATH)
    df = pd.read_sql_query(
        """SELECT bar_time, open, high, low, close, volume
           FROM bars_1min WHERE instrument_id = ?
           ORDER BY bar_time LIMIT ?""",
        conn, params=(instrument, limit),
    )
    conn.close()
    bars = []
    for _, r in df.iterrows():
        bars.append(Bar(
            dt=pd.Timestamp(r["bar_time"]).to_pydatetime(),
            open=float(r["open"]) / PRICE_SCALE,
            high=float(r["high"]) / PRICE_SCALE,
            low=float(r["low"]) / PRICE_SCALE,
            close=float(r["close"]) / PRICE_SCALE,
            vol=int(r["volume"]),
        ))
    return bars


# ── Chart building ────────────────────────────────────────────

def build_chart(bars: list[Bar], instrument: str, min_bi_len: int = 7,
                hide_gaps: bool = True) -> go.Figure:
    """构建交互式缠论分析图

    Args:
        bars: 原始K线列表
        instrument: 品种代码
        min_bi_len: 最小笔长度
        hide_gaps: True=用连续索引轴隐藏非交易时段空白, False=真实时间轴
    """
    result = analyze(bars, min_bi_len=min_bi_len)
    display_bars = result.std_bars
    times = [b.dt for b in display_bars]
    n = len(display_bars)

    # ── Helper: 时间→索引映射 ──
    def time_to_index(dt):
        for i, t in enumerate(times):
            if t >= dt:
                return i
        return n - 1

    # ── X轴设置 ──
    if hide_gaps:
        x_values = list(range(n))
        x_title = "日期"
        # 每天只显示一个日期标签
        tick_vals, tick_texts = [], []
        prev_day = None
        for i, t in enumerate(times):
            day_key = t.strftime("%Y%m%d")
            if day_key != prev_day:
                tick_vals.append(i)
                tick_texts.append(t.strftime("%m/%d"))
                prev_day = day_key
        xaxis_config = dict(
            title=x_title, tickmode="array",
            tickvals=tick_vals, ticktext=tick_texts,
            showgrid=True, rangeslider=dict(visible=True, thickness=0.05),
        )
        # 生成带时间的 hover 文本
        hover_texts = [
            f"{t.strftime('%Y-%m-%d %H:%M')}<br>"
            f"O:{b.open:.1f} H:{b.high:.1f} L:{b.low:.1f} C:{b.close:.1f}"
            for t, b in zip(times, display_bars)
        ]
    else:
        x_values = times
        x_title = "时间"
        xaxis_config = dict(title=x_title, rangeslider=dict(visible=False))
        hover_texts = None

    fig = go.Figure()

    # ── K线 ──
    candlestick_kwargs = dict(
        x=x_values,
        open=[b.open for b in display_bars],
        high=[b.high for b in display_bars],
        low=[b.low for b in display_bars],
        close=[b.close for b in display_bars],
        name="K线",
        showlegend=True,
    )
    if hover_texts:
        candlestick_kwargs.update(hovertext=hover_texts, hoverinfo="text")
    fig.add_trace(go.Candlestick(**candlestick_kwargs))

    # ── 分型 ──
    def fx_x(f): return time_to_index(f.dt) if hide_gaps else f.dt
    def fx_label(f): return f"{'顶' if f.type == FractalType.Top else '底'}分型"

    top_fx = [f for f in result.fractals if f.type == FractalType.Top]
    bot_fx = [f for f in result.fractals if f.type == FractalType.Bottom]

    scatter_kwargs = dict(mode="markers", showlegend=True)
    if hide_gaps:
        scatter_kwargs.update(hoverinfo="text")

    if top_fx:
        fig.add_trace(go.Scatter(
            **scatter_kwargs,
            x=[fx_x(f) for f in top_fx], y=[f.price for f in top_fx],
            marker=dict(symbol="triangle-down", size=8, color="red"),
            name=f"顶分型({len(top_fx)})",
            hovertext=[f"{f.dt.strftime('%m/%d %H:%M')} {f.price:.1f}" for f in top_fx] if hide_gaps else None,
        ))
    if bot_fx:
        fig.add_trace(go.Scatter(
            **scatter_kwargs,
            x=[fx_x(f) for f in bot_fx], y=[f.price for f in bot_fx],
            marker=dict(symbol="triangle-up", size=8, color="green"),
            name=f"底分型({len(bot_fx)})",
            hovertext=[f"{f.dt.strftime('%m/%d %H:%M')} {f.price:.1f}" for f in bot_fx] if hide_gaps else None,
        ))

    # ── 笔 ──
    for bi in result.bis[:80]:
        color = "blue" if bi.type == Direction.Up else "orange"
        x0 = time_to_index(bi.dt_start) if hide_gaps else bi.dt_start
        x1 = time_to_index(bi.dt_end) if hide_gaps else bi.dt_end
        fig.add_trace(go.Scatter(
            x=[x0, x1], y=[bi.start_fx.price, bi.end_fx.price],
            mode="lines+markers", line=dict(color=color, width=2), marker=dict(size=4),
            showlegend=False,
            hovertext=f"power={bi.power:.1f} len={bi.bar_count}K",
            hoverinfo="text",
        ))

    # ── 中枢 ──
    for i, zs in enumerate(result.zhongshus[:15]):
        if zs.dt_start and zs.dt_end:
            x0 = time_to_index(zs.dt_start) if hide_gaps else zs.dt_start
            x1 = time_to_index(zs.dt_end) if hide_gaps else zs.dt_end
            fig.add_trace(go.Scatter(
                x=[x0, x1, x1, x0, x0],
                y=[zs.zd, zs.zd, zs.zg, zs.zg, zs.zd],
                mode="lines", fill="toself", fillcolor="rgba(128,0,128,0.08)",
                line=dict(color="purple", width=1, dash="dash"),
                name="中枢" if i == 0 else "", showlegend=(i == 0),
                hovertext=f"[{zs.zd:.1f},{zs.zg:.1f}] zz={zs.zz:.1f}",
                hoverinfo="text",
            ))

    # ── Layout ──
    up_count = sum(1 for b in result.bis if b.type == Direction.Up)
    dn_count = sum(1 for b in result.bis if b.type == Direction.Down)
    gap_label = "无间隙" if hide_gaps else "真实时间"

    fig.update_layout(
        title=dict(
            text=f"缠论分析 — {instrument} (15min K线, MIN_BI_LEN={min_bi_len}, {gap_label})<br>"
                 f"<sup>原始{len(result.raw_bars)}根 → 标准{len(result.std_bars)}根 → "
                 f"分型{len(result.fractals)} → 笔{len(result.bis)} (UP={up_count} DOWN={dn_count}) → "
                 f"中枢{len(result.zhongshus)} → 走势: {result.trend}</sup>",
            font=dict(size=14),
        ),
        xaxis=xaxis_config,
        yaxis=dict(title="价格"),
        height=750,
        hovermode="x unified",
        template="plotly_white",
    )

    return fig


def load_and_resample(instrument: str, freq: str = "15min", limit_1min: int = 24000) -> list[Bar]:
    """加载 1min 数据并聚合到指定周期"""
    conn = sqlite3.connect(DB_PATH)
    df = pd.read_sql_query(
        """SELECT bar_time, open, high, low, close, volume
           FROM bars_1min WHERE instrument_id = ?
           ORDER BY bar_time LIMIT ?""",
        conn, params=(instrument, limit_1min),
    )
    conn.close()

    df["bar_time"] = pd.to_datetime(df["bar_time"])
    df.set_index("bar_time", inplace=True)
    for col in ["open", "high", "low", "close"]:
        df[col] = df[col].astype(float) / PRICE_SCALE

    resampled = df.resample(freq).agg({
        "open": "first", "high": "max", "low": "min",
        "close": "last", "volume": "sum",
    }).dropna()

    bars = []
    for i, r in resampled.iterrows():
        bars.append(Bar(
            dt=i.to_pydatetime(),
            open=float(r["open"]), high=float(r["high"]),
            low=float(r["low"]), close=float(r["close"]),
            vol=int(r["volume"]),
        ))
    return bars


def main() -> None:
    out_dir = Path(r"C:\Works\ClaudeCode\TradingStudio\scripts\chanlun\output")
    out_dir.mkdir(parents=True, exist_ok=True)

    instrument = "ag2412"

    # 15min data (recommended for 缠论)
    bars_15 = load_and_resample(instrument, freq="15min", limit_1min=24000)
    print(f"15min: {len(bars_15)} bars  [{bars_15[0].dt} → {bars_15[-1].dt}]")

    # Generate gap-free chart
    fig = build_chart(bars_15, instrument, min_bi_len=5, hide_gaps=True)
    out_path = out_dir / "chanlun_15min_nogaps.html"
    fig.write_html(str(out_path))
    print(f"Gap-free chart → {out_path}  ({out_path.stat().st_size/1024:.0f} KB)")

    # Generate time-based chart (with gaps) for comparison
    fig_time = build_chart(bars_15, instrument, min_bi_len=5, hide_gaps=False)
    out_path2 = out_dir / "chanlun_15min_time.html"
    fig_time.write_html(str(out_path2))
    print(f"Time-based chart → {out_path2}  ({out_path2.stat().st_size/1024:.0f} KB)")

    print(f"\nOpen in browser to compare.")


if __name__ == "__main__":
    main()
