#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""创新药/医药/红利 三大板块筛选推荐"""
import subprocess, json
try: import sys; sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except: pass

# 候选池：按细分环节覆盖
CANDIDATES = [
    # --- 创新药 (CXO+创新药企) ---
    ("sh603259","药明康德","创新药","CXO龙头"),
    ("sz300759","康龙化成","创新药","CXO"),
    ("sh688180","君实生物","创新药","PD-1/创新药"),
    ("sh688266","泽璟制药","创新药","创新药"),
    ("sh688331","荣昌生物","创新药","ADC"),
    ("sz000963","华东医药","创新药","转型创新药+GLP-1"),
    # --- 医药 (综合) ---
    ("sh600276","恒瑞医药","医药","创新+仿制药龙头"),
    ("sz002001","新和成","医药","维生素/原料药"),
    ("sh600085","同仁堂","医药","中药品牌"),
    ("sh600196","复星医药","医药","综合医药"),
    ("sz300347","泰格医药","医药","CRO"),
    ("sh688617","惠泰医疗","医药","医疗器械"),
    ("sh300760","迈瑞医疗","医药","医疗器械龙头"),
    # --- 红利 (高股息) ---
    ("sh601088","中国神华","红利","煤炭/高股息"),
    ("sh600036","招商银行","红利","银行高股息"),
    ("sh601398","工商银行","红利","银行+稳定分红"),
    ("sh600900","长江电力","红利","水电/高股息标杆"),
    ("sh601857","中国石油","红利","石油/高股息"),
    ("sh600585","海螺水泥","红利","水泥/高股息"),
    ("sh601006","大秦铁路","红利","铁路/高股息"),
    ("sh600028","中国石化","红利","石化/高股息"),
]

def fetch_weekly(code):
    sid='1.'+code[2:] if code.startswith('sh') else '0.'+code[2:]
    url=f'https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={sid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57&klt=102&fqt=0&end=20500101&lmt=60'
    try:
        r=subprocess.run(['curl','-s','--max-time','15','-H','Referer: https://quote.eastmoney.com','-H','User-Agent: Mozilla/5.0',url],capture_output=True,timeout=20)
        data=json.loads(r.stdout)['data']
        if not data: return None
        return [(l.split(',')[0],float(l.split(',')[2]),float(l.split(',')[3]),float(l.split(',')[4]),float(l.split(',')[5])) for l in data['klines']]
    except: return None

results=[]
for code,name,sector,note in CANDIDATES:
    wk=fetch_weekly(code)
    if not wk or len(wk)<5:
        results.append((sector,name,code,note,None,None,None,None,None))
        continue
    cur_c=wk[-1][1]; all_h=max(w[2] for w in wk); dd=(cur_c-all_h)/all_h*100
    prev_c=wk[-2][1]; wk_chg=(cur_c-prev_c)/prev_c*100
    c4w=wk[-4][1]; tr4=(cur_c-c4w)/c4w*100
    # 5-week MA
    if len(wk)>=5:
        ma5=sum(w[1] for w in wk[-5:])/5; ma5_pos=(cur_c-ma5)/ma5*100
    else: ma5_pos=0
    results.append((sector,name,code,note,cur_c,wk_chg,tr4,dd,ma5_pos))

valid=[r for r in results if r[4] is not None]

# ===== 输出 =====
for sector in ['创新药','医药','红利']:
    sec=[r for r in valid if r[0]==sector]
    print(f"\n{'='*75}")
    print(f"  [{sector}]  筛选标准:"+
          ("强研发管线+国际化" if sector=='创新药' else
           ("多细分覆盖+龙头地位" if sector=='医药' else
            "高股息+低波动+稳定现金流")))
    print(f"{'='*75}")
    print(f"{'推荐':<4}{'名称':<8}{'代码':<10}{'细分':<16}{'收盘':>8}{'本周':>8}{'近4周':>8}{'回撤':>8}{'距5wMA':>8}")
    print('-'*75)
    sec_sorted=sorted(sec,key=lambda x:x[5],reverse=True)
    for i,r in enumerate(sec_sorted):
        print(f"{i+1:<4}{r[1]:<8}{r[2]:<10}{r[3]:<16}{r[4]:>8.2f}{r[5]:>+7.2f}%{r[6]:>+7.2f}%{r[7]:>+7.1f}%{r[8]:>+7.2f}%")
    # 板块均值
    avg_wk=sum(r[5] for r in sec)/len(sec)
    avg_dd=sum(r[7] for r in sec)/len(sec)
    avg_4w=sum(r[6] for r in sec)/len(sec)
    print(f"  板块均值: 本周{avg_wk:+.2f}%  近4周{avg_4w:+.2f}%  回撤{avg_dd:.1f}%")

# ===== 综合推荐TOP10 =====
print(f"\n\n{'='*75}")
print("  综合推荐 TOP 10（按 本周强弱 + 近4周抗跌 + 回撤可控 综合）")
print(f"{'='*75}")
# score: 本周*0.4 + 近4周*0.2 + 回撤绝对值*0.2 + 距5wMA*0.1 + (本周>0加分)*0.1
def score(r):
    s=0
    if r[5] is not None:
        s+=r[5]*0.4 + r[6]*0.2 - abs(r[7])*0.1 + r[8]*0.2
        if r[5]>0: s+=2
    return s
ranked=sorted(valid,key=score,reverse=True)[:10]
for i,r in enumerate(ranked):
    flag='' if r[5] is None else ('' if r[5]>0 else '')
    print(f"  {i+1}. {r[1]:<8} [{r[0]}] {r[3]:<16}  本周{r[5]:>+6.2f}%  回撤{r[7]:>+6.1f}%")
