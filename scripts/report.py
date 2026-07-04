"""
TradingStudio 回测报告可视化 — 交互式 Dashboard
用法:
  python scripts/report.py                           # 列出可用报告文件
  python scripts/report.py report.json                # 指定报告文件
  python scripts/report.py macross_rb_15min           # 按名称匹配
  python scripts/report.py --open                     # 打开最近生成的报告

输出: data/charts/ 下单个交互式 HTML (含 5 个面板)
"""
import sys
import io
import json
import webbrowser

# Windows GKB 终端下 emoji 和中文输出兼容
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from pathlib import Path
from datetime import datetime
from collections import defaultdict

import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots

# ── 配置 ──────────────────────────────────────────
CONFIGS_DIR = Path("c:/Works/ClaudeCode/TradingStudio/configs")
OUT_DIR = Path("c:/Works/ClaudeCode/TradingStudio/data/charts")
OUT_DIR.mkdir(exist_ok=True)

# 默认查找路径
SEARCH_PATHS = [
    CONFIGS_DIR,
    CONFIGS_DIR / "batch",
    CONFIGS_DIR / "grid",
]


def find_reports():
    """扫描所有 report.json 文件"""
    reports = []
    for base in SEARCH_PATHS:
        if not base.exists():
            continue
        for f in sorted(base.rglob("*.report.json")):
            # 排除副本备份文件
            if "Copy" in str(f) or "副本" in str(f):
                continue
            size_kb = f.stat().st_size / 1024
            reports.append((f, size_kb))
    return reports


def list_reports():
    """列出可用报告"""
    reports = find_reports()
    if not reports:
        print("未找到回测报告文件")
        return

    print(f"\n{'='*80}")
    print(f"TradingStudio 回测报告 — 共 {len(reports)} 个")
    print(f"{'='*80}")
    print(f"{'文件':<55} {'大小':>8}")
    print(f"{'-'*80}")

    for path, size in reports:
        # 读取策略名
        try:
            with open(path) as f:
                r = json.load(f)
            sr = r.get("strategyReports", [{}])[0]
            sid = sr.get("strategyId", "?")
            trades = len(sr.get("trades", []))
            pnl = sr.get("totalNetProfit", 0)
            win = sr.get("winRate", 0) * 100
            rel = path.relative_to(CONFIGS_DIR)
            print(f"  {str(rel):<53} {size:>5.0f}KB   {sid:<30} {trades:>4}笔  PnL={pnl:>+.0f}  WR={win:.0f}%")
        except Exception:
            rel = path.relative_to(CONFIGS_DIR)
            print(f"  {str(rel):<53} {size:>5.0f}KB   [读取失败]")

    print(f"\n用法: python scripts/report.py <文件名或关键词>")
    print(f"示例: python scripts/report.py macross_rb_15min\n")


def resolve_report_path(query: str) -> Path | None:
    """根据用户输入查找报告文件"""
    reports = find_reports()
    query_lower = query.lower().replace(".report.json", "").replace(".json", "")

    # 精确匹配
    for path, _ in reports:
        if path.name == query or path.name == query + ".report.json":
            return path

    # 模糊匹配
    candidates = []
    for path, _ in reports:
        name = str(path).lower()
        if query_lower in name:
            # 优先匹配主 report.json (文件名较短)
            score = len(path.name)
            candidates.append((score, path))

    if candidates:
        candidates.sort()
        return candidates[0][1]

    return None


def load_report(path: Path) -> dict:
    """加载报告 JSON"""
    with open(path) as f:
        return json.load(f)


def build_dashboard(r: dict, report_path: Path) -> Path:
    """构建交互式回测 Dashboard HTML"""
    sr = r["strategyReports"][0]
    trades = sr.get("trades", [])
    starting = sr["startingCapital"]
    strategy_id = sr.get("strategyId", report_path.stem)

    # ── 从 EquityCurve 提取数据 ──
    eq_data = sr.get("equityCurve", [])
    # 兼容空对象 (引擎占位) 和有数据的格式
    if eq_data and eq_data[0] and isinstance(eq_data[0], dict) and "date" in eq_data[0]:
        eq_times = [datetime.fromisoformat(e["date"].replace("Z", "+00:00")) for e in eq_data]
        eq_values = [e["equity"] for e in eq_data]
    elif eq_data and eq_data[0] and isinstance(eq_data[0], dict) and "time" in eq_data[0]:
        eq_times = [datetime.fromisoformat(e["time"].replace("Z", "+00:00")) for e in eq_data]
        eq_values = [e["value"] for e in eq_data]
    else:
        # 从交易记录重建
        eq_times, eq_values = rebuild_equity(trades, starting)

    dd_pct = compute_drawdown(eq_values)
    daily_ret = compute_daily_returns(eq_values)

    # ── 构建 5 面板布局 ──
    fig = make_subplots(
        rows=5, cols=1,
        vertical_spacing=0.04,
        row_heights=[0.28, 0.20, 0.20, 0.16, 0.16],
        subplot_titles=(
            f"权益曲线 & 回撤 — {strategy_id}",
            "月度收益热力图 (%)",
            "滚动 Sharpe (90日)",
            "交易 PnL 分布 & 累计盈亏",
            "年度收益分解",
        ),
    )

    # ── Panel 1: 权益曲线 + 回撤 ──
    fig.add_trace(go.Scatter(
        x=eq_times, y=eq_values, mode="lines",
        line=dict(color="#4fc3f7", width=1.2),
        name="权益", fill="tozeroy",
        fillcolor="rgba(79,195,247,0.08)",
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=[eq_times[0], eq_times[-1]], y=[starting, starting],
        mode="lines", line=dict(color="gray", width=0.8, dash="dash"),
        name=f"初始资金 ¥{starting:,.0f}", showlegend=False,
    ), row=1, col=1)

    # 回撤 (右轴)
    fig.add_trace(go.Scatter(
        x=eq_times, y=dd_pct, mode="lines",
        line=dict(color="#ef5350", width=0.6),
        name=f"回撤 (最大 {min(dd_pct):.1f}%)",
        fill="tozeroy", fillcolor="rgba(239,83,80,0.12)",
    ), row=1, col=1)

    # ── Panel 2: 月度收益热力图 ──
    monthly = compute_monthly_returns(eq_values, eq_times)
    if monthly:
        years = sorted(set(k[0] for k in monthly))
        months_12 = list(range(1, 13))
        z_data = np.full((12, len(years)), np.nan)
        annot_text = np.full((12, len(years)), "", dtype=object)
        for (yr, mo), ret in monthly.items():
            if yr in years:
                z_data[mo - 1, years.index(yr)] = ret
                annot_text[mo - 1, years.index(yr)] = f"{ret:.1f}%"

        fig.add_trace(go.Heatmap(
            z=z_data,
            x=years, y=["1月","2月","3月","4月","5月","6月",
                         "7月","8月","9月","10月","11月","12月"],
            colorscale="RdYlGn", zmid=0, zmin=-20, zmax=20,
            text=annot_text, texttemplate="%{text}", textfont=dict(size=9),
            name="月度收益", showscale=False,
        ), row=2, col=1)

    # ── Panel 3: 滚动 Sharpe ──
    if len(daily_ret) > 90:
        roll_sharpe = compute_rolling_sharpe(daily_ret, 90)
        r_times = eq_times[90:]
        fig.add_trace(go.Scatter(
            x=r_times, y=roll_sharpe, mode="lines",
            line=dict(color="#66bb6a", width=0.8),
            name="Sharpe(90d)", fill="tozeroy",
            fillcolor="rgba(102,187,106,0.1)",
        ), row=3, col=1)
        fig.add_trace(go.Scatter(
            x=[r_times[0], r_times[-1]], y=[0, 0],
            mode="lines", line=dict(color="gray", width=0.5, dash="dot"),
            showlegend=False,
        ), row=3, col=1)
        fig.add_trace(go.Scatter(
            x=[r_times[0], r_times[-1]], y=[1, 1],
            mode="lines", line=dict(color="green", width=0.5, dash="dot"),
            showlegend=False,
        ), row=3, col=1)

    # ── Panel 4: 交易 PnL 分布 ──
    if trades:
        pnls = [t["pnL"] for t in trades]
        cum_pnl = np.cumsum(pnls)
        colors = ["#66bb6a" if p > 0 else "#ef5350" for p in pnls]
        fig.add_trace(go.Bar(
            x=list(range(len(pnls))), y=cum_pnl,
            marker_color=colors, name="累计盈亏",
            marker_line_width=0, opacity=0.85,
        ), row=4, col=1)
        fig.add_trace(go.Scatter(
            x=[0, len(pnls)-1], y=[0, 0],
            mode="lines", line=dict(color="gray", width=0.5),
            showlegend=False,
        ), row=4, col=1)

    # ── Panel 5: 年度分解 ──
    if trades:
        annual = compute_annual(trades)
        years = sorted(annual.keys())
        pnl_vals = [annual[y]["pnl"] for y in years]
        bar_colors = ["#66bb6a" if p > 0 else "#ef5350" for p in pnl_vals]
        fig.add_trace(go.Bar(
            x=years, y=pnl_vals, marker_color=bar_colors,
            name="年度盈亏", marker_line_width=0, opacity=0.85,
            text=[f"¥{p:,.0f}<br>{annual[y]['wins']}W/{annual[y]['losses']}L" for y, p in zip(years, pnl_vals)],
            textposition="outside",
        ), row=5, col=1)

    # ── 全局布局 ──
    # KPI 摘要
    total_return = sr.get("totalNetProfit", 0) / starting * 100
    max_dd = sr.get("maxDrawdown", 0) * 100 if sr.get("maxDrawdown", 0) < 1 else sr.get("maxDrawdown", 0)
    sharpe = sr.get("sharpeRatio", 0)
    win_rate = sr.get("winRate", 0) * 100
    pl_ratio = sr.get("profitLossRatio", 0)
    total_trades = sr.get("totalTrades", len(trades))
    annual_ret = sr.get("compoundingAnnualReturn", 0) * 100

    kpi_text = (
        f"<b>{strategy_id}</b> ｜ "
        f"初始 ¥{starting:,.0f} → 最终 ¥{eq_values[-1]:,.0f} ｜ "
        f"总收益 <b>{total_return:+.1f}%</b> ｜ "
        f"年化 {annual_ret:+.1f}% ｜ "
        f"最大回撤 {max_dd:.1f}% ｜ "
        f"Sharpe {sharpe:.2f} ｜ "
        f"胜率 {win_rate:.0f}% ｜ "
        f"盈亏比 {pl_ratio:.2f} ｜ "
        f"交易 {total_trades}笔"
    )

    fig.update_layout(
        title=dict(
            text=kpi_text, font=dict(size=13, color="#b0bec5"),
            x=0.02, xanchor="left",
        ),
        template="plotly_dark",
        height=1600,
        hovermode="x unified",
        margin=dict(l=50, r=30, t=50, b=10),
        showlegend=False,
    )

    # 子图标题样式
    for ann in fig.layout.annotations:
        ann.font = dict(size=12, color="#90a4ae")

    # 输出
    report_name = report_path.stem.replace(".report", "")
    out_file = OUT_DIR / f"_report_{report_name}.html"
    fig.write_html(str(out_file), include_plotlyjs="cdn")
    return out_file


# ── 计算工具 ──

def rebuild_equity(trades, starting_capital):
    """从交易记录重建权益曲线"""
    sorted_trades = sorted(trades, key=lambda t: t["exitTime"])
    times = []
    equity = []
    cur = starting_capital
    for t in sorted_trades:
        et = datetime.fromisoformat(t["exitTime"].replace("Z", "+00:00"))
        cur += t["pnL"]
        times.append(et)
        equity.append(cur)
    return times, equity


def compute_drawdown(equity):
    """计算回撤百分比序列"""
    eq = np.array(equity)
    peak = np.maximum.accumulate(eq)
    return ((eq - peak) / peak * 100).tolist()


def compute_daily_returns(equity):
    """计算日收益率"""
    eq = np.array(equity)
    rets = []
    for i in range(1, len(eq)):
        if eq[i-1] > 0:
            rets.append((eq[i] - eq[i-1]) / eq[i-1])
        else:
            rets.append(0.0)
    return rets


def compute_rolling_sharpe(daily_ret, window=90):
    """滚动夏普比率"""
    rets = np.array(daily_ret)
    sharpe_vals = []
    for i in range(window, len(rets)):
        w = rets[i-window:i]
        mu = np.mean(w)
        sd = np.std(w) or 1e-10
        sharpe_vals.append(mu / sd * np.sqrt(252))
    return sharpe_vals


def compute_monthly_returns(equity, times):
    """计算月度收益率"""
    monthly_eq = {}
    for t, e in zip(times, equity):
        key = (t.year, t.month)
        monthly_eq[key] = e
    keys = sorted(monthly_eq.keys())
    rets = {}
    for i in range(1, len(keys)):
        e_prev = monthly_eq[keys[i-1]]
        e_cur = monthly_eq[keys[i]]
        if e_prev > 0:
            rets[keys[i]] = (e_cur - e_prev) / e_prev * 100
    return rets


def compute_annual(trades):
    """年度盈亏统计"""
    annual = defaultdict(lambda: {"wins": 0, "losses": 0, "pnl": 0.0, "fees": 0.0})
    for t in trades:
        yr = datetime.fromisoformat(t["exitTime"].replace("Z", "+00:00")).year
        a = annual[yr]
        if t["pnL"] > 0:
            a["wins"] += 1
        else:
            a["losses"] += 1
        a["pnl"] += t["pnL"]
        a["fees"] += t["fee"]
    return dict(annual)


# ── CLI ──

def main():
    if len(sys.argv) < 2 or sys.argv[1] in ("--list", "-l", "list"):
        list_reports()
        return

    if sys.argv[1] == "--open":
        # 打开最近生成的报告 HTML
        htmls = sorted(OUT_DIR.glob("_report_*.html"), key=lambda p: p.stat().st_mtime, reverse=True)
        if htmls:
            path = htmls[0]
            webbrowser.open(f"file:///{path.resolve()}")
            print(f"  打开: {path.name}")
        else:
            print("  没有找到已生成的报告 HTML。先运行: python scripts/report.py <报告名>")
        return

    query = sys.argv[1]
    report_path = resolve_report_path(query)

    if not report_path:
        print(f"  ❌ 未找到匹配的报告: {query}")
        print(f"  提示: 运行 'python scripts/report.py --list' 查看可用报告")
        return

    print(f"  📋 加载: {report_path}")
    r = load_report(report_path)

    out_file = build_dashboard(r, report_path)
    webbrowser.open(f"file:///{out_file.resolve()}")
    print(f"  ✅ 报告已生成: {out_file.name}\n")


if __name__ == "__main__":
    main()
