"""
SA 加权价格指数可视化 — 交互式 K线图
用法: python visualize_weighted.py [品种] [频率]
示例: python visualize_weighted.py SA day
      python visualize_weighted.py rb 30min
"""
import sys
import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots
import webbrowser
from pathlib import Path

DB = "c:/Works/ClaudeCode/TradingStudio/data/bars_history.duckdb"
OUT_DIR = Path("c:/Works/ClaudeCode/TradingStudio/data/charts")
OUT_DIR.mkdir(exist_ok=True)

# 参数
product = sys.argv[1] if len(sys.argv) > 1 else "SA"
freq = sys.argv[2] if len(sys.argv) > 2 else "day"

conn = duckdb.connect(DB)
table = f"bars_{product}_1min".lower()

# 检查表是否存在
exists = conn.execute(f"SELECT COUNT(*) FROM information_schema.tables WHERE table_name='{table}'").fetchone()[0]
if not exists:
    # 尝试从 bars_1min 直接查
    print(f"表 {table} 不存在，从 bars_1min 直接聚合...")
    table = "bars_1min"
    use_raw = True
else:
    use_raw = False

# 按频率聚合
if freq == "day":
    if use_raw:
        sql = f"""
            SELECT bar_time::DATE as dt,
                SUM(open * volume) / NULLIF(SUM(volume),0) / 1e7 as open,
                MAX(high) / 1e7 as high,
                MIN(low) / 1e7 as low,
                SUM(close * volume) / NULLIF(SUM(volume),0) / 1e7 as close,
                SUM(volume) as volume
            FROM bars_1min
            WHERE instrument_id LIKE '{product}%' AND volume > 0
            GROUP BY bar_time::DATE ORDER BY dt
        """
    else:
        sql = f"""
            SELECT trading_day as dt,
                FIRST(open) / 1e7 as open,
                MAX(high) / 1e7 as high,
                MIN(low) / 1e7 as low,
                LAST(close) / 1e7 as close,
                SUM(volume) as volume
            FROM {table}
            GROUP BY trading_day ORDER BY dt
        """
elif freq == "15min":
    if use_raw:
        sql = f"""
            SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                   INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / 15) * 15) MINUTE as dt,
                SUM(open * volume) / NULLIF(SUM(volume),0) / 1e7 as open,
                MAX(high) / 1e7 as high,
                MIN(low) / 1e7 as low,
                SUM(close * volume) / NULLIF(SUM(volume),0) / 1e7 as close,
                SUM(volume) as volume
            FROM bars_1min
            WHERE instrument_id LIKE '{product}%' AND volume > 0
            GROUP BY dt ORDER BY dt
        """
    else:
        sql = f"""
            SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                   INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / 15) * 15) MINUTE as dt,
                FIRST(open) / 1e7 as open,
                MAX(high) / 1e7 as high,
                MIN(low) / 1e7 as low,
                LAST(close) / 1e7 as close,
                SUM(volume) as volume
            FROM {table}
            GROUP BY dt ORDER BY dt
        """
elif freq == "30min":
    if use_raw:
        sql = f"""
            SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                   INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / 30) * 30) MINUTE as dt,
                SUM(open * volume) / NULLIF(SUM(volume),0) / 1e7 as open,
                MAX(high) / 1e7 as high,
                MIN(low) / 1e7 as low,
                SUM(close * volume) / NULLIF(SUM(volume),0) / 1e7 as close,
                SUM(volume) as volume
            FROM bars_1min
            WHERE instrument_id LIKE '{product}%' AND volume > 0
            GROUP BY dt ORDER BY dt
        """
    else:
        sql = f"""
            SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                   INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / 30) * 30) MINUTE as dt,
                FIRST(open) / 1e7 as open,
                MAX(high) / 1e7 as high,
                MIN(low) / 1e7 as low,
                LAST(close) / 1e7 as close,
                SUM(volume) as volume
            FROM {table}
            GROUP BY dt ORDER BY dt
        """
else:
    sql = f"""
        SELECT bar_time::TIMESTAMP as dt,
            open / 1e7 as open, high / 1e7 as high,
            low / 1e7 as low, close / 1e7 as close,
            volume
        FROM {table} ORDER BY bar_time
    """

df = conn.execute(sql).df()
conn.close()

print(f"数据: {len(df):,} bars, {freq} 频率, 品种 {product}")
print(f"价格: {df['close'].min():.0f} ~ {df['close'].max():.0f}")
print(f"时间: {df['dt'].min()} ~ {df['dt'].max()}")

# ── 构建图表 ──
fig = make_subplots(
    rows=3, cols=1, shared_xaxes=True,
    vertical_spacing=0.02,
    row_heights=[0.6, 0.2, 0.2],
    subplot_titles=(f"{product} 加权价格指数 ({freq})", "成交量", "合约数"),
)

# 1. K线
fig.add_trace(
    go.Candlestick(
        x=df["dt"], open=df["open"], high=df["high"],
        low=df["low"], close=df["close"],
        name="价格",
        increasing_line_color="#ef5350", decreasing_line_color="#26a69a",
    ),
    row=1, col=1,
)

# 2. 成交量
colors = ["#ef5350" if c >= o else "#26a69a" for o, c in zip(df["open"], df["close"])]
fig.add_trace(
    go.Bar(x=df["dt"], y=df["volume"], name="成交量",
           marker_color=colors, opacity=0.5),
    row=2, col=1,
)

# 3. 合约数（如果可用）
if "active_contracts" in df.columns:
    fig.add_trace(
        go.Scatter(x=df["dt"], y=df["active_contracts"], name="活跃合约",
                   mode="lines", line=dict(color="#ff9800", width=1)),
        row=3, col=1,
    )

# ── 移除非交易时间空隙 ──
# 日线：仅移除周末
# 日内：移除周末 + 夜盘间隙 + 午休 + 小节歇
if freq == "day":
    rangebreaks = [
        dict(bounds=["sat", "mon"]),  # 周末
    ]
else:
    rangebreaks = [
        dict(bounds=["sat", "mon"]),           # 周末
        dict(bounds=[15, 21], pattern="hour"), # 日盘收盘→夜盘开盘 (15:00-21:00)
        dict(bounds=[23, 9], pattern="hour"),  # 夜盘收盘→次日日盘 (23:00-09:00)
        dict(bounds=[11.5, 13.5], pattern="hour"),  # 午休 (11:30-13:30)
        dict(bounds=[10.25, 10.5], pattern="hour"), # 小节歇 (10:15-10:30)
    ]

# 布局
fig.update_layout(
    title=dict(text=f"{product} 加权价格指数 ({freq})", font=dict(size=18)),
    xaxis=dict(
        rangeslider_visible=False,
        rangebreaks=rangebreaks,
    ),
    template="plotly_dark",
    height=900,
    hovermode="x unified",
    margin=dict(l=10, r=10, t=50, b=10),
)
fig.update_yaxes(title_text="价格 (元)", row=1, col=1)
fig.update_yaxes(title_text="成交量", row=2, col=1)

# 保存
out_file = OUT_DIR / f"{product}_{freq}.html"
fig.write_html(out_file)
print(f"\n图表已保存: {out_file}")

# 打开浏览器
webbrowser.open(f"file:///{out_file.resolve()}")
print("已在浏览器中打开")
