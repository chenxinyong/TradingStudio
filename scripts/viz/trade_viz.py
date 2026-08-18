"""
交易信号可视化 — K线图叠加买卖点标记
用法: python scripts/trade_viz.py <品种> [时间段]
示例: python scripts/trade_viz.py RB 2024-01 2024-06
"""
import sys, json, webbrowser
from pathlib import Path
from datetime import datetime
import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots

DB = Path("data/bars_history.duckdb")
OUT = Path("data/charts")

def load_bars(instrument_id, table="bars_15min"):
    conn = duckdb.connect(str(DB), read_only=True)
    df = conn.execute(f"""
        SELECT bar_time::TIMESTAMP as dt, open/1e7 as o, high/1e7 as h, low/1e7 as l, close/1e7 as c, volume as v
        FROM {table} WHERE instrument_id = '{instrument_id}' ORDER BY dt
    """).df()
    conn.close()
    return df

def load_trades(report_pattern, instrument, start=None, end=None):
    """从报告加载交易"""
    report_dir = Path("configs/bollinger_top30")
    path = report_dir / f"bb_{instrument}_15min.report.json"
    if not path.exists():
        path = Path(f"configs/bollinger_{instrument.lower()}_15min.report.json")
    if not path.exists():
        print(f"Report not found: {path}")
        return []

    with open(path) as f:
        trades = json.load(f)['strategyReports'][0]['trades']

    filtered = []
    for t in trades:
        et = t['entryTime'][:10]
        if start and et < start: continue
        if end and et > end: continue
        filtered.append(t)
    return filtered

def make_chart(df, trades, instrument, freq, out_file):
    fig = make_subplots(rows=2, cols=1, shared_xaxes=True,
                        row_heights=[0.65, 0.35], vertical_spacing=0.03)

    # K线
    fig.add_trace(go.Candlestick(x=df['dt'], open=df['o'], high=df['h'], low=df['l'], close=df['c'],
        name="K线", increasing_line_color='#ef5350', decreasing_line_color='#26a69a'), row=1, col=1)

    # 成交量
    colors = ['#ef5350' if c >= o else '#26a69a' for o, c in zip(df['o'], df['c'])]
    fig.add_trace(go.Bar(x=df['dt'], y=df['v'], name="量", marker_color=colors, opacity=0.4), row=2, col=1)

    # 交易标记
    for t in trades:
        et = datetime.fromisoformat(t['entryTime'].replace('Z', '+00:00'))
        xt = datetime.fromisoformat(t['exitTime'].replace('Z', '+00:00'))
        is_win = t['pnL'] > 0
        color = '#00ff00' if is_win else '#ff4444'
        marker = 'triangle-up' if t['pnL'] > 0 else 'triangle-down'

        # 入场标记
        fig.add_trace(go.Scatter(x=[et], y=[t['entryPrice']], mode='markers',
            marker=dict(symbol=marker, size=10, color=color, line=dict(width=1, color='white')),
            name=f"{'赢' if is_win else '亏'} {t['pnL']:,.0f}",
            showlegend=False, hovertext=f"入场@{t['entryPrice']:.0f} | {t['quantity']}手 | PnL={t['pnL']:,.0f}"), row=1, col=1)

        # 出场标记
        fig.add_trace(go.Scatter(x=[xt], y=[t['exitPrice']], mode='markers',
            marker=dict(symbol='x', size=8, color=color),
            showlegend=False, hovertext=f"出场@{t['exitPrice']:.0f} | PnL={t['pnL']:,.0f}"), row=1, col=1)

    # 统计
    wins = [t for t in trades if t['pnL'] > 0]
    wr = len(wins)/len(trades)*100 if trades else 0
    total = sum(t['pnL'] for t in trades)
    title = f"{instrument} {freq} | {len(trades)}笔 胜率{wr:.0f}% PnL={total:,.0f}"

    fig.update_layout(title=title, template='plotly_dark', height=800,
        hovermode='x unified', xaxis_rangeslider_visible=False)

    fig.write_html(str(out_file), include_plotlyjs='cdn')
    webbrowser.open(f"file:///{out_file.resolve()}")
    print(f"Saved: {out_file}")

if __name__ == '__main__':
    inst = sys.argv[1] if len(sys.argv) > 1 else 'RB'
    start = sys.argv[2] if len(sys.argv) > 2 else None
    end = sys.argv[3] if len(sys.argv) > 3 else None
    inst_id = inst.lower() + '000'

    print(f"Loading {inst} ({inst_id})...")
    df = load_bars(inst_id)
    trades = load_trades('', inst, start, end)
    print(f"  Bars: {len(df):,} | Trades: {len(trades)}")

    OUT.mkdir(exist_ok=True)
    out = OUT / f"trade_{inst}_{start or 'all'}_{end or ''}.html"
    make_chart(df, trades, inst, '15min', out)
