"""
SA 缠论策略回测 v2 — 修复资金计算 + ATR止损
"""
import pandas as pd
import numpy as np
from dataclasses import dataclass, field
from typing import List, Optional, Tuple
from czsc import CZSC, RawBar, Freq, Direction

# Config
INITIAL_CAPITAL = 1_000_000
RISK_PER_TRADE = 0.02
SA_MULT = 20
FEE_PER_LOT = 20.0
MARGIN_RATE = 0.08
MIN_BI_POWER = 300
MIN_BI_DAYS = 3
ATR_PERIOD = 20
STOP_ATR_MULT = 2.0

@dataclass
class Trade:
    entry_time: pd.Timestamp
    exit_time: pd.Timestamp
    direction: str
    entry_price: float
    exit_price: float
    quantity: int
    pnl: float
    pnl_pct: float
    exit_reason: str


def calc_atr(df: pd.DataFrame, period: int = 20) -> pd.Series:
    """True Range rolling average"""
    high, low, close = df["high"], df["low"], df["close"]
    prev_close = close.shift(1)
    tr = pd.concat([
        high - low,
        (high - prev_close).abs(),
        (low - prev_close).abs()
    ], axis=1).max(axis=1)
    return tr.rolling(period).mean()


def run_signal_backtest(
    df: pd.DataFrame,
    czsc: CZSC,
    train_years: Tuple[int, int] = (2020, 2022),
    test_years: Tuple[int, int] = (2023, 2025),
) -> dict:
    """Full backtest pipeline"""

    # Pre-compute ATR
    df["atr"] = calc_atr(df, ATR_PERIOD)
    price_map = {row["dt"]: (i, row) for i, (_, row) in enumerate(df.iterrows())}

    # Generate signals from BI turns
    signals = []
    for i in range(1, len(czsc.bi_list)):
        prev = czsc.bi_list[i - 1]
        curr = czsc.bi_list[i]

        if prev.power < MIN_BI_POWER or len(prev.bars) < MIN_BI_DAYS:
            continue

        sig_dt = curr.fx_a.dt

        # Find the next trading bar after signal (signal_date may not be a trading day exactly)
        if sig_dt not in price_map:
            # Find next available date
            future_dates = [d for d in price_map if d >= sig_dt]
            if not future_dates:
                continue
            sig_dt = min(future_dates)

        idx, bar = price_map[sig_dt]
        if pd.isna(bar["atr"]) or bar["atr"] <= 0:
            continue

        atr = bar["atr"]
        entry_price = bar["close"]

        # BUY: Down BI → Up BI
        if prev.direction == Direction.Down and curr.direction == Direction.Up:
            stop_price = entry_price - STOP_ATR_MULT * atr
            signals.append({
                "idx": idx, "dt": sig_dt, "type": "BUY",
                "price": entry_price, "stop": stop_price,
                "power": prev.power, "atr": atr,
            })

        # SELL: Up BI → Down BI
        elif prev.direction == Direction.Up and curr.direction == Direction.Down:
            stop_price = entry_price + STOP_ATR_MULT * atr
            signals.append({
                "idx": idx, "dt": sig_dt, "type": "SELL",
                "price": entry_price, "stop": stop_price,
                "power": prev.power, "atr": atr,
            })

    print(f"  Signals: {len(signals)} ({len([s for s in signals if s['type']=='BUY'])} Buy, {len([s for s in signals if s['type']=='SELL'])} Sell)")

    # Sort signals by idx for sequential processing
    signals.sort(key=lambda s: s["idx"])

    # Backtest engine
    cash = INITIAL_CAPITAL
    margin_locked = 0.0
    pos_qty = 0          # +long, -short, 0=none
    pos_entry = 0.0
    pos_stop = 0.0
    pos_start_idx = 0
    pos_direction = ""
    pos_power = 0.0

    trades = []
    equity_daily = {}
    sig_ptr = 0

    for idx, (_, bar) in enumerate(df.iterrows()):
        dt = bar["dt"]

        # Skip if no ATR yet
        if pd.isna(bar["atr"]):
            continue

        close, high, low = bar["close"], bar["high"], bar["low"]

        # === Check signals (only enter when flat) ===
        while sig_ptr < len(signals) and signals[sig_ptr]["idx"] <= idx:
            sig = signals[sig_ptr]
            if sig["idx"] != idx or pos_qty != 0:
                sig_ptr += 1
                continue

            stop_dist = abs(sig["price"] - sig["stop"])
            if stop_dist < sig["price"] * 0.005:
                sig_ptr += 1
                continue

            # Position sizing
            risk_amount = (cash + margin_locked) * RISK_PER_TRADE
            lots = max(1, int(risk_amount / (stop_dist * SA_MULT)))

            # Margin check
            margin_per_lot = sig["price"] * SA_MULT * MARGIN_RATE
            total_margin = margin_per_lot * lots
            max_margin = (cash + margin_locked) * 0.6

            while total_margin > max_margin and lots > 1:
                lots -= 1
                total_margin = margin_per_lot * lots
            if lots < 1:
                sig_ptr += 1
                continue

            # Enter position
            if sig["type"] == "BUY":
                pos_qty = lots
                pos_direction = "Long"
            else:
                pos_qty = -lots
                pos_direction = "Short"

            pos_entry = sig["price"]
            pos_stop = sig["stop"]
            pos_start_idx = idx
            pos_power = sig["power"]

            cash -= margin_per_lot * lots  # Lock margin
            cash -= lots * FEE_PER_LOT     # Entry fee
            margin_locked += margin_per_lot * lots

            sig_ptr += 1

        # === Check exit conditions ===
        if pos_qty != 0:
            exit_price = None
            exit_reason = None

            if pos_qty > 0:  # Long
                if low <= pos_stop:
                    exit_price = pos_stop
                    exit_reason = "StopLoss"
            else:  # Short
                if high >= pos_stop:
                    exit_price = pos_stop
                    exit_reason = "StopLoss"

            # Check for reverse signal
            if exit_reason is None:
                for sig in signals:
                    if sig["idx"] == idx:
                        if pos_qty > 0 and sig["type"] == "SELL":
                            exit_price = sig["price"]
                            exit_reason = "ReverseSignal"
                            break
                        elif pos_qty < 0 and sig["type"] == "BUY":
                            exit_price = sig["price"]
                            exit_reason = "ReverseSignal"
                            break

            if exit_reason:
                margin_used = pos_entry * SA_MULT * abs(pos_qty) * MARGIN_RATE
                margin_locked -= margin_used
                cash += margin_used  # Unlock margin

                if pos_qty > 0:
                    pnl = (exit_price - pos_entry) * pos_qty * SA_MULT
                else:
                    pnl = (pos_entry - exit_price) * abs(pos_qty) * SA_MULT

                cash -= abs(pos_qty) * FEE_PER_LOT  # Exit fee
                cash += pnl

                trades.append(Trade(
                    entry_time=df.iloc[pos_start_idx]["dt"],
                    exit_time=dt,
                    direction=pos_direction,
                    entry_price=pos_entry,
                    exit_price=exit_price,
                    quantity=abs(pos_qty),
                    pnl=pnl,
                    pnl_pct=pnl / (margin_used) * 100 if margin_used > 0 else 0,
                    exit_reason=exit_reason,
                ))

                pos_qty = 0

        # Record equity
        equity = cash + margin_locked
        if pos_qty != 0:
            if pos_qty > 0:
                unrealized = (close - pos_entry) * pos_qty * SA_MULT
            else:
                unrealized = (pos_entry - close) * abs(pos_qty) * SA_MULT
            equity += unrealized
        equity_daily[dt] = equity

    # Results
    eq_list = [{"dt": d, "equity": e} for d, e in sorted(equity_daily.items())]

    return {
        "trades": trades,
        "equity": eq_list,
        "signals": signals,
    }


def report(result: dict, label: str):
    """Print report for one period"""
    trades = result["trades"]
    eq = result["equity"]

    print(f"\n{'='*70}")
    print(f"  {label}")
    print(f"{'='*70}")

    if not trades:
        print("  No trades!")
        return

    n = len(trades)
    nw = len([t for t in trades if t.pnl > 0])
    nl = n - nw
    total_pnl = sum(t.pnl for t in trades)
    win_rate = nw / n * 100
    avg_w = np.mean([t.pnl for t in trades if t.pnl > 0]) if nw > 0 else 0
    avg_l = np.mean([t.pnl for t in trades if t.pnl <= 0]) if nl > 0 else 0

    long_t = [t for t in trades if t.direction == "Long"]
    short_t = [t for t in trades if t.direction == "Short"]

    print(f"\n  Trades: {n} | Win: {win_rate:.1f}% ({nw}W/{nl}L)")
    print(f"  Total PnL: {total_pnl:,.0f} CNY ({total_pnl/INITIAL_CAPITAL*100:.1f}%)")
    print(f"  Avg Win: {avg_w:,.0f} | Avg Loss: {avg_l:,.0f} | PF: {abs(avg_w/avg_l) if avg_l!=0 else float('inf'):.2f}")

    if long_t:
        lpnl = sum(t.pnl for t in long_t)
        lwin = len([t for t in long_t if t.pnl > 0])
        print(f"  Long: {len(long_t)} trades, PnL={lpnl:,.0f}, Win={lwin/len(long_t)*100:.1f}%")

    if short_t:
        spnl = sum(t.pnl for t in short_t)
        swin = len([t for t in short_t if t.pnl > 0])
        print(f"  Short: {len(short_t)} trades, PnL={spnl:,.0f}, Win={swin/len(short_t)*100:.1f}%")

    # Exit reasons
    reasons = {}
    for t in trades:
        r = t.exit_reason
        if r not in reasons:
            reasons[r] = {"n": 0, "pnl": 0, "wins": 0}
        reasons[r]["n"] += 1
        reasons[r]["pnl"] += t.pnl
        if t.pnl > 0:
            reasons[r]["wins"] += 1

    print(f"\n  Exit Reasons:")
    for r, s in reasons.items():
        wr = s["wins"] / s["n"] * 100 if s["n"] > 0 else 0
        print(f"    {r}: {s['n']}x, PnL={s['pnl']:,.0f}, Win={wr:.1f}%")

    # Equity stats
    eq_vals = [e["equity"] for e in eq]
    eq_dates = [e["dt"] for e in eq]

    peak = eq_vals[0]
    max_dd, dd_start, dd_end = 0, eq_dates[0], eq_dates[0]
    cur_dd_start = eq_dates[0]
    for i, v in enumerate(eq_vals):
        if v > peak:
            peak = v
            cur_dd_start = eq_dates[i]
        dd = (peak - v) / peak * 100
        if dd > max_dd:
            max_dd, dd_start, dd_end = dd, cur_dd_start, eq_dates[i]

    days = (eq_dates[-1] - eq_dates[0]).days
    total_ret = (eq_vals[-1] - INITIAL_CAPITAL) / INITIAL_CAPITAL
    ann_ret = ((1 + total_ret) ** (365 / days) - 1) * 100 if days > 0 else 0

    daily_rets = [(eq_vals[i] - eq_vals[i-1]) / eq_vals[i-1] for i in range(1, len(eq_vals)) if eq_vals[i-1] > 0]
    sharpe = np.mean(daily_rets) / np.std(daily_rets) * np.sqrt(250) if daily_rets else 0

    # Max consec losses
    mcl = cur = 0
    for t in trades:
        if t.pnl <= 0:
            cur += 1; mcl = max(mcl, cur)
        else:
            cur = 0

    print(f"\n  Final Equity: {eq_vals[-1]:,.0f} | Return: {total_ret*100:.1f}% | Ann: {ann_ret:.1f}%")
    print(f"  MaxDD: {max_dd:.1f}% | Sharpe: {sharpe:.2f} | MaxConsecLoss: {mcl}")

    # Yearly
    print(f"\n  Yearly:")
    for yr in sorted(set(t.entry_time.year for t in trades)):
        yt = [t for t in trades if t.entry_time.year == yr]
        ypnl = sum(t.pnl for t in yt)
        ywin = len([t for t in yt if t.pnl > 0])
        yeq = [e["equity"] for e in eq if e["dt"].year == yr]
        yret = (yeq[-1] - yeq[0]) / yeq[0] * 100 if yeq else 0
        print(f"    {yr}: {len(yt):>3} trades, PnL={ypnl:>12,.0f}, Win={ywin/len(yt)*100:.1f}%, Ret={yret:.1f}%")


# ═══════════════════
# Main
# ═══════════════════
if __name__ == "__main__":
    df = pd.read_parquet("c:/Works/ClaudeCode/TradingStudio/data/sa_chanlun/sa_day.parquet")
    df["dt"] = pd.to_datetime(df["dt"])

    raw_bars = [RawBar('SA', r['dt'], Freq.D, r['open'], r['close'],
                        r['high'], r['low'], r['vol'], r['amount'], id=i)
                for i, (_, r) in enumerate(df.iterrows())]
    czsc = CZSC(raw_bars, max_bi_num=500)

    print(f"SA: {len(df)} bars | CZSC: {len(czsc.fx_list)} FX, {len(czsc.bi_list)} BI")

    # Train: 2020-2022
    train_df = df[df["dt"].dt.year <= 2022].reset_index(drop=True)
    train_bars = [b for b in raw_bars if b.dt.year <= 2022]
    train_czsc = CZSC(train_bars, max_bi_num=500)
    print(f"\n--- Train: 2020-2022 ---")
    train_result = run_signal_backtest(train_df, train_czsc)

    # Test: 2023-2025
    test_df = df[df["dt"].dt.year >= 2023].reset_index(drop=True)
    test_bars = [b for b in raw_bars if b.dt.year >= 2023]
    test_czsc = CZSC(test_bars, max_bi_num=500)
    print(f"\n--- Test: 2023-2025 ---")
    test_result = run_signal_backtest(test_df, test_czsc)

    # Full period
    print(f"\n--- Full: 2020-2025 ---")
    full_result = run_signal_backtest(df, czsc)

    # Reports
    report(train_result, "TRAIN (2020-2022)")
    report(test_result, "TEST (2023-2025)")
    report(full_result, "FULL (2020-2025)")
