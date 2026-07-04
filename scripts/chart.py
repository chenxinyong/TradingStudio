"""
TradingStudio 行情可视化 — 交互式 K 线图
用法:
  python scripts/chart.py --list              # 列出所有可用品种
  python scripts/chart.py SA day              # SA 日线
  python scripts/chart.py rb 15min            # RB 15分钟线
  python scripts/chart.py ag 5min             # AG 5分钟线
  python scripts/chart.py cu 1min             # CU 1分钟线 (实时聚合)
"""
import sys
import io
import webbrowser

# Windows GKB 终端下 emoji 和中文输出兼容
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from pathlib import Path

import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots

# ── 配置 ──────────────────────────────────────────
DB = Path("c:/Works/ClaudeCode/TradingStudio/data/bars_history.duckdb")
OUT_DIR = Path("c:/Works/ClaudeCode/TradingStudio/data/charts")
OUT_DIR.mkdir(exist_ok=True)

# 常用品种别名
ALIASES = {
    "rb": "rb000", "RB": "rb000", "螺纹": "rb000",
    "ag": "ag000", "AG": "ag000", "白银": "ag000",
    "au": "au000", "AU": "au000", "黄金": "au000",
    "cu": "cu000", "CU": "cu000", "沪铜": "cu000",
    "al": "al000", "AL": "al000", "沪铝": "al000",
    "zn": "zn000", "ZN": "zn000", "沪锌": "zn000",
    "ni": "ni000", "NI": "ni000", "沪镍": "ni000",
    "sn": "sn000", "SN": "sn000", "沪锡": "sn000",
    "pb": "pb000", "PB": "pb000", "沪铅": "pb000",
    "ss": "ss000", "SS": "ss000", "不锈钢": "ss000",
    "hc": "hc000", "HC": "hc000", "热卷": "hc000",
    "bu": "bu000", "BU": "bu000", "沥青": "bu000",
    "ru": "ru000", "RU": "ru000", "橡胶": "ru000",
    "fu": "fu000", "FU": "fu000", "燃油": "fu000",
    "sc": "sc000", "SC": "sc000", "原油": "sc000",
    "lu": "lu000", "LU": "lu000", "低硫燃油": "lu000",
    "sp": "sp000", "SP": "sp000", "纸浆": "sp000",
    "sa": "sa000", "SA": "sa000", "纯碱": "sa000",
    "fg": "fg000", "FG": "fg000", "玻璃": "fg000",
    "ma": "ma000", "MA": "ma000", "甲醇": "ma000",
    "ta": "ta000", "TA": "ta000", "PTA": "ta000",
    "eg": "eg000", "EG": "eg000", "乙二醇": "eg000",
    "eb": "eb000", "EB": "eb000", "苯乙烯": "eb000",
    "pp": "pp000", "PP": "pp000", "聚丙烯": "pp000",
    "v": "v000", "V": "v000", "PVC": "v000",
    "l": "l000", "L": "l000", "塑料": "l000",
    "pg": "pg000", "PG": "pg000", "LPG": "pg000",
    "jm": "jm000", "JM": "jm000", "焦煤": "jm000",
    "j": "j000", "J": "j000", "焦炭": "j000",
    "i": "i000", "I": "i000", "铁矿石": "i000",
    "m": "m000", "M": "m000", "豆粕": "m000",
    "y": "y000", "Y": "y000", "豆油": "y000",
    "p": "p000", "P": "p000", "棕榈油": "p000",
    "a": "a000", "A": "a000", "豆一": "a000",
    "b": "b000", "B": "b000", "豆二": "b000",
    "c": "c000", "C": "c000", "玉米": "c000",
    "cs": "cs000", "CS": "cs000", "淀粉": "cs000",
    "jd": "jd000", "JD": "jd000", "鸡蛋": "jd000",
    "lh": "lh000", "LH": "lh000", "生猪": "lh000",
    "ap": "ap000", "AP": "ap000", "苹果": "ap000",
    "cf": "cf000", "CF": "cf000", "棉花": "cf000",
    "sr": "sr000", "SR": "sr000", "白糖": "sr000",
    "oi": "oi000", "OI": "oi000", "菜油": "oi000",
    "rm": "rm000", "RM": "rm000", "菜粕": "rm000",
    "cy": "cy000", "CY": "cy000", "棉纱": "cy000",
    "cj": "cj000", "CJ": "cj000", "红枣": "cj000",
    "pk": "pk000", "PK": "pk000", "花生": "pk000",
    "sh": "sh000", "SH": "sh000", "烧碱": "sh000",
    "si": "si000", "SI": "si000", "工业硅": "si000",
    "lc": "lc000", "LC": "lc000", "碳酸锂": "lc000",
    "ec": "ec000", "EC": "ec000", "欧线集运": "ec000",
    "ao": "ao000", "AO": "ao000", "氧化铝": "ao000",
    "br": "br000", "BR": "br000", "丁二烯橡胶": "br000",
    "if": "if000", "IF": "if000", "沪深300": "if000",
    "ic": "ic000", "IC": "ic000", "中证500": "ic000",
    "im": "im000", "IM": "im000", "中证1000": "im000",
    "ih": "ih000", "IH": "ih000", "上证50": "ih000",
    "ts": "ts000", "TS": "ts000", "2年国债": "ts000",
    "tf": "tf000", "TF": "tf000", "5年国债": "tf000",
    "t": "t000", "T": "t000", "10年国债": "t000",
    "tl": "tl000", "TL": "tl000", "30年国债": "tl000",
}


def get_connection():
    # 只读模式打开，避免和运行的 TradingStudio 进程冲突
    return duckdb.connect(str(DB), read_only=True)


def list_products():
    """列出所有可用品种及其数据范围"""
    conn = get_connection()
    # 从 bars_5min 获取品种列表 (覆盖最全)
    sql = """
        SELECT
            instrument_id,
            COUNT(*) as bars,
            MIN(bar_time) as first_bar,
            MAX(bar_time) as last_bar
        FROM bars_5min
        WHERE instrument_id LIKE '%000'
          AND instrument_id NOT LIKE '%F000'
        GROUP BY instrument_id
        ORDER BY instrument_id
    """
    rows = conn.execute(sql).fetchall()
    conn.close()

    print(f"\n{'='*70}")
    print(f"TradingStudio 可用品种 — bars_history.duckdb")
    print(f"{'='*70}")
    print(f"{'品种':<12} {'名称':<12} {'Bar数':>10} {'数据范围'}")
    print(f"{'-'*70}")

    name_map = {v: k for k, v in ALIASES.items() if not k.isupper() and len(k) <= 4}
    for inst_id, cnt, first, last in rows:
        short = inst_id.replace("000", "")
        name = name_map.get(inst_id, "")
        print(f"  {short:<10} {name:<10} {cnt:>10,}  {first} ~ {last}")

    print(f"\n共 {len(rows)} 个连续合约品种")
    print(f"用法: python scripts/chart.py <品种代码> <周期>")
    print(f"示例: python scripts/chart.py rb 15min\n")


def resolve_product(raw: str) -> str:
    """将用户输入的品种代码解析为 DuckDB instrument_id"""
    # 先查别名
    if raw in ALIASES:
        return ALIASES[raw]
    if raw.lower() in ALIASES:
        return ALIASES[raw.lower()]
    # 如果已经是 xxx000 格式
    if raw.endswith("000"):
        return raw
    # 自动补齐
    candidate = raw.lower() + "000"
    return candidate


def resolve_freq(freq: str) -> tuple[str, str]:
    """解析频率 -> (表名, 列名)"""
    freq = freq.lower().replace("min", "").replace("m", "")
    mapping = {
        "1": ("bars_1min", "bar_time"),
        "5": ("bars_5min", "bar_time"),
        "15": ("bars_15min", "bar_time"),
        "30": ("bars_15min", "bar_time"),  # 从15min聚合
        "60": ("bars_15min", "bar_time"),  # 从15min聚合
        "day": ("bars_day", "trading_day"),
        "week": ("bars_week", "trading_week"),
    }
    if freq in mapping:
        return mapping[freq]
    # 默认 15min
    print(f"  未知频率 '{freq}', 使用 15min")
    return mapping["15"]


def load_bars(instrument_id: str, freq: str):
    """从 DuckDB 加载指定品种和周期的 Bar 数据"""
    conn = get_connection()
    table, time_col = resolve_freq(freq)

    # 检查品种是否存在
    check = conn.execute(
        f"SELECT COUNT(*) FROM {table} WHERE instrument_id = ?",
        [instrument_id]
    ).fetchone()[0]

    if check == 0:
        # 尝试从 bars_1min 聚合
        print(f"  品种 {instrument_id} 在 {table} 中无数据, 从 bars_1min 聚合...")
        return load_from_1min(conn, instrument_id, freq)

    sql = f"""
        SELECT {time_col} as dt,
            open / 1e7 as open,
            high / 1e7 as high,
            low / 1e7 as low,
            close / 1e7 as close,
            volume
        FROM {table}
        WHERE instrument_id = ?
        ORDER BY dt
    """
    df = conn.execute(sql, [instrument_id]).df()
    conn.close()
    return df


def load_from_1min(conn, instrument_id: str, freq: str):
    """从 bars_1min 按频率聚合"""
    freq_min = int(freq.replace("min", "").replace("m", ""))
    period_sql = f"""
        SELECT
            date_trunc('hour', bar_time::TIMESTAMP) +
            INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / {freq_min}) * {freq_min}) MINUTE as dt,
            FIRST(open) / 1e7 as open,
            MAX(high) / 1e7 as high,
            MIN(low) / 1e7 as low,
            LAST(close) / 1e7 as close,
            SUM(volume) as volume
        FROM bars_1min
        WHERE instrument_id = ?
        GROUP BY dt
        ORDER BY dt
    """
    df = conn.execute(period_sql, [instrument_id]).df()
    conn.close()
    return df


def make_chart(df, product: str, freq: str, out_file: Path):
    """构建 Plotly 交互式 K 线图"""
    # 计算移动平均线
    df["ma5"] = df["close"].rolling(5).mean()
    df["ma20"] = df["close"].rolling(20).mean()
    df["ma60"] = df["close"].rolling(60).mean()

    fig = make_subplots(
        rows=2, cols=1, shared_xaxes=True,
        vertical_spacing=0.03,
        row_heights=[0.65, 0.35],
    )

    # ── 窗格1: K线 + 均线 ──
    fig.add_trace(
        go.Candlestick(
            x=df["dt"], open=df["open"], high=df["high"],
            low=df["low"], close=df["close"],
            name="价格",
            increasing=dict(line=dict(color="#ef5350"), fillcolor="#ef5350"),
            decreasing=dict(line=dict(color="#26a69a"), fillcolor="#26a69a"),
        ),
        row=1, col=1,
    )
    fig.add_trace(go.Scatter(
        x=df["dt"], y=df["ma5"], mode="lines",
        line=dict(color="#ffd54f", width=0.8), name="MA5",
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=df["dt"], y=df["ma20"], mode="lines",
        line=dict(color="#e040fb", width=0.8), name="MA20",
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=df["dt"], y=df["ma60"], mode="lines",
        line=dict(color="#00bcd4", width=0.8), name="MA60",
    ), row=1, col=1)

    # ── 窗格2: 成交量 ──
    vol_colors = [
        "#ef5350" if c >= o else "#26a69a"
        for o, c in zip(df["open"], df["close"])
    ]
    fig.add_trace(
        go.Bar(x=df["dt"], y=df["volume"], name="成交量",
               marker_color=vol_colors, opacity=0.45,
               marker_line_width=0),
        row=2, col=1,
    )

    # ── 非交易时段 rangebreaks ──
    if freq in ("1min", "5min", "15min", "30min", "60min"):
        rangebreaks = [
            dict(bounds=["sat", "mon"]),
            dict(bounds=[15, 21], pattern="hour"),
            dict(bounds=[3, 9], pattern="hour"),
            dict(bounds=[11.5, 13.5], pattern="hour"),
            dict(bounds=[10.25, 10.5], pattern="hour"),
        ]
    else:
        rangebreaks = [dict(bounds=["sat", "mon"])]

    # 摘要信息 (兼容 string 和 datetime 两种日期格式)
    pct_change = (df["close"].iloc[-1] / df["close"].iloc[0] - 1) * 100
    direction = "↑" if pct_change > 0 else "↓"
    t0, t1 = df["dt"].iloc[0], df["dt"].iloc[-1]
    if hasattr(t0, "strftime"):
        t0, t1 = t0.strftime("%Y-%m-%d"), t1.strftime("%Y-%m-%d")
    else:
        t0, t1 = str(t0)[:10], str(t1)[:10]
    title = (
        f"{product} {freq} ｜ "
        f"{t0} → {t1} ｜ "
        f"{len(df):,} bars ｜ "
        f"{direction}{abs(pct_change):.1f}%"
    )

    fig.update_layout(
        title=dict(text=title, font=dict(size=16), x=0.02, xanchor="left"),
        template="plotly_dark",
        height=800,
        hovermode="x unified",
        margin=dict(l=10, r=50, t=60, b=10),
        showlegend=True,
        legend=dict(orientation="h", yanchor="bottom", y=1.02, xanchor="left", x=0),
        xaxis=dict(rangeslider_visible=False, rangebreaks=rangebreaks),
        xaxis2=dict(rangebreaks=rangebreaks),
    )
    fig.update_yaxes(title_text="价格", row=1, col=1, fixedrange=False)
    fig.update_yaxes(title_text="成交量", row=2, col=1, fixedrange=False)

    # 写入文件并打开
    fig.write_html(str(out_file), include_plotlyjs="cdn")
    webbrowser.open(f"file:///{out_file.resolve()}")
    print(f"\n  ✅ 图表已生成: {out_file}")
    print(f"  📊 {len(df):,} bars  |  "
          f"¥{df['close'].min():.0f} ~ ¥{df['close'].max():.0f}  |  "
          f"{pct_change:+.1f}%\n")


def main():
    if len(sys.argv) < 2 or sys.argv[1] in ("--list", "-l", "list"):
        list_products()
        return

    product = sys.argv[1]
    freq = sys.argv[2] if len(sys.argv) > 2 else "15min"

    instrument_id = resolve_product(product)
    short_name = instrument_id.replace("000", "").upper()

    print(f"\n  🔍 查询: {short_name} {freq} ({instrument_id})")

    df = load_bars(instrument_id, freq)
    if len(df) == 0:
        print(f"  ❌ 无数据: {instrument_id} @ {freq}")
        print(f"  提示: 运行 'python scripts/chart.py --list' 查看可用品种")
        return

    print(f"  📦 {len(df):,} bars | "
          f"{df['dt'].iloc[0]} → {df['dt'].iloc[-1]} | "
          f"¥{df['close'].min():.0f} ~ ¥{df['close'].max():.0f}")

    out_file = OUT_DIR / f"{short_name}_{freq}.html"
    make_chart(df, short_name, freq, out_file)


if __name__ == "__main__":
    main()
