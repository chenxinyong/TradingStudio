"""
Phase A.4-6: 缠论策略回测 — SA 日线 BI 转折信号
"""
import pandas as pd
import numpy as np
from dataclasses import dataclass, field
from typing import List, Optional
from czsc import CZSC, RawBar, Freq, Direction

# ═══════════════════════════════════════
# Config
# ═══════════════════════════════════════
INITIAL_CAPITAL = 1_000_000      # 100万
RISK_PER_TRADE = 0.02             # 单笔风险 2%
SA_MULTIPLIER = 20                # 20吨/手
FEE_PER_LOT = 20                  # 双边 20元/手
MARGIN_RATE = 0.08                # 8%保证金
MIN_BI_POWER = 300                # 最小笔力度
MIN_BI_DAYS = 3                   # 笔最少K线数

# ═══════════════════════════════════════
# Data Structures
# ═══════════════════════════════════════
@dataclass
class Position:
    direction: int        # 1=long, -1=short
    entry_price: float
    entry_time: pd.Timestamp
    quantity: int         # lots
    stop_price: float
    bi_power: float

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

@dataclass
class BacktestResult:
    trades: List[Trade] = field(default_factory=list)
    equity_curve: List[dict] = field(default_factory=list)
    annual_stats: dict = field(default_factory=dict)

# ═══════════════════════════════════════
# Main
# ═══════════════════════════════════════
def main():
    # 1. Load data
    df = pd.read_parquet("c:/Works/ClaudeCode/TradingStudio/data/sa_chanlun/sa_day.parquet")
    df["dt"] = pd.to_datetime(df["dt"])
    print(f"SA daily: {len(df)} bars, {df['dt'].min().date()} ~ {df['dt'].max().date()}")

    # 2. CZSC analysis
    raw_bars = [RawBar('SA', row['dt'], Freq.D, row['open'], row['close'],
                        row['high'], row['low'], row['vol'], row['amount'], id=i)
                for i, (_, row) in enumerate(df.iterrows())]
    czsc = CZSC(raw_bars, max_bi_num=500)

    print(f"CZSC: {len(czsc.fx_list)} FX, {len(czsc.bi_list)} BI")

    # 3. Build BI signals → DataFrame for easy lookup
    bi_signals = []
    for bi in czsc.bi_list:
        bi_signals.append({
            "start_dt": bi.fx_a.dt,
            "end_dt": bi.fx_b.dt,
            "direction": "Up" if bi.direction == Direction.Up else "Down",
            "start_price": bi.fx_a.fx,   # fx value is the pivot price
            "end_price": bi.fx_b.fx,
            "change_pct": bi.change,      # BI 涨跌幅
            "power": bi.power,            # BI 力度
            "length": len(bi.bars),       # K线数
            "slope": bi.slope,
            "angle": bi.angle,
            "rsq": bi.rsq,
        })

    bi_df = pd.DataFrame(bi_signals)
    print(f"\nBI signals: {len(bi_df)}")
    print(f"  Up: {len(bi_df[bi_df['direction']=='Up'])}, Down: {len(bi_df[bi_df['direction']=='Down'])}")

    # 4. Generate trade signals at BI completion points
    # Strategy: When a down-BI ends and an up-BI begins → BUY
    #           When an up-BI ends and a down-BI begins → SELL
    # Filter: minimum BI power and length

    trades = generate_signals(bi_df, df, czsc)

    # 5. Run backtest
    result = run_backtest(df, trades, czsc)

    # 6. Report
    print_report(result, df)


def generate_signals(bi_df: pd.DataFrame, price_df: pd.DataFrame, czsc: CZSC) -> List[dict]:
    """Generate trade signals from BI turns"""
    signals = []

    for i in range(1, len(czsc.bi_list)):
        prev_bi = czsc.bi_list[i - 1]
        curr_bi = czsc.bi_list[i]

        # Check BI quality
        if prev_bi.power < MIN_BI_POWER or len(prev_bi.bars) < MIN_BI_DAYS:
            continue

        signal_dt = curr_bi.fx_a.dt  # Signal time = current BI start (previous BI end confirmed)

        # BUY: Down BI ends → Up BI begins (底分型)
        if prev_bi.direction == Direction.Down and curr_bi.direction == Direction.Up:
            entry_price = price_df.loc[price_df["dt"] == signal_dt, "close"].values
            if len(entry_price) == 0:
                continue
            entry_price = float(entry_price[0])

            signals.append({
                "dt": signal_dt,
                "type": "BUY",
                "price": entry_price,
                "prev_bi_power": prev_bi.power,
                "prev_bi_change": prev_bi.change,
                "prev_bi_days": len(prev_bi.bars),
                "stop_price": prev_bi.fx_b.fx,  # 底分型最低点
            })

        # SELL: Up BI ends → Down BI begins (顶分型)
        elif prev_bi.direction == Direction.Up and curr_bi.direction == Direction.Down:
            entry_price = price_df.loc[price_df["dt"] == signal_dt, "close"].values
            if len(entry_price) == 0:
                continue
            entry_price = float(entry_price[0])

            signals.append({
                "dt": signal_dt,
                "type": "SELL",
                "price": entry_price,
                "prev_bi_power": prev_bi.power,
                "prev_bi_change": prev_bi.change,
                "prev_bi_days": len(prev_bi.bars),
                "stop_price": prev_bi.fx_b.fx,  # 顶分型最高点
            })

    return signals


def run_backtest(df: pd.DataFrame, signals: List[dict], czsc: CZSC) -> BacktestResult:
    """Bar-level backtest engine"""
    result = BacktestResult()
    cash = INITIAL_CAPITAL
    pos: Optional[Position] = None

    # Create price index for fast lookup
    price_map = {row["dt"]: row for _, row in df.iterrows()}

    signal_idx = 0
    daily_equity = {}

    for _, bar in df.iterrows():
        dt = bar["dt"]
        close = bar["close"]
        high = bar["high"]
        low = bar["low"]

        # Check pending signals
        while signal_idx < len(signals) and signals[signal_idx]["dt"] <= dt:
            sig = signals[signal_idx]

            # Only execute signal at or after its timestamp
            if sig["dt"] != dt or pos is not None:
                # Skip if we already have a position (no pyramiding in v1)
                signal_idx += 1
                continue

            # Position sizing
            stop_dist = abs(sig["price"] - sig["stop_price"])
            if stop_dist < sig["price"] * 0.005:  # Stop too close (< 0.5%)
                signal_idx += 1
                continue

            risk_amount = cash * RISK_PER_TRADE
            lots = int(risk_amount / (stop_dist * SA_MULTIPLIER))
            if lots < 1:
                lots = 1

            # Margin check
            margin_required = sig["price"] * SA_MULTIPLIER * lots * MARGIN_RATE
            if margin_required > cash * 0.5:  # Max 50% margin usage
                lots = int(cash * 0.5 / (sig["price"] * SA_MULTIPLIER * MARGIN_RATE))
                if lots < 1:
                    signal_idx += 1
                    continue

            if sig["type"] == "BUY":
                pos = Position(
                    direction=1,
                    entry_price=sig["price"],
                    entry_time=dt,
                    quantity=lots,
                    stop_price=sig["stop_price"],
                    bi_power=sig["prev_bi_power"],
                )
                cash -= lots * FEE_PER_LOT  # Entry fee
            elif sig["type"] == "SELL":
                pos = Position(
                    direction=-1,
                    entry_price=sig["price"],
                    entry_time=dt,
                    quantity=lots,
                    stop_price=sig["stop_price"],
                    bi_power=sig["prev_bi_power"],
                )
                cash -= lots * FEE_PER_LOT

            signal_idx += 1

        # Check stop loss / take profit / reverse signal
        if pos is not None:
            exit_reason = None
            exit_price = None

            if pos.direction == 1:  # Long
                if low <= pos.stop_price:
                    exit_reason = "StopLoss"
                    exit_price = pos.stop_price
                # Also check for reverse signal (new SELL signal = exit long)
                for sig2 in signals:
                    if sig2["dt"] == dt and sig2["type"] == "SELL":
                        exit_reason = "ReverseSignal"
                        exit_price = sig2["price"]
                        break
            else:  # Short
                if high >= pos.stop_price:
                    exit_reason = "StopLoss"
                    exit_price = pos.stop_price
                for sig2 in signals:
                    if sig2["dt"] == dt and sig2["type"] == "BUY":
                        exit_reason = "ReverseSignal"
                        exit_price = sig2["price"]
                        break

            if exit_reason:
                # Calculate PnL
                if pos.direction == 1:
                    pnl = (exit_price - pos.entry_price) * pos.quantity * SA_MULTIPLIER
                else:
                    pnl = (pos.entry_price - exit_price) * pos.quantity * SA_MULTIPLIER

                pnl -= pos.quantity * FEE_PER_LOT  # Exit fee
                pnl_pct = pnl / (pos.entry_price * SA_MULTIPLIER * pos.quantity * MARGIN_RATE) * 100

                cash += pos.entry_price * SA_MULTIPLIER * pos.quantity * MARGIN_RATE  # Return margin
                cash += pnl

                result.trades.append(Trade(
                    entry_time=pos.entry_time,
                    exit_time=dt,
                    direction="Long" if pos.direction == 1 else "Short",
                    entry_price=pos.entry_price,
                    exit_price=exit_price,
                    quantity=pos.quantity,
                    pnl=pnl,
                    pnl_pct=pnl_pct,
                    exit_reason=exit_reason,
                ))
                pos = None

        # Record daily equity
        equity = cash
        if pos is not None:
            # Mark-to-market
            margin = pos.entry_price * SA_MULTIPLIER * pos.quantity * MARGIN_RATE
            if pos.direction == 1:
                unrealized = (close - pos.entry_price) * pos.quantity * SA_MULTIPLIER
            else:
                unrealized = (pos.entry_price - close) * pos.quantity * SA_MULTIPLIER
            equity = cash + margin + unrealized

        daily_equity[dt] = equity

    # Final equity curve
    for dt, eq in sorted(daily_equity.items()):
        result.equity_curve.append({"dt": dt, "equity": eq})

    return result


def print_report(result: BacktestResult, df: pd.DataFrame):
    """Print comprehensive backtest report"""
    trades = result.trades
    equity = result.equity_curve

    if not trades:
        print("\n!!! No trades generated !!!")
        return

    # Basic stats
    n_trades = len(trades)
    n_wins = len([t for t in trades if t.pnl > 0])
    n_losses = n_trades - n_wins
    total_pnl = sum(t.pnl for t in trades)
    avg_win = np.mean([t.pnl for t in trades if t.pnl > 0]) if n_wins > 0 else 0
    avg_loss = np.mean([t.pnl for t in trades if t.pnl <= 0]) if n_losses > 0 else 0
    win_rate = n_wins / n_trades * 100
    profit_factor = abs(sum(t.pnl for t in trades if t.pnl > 0) / sum(t.pnl for t in trades if t.pnl <= 0)) if n_losses > 0 else float('inf')

    print(f"\n{'='*60}")
    print(f"  SA 缠论策略回测报告")
    print(f"{'='*60}")
    print(f"\n=== 交易统计 ===")
    print(f"  总交易: {n_trades}")
    print(f"  胜率: {win_rate:.1f}% ({n_wins}W / {n_losses}L)")
    print(f"  总盈亏: {total_pnl:,.0f} CNY ({total_pnl/INITIAL_CAPITAL*100:.1f}%)")
    print(f"  平均盈利: {avg_win:,.0f} CNY")
    print(f"  平均亏损: {avg_loss:,.0f} CNY")
    print(f"  盈亏比: {abs(avg_win/avg_loss):.2f}" if avg_loss != 0 else "  盈亏比: INF")
    print(f"  盈利因子: {profit_factor:.2f}")

    # By direction
    for d in ["Long", "Short"]:
        dtrades = [t for t in trades if t.direction == d]
        if dtrades:
            dpnl = sum(t.pnl for t in dtrades)
            dwin = len([t for t in dtrades if t.pnl > 0])
            print(f"  {d}: {len(dtrades)} trades, PnL={dpnl:,.0f}, Win={dwin/len(dtrades)*100:.1f}%")

    # By exit reason
    reasons = {}
    for t in trades:
        r = t.exit_reason
        if r not in reasons:
            reasons[r] = {"count": 0, "pnl": 0, "wins": 0}
        reasons[r]["count"] += 1
        reasons[r]["pnl"] += t.pnl
        if t.pnl > 0:
            reasons[r]["wins"] += 1

    print(f"\n=== 出场原因 ===")
    for r, s in reasons.items():
        print(f"  {r}: {s['count']}次, PnL={s['pnl']:,.0f}, Win={s['wins']/s['count']*100:.1f}%")

    # Equity curve analysis
    if equity:
        eq_values = [e["equity"] for e in equity]
        eq_dates = [e["dt"] for e in equity]

        # Max drawdown
        peak = eq_values[0]
        max_dd = 0
        max_dd_start = max_dd_end = eq_dates[0]
        dd_start = eq_dates[0]
        for i, eq in enumerate(eq_values):
            if eq > peak:
                peak = eq
                dd_start = eq_dates[i]
            dd = (peak - eq) / peak * 100
            if dd > max_dd:
                max_dd = dd
                max_dd_start = dd_start
                max_dd_end = eq_dates[i]

        # Annualized return
        days = (eq_dates[-1] - eq_dates[0]).days
        total_return = (eq_values[-1] - INITIAL_CAPITAL) / INITIAL_CAPITAL
        ann_return = ((1 + total_return) ** (365 / days) - 1) * 100 if days > 0 else 0

        # Sharpe (daily)
        daily_rets = []
        for i in range(1, len(eq_values)):
            if eq_values[i-1] > 0:
                daily_rets.append((eq_values[i] - eq_values[i-1]) / eq_values[i-1])
        sharpe = np.mean(daily_rets) / np.std(daily_rets) * np.sqrt(250) if daily_rets else 0

        print(f"\n=== 资金曲线 ===")
        print(f"  初始资金: {INITIAL_CAPITAL:,.0f}")
        print(f"  最终权益: {eq_values[-1]:,.0f}")
        print(f"  总收益率: {total_return*100:.1f}%")
        print(f"  年化收益: {ann_return:.1f}%")
        print(f"  最大回撤: {max_dd:.1f}% ({max_dd_start.date()} ~ {max_dd_end.date()})")
        print(f"  夏普比率: {sharpe:.2f}")

        # Max consecutive losses
        max_consec_loss = cur_consec = 0
        for t in trades:
            if t.pnl <= 0:
                cur_consec += 1
                max_consec_loss = max(max_consec_loss, cur_consec)
            else:
                cur_consec = 0
        print(f"  最大连续亏损: {max_consec_loss} 笔")

    # Yearly breakdown
    print(f"\n=== 分年统计 ===")
    for yr in range(2020, 2026):
        yr_trades = [t for t in trades if t.entry_time.year == yr]
        if not yr_trades:
            continue
        yr_pnl = sum(t.pnl for t in yr_trades)
        yr_wins = len([t for t in yr_trades if t.pnl > 0])
        yr_eq = [e["equity"] for e in equity if e["dt"].year == yr]
        yr_ret = (yr_eq[-1] - yr_eq[0]) / yr_eq[0] * 100 if yr_eq else 0
        print(f"  {yr}: {len(yr_trades)}笔, PnL={yr_pnl:,.0f}, Win={yr_wins/len(yr_trades)*100:.1f}%, Return={yr_ret:.1f}%")


if __name__ == "__main__":
    main()
