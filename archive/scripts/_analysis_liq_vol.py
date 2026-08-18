"""品种流动性+波动性排名分析 - 基于 DuckDB 全量数据"""
import duckdb, math
from collections import defaultdict

DB = 'c:/Works/ClaudeCode/TradingStudio/data/bars_merged.duckdb'
MIN_DAYS = 200

NAMES = {
    'ag':'白银','al':'铝','ao':'氧化铝','au':'黄金','br':'BR橡胶','bu':'沥青',
    'cu':'铜','fu':'燃油','hc':'热卷','ni':'镍','pb':'铅','rb':'螺纹钢',
    'ru':'橡胶','sn':'锡','sp':'纸浆','ss':'不锈钢','wr':'线材','zn':'锌',
    'a':'豆一','b':'豆二','bb':'胶板','c':'玉米','cs':'淀粉','eb':'苯乙烯',
    'eg':'乙二醇','fb':'纤板','i':'铁矿石','j':'焦炭','jd':'鸡蛋','jm':'焦煤',
    'l':'塑料','lh':'生猪','m':'豆粕','p':'棕榈','pg':'LPG','pp':'聚丙烯',
    'rr':'粳米','v':'PVC','y':'豆油',
    'MA':'甲醇','TA':'PTA','SA':'纯碱','FG':'玻璃','RM':'菜粕','SR':'白糖',
    'CF':'棉花','CY':'棉纱','OI':'菜油','PF':'短纤','PK':'花生','SF':'硅铁',
    'SM':'锰硅','UR':'尿素','AP':'苹果','CJ':'红枣','ZC':'动力煤','SH':'烧碱',
    'sc':'原油','lu':'低硫燃油','nr':'20号胶','bc':'铜国际','ec':'集运指数',
    'IC':'中证500','IF':'沪深300','IH':'上证50','IM':'中证1000',
    'T':'10年国债','TF':'5年国债','TL':'30年国债','TS':'2年国债',
    'lc':'碳酸锂','ps':'多晶硅','si':'工业硅',
    'pt':'对二甲苯','pd':'瓶片',
}

EXCH = {
    'ag':'SHFE','al':'SHFE','ao':'SHFE','au':'SHFE','br':'SHFE','bu':'SHFE',
    'cu':'SHFE','fu':'SHFE','hc':'SHFE','ni':'SHFE','pb':'SHFE','rb':'SHFE',
    'ru':'SHFE','sn':'SHFE','sp':'SHFE','ss':'SHFE','wr':'SHFE','zn':'SHFE',
    'a':'DCE','b':'DCE','bb':'DCE','c':'DCE','cs':'DCE','eb':'DCE','eg':'DCE',
    'fb':'DCE','i':'DCE','j':'DCE','jd':'DCE','jm':'DCE','l':'DCE','lh':'DCE',
    'm':'DCE','p':'DCE','pg':'DCE','pp':'DCE','rr':'DCE','v':'DCE','y':'DCE',
    'MA':'CZCE','TA':'CZCE','SA':'CZCE','FG':'CZCE','RM':'CZCE','SR':'CZCE',
    'CF':'CZCE','CY':'CZCE','OI':'CZCE','PF':'CZCE','PK':'CZCE','SF':'CZCE',
    'SM':'CZCE','UR':'CZCE','AP':'CZCE','CJ':'CZCE','SH':'CZCE','ZC':'CZCE',
    'sc':'INE','lu':'INE','nr':'INE','bc':'INE','ec':'INE',
    'IC':'CFFEX','IF':'CFFEX','IH':'CFFEX','IM':'CFFEX',
    'T':'CFFEX','TF':'CFFEX','TL':'CFFEX','TS':'CFFEX',
    'lc':'GFEX','ps':'GFEX','si':'GFEX',
    'pt':'CZCE','pd':'CZCE',
}


def main():
    conn = duckdb.connect(DB)

    # 1. Instrument-daily aggregation from 1min bars
    daily_inst = conn.execute('''
        SELECT
            instrument_id,
            bar_time::DATE as dt,
            MIN(CASE WHEN volume > 0 THEN open END) as day_open,
            MAX(high) as day_high,
            MIN(low) as day_low,
            MAX(close) as day_close,
            SUM(volume) as day_vol,
            SUM(turnover) as day_turnover,
            MAX(open_interest) as day_oi
        FROM bars_1min
        GROUP BY instrument_id, bar_time::DATE
        HAVING SUM(volume) > 0 AND MAX(close) > 0
    ''').fetchall()

    print(f"Instrument-daily rows: {len(daily_inst):,}")

    # 2. Aggregate to product level
    pd_dict = defaultdict(lambda: {
        'daily_vol':[], 'daily_to':[], 'daily_oi':[],
        'daily_range_pct':[], 'daily_ret':[]
    })

    for inst, dt, o, h, l, c, v, to, oi in daily_inst:
        prod = ''.join(ch for ch in inst if not ch.isdigit())
        pd_dict[prod]['daily_vol'].append(v)
        pd_dict[prod]['daily_to'].append(to)
        pd_dict[prod]['daily_oi'].append(oi)
        if o and c and o > 0 and c > 0:
            pd_dict[prod]['daily_ret'].append(float((c - o) / o))
        if c and c > 0 and h and l:
            pd_dict[prod]['daily_range_pct'].append(float((h - l) / c * 100))

    # 3. Compute metrics
    results = []
    short_products = []
    for prod, d in pd_dict.items():
        n = len(d['daily_vol'])
        if n < 20:
            continue

        avg_vol = sum(d['daily_vol']) / n
        avg_to = sum(d['daily_to']) / n
        avg_oi = sum(d['daily_oi']) / n
        avg_range = sum(d['daily_range_pct'])/len(d['daily_range_pct']) if d['daily_range_pct'] else 0
        rets = d['daily_ret']
        if rets:
            mean_r = sum(rets)/len(rets)
            std_r = (sum((r-mean_r)**2 for r in rets)/len(rets))**0.5
            ann_vol = std_r * math.sqrt(250) * 100
        else:
            ann_vol = 0

        r = {
            'prod': prod, 'days': n,
            'avg_vol': avg_vol, 'avg_to': avg_to, 'avg_oi': avg_oi,
            'avg_range_pct': avg_range, 'ann_vol': ann_vol,
        }
        if n >= MIN_DAYS:
            results.append(r)
        else:
            short_products.append(r)

    print(f"Qualified (>= {MIN_DAYS} days): {len(results)}")
    print(f"Short-history (20-{MIN_DAYS-1} days): {len(short_products)}")

    # 4. Normalize and score
    def norm(vals):
        mn, mx = min(vals), max(vals)
        return [(v-mn)/(mx-mn) if mx>mn else 0.5 for v in vals]

    n_vol = norm([math.log(r['avg_vol']+1) for r in results])
    n_to = norm([math.log(r['avg_to']+1) for r in results])
    n_oi = norm([math.log(r['avg_oi']+1) for r in results])
    n_range = norm([r['avg_range_pct'] for r in results])
    n_ann = norm([r['ann_vol'] for r in results])

    for i, r in enumerate(results):
        liq = n_vol[i]*0.4 + n_to[i]*0.3 + n_oi[i]*0.3
        vol = n_range[i]*0.5 + n_ann[i]*0.5
        r['score'] = liq * 0.5 + vol * 0.5
        r['liq'] = liq
        r['vol'] = vol

    ranked = sorted(results, key=lambda x: x['score'], reverse=True)

    # 5. Print
    print(f"\n{'='*120}")
    print(f"  综合排名 Top 30 — 流动性(50%) + 波动性(50%) | 最低 {MIN_DAYS} 个交易日")
    print(f"{'='*120}")
    hdr = f"{'#':>3} | {'品种':>12} | {'交易所':>5} | {'天数':>5} | {'日均量':>10} | {'日均额(亿)':>10} | {'日均OI':>10} | {'日内波幅%':>8} | {'年化波%':>7} | {'流动性':>5} | {'波动性':>5} | {'综合':>5}"
    print(hdr)
    print('-' * len(hdr))

    for i, r in enumerate(ranked[:30]):
        name = NAMES.get(r['prod'], r['prod'])
        exch = EXCH.get(r['prod'], '?')
        if r['avg_vol'] >= 1e6:
            vol_s = f"{r['avg_vol']/1e6:.2f}M"
        elif r['avg_vol'] >= 1e4:
            vol_s = f"{r['avg_vol']/1e4:.0f}万"
        else:
            vol_s = f"{r['avg_vol']:.0f}"

        print(f"{i+1:>3} | {name:>6}({r['prod']}) | {exch:>5} | {r['days']:>5} | {vol_s:>10} | {r['avg_to']/1e8:>10.1f} | {r['avg_oi']/1e4:>9.0f}万 | {r['avg_range_pct']:>7.2f}% | {r['ann_vol']:>6.1f}% | {r['liq']:>5.3f} | {r['vol']:>5.3f} | {r['score']:>5.3f}")

    # 6. Dimension leaders
    print(f"\n--- 纯流动性 Top 10 ---")
    for r in sorted(ranked, key=lambda x: x['liq'], reverse=True)[:10]:
        name = NAMES.get(r['prod'], r['prod'])
        print(f"  {name}({r['prod']}): {r['liq']:.3f}, 日均量={r['avg_vol']/1e4:.0f}万, 日均额={r['avg_to']/1e8:.1f}亿")

    print(f"\n--- 纯波动性 Top 10 ---")
    for r in sorted(ranked, key=lambda x: x['vol'], reverse=True)[:10]:
        name = NAMES.get(r['prod'], r['prod'])
        print(f"  {name}({r['prod']}): {r['vol']:.3f}, 日内波幅={r['avg_range_pct']:.2f}%, 年化波={r['ann_vol']:.1f}%")

    # 7. Short-history high-vol products (not ranked but notable)
    if short_products:
        print(f"\n--- 短历史高波动品种 (20-{MIN_DAYS-1}天, 未入榜) ---")
        for r in sorted(short_products, key=lambda x: x['ann_vol'], reverse=True)[:15]:
            name = NAMES.get(r['prod'], r['prod'])
            print(f"  {name}({r['prod']}): {r['days']}天, annVol={r['ann_vol']:.1f}%, range={r['avg_range_pct']:.2f}%")

    # 8. Classification
    print(f"\n--- 品种分类建议 ---")
    high_liq = sorted(ranked, key=lambda x: x['liq'], reverse=True)[:20]
    high_vol = sorted(ranked, key=lambda x: x['vol'], reverse=True)[:20]
    # Intersection: high liq AND high vol (top 20 in both)
    liq_set = {r['prod'] for r in high_liq}
    vol_set = {r['prod'] for r in high_vol}
    both = liq_set & vol_set
    liq_only = liq_set - vol_set
    vol_only = vol_set - liq_set

    print(f"\n  【双优】流动性+波动性俱佳 (Top20双榜):")
    for p in sorted(both, key=lambda x: next(r['score'] for r in ranked if r['prod']==x), reverse=True):
        r = next(x for x in ranked if x['prod']==p)
        name = NAMES.get(p, p)
        print(f"    {name}({p}) — liq={r['liq']:.3f}, vol={r['vol']:.3f}")

    print(f"\n  【流质】高流动性低波动 (适合大资金/套利):")
    for p in sorted(liq_only, key=lambda x: next(r['liq'] for r in ranked if r['prod']==x), reverse=True):
        r = next(x for x in ranked if x['prod']==p)
        name = NAMES.get(p, p)
        print(f"    {name}({p}) — liq={r['liq']:.3f}, vol={r['vol']:.3f}")

    print(f"\n  【波动】高波动偏低流动性 (适合小资金/CTA):")
    for p in sorted(vol_only, key=lambda x: next(r['vol'] for r in ranked if r['prod']==x), reverse=True):
        r = next(x for x in ranked if x['prod']==p)
        name = NAMES.get(p, p)
        print(f"    {name}({p}) — liq={r['liq']:.3f}, vol={r['vol']:.3f}")

    conn.close()


if __name__ == '__main__':
    main()
