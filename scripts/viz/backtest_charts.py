"""回测分析图表生成 — 从 report.json 生成 6 张分析图"""
import json, sys
from pathlib import Path
from datetime import datetime
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
import numpy as np

plt.rcParams['font.family'] = 'sans-serif'
plt.rcParams['font.sans-serif'] = ['SimHei', 'DejaVu Sans']
plt.rcParams['axes.unicode_minus'] = False

def load(path):
    with open(path) as f: return json.load(f)

def rebuild_equity(trades, starting_capital, start_date, end_date):
    """从交易记录重建权益曲线"""
    # Sort trades by exit time
    sorted_trades = sorted(trades, key=lambda t: t['exitTime'])
    points = [(datetime.fromisoformat(start_date), starting_capital)]
    equity = starting_capital
    for t in sorted_trades:
        exit_time = datetime.fromisoformat(t['exitTime'].replace('Z', '+00:00'))
        equity += t['pnL']
        points.append((exit_time, equity))
    # Add final point
    points.append((datetime.fromisoformat(end_date), equity))
    return points

def make_charts(report_path, out_dir):
    r = load(report_path)
    sr = r['strategyReports'][0]
    trades = sr.get('trades', [])
    starting = sr['startingCapital']

    if not trades:
        print("No trades"); return

    # Rebuild equity from trades
    first_trade = min(t['entryTime'] for t in trades)
    last_trade = max(t['exitTime'] for t in trades)
    equity = rebuild_equity(trades, starting, first_trade, last_trade)

    times = [e[0] for e in equity]
    eq = np.array([e[1] for e in equity])

    # ── Fig 1: Equity + Drawdown ──
    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(14, 8), height_ratios=[3, 1])
    ax1.plot(times, eq, '#1a73e8', linewidth=0.8, label='Equity')
    ax1.axhline(starting, color='gray', linestyle='--', alpha=0.5, label=f'Start ¥{starting:,.0f}')
    ax1.set_title(f'RB 双均线(10/30) 15min — {times[0].year}-{times[-1].year} Equity Curve', fontsize=13, fontweight='bold')
    ax1.yaxis.set_major_formatter(mticker.FuncFormatter(lambda x, _: f'¥{x/1e6:.1f}M'))
    ax1.legend(loc='upper left'); ax1.grid(alpha=0.3)

    # Drawdown
    peak = np.maximum.accumulate(eq)
    dd = (eq - peak) / peak * 100
    ax2.fill_between(times, dd, 0, color='#e74c3c', alpha=0.3)
    ax2.plot(times, dd, '#e74c3c', linewidth=0.5)
    ax2.set_ylabel('Drawdown %'); ax2.grid(alpha=0.3)
    max_dd = dd.min()
    ax2.set_title(f'Drawdown (Max: {max_dd:.1f}%)', fontsize=10)

    fig.tight_layout()
    fig.savefig(out_dir / '01_equity_drawdown.png', dpi=150, bbox_inches='tight')
    plt.close(fig)
    print(f'[1/6] Equity + Drawdown')

    # ── Fig 2: Monthly Returns Heatmap ──
    monthly = {}
    for t, e in zip(times, eq):
        key = f"{t.year}-{t.month:02d}"
        monthly[key] = e
    months = sorted(monthly.keys())
    rets = []
    for i in range(1, len(months)):
        r_m = (monthly[months[i]] - monthly[months[i-1]]) / monthly[months[i-1]] * 100
        y, m = months[i].split('-')
        rets.append((int(y), int(m), r_m))

    if rets:
        years = sorted(set(r[0] for r in rets))
        data = np.full((12, len(years)), np.nan)
        for y, m, r_m in rets:
            data[m-1, years.index(y)] = r_m

        fig, ax = plt.subplots(figsize=(14, 6))
        cmap = plt.cm.RdYlGn
        im = ax.imshow(data, cmap=cmap, aspect='auto', vmin=-15, vmax=15)
        ax.set_xticks(range(len(years))); ax.set_xticklabels(years)
        ax.set_yticks(range(12)); ax.set_yticklabels(['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'])
        for i in range(12):
            for j in range(len(years)):
                v = data[i, j]
                if not np.isnan(v):
                    ax.text(j, i, f'{v:.1f}%', ha='center', va='center', fontsize=8,
                            color='white' if abs(v) > 8 else 'black')
        ax.set_title('Monthly Returns (%)', fontsize=13, fontweight='bold')
        plt.colorbar(im, ax=ax, shrink=0.8)
        fig.tight_layout()
        fig.savefig(out_dir / '02_monthly_returns.png', dpi=150, bbox_inches='tight')
        plt.close(fig)
    print('[2/6] Monthly Returns')

    # ── Fig 3: Rolling Sharpe (90-day) ──
    daily_ret = []
    for i in range(1, len(eq)):
        if eq[i-1] > 0:
            daily_ret.append((eq[i] - eq[i-1]) / eq[i-1])
        else:
            daily_ret.append(0)

    if len(daily_ret) > 90:
        window = 90
        rolling_sharpe = []
        for i in range(window, len(daily_ret)):
            w = daily_ret[i-window:i]
            mu = np.mean(w)
            sd = np.std(w) or 1e-10
            rolling_sharpe.append((times[i], mu / sd * np.sqrt(252)))

        fig, ax = plt.subplots(figsize=(14, 3))
        r_times, r_vals = zip(*rolling_sharpe)
        ax.plot(r_times, r_vals, '#2ecc71', linewidth=0.8)
        ax.axhline(0, color='gray', linestyle='--', alpha=0.5)
        ax.axhline(1, color='green', linestyle='--', alpha=0.3, label='Sharpe=1')
        ax.fill_between(r_times, 0, r_vals, alpha=0.15, color='#2ecc71')
        ax.set_title('Rolling 90-Day Sharpe Ratio', fontsize=13, fontweight='bold')
        ax.legend(); ax.grid(alpha=0.3)
        fig.tight_layout()
        fig.savefig(out_dir / '03_rolling_sharpe.png', dpi=150, bbox_inches='tight')
        plt.close(fig)
    print('[3/6] Rolling Sharpe')

    # ── Fig 4: Trade PnL Distribution ──
    if trades:
        pnls = [t['pnL'] for t in trades]
        wins = [p for p in pnls if p > 0]
        losses = [p for p in pnls if p <= 0]

        fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(14, 4))
        ax1.hist(wins, bins=30, color='#27ae60', alpha=0.7, edgecolor='white', label=f'Wins ({len(wins)})')
        ax1.hist(losses, bins=30, color='#e74c3c', alpha=0.7, edgecolor='white', label=f'Losses ({len(losses)})')
        ax1.axvline(np.mean(wins), color='darkgreen', linestyle='--', label=f'Avg Win ¥{np.mean(wins):,.0f}')
        ax1.axvline(np.mean(losses), color='darkred', linestyle='--', label=f'Avg Loss ¥{np.mean(losses):,.0f}')
        ax1.set_title('Trade PnL Distribution', fontweight='bold')
        ax1.legend(fontsize=8); ax1.grid(alpha=0.2)

        cum_pnl = np.cumsum(pnls)
        colors = ['#27ae60' if p > 0 else '#e74c3c' for p in pnls]
        ax2.bar(range(len(pnls)), cum_pnl, color=colors, width=1.0, alpha=0.8)
        ax2.axhline(0, color='gray', linestyle='-', alpha=0.3)
        ax2.set_title(f'Cumulative PnL (¥{cum_pnl[-1]:,.0f})', fontweight='bold')
        ax2.grid(alpha=0.2)

        fig.tight_layout()
        fig.savefig(out_dir / '04_trade_analysis.png', dpi=150, bbox_inches='tight')
        plt.close(fig)
    print('[4/6] Trade Analysis')

    # ── Fig 5: Annual Breakdown ──
    if trades:
        annual = {}
        for t in trades:
            yr = datetime.fromisoformat(t['exitTime'].replace('Z','+00:00')).year
            if yr not in annual: annual[yr] = {'wins': 0, 'losses': 0, 'pnl': 0, 'fees': 0}
            a = annual[yr]
            if t['pnL'] > 0: a['wins'] += 1
            else: a['losses'] += 1
            a['pnl'] += t['pnL']
            a['fees'] += t['fee']

        years = sorted(annual.keys())
        fig, ax = plt.subplots(figsize=(14, 5))
        x = np.arange(len(years)); w = 0.35
        pnls = [annual[y]['pnl'] for y in years]
        wins_c = [annual[y]['wins'] for y in years]
        losses_c = [annual[y]['losses'] for y in years]

        bars = ax.bar(x, pnls, w, color=['#27ae60' if p > 0 else '#e74c3c' for p in pnls], edgecolor='white')
        for i, (b, p) in enumerate(zip(bars, pnls)):
            ax.text(b.get_x() + b.get_width()/2, b.get_height() + (5000 if p > 0 else -5000),
                    f'¥{p:,.0f}\n{wins_c[i]}W/{losses_c[i]}L',
                    ha='center', va='bottom' if p > 0 else 'top', fontsize=9, fontweight='bold')

        ax.set_xticks(x); ax.set_xticklabels(years)
        ax.set_title('Annual PnL Breakdown', fontsize=13, fontweight='bold')
        ax.axhline(0, color='black', linewidth=0.5)
        ax.yaxis.set_major_formatter(mticker.FuncFormatter(lambda x, _: f'¥{x/1e3:.0f}K'))
        ax.grid(alpha=0.2, axis='y')
        fig.tight_layout()
        fig.savefig(out_dir / '05_annual_breakdown.png', dpi=150, bbox_inches='tight')
        plt.close(fig)
    print('[5/6] Annual Breakdown')

    # ── Fig 6: Summary Dashboard ──
    fig, ax = plt.subplots(figsize=(12, 6))
    ax.axis('off')

    kpis = [
        ('策略', 'RB 双均线(10/30) 15min'),
        ('回测周期', f'{times[0].strftime("%Y-%m-%d")} → {times[-1].strftime("%Y-%m-%d")}'),
        ('', ''),
        ('初始资金', f'¥{starting:,.0f}'),
        ('最终权益', f'¥{eq[-1]:,.0f}'),
        ('总收益率', f'{(eq[-1]/starting - 1)*100:.1f}%'),
        ('年化收益', f'{((eq[-1]/starting)**(1/(max(1,len(daily_ret))/252)) - 1)*100:.1f}%' if daily_ret else '-'),
        ('最大回撤', f'{max_dd:.1f}%'),
        ('', ''),
        ('总交易', f'{len(trades)} 笔'),
        ('胜率', f'{sr["winRate"]*100:.1f}%'),
        ('盈亏比', f'{sr.get("profitLossRatio", 0):.2f}'),
        ('手续费', f'¥{sr["totalFees"]:,.0f}'),
        ('滑点成本', f'¥{sr["totalSlippage"]:,.0f}'),
        ('', ''),
        ('Sharpe', f'{sr["sharpeRatio"]:.2f}'),
        ('Sortino', f'{sr["sortinoRatio"]:.2f}'),
    ]

    for i, (label, value) in enumerate(kpis):
        y = 1 - i * 0.05
        if label:
            ax.text(0.1, y, label + ':', fontsize=11, fontweight='bold', ha='left', va='center')
            ax.text(0.55, y, value, fontsize=11, ha='left', va='center',
                    fontweight='bold' if label in ('策略','总收益率','最大回撤','胜率','Sharpe') else 'normal')
        else:
            pass  # spacer

    ax.set_title('Backtest Summary — RB MaCross 15min', fontsize=16, fontweight='bold', pad=20)
    fig.tight_layout()
    fig.savefig(out_dir / '06_summary.png', dpi=150, bbox_inches='tight')
    plt.close(fig)
    print('[6/6] Summary Dashboard')
    print(f'\nDone! Charts saved to: {out_dir}')

if __name__ == '__main__':
    report = sys.argv[1] if len(sys.argv) > 1 else 'configs/macross_rb_15min.report.json'
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path('data/charts')
    out.mkdir(parents=True, exist_ok=True)
    make_charts(report, out)
