"""
缠论学习与实践 — 06: 三类买卖点 + 回测
=========================================
信号检测:
  B1/S1: 趋势背驰 — 同向笔力度衰减（替代MACD面积比较）
  B2/S2: 回踩/反弹 — 买/卖点后不破前低/高
  B3/S3: 中枢突破/跌破 — 离开中枢后回抽不入

回测引擎: 简化事件驱动，逐K线推进
输出: 交易明细 + 绩效指标 + 可视化

运行: cd /tmp && PYTHONIOENCODING=utf-8 python scripts/chanlun/06_backtest_signals.py
"""
from __future__ import annotations

import sys
sys.path = [p for p in sys.path if 'ClaudeCode/czsc' not in p]
sys.path.insert(0, r"C:\Works\ClaudeCode\TradingStudio\scripts\chanlun")

import sqlite3
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import List, Optional, Tuple

import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots

from chanlun_core import (
    Bar, analyze, Direction, FractalType, Bi, Zhongshu, ChanLunResult,
    MIN_BI_LEN, process_inclusions, find_fractals, deduplicate_fractals,
    build_bis, build_zhongshus, classify_trend,
)

# ── Config ────────────────────────────────────────────────────

DB_PATH = r"C:\Works\ClaudeCode\TradingStudio\data\archive\bars_2024.db"
PRICE_SCALE = 10_000_000
DIVERGENCE_RATIO = 0.5  # 背驰力度比阈值


# ── Signal types ──────────────────────────────────────────────

@dataclass
class TradeSignal:
    """买卖点信号"""
    type: str           # "B1"|"B2"|"B3"|"S1"|"S2"|"S3"
    dt: datetime        # 信号触发时间
    price: float        # 触发价格
    bi_index: int       # 关联笔索引
    zs_index: int       # 关联中枢索引 (-1 if none)
    description: str


@dataclass
class Trade:
    """单笔交易"""
    entry_signal: TradeSignal
    exit_signal: TradeSignal
    entry_price: float
    exit_price: float
    direction: str      # "LONG" | "SHORT"
    pnl: float
    pnl_pct: float
    entry_dt: datetime
    exit_dt: datetime
    bars_held: int


# ── Signal Detection ──────────────────────────────────────────

def detect_signals(result: ChanLunResult, bars: List[Bar]) -> List[TradeSignal]:
    """
    检测三类买卖点 (改进版 — 更实用的信号生成)。

    B1: 任意两根DOWN笔力度衰减 > 阈值 → 底背驰买点
    S1: 任意两根UP笔力度衰减 > 阈值 → 顶背驰卖点
    B2: 任意中枢下方，DOWN笔低点抬高 → 二买
    S2: 任意中枢上方，UP笔高点降低 → 二卖
    B3: 价格向上突破中枢上沿后回抽不破 ZG
    S3: 价格向下跌破中枢下沿后反弹不破 ZD
    """
    signals: List[TradeSignal] = []
    if len(result.bis) < 2:
        return signals

    bis = result.bis
    zhongshus = result.zhongshus

    # ── B1: 底背驰 (DOWN笔力度衰减) ──
    down_bis = [(i, b) for i, b in enumerate(bis) if b.type == Direction.Down]
    for idx in range(1, len(down_bis)):
        i_prev, prev_bi = down_bis[idx - 1]
        i_curr, curr_bi = down_bis[idx]
        decay_ratio = curr_bi.power / prev_bi.power if prev_bi.power > 0 else 1.0
        if decay_ratio < DIVERGENCE_RATIO:
            signals.append(TradeSignal(
                type="B1", dt=curr_bi.dt_end, price=curr_bi.end_fx.price,
                bi_index=i_curr, zs_index=-1,
                description=f"一买: DOWN力度衰减 {prev_bi.power:.1f}→{curr_bi.power:.1f} ratio={decay_ratio:.2f}",
            ))

    # ── S1: 顶背驰 (UP笔力度衰减) ──
    up_bis = [(i, b) for i, b in enumerate(bis) if b.type == Direction.Up]
    for idx in range(1, len(up_bis)):
        i_prev, prev_bi = up_bis[idx - 1]
        i_curr, curr_bi = up_bis[idx]
        decay_ratio = curr_bi.power / prev_bi.power if prev_bi.power > 0 else 1.0
        if decay_ratio < DIVERGENCE_RATIO:
            signals.append(TradeSignal(
                type="S1", dt=curr_bi.dt_end, price=curr_bi.end_fx.price,
                bi_index=i_curr, zs_index=-1,
                description=f"一卖: UP力度衰减 {prev_bi.power:.1f}→{curr_bi.power:.1f} ratio={decay_ratio:.2f}",
            ))

    # ── B2/S2: 中枢附近回踩不破/反弹不过 ──
    for zs in zhongshus:
        # B2: 中枢下方出现DOWN笔，低点比前一个DOWN笔更高
        zs_down_bis = [(i, b) for i, b in enumerate(bis) if b.type == Direction.Down
                       and b.dt_end > (zs.dt_start or b.dt_start)
                       and b.end_fx.price < zs.zz]
        if len(zs_down_bis) >= 2:
            i1, b1 = zs_down_bis[0]
            i2, b2 = zs_down_bis[1]
            if b2.end_fx.price > b1.end_fx.price:  # 低点抬高
                if not any(s.type == "B2" for s in signals if abs((s.dt - b2.dt_end).total_seconds()) < 3600):
                    signals.append(TradeSignal(
                        type="B2", dt=b2.dt_end, price=b2.end_fx.price,
                        bi_index=i2, zs_index=-1,
                        description=f"二买: 中枢{zs.zz:.0f}下方低点抬高 {b1.end_fx.price:.1f}→{b2.end_fx.price:.1f}",
                    ))

        # S2: 中枢上方出现UP笔，高点比前一个UP笔更低
        zs_up_bis = [(i, b) for i, b in enumerate(bis) if b.type == Direction.Up
                     and b.dt_end > (zs.dt_start or b.dt_start)
                     and b.end_fx.price > zs.zz]
        if len(zs_up_bis) >= 2:
            i1, b1 = zs_up_bis[0]
            i2, b2 = zs_up_bis[1]
            if b2.end_fx.price < b1.end_fx.price:  # 高点降低
                if not any(s.type == "S2" for s in signals if abs((s.dt - b2.dt_end).total_seconds()) < 3600):
                    signals.append(TradeSignal(
                        type="S2", dt=b2.dt_end, price=b2.end_fx.price,
                        bi_index=i2, zs_index=-1,
                        description=f"二卖: 中枢{zs.zz:.0f}上方高点降低 {b1.end_fx.price:.1f}→{b2.end_fx.price:.1f}",
                    ))

    # ── B3/S3: 中枢突破确认 ──
    for zs_idx, zs in enumerate(zhongshus):
        # B3: UP笔突破ZG，随后DOWN笔低点不破ZG
        for i, bi in enumerate(bis):
            if bi.type == Direction.Up and bi.high > zs.zg and bi.dt_start > (zs.dt_start or bi.dt_start):
                # 找后续DOWN笔
                for j in range(i + 1, min(i + 5, len(bis))):
                    bi2 = bis[j]
                    if bi2.type == Direction.Down and bi2.dt_start > bi.dt_end:
                        if bi2.low > zs.zg * 0.99:  # 回抽不破ZG (放宽至1%)
                            signals.append(TradeSignal(
                                type="B3", dt=bi2.dt_end, price=bi2.end_fx.price,
                                bi_index=j, zs_index=zs_idx,
                                description=f"三买: 突破ZG{zs.zg:.1f}回踩{bi2.low:.1f}不破",
                            ))
                        break
                break

        # S3: DOWN笔跌破ZD，随后UP笔高点不破ZD
        for i, bi in enumerate(bis):
            if bi.type == Direction.Down and bi.low < zs.zd and bi.dt_start > (zs.dt_start or bi.dt_start):
                for j in range(i + 1, min(i + 5, len(bis))):
                    bi2 = bis[j]
                    if bi2.type == Direction.Up and bi2.dt_start > bi.dt_end:
                        if bi2.high < zs.zd * 1.01:
                            signals.append(TradeSignal(
                                type="S3", dt=bi2.dt_end, price=bi2.end_fx.price,
                                bi_index=j, zs_index=zs_idx,
                                description=f"三卖: 跌破ZD{zs.zd:.1f}反弹{bi2.high:.1f}不破",
                            ))
                        break
                break

    signals.sort(key=lambda s: s.dt)
    return signals


# ── Backtest Engine ───────────────────────────────────────────

@dataclass
class BacktestResult:
    trades: List[Trade] = field(default_factory=list)
    equity_curve: List[Tuple[datetime, float]] = field(default_factory=list)
    initial_capital: float = 100_000
    final_capital: float = 100_000


def run_backtest(
    bars_15min: List[Bar],
    signals: List[TradeSignal],
    capital: float = 100_000,
    contract_size: int = 1,
    stop_loss_pct: float = 0.02,
    take_profit_pct: float = 0.03,
    max_hold_bars: int = 60,
) -> BacktestResult:
    """
    回测引擎 v2:
    - B信号 → 做多 (如已有空仓则先平仓)
    - S信号 → 做空 (如已有多仓则先平仓)
    - 止损: -2%, 止盈: +3%, 最大持仓60根K线
    - 用bar.open作为执行价 (下一根K线开盘)
    """
    if not signals:
        return BacktestResult()

    result = BacktestResult(initial_capital=capital)
    equity = capital
    position: Optional[dict] = None
    trades: List[Trade] = []
    equity_curve: List[Tuple[datetime, float]] = [(bars_15min[0].dt, capital)]

    sig_idx = 0
    for bar_idx, bar in enumerate(bars_15min):
        # 检查止损止盈
        if position is not None:
            exit_reason = None
            exit_price = None
            pos_type = position["type"]
            entry_price = position["entry_price"]

            if pos_type == "LONG":
                if bar.low <= entry_price * (1 - stop_loss_pct):
                    exit_price = entry_price * (1 - stop_loss_pct)
                    exit_reason = "止损"
                elif bar.high >= entry_price * (1 + take_profit_pct):
                    exit_price = entry_price * (1 + take_profit_pct)
                    exit_reason = "止盈"
            else:
                if bar.high >= entry_price * (1 + stop_loss_pct):
                    exit_price = entry_price * (1 + stop_loss_pct)
                    exit_reason = "止损"
                elif bar.low <= entry_price * (1 - take_profit_pct):
                    exit_price = entry_price * (1 - take_profit_pct)
                    exit_reason = "止盈"

            # 超时平仓
            if exit_reason is None and (bar_idx - position["bar_idx"]) >= max_hold_bars:
                exit_price = bar.close
                exit_reason = "超时"

            if exit_reason:
                if pos_type == "LONG":
                    pnl = (exit_price - entry_price) * contract_size
                    pnl_pct = (exit_price - entry_price) / entry_price
                else:
                    pnl = (entry_price - exit_price) * contract_size
                    pnl_pct = (entry_price - exit_price) / entry_price

                equity += pnl
                trades.append(Trade(
                    entry_signal=position["entry_signal"],
                    exit_signal=TradeSignal(exit_reason, bar.dt, exit_price, -1, -1, exit_reason),
                    entry_price=entry_price, exit_price=exit_price,
                    direction=pos_type, pnl=pnl, pnl_pct=pnl_pct,
                    entry_dt=position["entry_dt"], exit_dt=bar.dt,
                    bars_held=bar_idx - position["bar_idx"],
                ))
                position = None

        # 检查新信号
        while sig_idx < len(signals) and signals[sig_idx].dt <= bar.dt:
            sig = signals[sig_idx]
            is_buy = sig.type.startswith("B")
            is_sell = sig.type.startswith("S")

            if position is None:
                # 无持仓 → 入场
                entry_price = bar.open
                position = {
                    "type": "LONG" if is_buy else "SHORT",
                    "entry_price": entry_price,
                    "entry_dt": bar.dt,
                    "entry_signal": sig,
                    "bar_idx": bar_idx,
                }
            else:
                # 有持仓 → 反向信号平仓+开新仓
                pos_type = position["type"]
                if (pos_type == "LONG" and is_sell) or (pos_type == "SHORT" and is_buy):
                    # 平旧仓
                    exit_price = bar.open
                    entry_price_old = position["entry_price"]
                    if pos_type == "LONG":
                        pnl = (exit_price - entry_price_old) * contract_size
                        pnl_pct = (exit_price - entry_price_old) / entry_price_old
                    else:
                        pnl = (entry_price_old - exit_price) * contract_size
                        pnl_pct = (entry_price_old - exit_price) / entry_price_old
                    equity += pnl
                    trades.append(Trade(
                        entry_signal=position["entry_signal"],
                        exit_signal=sig,
                        entry_price=entry_price_old, exit_price=exit_price,
                        direction=pos_type, pnl=pnl, pnl_pct=pnl_pct,
                        entry_dt=position["entry_dt"], exit_dt=bar.dt,
                        bars_held=bar_idx - position["bar_idx"],
                    ))
                    # 开新仓
                    position = {
                        "type": "LONG" if is_buy else "SHORT",
                        "entry_price": exit_price,
                        "entry_dt": bar.dt,
                        "entry_signal": sig,
                        "bar_idx": bar_idx,
                    }
                # 同向信号 → 忽略 (已在仓位中)

            sig_idx += 1

        equity_curve.append((bar.dt, equity))

    # 强制平仓
    if position is not None:
        last_bar = bars_15min[-1]
        exit_price = last_bar.close
        entry_price = position["entry_price"]
        pos_type = position["type"]
        if pos_type == "LONG":
            pnl = (exit_price - entry_price) * contract_size
            pnl_pct = (exit_price - entry_price) / entry_price
        else:
            pnl = (entry_price - exit_price) * contract_size
            pnl_pct = (entry_price - exit_price) / entry_price
        equity += pnl
        trades.append(Trade(
            entry_signal=position["entry_signal"],
            exit_signal=TradeSignal("CLOSE", last_bar.dt, exit_price, -1, -1, "强制平仓"),
            entry_price=entry_price, exit_price=exit_price,
            direction=pos_type, pnl=pnl, pnl_pct=pnl_pct,
            entry_dt=position["entry_dt"], exit_dt=last_bar.dt,
            bars_held=len(bars_15min) - position["bar_idx"],
        ))

    result.trades = trades
    result.equity_curve = equity_curve
    result.final_capital = equity
    return result


# ── Performance Report ────────────────────────────────────────

def report(result: BacktestResult, signals: List[TradeSignal]) -> None:
    """打印回测绩效报告"""
    trades = result.trades
    if not trades:
        print("无交易记录")
        return

    # 基本统计
    wins = [t for t in trades if t.pnl > 0]
    losses = [t for t in trades if t.pnl <= 0]
    win_rate = len(wins) / len(trades) * 100

    total_pnl = sum(t.pnl for t in trades)
    total_return = (result.final_capital - result.initial_capital) / result.initial_capital * 100

    long_trades = [t for t in trades if t.direction == "LONG"]
    short_trades = [t for t in trades if t.direction == "SHORT"]

    # 按信号类型统计
    by_signal = {}
    for t in trades:
        st = t.entry_signal.type
        if st not in by_signal:
            by_signal[st] = {"count": 0, "wins": 0, "pnl": 0.0}
        by_signal[st]["count"] += 1
        if t.pnl > 0:
            by_signal[st]["wins"] += 1
        by_signal[st]["pnl"] += t.pnl

    # 输出
    print("=" * 70)
    print("缠论买卖点回测报告 (v2 — 含止损止盈)")
    print("=" * 70)
    print(f"\n  信号总数: {len(signals)}")
    print(f"  交易次数: {len(trades)}")
    print(f"  胜率: {win_rate:.1f}% ({len(wins)}赢/{len(losses)}亏)")
    print(f"  初始资金: {result.initial_capital:,.0f}")
    print(f"  最终资金: {result.final_capital:,.0f}")
    print(f"  总收益: {total_pnl:+,.0f} ({total_return:+.2f}%)")

    # 最大回撤
    equity_vals = [eq for _, eq in result.equity_curve]
    peak = equity_vals[0]
    max_dd = 0.0
    for eq in equity_vals:
        if eq > peak:
            peak = eq
        dd = (peak - eq) / peak * 100
        if dd > max_dd:
            max_dd = dd
    print(f"  最大回撤: {max_dd:.2f}%")

    # 盈亏比
    if wins:
        avg_win = sum(t.pnl for t in wins) / len(wins)
        avg_loss = sum(abs(t.pnl) for t in losses) / len(losses) if losses else 0
        profit_factor = sum(t.pnl for t in wins) / sum(abs(t.pnl) for t in losses) if losses else float('inf')
        print(f"  平均盈利: {avg_win:+,.0f}  平均亏损: {avg_loss:+,.0f}")
        print(f"  盈亏比: {profit_factor:.2f}")

    # 持仓时间
    hold_bars = [t.bars_held for t in trades]
    print(f"  平均持仓: {sum(hold_bars)/len(hold_bars):.0f}根K线 ({sum(hold_bars)/len(hold_bars)*15/60:.1f}小时)")

    # 出场原因统计
    exit_reasons = {}
    for t in trades:
        r = t.exit_signal.type
        exit_reasons[r] = exit_reasons.get(r, 0) + 1
    print(f"  出场原因: {exit_reasons}")

    # 多空对比
    print(f"\n  方向分析:")
    for label, tlist in [("做多", long_trades), ("做空", short_trades)]:
        if tlist:
            w = sum(1 for t in tlist if t.pnl > 0)
            print(f"    {label}: {len(tlist)}笔  胜率={w/len(tlist)*100:.1f}%  盈亏={sum(t.pnl for t in tlist):+,.0f}")

    # 信号分类统计
    print(f"\n  信号类型分析:")
    print(f"    {'类型':<6} {'交易数':>6} {'胜率':>8} {'总盈亏':>10}")
    print(f"    {'-' * 30}")
    for st in ["B1", "B2", "B3", "S1", "S2", "S3"]:
        if st in by_signal:
            s = by_signal[st]
            wr = s["wins"] / s["count"] * 100 if s["count"] else 0
            print(f"    {st:<6} {s['count']:>6} {wr:>7.1f}% {s['pnl']:>+10.0f}")

    # 交易明细
    print(f"\n  交易明细 (前15笔):")
    print(f"    {'入场':<16} {'出场':<16} {'方向':<6} {'入场价':>8} {'出场价':>8} {'盈亏':>10} {'盈亏%':>8} {'出场原因':<8}")
    print(f"    {'-' * 90}")
    for t in trades[:15]:
        print(f"    {str(t.entry_dt)[:16]:<16} {str(t.exit_dt)[:16]:<16} "
              f"{t.direction:<6} {t.entry_price:>8.1f} {t.exit_price:>8.1f} "
              f"{t.pnl:>+10.0f} {t.pnl_pct:>+7.2f}% {t.exit_signal.type:<8}")


# ── Visualization ─────────────────────────────────────────────

def visualize_backtest(
    bars: List[Bar], result: ChanLunResult,
    signals: List[TradeSignal], trades: List[Trade],
    equity_curve: List[Tuple[datetime, float]],
    instrument: str,
) -> go.Figure:
    """双面板: 上=K线+信号, 下=资金曲线"""
    fig = make_subplots(
        rows=2, cols=1, shared_xaxes=True,
        vertical_spacing=0.03,
        row_heights=[0.65, 0.35],
        subplot_titles=(f"缠论买卖点信号 — {instrument} (15min)", "资金曲线"),
    )

    display_bars = result.std_bars
    n = len(display_bars)
    times = [b.dt for b in display_bars]

    def t2i(dt):
        for i, t in enumerate(times):
            if t >= dt: return i
        return n - 1

    # Panel 1: K线 + 分型 + 信号
    fig.add_trace(go.Candlestick(
        x=list(range(n)),
        open=[b.open for b in display_bars], high=[b.high for b in display_bars],
        low=[b.low for b in display_bars], close=[b.close for b in display_bars],
        name="K线", showlegend=True,
        hovertext=[f"{t.strftime('%m/%d %H:%M')}" for t in times],
        hoverinfo="text",
    ), row=1, col=1)

    # 中枢
    for i, zs in enumerate(result.zhongshus[:20]):
        if zs.dt_start and zs.dt_end:
            x0, x1 = t2i(zs.dt_start), t2i(zs.dt_end)
            fig.add_trace(go.Scatter(
                x=[x0, x1, x1, x0, x0], y=[zs.zd, zs.zd, zs.zg, zs.zg, zs.zd],
                mode='lines', fill='toself', fillcolor='rgba(128,0,128,0.06)',
                line=dict(color='purple', width=1, dash='dot'),
                name='中枢' if i==0 else '', showlegend=(i==0),
            ), row=1, col=1)

    # 买卖点信号标记
    buy_colors = {"B1": "lime", "B2": "green", "B3": "darkgreen"}
    sell_colors = {"S1": "red", "S2": "orange", "S3": "darkred"}
    for sig in signals:
        x = t2i(sig.dt)
        color = buy_colors.get(sig.type, sell_colors.get(sig.type, "gray"))
        symbol = "triangle-up" if sig.type.startswith("B") else "triangle-down"
        fig.add_trace(go.Scatter(
            x=[x], y=[sig.price],
            mode='markers+text',
            marker=dict(symbol=symbol, size=12, color=color, line=dict(width=1, color='black')),
            text=[sig.type], textposition='top center',
            name=sig.type, showlegend=False,
            hovertext=sig.description, hoverinfo='text',
        ), row=1, col=1)

    # Panel 2: 资金曲线
    eq_times = [t for t, _ in equity_curve]
    eq_vals = [v for _, v in equity_curve]
    # Map equity times to bar indices
    eq_indices = [t2i(t) for t in eq_times]

    fig.add_trace(go.Scatter(
        x=eq_indices, y=eq_vals,
        mode='lines', line=dict(color='blue', width=2),
        name='资金曲线', showlegend=True,
        fill='tozeroy', fillcolor='rgba(0,0,255,0.05)',
    ), row=2, col=1)

    # Add trade entry markers on equity curve
    for t in trades:
        x_entry = t2i(t.entry_dt)
        # Find closest equity value at entry time
        eq_at_entry = eq_vals[-1]  # fallback
        for et, ev in zip(eq_times, eq_vals):
            if et >= t.entry_dt:
                eq_at_entry = ev
                break

        color = 'green' if t.direction == 'LONG' else 'red'
        fig.add_trace(go.Scatter(
            x=[x_entry], y=[eq_at_entry],
            mode='markers', marker=dict(symbol='triangle-right', size=8, color=color),
            showlegend=False, hovertext=f'{t.direction} {t.entry_signal.type}',
        ), row=2, col=1)

    # Layout
    tick_vals, tick_texts = [], []
    prev_day = None
    for i, t in enumerate(times):
        day_key = t.strftime("%Y%m%d")
        if day_key != prev_day:
            tick_vals.append(i)
            tick_texts.append(t.strftime("%m/%d"))
            prev_day = day_key

    fig.update_xaxes(
        tickmode='array', tickvals=tick_vals, ticktext=tick_texts,
        row=1, col=1,
    )
    fig.update_layout(
        height=900, hovermode='x unified', template='plotly_white',
        showlegend=True,
    )
    return fig


# ── Main ──────────────────────────────────────────────────────

def main() -> None:
    # Load data
    conn = sqlite3.connect(DB_PATH)
    df = pd.read_sql_query(
        "SELECT bar_time, open, high, low, close, volume FROM bars_1min "
        "WHERE instrument_id=? ORDER BY bar_time",
        conn, params=("ag2412",),
    )
    conn.close()

    df["bar_time"] = pd.to_datetime(df["bar_time"])
    df.set_index("bar_time", inplace=True)
    for col in ["open", "high", "low", "close"]:
        df[col] = df[col].astype(float) / PRICE_SCALE

    # Resample to 15min
    df_15 = df.resample("15min").agg({
        "open": "first", "high": "max", "low": "min",
        "close": "last", "volume": "sum",
    }).dropna()

    bars_15min = [
        Bar(dt=i.to_pydatetime(), open=float(r["open"]), high=float(r["high"]),
            low=float(r["low"]), close=float(r["close"]), vol=int(r["volume"]))
        for i, r in df_15.iterrows()
    ]
    print(f"15min bars: {len(bars_15min)}  [{bars_15min[0].dt} → {bars_15min[-1].dt}]")

    # 滑动窗口分析: 每2000根K线一个窗口, 滚动推进
    WINDOW = 2000
    STEP = 500
    all_signals: List[TradeSignal] = []
    all_results: List[ChanLunResult] = []

    for start in range(0, len(bars_15min) - WINDOW, STEP):
        window_bars = bars_15min[start:start + WINDOW]
        result = analyze(window_bars, min_bi_len=5)
        signals = detect_signals(result, window_bars)
        all_signals.extend(signals)
        all_results.append(result)

    # 去重（同一时间点的重复信号）
    all_signals.sort(key=lambda s: s.dt)
    unique_signals = []
    for s in all_signals:
        if not unique_signals or (s.dt - unique_signals[-1].dt).total_seconds() > 300:
            unique_signals.append(s)
        elif s.type != unique_signals[-1].type:
            unique_signals.append(s)

    print(f"Detection: {len(all_signals)} raw signals → {len(unique_signals)} unique")

    # 按类型统计
    for st in ["B1", "B2", "B3", "S1", "S2", "S3"]:
        count = sum(1 for s in unique_signals if s.type == st)
        print(f"  {st}: {count}")

    # 回测
    bt_result = run_backtest(bars_15min, unique_signals)

    # 报告
    report(bt_result, unique_signals)

    # 可视化
    # 取前3000根做图表
    viz_bars = bars_15min[:3000]
    viz_result = analyze(viz_bars, min_bi_len=5)
    viz_signals = [s for s in unique_signals if s.dt <= viz_bars[-1].dt]

    fig = visualize_backtest(
        viz_bars, viz_result, viz_signals,
        bt_result.trades[:30], bt_result.equity_curve,
        "ag2412",
    )

    out_dir = Path(r"C:\Works\ClaudeCode\TradingStudio\scripts\chanlun\output")
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / "backtest_signals.html"
    fig.write_html(str(out_path))
    print(f"\nChart: {out_path}  ({out_path.stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
