"""
SA 缠论回测 v3 — 日线定方向 + 30min BI转折入场
对比 v2 (纯日线信号)
"""
import pandas as pd
import numpy as np
from dataclasses import dataclass
from typing import List, Optional, Dict
from czsc import CZSC, RawBar, Freq, Direction

# ── Config ──
INITIAL_CAPITAL = 1_000_000
RISK_PER_TRADE = 0.02
SA_MULT = 20
FEE_PER_LOT = 20.0
MARGIN_RATE = 0.08
MIN_BI_POWER_DAILY = 300
MIN_BI_POWER_30M = 200
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
    exit_reason: str
    daily_dir: str  # "Up"/"Down" — what the daily BI said at entry


def load_data():
    """Load daily and 30min SA bars"""
    d = pd.read_parquet("c:/Works/ClaudeCode/TradingStudio/data/sa_chanlun/sa_day.parquet")
    m = pd.read_parquet("c:/Works/ClaudeCode/TradingStudio/data/sa_chanlun/sa_30min.parquet")
    for df in [d, m]:
        df["dt"] = pd.to_datetime(df["dt"])
    return d, m


def make_rawbars(df: pd.DataFrame, freq) -> list:
    return [RawBar('SA', r['dt'], freq, r['open'], r['close'],
                   r['high'], r['low'], r['vol'], r['amount'], id=i)
            for i, (_, r) in enumerate(df.iterrows())]


def calc_atr(df: pd.DataFrame, period: int = 20) -> pd.Series:
    h, l, c = df["high"], df["low"], df["close"]
    pc = c.shift(1)
    tr = pd.concat([h-l, (h-pc).abs(), (l-pc).abs()], axis=1).max(axis=1)
    return tr.rolling(period).mean()


def build_daily_bi_map(daily_czsc: CZSC, df30: pd.DataFrame) -> Dict[pd.Timestamp, str]:
    """
    For each 30min bar timestamp, determine the current daily BI direction.
    Returns: {30min_dt: "Up"|"Down"|"Unknown"}
    """
    bi_events = []  # (start_dt, end_dt, direction)
    for bi in daily_czsc.bi_list:
        bi_events.append((bi.fx_a.dt, bi.fx_b.dt,
                          "Up" if bi.direction == Direction.Up else "Down"))

    bi_map = {}
    for dt in df30["dt"]:
        assigned = "Unknown"
        for start, end, d in bi_events:
            if start <= dt <= end:
                assigned = d
                break
        # Also check: if dt is after last bi end, use last bi direction
        if assigned == "Unknown" and bi_events:
            last = bi_events[-1]
            if dt > last[1]:
                assigned = last[2]
        bi_map[dt] = assigned
    return bi_map


def generate_signals_30m(df30: pd.DataFrame, czsc_30m: CZSC, daily_bi_map: dict,
                          df30_price_idx: dict) -> list:
    """Generate BUY/SELL signals from 30min BI turns, filtered by daily BI direction"""
    signals = []

    for i in range(1, len(czsc_30m.bi_list)):
        prev = czsc_30m.bi_list[i - 1]
        curr = czsc_30m.bi_list[i]

        if prev.power < MIN_BI_POWER_30M or len(prev.bars) < 2:
            continue

        sig_dt = curr.fx_a.dt

        # Find next available 30min bar
        if sig_dt not in df30_price_idx:
            future = sorted([d for d in df30_price_idx if d >= sig_dt])
            if not future: continue
            sig_dt = future[0]

        idx, bar = df30_price_idx[sig_dt]
        if pd.isna(bar["atr"]) or bar["atr"] <= 0:
            continue

        daily_dir = daily_bi_map.get(sig_dt, "Unknown")
        atr = bar["atr"]
        entry_price = bar["close"]

        # BUY: 30m Down BI ends → only if daily direction is Up
        if prev.direction == Direction.Down and curr.direction == Direction.Up:
            if daily_dir != "Up":
                # Allow if daily is Unknown (edge case)
                if daily_dir != "Unknown":
                    continue
            stop_price = entry_price - STOP_ATR_MULT * atr
            signals.append({
                "idx": idx, "dt": sig_dt, "type": "BUY",
                "price": entry_price, "stop": stop_price,
                "power": prev.power, "atr": atr,
                "daily_dir": daily_dir,
            })

        # SELL: 30m Up BI ends → only if daily direction is Down
        elif prev.direction == Direction.Up and curr.direction == Direction.Down:
            if daily_dir not in ("Down", "Unknown"):
                continue
            stop_price = entry_price + STOP_ATR_MULT * atr
            signals.append({
                "idx": idx, "dt": sig_dt, "type": "SELL",
                "price": entry_price, "stop": stop_price,
                "power": prev.power, "atr": atr,
                "daily_dir": daily_dir,
            })

    signals.sort(key=lambda s: s["idx"])
    return signals


def run_backtest(df30: pd.DataFrame, signals: list) -> dict:
    """Bar-level backtest on 30min data"""
    cash = INITIAL_CAPITAL
    margin_locked = 0.0
    pos_qty = 0
    pos_entry = 0.0
    pos_stop = 0.0
    pos_start_idx = 0
    pos_direction = ""
    pos_daily_dir = ""

    trades = []
    equity_daily = {}
    sig_ptr = 0

    for idx, (_, bar) in enumerate(df30.iterrows()):
        dt = bar["dt"]

        if pd.isna(bar["atr"]):
            continue

        close, high, low = bar["close"], bar["high"], bar["low"]

        # Check signals (flat only)
        while sig_ptr < len(signals) and signals[sig_ptr]["idx"] <= idx:
            sig = signals[sig_ptr]
            if sig["idx"] != idx or pos_qty != 0:
                sig_ptr += 1
                continue

            stop_dist = abs(sig["price"] - sig["stop"])
            if stop_dist < sig["price"] * 0.003:
                sig_ptr += 1
                continue

            total_eq = cash + margin_locked
            risk_amount = total_eq * RISK_PER_TRADE
            lots = max(1, int(risk_amount / (stop_dist * SA_MULT)))

            margin_per_lot = sig["price"] * SA_MULT * MARGIN_RATE
            total_margin = margin_per_lot * lots
            while total_margin > total_eq * 0.6 and lots > 1:
                lots -= 1
                total_margin = margin_per_lot * lots
            if lots < 1:
                sig_ptr += 1
                continue

            if sig["type"] == "BUY":
                pos_qty = lots
                pos_direction = "Long"
            else:
                pos_qty = -lots
                pos_direction = "Short"

            pos_entry = sig["price"]
            pos_stop = sig["stop"]
            pos_start_idx = idx
            pos_daily_dir = sig["daily_dir"]

            cash -= total_margin
            cash -= lots * FEE_PER_LOT
            margin_locked += total_margin
            sig_ptr += 1

        # Check exits
        if pos_qty != 0:
            exit_price = None
            exit_reason = None

            if pos_qty > 0:
                if low <= pos_stop:
                    exit_price = pos_stop
                    exit_reason = "StopLoss"
            else:
                if high >= pos_stop:
                    exit_price = pos_stop
                    exit_reason = "StopLoss"

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
                cash += margin_used

                if pos_qty > 0:
                    pnl = (exit_price - pos_entry) * pos_qty * SA_MULT
                else:
                    pnl = (pos_entry - exit_price) * abs(pos_qty) * SA_MULT

                cash -= abs(pos_qty) * FEE_PER_LOT
                cash += pnl

                trades.append(Trade(
                    entry_time=df30.iloc[pos_start_idx]["dt"],
                    exit_time=dt,
                    direction=pos_direction,
                    entry_price=pos_entry,
                    exit_price=exit_price,
                    quantity=abs(pos_qty),
                    pnl=pnl,
                    exit_reason=exit_reason,
                    daily_dir=pos_daily_dir,
                ))
                pos_qty = 0

        # Equity (daily aggregation for reporting)
        day = dt.date()
        eq = cash + margin_locked
        if pos_qty != 0:
            if pos_qty > 0:
                eq += (close - pos_entry) * pos_qty * SA_MULT
            else:
                eq += (pos_entry - close) * abs(pos_qty) * SA_MULT
        if day not in equity_daily or dt.hour >= 15:  # use EOD or last available
            equity_daily[day] = eq

    return {
        "trades": trades,
        "equity": [{"dt": pd.Timestamp(d), "equity": e} for d, e in sorted(equity_daily.items())],
    }


def report(result: dict, label: str):
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
    total_pnl = sum(t.pnl for t in trades)
    win_rate = nw / n * 100
    avg_w = np.mean([t.pnl for t in trades if t.pnl > 0]) if nw > 0 else 0
    avg_l = np.mean([t.pnl for t in trades if t.pnl <= 0]) if n > nw else 0

    long_t = [t for t in trades if t.direction == "Long"]
    short_t = [t for t in trades if t.direction == "Short"]

    print(f"  Trades: {n} | Win: {win_rate:.1f}% | PnL: {total_pnl:,.0f} ({total_pnl/INITIAL_CAPITAL*100:.1f}%)")
    print(f"  AvgWin: {avg_w:,.0f} | AvgLoss: {avg_l:,.0f} | PF: {abs(avg_w/avg_l) if avg_l else float('inf'):.2f}")

    if long_t:
        lpnl = sum(t.pnl for t in long_t)
        lwin = len([t for t in long_t if t.pnl > 0])
        print(f"  Long: {len(long_t)} ({100*len(long_t)/n:.0f}%) PnL={lpnl:,.0f} Win={lwin/len(long_t)*100:.0f}%")
    if short_t:
        spnl = sum(t.pnl for t in short_t)
        swin = len([t for t in short_t if t.pnl > 0])
        print(f"  Short: {len(short_t)} ({100*len(short_t)/n:.0f}%) PnL={spnl:,.0f} Win={swin/len(short_t)*100:.0f}%")

    # Daily-filter performance
    print(f"\n  By daily BI alignment:")
    for dfilt in ["Up", "Down", "Unknown"]:
        dt = [t for t in trades if t.daily_dir == dfilt]
        if dt:
            dpnl = sum(t.pnl for t in dt)
            dwin = len([t for t in dt if t.pnl > 0])
            print(f"    Daily {dfilt}: {len(dt)} trades, PnL={dpnl:,.0f}, Win={dwin/len(dt)*100:.0f}%")

    # Exit reasons
    reasons = {}
    for t in trades:
        r = t.exit_reason
        if r not in reasons:
            reasons[r] = {"n": 0, "pnl": 0, "wins": 0}
        reasons[r]["n"] += 1
        reasons[r]["pnl"] += t.pnl
        if t.pnl > 0: reasons[r]["wins"] += 1
    print(f"\n  Exit reasons:")
    for r, s in reasons.items():
        print(f"    {r}: {s['n']}x PnL={s['pnl']:,.0f} Win={s['wins']/s['n']*100:.0f}%")

    # Equity
    eq_vals = [e["equity"] for e in eq]
    eq_dates = [e["dt"] for e in eq]
    peak = eq_vals[0]
    max_dd, dd_start, dd_end = 0, eq_dates[0], eq_dates[0]
    cur_dd_start = eq_dates[0]
    for i, v in enumerate(eq_vals):
        if v > peak:
            peak = v; cur_dd_start = eq_dates[i]
        dd = (peak - v) / peak * 100
        if dd > max_dd:
            max_dd, dd_start, dd_end = dd, cur_dd_start, eq_dates[i]

    days = max(1, (eq_dates[-1] - eq_dates[0]).days)
    total_ret = (eq_vals[-1] - INITIAL_CAPITAL) / INITIAL_CAPITAL
    ann_ret = ((1 + total_ret) ** (365 / days) - 1) * 100

    daily_rets = [(eq_vals[i] - eq_vals[i-1]) / eq_vals[i-1] for i in range(1, len(eq_vals)) if eq_vals[i-1] > 0]
    sharpe = np.mean(daily_rets) / np.std(daily_rets) * np.sqrt(250) if daily_rets else 0

    mcl = cur = 0
    for t in trades:
        if t.pnl <= 0: cur += 1; mcl = max(mcl, cur)
        else: cur = 0

    print(f"\n  Equity: {eq_vals[-1]:,.0f} | Return: {total_ret*100:.1f}% | Ann: {ann_ret:.1f}%")
    print(f"  MaxDD: {max_dd:.1f}% ({dd_start.date()}~{dd_end.date()})")
    print(f"  Sharpe: {sharpe:.2f} | MaxConsLoss: {mcl}")

    # Yearly
    print(f"\n  Yearly:")
    for yr in sorted(set(t.entry_time.year for t in trades)):
        yt = [t for t in trades if t.entry_time.year == yr]
        ypnl = sum(t.pnl for t in yt)
        ywin = len([t for t in yt if t.pnl > 0])
        yeq = [e["equity"] for e in eq if e["dt"].year == yr]
        yret = (yeq[-1] - yeq[0]) / yeq[0] * 100 if yeq else 0
        print(f"    {yr}: {len(yt):>3}t, PnL={ypnl:>12,.0f}, Win={ywin/len(yt)*100:.0f}%, Ret={yret:.1f}%")


# ═══════════ MAIN ═══════════
if __name__ == "__main__":
    print("Loading data...")
    df_day, df_30m = load_data()
    print(f"Daily: {len(df_day)} bars | 30min: {len(df_30m)} bars")

    # ATR on 30min
    df_30m["atr"] = calc_atr(df_30m, ATR_PERIOD)

    # CZSC on both timeframes
    print("\nRunning CZSC...")
    raw_day = make_rawbars(df_day, Freq.D)
    czsc_day = CZSC(raw_day, max_bi_num=500)
    print(f"Daily: {len(czsc_day.fx_list)} FX, {len(czsc_day.bi_list)} BI")

    raw_30m = make_rawbars(df_30m, Freq.F30)
    czsc_30m = CZSC(raw_30m, max_bi_num=800)
    print(f"30min: {len(czsc_30m.fx_list)} FX, {len(czsc_30m.bi_list)} BI")

    # Daily BI → 30min direction map
    print("\nBuilding daily BI direction map...")
    daily_bi_map = build_daily_bi_map(czsc_day, df_30m)
    up_dates = sum(1 for v in daily_bi_map.values() if v == "Up")
    dn_dates = sum(1 for v in daily_bi_map.values() if v == "Down")
    print(f"  30min bars aligned: Up={up_dates}, Down={dn_dates}, Unknown={len(daily_bi_map)-up_dates-dn_dates}")

    # Price index for 30min
    price_idx_30m = {row["dt"]: (i, row) for i, (_, row) in enumerate(df_30m.iterrows())}

    # Split train/test
    train_30m = df_30m[df_30m["dt"].dt.year <= 2022].reset_index(drop=True)
    test_30m = df_30m[df_30m["dt"].dt.year >= 2023].reset_index(drop=True)

    # Generate signals per period
    print("\n=== V3: Daily-filtered 30min signals ===")

    # Train
    train_bars_30m = [b for b in raw_30m if b.dt.year <= 2022]
    train_czsc_30m = CZSC(train_bars_30m, max_bi_num=800)
    train_pidx = {row["dt"]: (i, row) for i, (_, row) in enumerate(train_30m.iterrows())}
    train_sigs = generate_signals_30m(train_30m, train_czsc_30m, daily_bi_map, train_pidx)
    print(f"Train: {len(train_sigs)} signals ({len([s for s in train_sigs if s['type']=='BUY'])}B/{len([s for s in train_sigs if s['type']=='SELL'])}S)")
    train_r = run_backtest(train_30m, train_sigs)

    # Test
    test_bars_30m = [b for b in raw_30m if b.dt.year >= 2023]
    test_czsc_30m = CZSC(test_bars_30m, max_bi_num=800)
    test_pidx = {row["dt"]: (i, row) for i, (_, row) in enumerate(test_30m.iterrows())}
    test_sigs = generate_signals_30m(test_30m, test_czsc_30m, daily_bi_map, test_pidx)
    print(f"Test: {len(test_sigs)} signals ({len([s for s in test_sigs if s['type']=='BUY'])}B/{len([s for s in test_sigs if s['type']=='SELL'])}S)")
    test_r = run_backtest(test_30m, test_sigs)

    # Full
    all_pidx = {row["dt"]: (i, row) for i, (_, row) in enumerate(df_30m.iterrows())}
    all_sigs = generate_signals_30m(df_30m, czsc_30m, daily_bi_map, all_pidx)
    print(f"Full: {len(all_sigs)} signals ({len([s for s in all_sigs if s['type']=='BUY'])}B/{len([s for s in all_sigs if s['type']=='SELL'])}S)")
    full_r = run_backtest(df_30m, all_sigs)

    # Reports
    report(train_r, "V3 TRAIN (2020-2022) 日线过滤+30min信号")
    report(test_r, "V3 TEST  (2023-2025) 日线过滤+30min信号")
    report(full_r, "V3 FULL  (2020-2025) 日线过滤+30min信号")

    # Comparison with V2
    print(f"\n{'='*70}")
    print(f"  V2 vs V3 对比")
    print(f"{'='*70}")
    print(f"  V2 (纯日线): 32 trades, 84.4% WR, +22.6% ret, 4.2% DD, Sharpe 1.32")
    print(f"  V3 (日线+30min): {len(full_r['trades'])} trades, {len([t for t in full_r['trades'] if t.pnl>0])/len(full_r['trades'])*100:.1f}% WR, +{sum(t.pnl for t in full_r['trades'])/INITIAL_CAPITAL*100:.1f}% ret")
