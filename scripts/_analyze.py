import json, os, glob

main_reports, batch_reports, grid_reports = [], [], []

for f in glob.glob('configs/**/*.report.json', recursive=True):
    if 'Copy' in f:
        continue
    try:
        with open(f) as fp:
            r = json.load(fp)
        sr = r['strategyReports'][0]
        trades = sr.get('trades', [])
        item = {
            'file': os.path.relpath(f, 'configs'),
            'strategy': sr['strategyId'],
            'trades': len(trades),
            'pnl': sr.get('totalNetProfit', 0),
            'annual_ret': sr.get('compoundingAnnualReturn', 0) * 100,
            'max_dd': sr.get('maxDrawdown', 0) * 100,
            'win_rate': sr.get('winRate', 0) * 100,
            'pl_ratio': sr.get('profitLossRatio', 0),
            'sharpe': sr.get('sharpeRatio', 0),
            'fees': sr.get('totalFees', 0),
            'slippage': sr.get('totalSlippage', 0),
        }
        rel = os.path.relpath(f, 'configs')
        parts = rel.replace('\\', '/').split('/')
        if parts[0] == 'batch':
            batch_reports.append(item)
        elif parts[0] == 'grid':
            grid_reports.append(item)
        else:
            main_reports.append(item)
    except:
        pass

# A) MAIN STRATEGIES
print("=" * 100)
print("A) 主策略报告")
print("=" * 100)
main_reports.sort(key=lambda x: x['pnl'], reverse=True)
for r in main_reports:
    sign = '+' if r['pnl'] > 0 else ''
    print(f"  {r['strategy']:<30} {r['trades']:>4}t  PnL={sign}{r['pnl']:>12,.0f}  "
          f"年化={r['annual_ret']:>6.1f}%  回撤={r['max_dd']:>5.1f}%  "
          f"胜率={r['win_rate']:>4.0f}%  盈亏比={r['pl_ratio']:.2f}  "
          f"Sharpe={r['sharpe']:.2f}  手续费={r['fees']:,.0f}")
main_pnl = sum(r['pnl'] for r in main_reports)
main_win = sum(1 for r in main_reports if r['pnl'] > 0)
print(f"\n  主策略: {len(main_reports)}个 | 盈利 {main_win}/{len(main_reports)} | 总盈亏: {main_pnl:,.0f}")

# B) BATCH variety tests - top/bottom
print(f"\n{'=' * 100}")
print(f"B) 多品种批量测试 (前20最好 + 后10最差)")
print(f"{'=' * 100}")
batch_reports.sort(key=lambda x: x['pnl'], reverse=True)
for r in batch_reports[:20]:
    sign = '+' if r['pnl'] > 0 else ''
    print(f"  {r['strategy']:<30} {r['trades']:>4}t  PnL={sign}{r['pnl']:>12,.0f}  "
          f"DD={r['max_dd']:>5.1f}%  WR={r['win_rate']:>4.0f}%  PLR={r['pl_ratio']:.2f}")
print("  ...")
for r in batch_reports[-10:]:
    sign = '+' if r['pnl'] > 0 else ''
    print(f"  {r['strategy']:<30} {r['trades']:>4}t  PnL={sign}{r['pnl']:>12,.0f}  "
          f"DD={r['max_dd']:>5.1f}%  WR={r['win_rate']:>4.0f}%  PLR={r['pl_ratio']:.2f}")
batch_pnl = sum(r['pnl'] for r in batch_reports)
batch_win = sum(1 for r in batch_reports if r['pnl'] > 0)
print(f"\n  批量测试: {len(batch_reports)}个 | 盈利 {batch_win}/{len(batch_reports)} | 总盈亏: {batch_pnl:,.0f}")

# C) KEY FINDINGS
all_r = main_reports + batch_reports
profitable = [r for r in all_r if r['pnl'] > 0]
losing = [r for r in all_r if r['pnl'] <= 0]

print(f"\n{'=' * 100}")
print(f"C) 关键指标对比")
print(f"{'=' * 100}")

if profitable:
    n = len(profitable)
    print(f"  盈利策略 ({n}个):")
    print(f"    胜率  mean={sum(r['win_rate'] for r in profitable)/n:.0f}%")
    print(f"    盈亏比 mean={sum(r['pl_ratio'] for r in profitable)/n:.2f}")
    print(f"    Sharpe mean={sum(r['sharpe'] for r in profitable)/n:.2f}")
    print(f"    回撤  mean={sum(r['max_dd'] for r in profitable)/n:.1f}%")

if losing:
    n = len(losing)
    print(f"  亏损策略 ({n}个):")
    print(f"    胜率  mean={sum(r['win_rate'] for r in losing)/n:.0f}%")
    print(f"    盈亏比 mean={sum(r['pl_ratio'] for r in losing)/n:.2f}")
    print(f"    Sharpe mean={sum(r['sharpe'] for r in losing)/n:.2f}")
    print(f"    回撤  mean={sum(r['max_dd'] for r in losing)/n:.1f}%")

# Fee & slippage burden
all_fees = sum(r['fees'] for r in all_r)
all_slippage = sum(r['slippage'] for r in all_r)
total_pnl = sum(r['pnl'] for r in all_r)
print(f"\n  总手续费: {all_fees:,.0f}  总滑点成本: {all_slippage:,.0f}  总盈亏: {total_pnl:,.0f}")

# Few-trade strategies
few = [r for r in all_r if r['trades'] < 20]
print(f"  交易<20笔的策略: {len(few)}个 (统计意义低)")

# Annual return issue
zero_ret = [r for r in all_r if abs(r['annual_ret']) < 0.01]
print(f"  年化收益=0%的策略: {len(zero_ret)}个 (可能是报告生成Bug)")

# Strategy type analysis
print(f"\n{'=' * 100}")
print(f"D) 按策略类型分组")
print(f"{'=' * 100}")
strategies = {}
for r in all_r:
    key = r['strategy'].split('-')[0]
    if key not in strategies:
        strategies[key] = {'count': 0, 'pnl': 0, 'win': 0, 'trades': 0}
    strategies[key]['count'] += 1
    strategies[key]['pnl'] += r['pnl']
    strategies[key]['trades'] += r['trades']
    if r['pnl'] > 0:
        strategies[key]['win'] += 1

for k, v in sorted(strategies.items(), key=lambda x: x[1]['pnl'], reverse=True):
    avg_pnl = v['pnl'] / v['count']
    wr = v['win'] / v['count'] * 100
    print(f"  {k:<15} {v['count']:>3}个  总PnL={v['pnl']:>12,.0f}  平均PnL={avg_pnl:>10,.0f}  盈利率={wr:.0f}%  总交易={v['trades']}")
