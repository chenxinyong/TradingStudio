"""
Factor Phase 2: ML-based Factor Composite
Walk-forward validation with Equal-weight, Ridge, IC-weighted, and LightGBM models.

用法: cd TradingStudio && python scripts/factor_research/factor_ml.py

输入: scripts/factor_research/output/factors_panel_v1_clean.parquet
输出:
  - scripts/factor_research/output/composite_signal.csv      全量复合信号
  - scripts/factor_research/output/composite_metrics.csv     逐窗口评估指标
  - scripts/factor_research/output/composite_summary.csv     OOS 对比汇总表
  - 终端: 完整对比报告
"""

import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
from scipy.stats import spearmanr
from sklearn.linear_model import RidgeCV
from sklearn.preprocessing import StandardScaler
import warnings
warnings.filterwarnings('ignore')

# ── 配置 ──────────────────────────────────────────────────────────

OUTPUT_DIR = "scripts/factor_research/output"
INPUT_PATH = os.path.join(OUTPUT_DIR, "factors_panel_v1_clean.parquet")
COMPOSITE_SIGNAL_PATH = os.path.join(OUTPUT_DIR, "composite_signal.csv")
METRICS_PATH = os.path.join(OUTPUT_DIR, "composite_metrics.csv")
SUMMARY_PATH = os.path.join(OUTPUT_DIR, "composite_summary.csv")

# 因子选择
CORE_FACTORS = ['IntradayMom', 'VWAP_Dev', 'Amihud']
EXTRA_FACTOR = 'VolumeRatio_20d'  # OOS stable, optional
TARGET = 'FwdRet_1d'

# Walk-forward 参数
TRAIN_DAYS = 252      # 滚动训练窗口 (交易日)
TEST_DAYS = 21         # 测试窗口 (交易日)
STEP_DAYS = 21         # 步进 (交易日)
OOS_START = '2024-01-01'

# Ridge 参数
RIDGE_ALPHAS = np.logspace(-3, 3, 20)

# 分位数分组
N_QUANTILES = 5

print("=" * 80)
print("Factor Phase 2: ML-Based Factor Composite")
print("=" * 80)

# ── 1. 加载数据 ──────────────────────────────────────────────────

print("\n[1/6] 加载数据...")
panel = pd.read_parquet(INPUT_PATH)
panel['trading_day'] = pd.to_datetime(panel['trading_day'])
panel['bar_time'] = pd.to_datetime(panel['bar_time'])

# 时间排序
panel = panel.sort_values(['trading_day', 'instrument_id']).reset_index(drop=True)

print(f"  行数: {len(panel):,}")
print(f"  品种: {panel['instrument_id'].nunique()}")
print(f"  日期: {panel['trading_day'].min().date()} ~ {panel['trading_day'].max().date()}")

# ── 2. 准备因子 ─────────────────────────────────────────────────

print("\n[2/6] 准备因子...")

# 检查因子是否存在
missing = [f for f in CORE_FACTORS + [EXTRA_FACTOR] if f not in panel.columns]
if missing:
    print(f"  [ERROR] 缺失因子: {missing}")
    sys.exit(1)

print(f"  核心因子: {CORE_FACTORS}")
print(f"  可选因子: {EXTRA_FACTOR}")

# ── 3. Walk-Forward 框架 ─────────────────────────────────────────

print("\n[3/6] Walk-Forward 训练与评估...")

unique_days = sorted(panel['trading_day'].unique())
n_days = len(unique_days)

print(f"  总交易日: {n_days}")
print(f"  训练窗口: {TRAIN_DAYS}d, 测试窗口: {TEST_DAYS}d, 步进: {STEP_DAYS}d")
print(f"  OOS 起始: {OOS_START}")

# 生成窗口索引
windows = []
for start in range(0, n_days - TRAIN_DAYS, STEP_DAYS):
    train_end_idx = start + TRAIN_DAYS
    test_end_idx = min(train_end_idx + TEST_DAYS, n_days)
    train_days = unique_days[start:train_end_idx]
    test_days = unique_days[train_end_idx:test_end_idx]
    windows.append({
        'train_days': train_days,
        'test_days': test_days,
        'test_start_date': test_days[0],
        'is_oos': test_days[0] >= pd.Timestamp(OOS_START),
    })
    if test_end_idx >= n_days:
        break

print(f"  总窗口数: {len(windows)}")
print(f"  其中 IS: {sum(1 for w in windows if not w['is_oos'])}")
print(f"  其中 OOS: {sum(1 for w in windows if w['is_oos'])}")

# ── 模型训练与评估 ───────────────────────────────────────────────

all_metrics = []           # 逐窗口指标
all_signals = []           # 逐窗口信号 -> 合并为全量输出

for wi, w in enumerate(windows):
    train_mask = panel['trading_day'].isin(w['train_days'])
    test_mask = panel['trading_day'].isin(w['test_days'])
    train_data = panel[train_mask].copy()
    test_data = panel[test_mask].copy()

    if len(test_data) == 0:
        continue

    # 该窗口使用的因子 (基于训练数据可用性)
    factors_used = []
    for fac in CORE_FACTORS + [EXTRA_FACTOR]:
        train_valid = train_data[fac].notna().sum()
        test_valid = test_data[fac].notna().sum()
        if train_valid >= 30 and test_valid >= 5:
            factors_used.append(fac)

    if len(factors_used) < 2:
        continue

    # 准备训练数据 (dropna on factors + target)
    train_clean = train_data[factors_used + [TARGET]].dropna()
    if len(train_clean) < 30:
        continue

    X_train = train_clean[factors_used].values
    y_train = train_clean[TARGET].values

    # ── Model A: Equal-Weight ──
    ew_weights = np.ones(len(factors_used)) / len(factors_used)

    # ── Model B: Ridge Regression ──
    try:
        ridge = RidgeCV(alphas=RIDGE_ALPHAS, fit_intercept=True, cv=5)
        ridge.fit(X_train, y_train)
        ridge_ok = True
    except Exception:
        ridge_ok = False

    # ── Model C: IC-Weighted ──
    ic_weights_raw = []
    for j in range(len(factors_used)):
        mask = ~(np.isnan(X_train[:, j]) | np.isnan(y_train))
        if mask.sum() >= 10:
            ic, _ = spearmanr(X_train[mask, j], y_train[mask])
        else:
            ic = 0.0
        ic_weights_raw.append(ic if not np.isnan(ic) else 0.0)
    ic_weights_raw = np.array(ic_weights_raw)
    abs_sum = np.abs(ic_weights_raw).sum()
    ic_weights = ic_weights_raw / abs_sum if abs_sum > 1e-12 else np.ones(len(factors_used)) / len(factors_used)

    # ── 对测试集生成信号 ──
    test_clean = test_data.dropna(subset=factors_used)
    if len(test_clean) == 0:
        continue

    X_test = test_clean[factors_used].values

    # Equal-weight composite
    ew_signal = X_test @ ew_weights

    # Ridge composite
    if ridge_ok:
        ridge_signal = ridge.predict(X_test)
    else:
        ridge_signal = np.full(len(X_test), np.nan)

    # IC-weighted composite
    icw_signal = X_test @ ic_weights

    # 构建输出
    out = test_clean[['instrument_id', 'trading_day']].copy()
    out['ew_composite'] = ew_signal
    out['ridge_composite'] = ridge_signal
    out['icw_composite'] = icw_signal
    out['window_idx'] = wi
    out['is_oos'] = w['is_oos']
    all_signals.append(out)

    # ── 逐窗口评估 ──
    y_test = test_clean[TARGET].values

    for model_name, signal in [('EqualWeight', ew_signal),
                                 ('Ridge', ridge_signal),
                                 ('IC_Weighted', icw_signal)]:
        if model_name == 'Ridge' and not ridge_ok:
            continue

        valid = ~np.isnan(signal) & ~np.isnan(y_test)
        if valid.sum() < 10:
            continue

        s_valid = signal[valid]
        y_valid = y_test[valid]

        # Rank IC
        ic, ic_pval = spearmanr(s_valid, y_valid)
        ic = ic if not np.isnan(ic) else 0.0

        # Long-short quintile return
        # Group by date within test window
        eval_df = pd.DataFrame({
            'trading_day': test_clean['trading_day'].values[valid],
            'signal': s_valid,
            'fwd_ret': y_valid,
        })
        ls_returns = []
        for day, grp in eval_df.groupby('trading_day'):
            if len(grp) < N_QUANTILES:
                continue
            grp = grp.copy()
            grp['quantile'] = pd.qcut(grp['signal'], N_QUANTILES, labels=False, duplicates='drop')
            top = grp[grp['quantile'] == grp['quantile'].max()]['fwd_ret'].mean()
            bot = grp[grp['quantile'] == grp['quantile'].min()]['fwd_ret'].mean()
            ls_returns.append({'trading_day': day, 'ls_ret': top - bot})

        ls_mean = np.mean([r['ls_ret'] for r in ls_returns]) if ls_returns else np.nan
        ls_std = np.std([r['ls_ret'] for r in ls_returns]) if ls_returns else np.nan

        all_metrics.append({
            'window': wi,
            'test_start': w['test_start_date'],
            'is_oos': w['is_oos'],
            'model': model_name,
            'n_test_samples': int(valid.sum()),
            'n_test_days': len(ls_returns),
            'rank_ic': round(ic, 6),
            'ls_ret_mean': round(ls_mean, 6) if not np.isnan(ls_mean) else np.nan,
            'ls_ret_std': round(ls_std, 6) if not np.isnan(ls_std) else np.nan,
            'ridge_alpha': round(ridge.alpha_, 6) if (model_name == 'Ridge' and ridge_ok) else np.nan,
            'factors_used': '+'.join(factors_used),
        })

    if (wi + 1) % 10 == 0 or wi == 0:
        print(f"  窗口 {wi+1}/{len(windows)} "
              f"({w['test_start_date'].date()}, "
              f"{'OOS' if w['is_oos'] else 'IS'}), "
              f"样本={len(test_clean):,}")

# ── 4. 合并信号 ──────────────────────────────────────────────────

print("\n[4/6] 合并基础信号...")
signals_df = pd.concat(all_signals, ignore_index=True)
signals_df = signals_df.sort_values(['trading_day', 'instrument_id']).reset_index(drop=True)
print(f"  基础信号行数: {len(signals_df):,}, 覆盖品种: {signals_df['instrument_id'].nunique()}")

# ── 5. LightGBM 扩展 ──────────────────────────────────────────────

print("\n[5/6] LightGBM 扩展 (可选模型)...")
all_lgb_metrics = []
all_lgb_signals = []
lgb_feature_importances = []

try:
    import lightgbm as lgb

    LGB_FACTORS = CORE_FACTORS + [EXTRA_FACTOR]  # 用全部4个因子

    for wi, w in enumerate(windows):
        train_mask = panel['trading_day'].isin(w['train_days'])
        test_mask = panel['trading_day'].isin(w['test_days'])
        train_data = panel[train_mask].copy()
        test_data = panel[test_mask].copy()

        train_clean = train_data[LGB_FACTORS + [TARGET]].dropna()
        test_clean = test_data.dropna(subset=LGB_FACTORS)

        if len(train_clean) < 100 or len(test_clean) < 10:
            continue

        X_train_lgb = train_clean[LGB_FACTORS].values
        y_train_lgb = train_clean[TARGET].values
        X_test_lgb = test_clean[LGB_FACTORS].values
        y_test_lgb = test_clean[TARGET].values

        # 20% 验证集用于 early stopping
        n_val = max(int(len(X_train_lgb) * 0.2), 20)
        X_tr, X_val = X_train_lgb[:-n_val], X_train_lgb[-n_val:]
        y_tr, y_val = y_train_lgb[:-n_val], y_train_lgb[-n_val:]

        try:
            model = lgb.LGBMRegressor(
                n_estimators=200,
                max_depth=5,
                learning_rate=0.05,
                num_leaves=31,
                min_child_samples=50,
                subsample=0.8,
                colsample_bytree=0.8,
                reg_alpha=0.1,
                reg_lambda=0.1,
                random_state=42,
                verbose=-1,
                force_col_wise=True,
            )
            model.fit(
                X_tr, y_tr,
                eval_set=[(X_val, y_val)],
                eval_metric='l1',
                callbacks=[lgb.early_stopping(20), lgb.log_evaluation(0)],
            )

            y_pred = model.predict(X_test_lgb)

            # 评估
            valid = ~np.isnan(y_pred) & ~np.isnan(y_test_lgb)
            if valid.sum() >= 10:
                ic, _ = spearmanr(y_pred[valid], y_test_lgb[valid])
                ic = ic if not np.isnan(ic) else 0.0

                # Long-short
                eval_df = pd.DataFrame({
                    'trading_day': test_clean['trading_day'].values[valid],
                    'signal': y_pred[valid],
                    'fwd_ret': y_test_lgb[valid],
                })
                ls_returns = []
                for day, grp in eval_df.groupby('trading_day'):
                    if len(grp) < N_QUANTILES:
                        continue
                    grp = grp.copy()
                    grp['quantile'] = pd.qcut(grp['signal'], N_QUANTILES, labels=False, duplicates='drop')
                    top = grp[grp['quantile'] == grp['quantile'].max()]['fwd_ret'].mean()
                    bot = grp[grp['quantile'] == grp['quantile'].min()]['fwd_ret'].mean()
                    ls_returns.append({'trading_day': day, 'ls_ret': top - bot})

                ls_mean = np.mean([r['ls_ret'] for r in ls_returns]) if ls_returns else np.nan
                ls_std = np.std([r['ls_ret'] for r in ls_returns]) if ls_returns else np.nan

                all_lgb_metrics.append({
                    'window': wi,
                    'test_start': w['test_start_date'],
                    'is_oos': w['is_oos'],
                    'model': 'LightGBM',
                    'n_test_samples': int(valid.sum()),
                    'n_test_days': len(ls_returns),
                    'rank_ic': round(ic, 6),
                    'ls_ret_mean': round(ls_mean, 6) if not np.isnan(ls_mean) else np.nan,
                    'ls_ret_std': round(ls_std, 6) if not np.isnan(ls_std) else np.nan,
                    'ridge_alpha': np.nan,
                    'factors_used': '+'.join(LGB_FACTORS),
                })

                # Feature importance
                fi = model.feature_importances_
                lgb_feature_importances.append({
                    'window': wi,
                    **{LGB_FACTORS[k]: fi[k] for k in range(len(LGB_FACTORS))},
                })

            # 保存信号
            out_lgb = test_clean[['instrument_id', 'trading_day']].copy()
            out_lgb['lgb_composite'] = y_pred
            out_lgb['window_idx'] = wi
            out_lgb['is_oos'] = w['is_oos']
            all_lgb_signals.append(out_lgb)

        except Exception as e:
            continue

        if (wi + 1) % 10 == 0 or wi == 0:
            print(f"  LGB 窗口 {wi+1}/{len(windows)} ({w['test_start_date'].date()})")

    if all_lgb_signals:
        lgb_signals_df = pd.concat(all_lgb_signals, ignore_index=True)
        lgb_signals_df = lgb_signals_df.sort_values(['trading_day', 'instrument_id']).reset_index(drop=True)
        print(f"  LightGBM 信号: {len(lgb_signals_df):,} 行")

        # 合并到主信号表
        signals_df = signals_df.merge(
            lgb_signals_df[['instrument_id', 'trading_day', 'lgb_composite']],
            on=['instrument_id', 'trading_day'], how='left'
        )
    else:
        print("  [WARN] LightGBM 未能生成任何信号")

except ImportError:
    print("  [SKIP] lightgbm 未安装")
except Exception as e:
    print(f"  [WARN] LightGBM 扩展出错: {e}")

# ── 保存全量复合信号 ────────────────────────────────────────────

signals_df.to_csv(COMPOSITE_SIGNAL_PATH, index=False, encoding='utf-8-sig')
print(f"\n  [SAVED] {COMPOSITE_SIGNAL_PATH}")
print(f"  总信号行数: {len(signals_df):,}, 列: {list(signals_df.columns)}")

# ── 6. OOS 性能汇总 ──────────────────────────────────────────────

print("\n[6/6] OOS 性能汇总...")

metrics_df = pd.DataFrame(all_metrics)
if len(all_lgb_metrics) > 0:
    lgb_metrics_df = pd.DataFrame(all_lgb_metrics)
    metrics_df = pd.concat([metrics_df, lgb_metrics_df], ignore_index=True)

metrics_df.to_csv(METRICS_PATH, index=False, encoding='utf-8-sig')
print(f"  [SAVED] {METRICS_PATH}")

# ── OOS 汇总统计 ──
oos_mask = metrics_df['is_oos'] == True

def compute_summary(df: pd.DataFrame) -> dict:
    """从逐窗口指标计算汇总统计"""
    ics = df['rank_ic'].dropna().to_numpy(copy=True)
    ls_means = df['ls_ret_mean'].dropna().to_numpy(copy=True)

    if len(ics) == 0:
        return {'IC_IR': np.nan, 'Mean_IC': np.nan, 'IC_pos_ratio': np.nan,
                'LS_Sharpe': np.nan, 'LS_MaxDD': np.nan, 'LS_AnnRet': np.nan}

    ic_ir = np.mean(ics) / np.std(ics) if np.std(ics) > 0 else 0.0
    mean_ic = np.mean(ics)
    ic_pos_ratio = np.mean(ics > 0)

    # Long-short cumulative: compound daily returns
    if len(ls_means) > 0:
        cum_ret = np.cumprod(1 + np.nan_to_num(ls_means, 0))
        peak = np.maximum.accumulate(cum_ret)
        dd = (cum_ret - peak) / (peak + 1e-12)
        max_dd = np.min(dd)
        daily_mean = np.mean(ls_means)
        daily_std = np.std(ls_means)
        sharpe = daily_mean / daily_std * np.sqrt(252) if daily_std > 0 else 0.0
        ann_ret = daily_mean * 252
    else:
        sharpe, max_dd, ann_ret = np.nan, np.nan, np.nan

    return {
        'IC_IR': round(ic_ir, 4),
        'Mean_IC': round(mean_ic, 6),
        'IC_pos_ratio': round(ic_pos_ratio, 4),
        'LS_Sharpe': round(sharpe, 4),
        'LS_MaxDD': round(max_dd, 4) if not np.isnan(max_dd) else np.nan,
        'LS_AnnRet': round(ann_ret, 6) if not np.isnan(ann_ret) else np.nan,
    }

summary_rows = []
for model_name in metrics_df['model'].unique():
    model_df = metrics_df[(metrics_df['model'] == model_name) & oos_mask]
    is_df = metrics_df[(metrics_df['model'] == model_name) & ~oos_mask]

    oos_stats = compute_summary(model_df)
    is_stats = compute_summary(is_df)

    summary_rows.append({
        'Model': model_name,
        'IS_IC_IR': is_stats['IC_IR'],
        'IS_Mean_IC': is_stats['Mean_IC'],
        'IS_LS_Sharpe': is_stats['LS_Sharpe'],
        'IS_LS_MaxDD': is_stats['LS_MaxDD'],
        'OOS_IC_IR': oos_stats['IC_IR'],
        'OOS_Mean_IC': oos_stats['Mean_IC'],
        'OOS_IC_pos%': oos_stats['IC_pos_ratio'],
        'OOS_LS_Sharpe': oos_stats['LS_Sharpe'],
        'OOS_LS_AnnRet': oos_stats['LS_AnnRet'],
        'OOS_LS_MaxDD': oos_stats['LS_MaxDD'],
        'OOS_Windows': len(model_df),
    })

summary_df = pd.DataFrame(summary_rows)
summary_df.to_csv(SUMMARY_PATH, index=False, encoding='utf-8-sig')
print(f"  [SAVED] {SUMMARY_PATH}")

# ── 最终报告 ─────────────────────────────────────────────────────

print(f"\n{'='*80}")
print(f"Factor Phase 2: ML Composite — 结果报告")
print(f"{'='*80}")

print(f"\n数据: {len(signals_df):,} 行复合信号, {signals_df['instrument_id'].nunique()} 品种")
print(f"因子: {CORE_FACTORS + [EXTRA_FACTOR]}")
print(f"Walk-Forward: 训练{TRAIN_DAYS}d | 测试{TEST_DAYS}d | 步进{STEP_DAYS}d")
print(f"总窗口: {len(windows)} (IS={sum(1 for w in windows if not w['is_oos'])}, OOS={sum(1 for w in windows if w['is_oos'])})")

# ── OOS 对比表 ──
print(f"\n{'─'*80}")
print(f"OOS 性能对比表 (2024-01 ~ 2026-06)")
print(f"{'─'*80}")

header = (f"{'Model':<16s} {'IC_IR':>7s} {'Mean_IC':>9s} {'IC>0%':>7s} "
          f"{'LS_Sharpe':>10s} {'LS_AnnRet':>10s} {'LS_MaxDD':>9s} {'Windows':>8s}")
print(header)
print(f"{'─'*16} {'─'*7} {'─'*9} {'─'*7} {'─'*10} {'─'*10} {'─'*9} {'─'*8}")

for _, row in summary_df.iterrows():
    line = (f"{row['Model']:<16s} "
            f"{row['OOS_IC_IR']:7.4f} "
            f"{row['OOS_Mean_IC']:9.4f} "
            f"{row['OOS_IC_pos%']:7.1%} "
            f"{row['OOS_LS_Sharpe']:10.4f} "
            f"{row['OOS_LS_AnnRet']:10.4f} "
            f"{row['OOS_LS_MaxDD']:9.4f} "
            f"{int(row['OOS_Windows']):8d}")
    best_ic = summary_df['OOS_IC_IR'].max()
    best_sharpe = summary_df['OOS_LS_Sharpe'].max()
    markers = []
    if row['OOS_IC_IR'] == best_ic and not np.isnan(best_ic):
        markers.append('BEST IC')
    if row['OOS_LS_Sharpe'] == best_sharpe and not np.isnan(best_sharpe):
        markers.append('BEST SHARPE')
    if markers:
        line += f"  ← {' | '.join(markers)}"
    print(line)

# ── IS vs OOS 稳定性 ──
print(f"\n{'─'*80}")
print(f"IS vs OOS 稳定性 (IC_IR 对比)")
print(f"{'─'*80}")
print(f"{'Model':<16s} {'IS IC_IR':>9s} {'OOS IC_IR':>9s} {'OOS/IS':>9s} {'稳定性':>10s}")
print(f"{'─'*16} {'─'*9} {'─'*9} {'─'*9} {'─'*10}")
for _, row in summary_df.iterrows():
    is_ir = row['IS_IC_IR']
    oos_ir = row['OOS_IC_IR']
    if np.isnan(is_ir) or np.isnan(oos_ir):
        continue
    ratio = oos_ir / is_ir if abs(is_ir) > 0.001 else np.nan
    if np.isnan(ratio):
        status = "N/A"
    elif ratio >= 0.9:
        status = "优秀"
    elif ratio >= 0.7:
        status = "稳定"
    elif ratio >= 0.5:
        status = "衰减"
    else:
        status = "过拟合"
    print(f"{row['Model']:<16s} {is_ir:9.4f} {oos_ir:9.4f} {ratio:9.2f} {status:>10s}")

# ── LightGBM 特征重要性 ──
if lgb_feature_importances:
    print(f"\n{'─'*80}")
    print(f"LightGBM 特征重要性 (均值 ± std, 共 {len(lgb_feature_importances)} 个窗口)")
    print(f"{'─'*80}")
    fi_df = pd.DataFrame(lgb_feature_importances)
    fi_cols = [c for c in fi_df.columns if c != 'window']
    # Normalize to percentage (0-100) for display
    total_fi = fi_df[fi_cols].mean().sum()
    fi_sorted = sorted(fi_cols, key=lambda c: fi_df[c].mean(), reverse=True)

    print(f"{'Feature':<22s} {'Importance %':>14s} {'Std %':>8s}")
    print(f"{'─'*22} {'─'*14} {'─'*8}")
    for col in fi_sorted:
        mean_fi_pct = fi_df[col].mean() / total_fi * 100
        std_fi_pct = fi_df[col].std() / total_fi * 100
        bar_len = int(mean_fi_pct / 2.5)  # max ~40 chars for ~100%
        bar = '█' * bar_len
        print(f"{col:<22s} {mean_fi_pct:>10.1f}% ({bar}) {std_fi_pct:>6.1f}%")

# ── IC 时序稳定性 ──
print(f"\n{'─'*80}")
print(f"OOS Rank IC 逐窗口对比 (仅 OOS 窗口)")
print(f"{'─'*80}")

oos_metrics = metrics_df[metrics_df['is_oos'] == True]

# 计算 OOS IC 均值变化
for model in sorted(oos_metrics['model'].unique()):
    model_ics = oos_metrics[oos_metrics['model'] == model]['rank_ic'].dropna().values
    if len(model_ics) == 0:
        continue
    print(f"\n  {model}:")
    print(f"    IC Mean={np.mean(model_ics):.4f}, IC Std={np.std(model_ics):.4f}, IC_IR={np.mean(model_ics)/np.std(model_ics):.4f}")
    print(f"    IC Min={np.min(model_ics):.4f}, IC Max={np.max(model_ics):.4f}")
    print(f"    IC >0 占比: {np.mean(model_ics>0):.1%} ({np.sum(model_ics>0)}/{len(model_ics)})")

print(f"\n{'='*80}")
print(f"完成! 输出文件:")
print(f"  复合信号:     {COMPOSITE_SIGNAL_PATH}")
print(f"  逐窗口指标:   {METRICS_PATH}")
print(f"  汇总对比表:   {SUMMARY_PATH}")
print(f"{'='*80}")
