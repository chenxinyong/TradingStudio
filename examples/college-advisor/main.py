#!/usr/bin/env python3
"""
高考志愿量化决策助手 — CLI 入口
================================

用法:
  # 交互模式（引导式问答）
  python main.py

  # 参数模式（直接传参）
  python main.py --province 浙江 --rank 3500 --goal "互联网大厂" --budget 80000

  # 使用自定义权重
  python main.py --province 北京 --rank 2800 --goal "金融" --plan-postgrad --weights "0.22,0.28,0.14,0.18,0.10,0.08"

  # 查看可用数据
  python main.py --info
"""

import argparse
import sys
from pathlib import Path

# 确保可以导入 model
sys.path.insert(0, str(Path(__file__).parent))

from model import (
    AdvisorEngine,
    StudentProfile,
    ScoringEngine,
    print_recommendations,
)


# ── 预设权重方案 ────────────────────────────────────────────

WEIGHT_PRESETS = {
    "通用":    {"city": 0.18, "school": 0.22, "program": 0.22, "rank_match": 0.20, "career_fit": 0.10, "transfer_risk": 0.08},
    "互联网":  {"city": 0.22, "school": 0.18, "program": 0.24, "rank_match": 0.20, "career_fit": 0.08, "transfer_risk": 0.08},
    "金融":    {"city": 0.22, "school": 0.28, "program": 0.14, "rank_match": 0.18, "career_fit": 0.10, "transfer_risk": 0.08},
    "体制内":  {"city": 0.10, "school": 0.32, "program": 0.14, "rank_match": 0.22, "career_fit": 0.14, "transfer_risk": 0.08},
    "学术":    {"city": 0.10, "school": 0.22, "program": 0.32, "rank_match": 0.14, "career_fit": 0.14, "transfer_risk": 0.08},
    "保守策略": {"city": 0.14, "school": 0.18, "program": 0.28, "rank_match": 0.22, "career_fit": 0.10, "transfer_risk": 0.08},
    "激进策略": {"city": 0.22, "school": 0.22, "program": 0.20, "rank_match": 0.16, "career_fit": 0.12, "transfer_risk": 0.08},
}


def parse_args():
    parser = argparse.ArgumentParser(
        description="高考志愿量化决策助手",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  python main.py --province 浙江 --rank 3500 --goal "互联网大厂"
  python main.py --province 北京 --rank 2800 --goal "金融" --plan-postgrad
  python main.py --info
        """,
    )

    parser.add_argument("--province", type=str, help="考生的省份")
    parser.add_argument("--rank", type=int, help="省排名/位次")
    parser.add_argument("--score", type=float, help="高考总分（可选，仅用于展示）")
    parser.add_argument("--total-examinees", type=int, default=1_000_000,
                        help="省考生总数（默认100万，用于百分位计算）")
    parser.add_argument("--goal", type=str, help="职业目标（互联网大厂/金融/体制内/学术/制造业/医学）")
    parser.add_argument("--budget", type=int, default=100_000, help="家庭年预算（元）")
    parser.add_argument("--prefer-cities", type=str, help="偏好城市，逗号分隔")
    parser.add_argument("--exclude-cities", type=str, help="排除城市，逗号分隔")
    parser.add_argument("--prefer-disciplines", type=str, help="偏好专业，逗号分隔")
    parser.add_argument("--exclude-disciplines", type=str, help="排除专业，逗号分隔")
    parser.add_argument("--plan-postgrad", action="store_true", default=False,
                        help="有明确考研计划")
    parser.add_argument("--risk", type=float, default=0.5,
                        help="风险偏好 (0=极度保守, 1=极度激进)")
    parser.add_argument("--weights", type=str,
                        help="六因子权重(city,school,program,rank_match,career_fit,transfer_risk)，逗号分隔")
    parser.add_argument("--weight-preset", type=str,
                        choices=list(WEIGHT_PRESETS.keys()),
                        help=f"预设权重方案: {', '.join(WEIGHT_PRESETS.keys())}")
    parser.add_argument("--top-n", type=int, default=20, help="返回前N条结果")
    parser.add_argument("--info", action="store_true", help="查看数据库信息")
    parser.add_argument("--subject-combo", type=str, default="物理+化学+生物",
                        help="选科组合")

    return parser.parse_args()


def show_info(advisor: AdvisorEngine):
    """展示数据库概况"""
    print("\n📚 当前数据库概况")
    print("=" * 60)
    print(f"  高校数量: {len(advisor.universities)}")
    print(f"  专业数量: {len(advisor.programs)}")
    print()

    # 按层次统计
    from collections import Counter
    tier_names = {1: "C9", 2: "Top985", 3: "Other985", 4: "Top211",
                  5: "Other211", 6: "双一流", 7: "省重点", 8: "普通"}
    tier_count = Counter(u.school_tier for u in advisor.universities)
    print("  学校层次分布:")
    for tier, count in sorted(tier_count.items()):
        print(f"    {tier_names.get(tier, '?')}: {count}所")

    print()
    print("  城市分布:")
    city_count = Counter(u.city for u in advisor.universities)
    for city, count in city_count.most_common(10):
        print(f"    {city}: {count}所")

    print()
    print("  学科分布:")
    disc_count = Counter(p.discipline for p in advisor.programs)
    for disc, count in disc_count.most_common():
        print(f"    {disc}: {count}个")

    print()
    print("  ⚠️ 当前为示例数据集，实际使用时请替换为你所在省份的录取数据")
    print("    数据文件: data/universities.json")
    print()


def interactive_mode(advisor: AdvisorEngine):
    """交互式采集考生信息"""
    print("\n" + "=" * 60)
    print("  高考志愿量化决策助手 — 交互模式")
    print("=" * 60)
    print("  （直接回车使用默认值）\n")

    province = input("省份? [浙江]: ").strip() or "浙江"
    rank_str = input("省排名/位次? [3500]: ").strip() or "3500"
    rank = int(rank_str)
    score_str = input("总分（可选）? []: ").strip()
    score = float(score_str) if score_str else 0.0

    print(f"\n  可选职业目标: 互联网大厂 / 金融 / 体制内 / 学术 / 制造业 / 医学")
    goal = input("职业目标? [互联网大厂]: ").strip() or "互联网大厂"

    budget_str = input("家庭年预算（元）? [100000]: ").strip() or "100000"
    budget = int(budget_str)

    prefer_cities = input("偏好城市（逗号分隔）? [杭州,上海,深圳,北京]: ").strip() or "杭州,上海,深圳,北京"
    prefer_cities = [c.strip() for c in prefer_cities.split(",") if c.strip()]

    exclude_cities = input("排除城市（逗号分隔）? []: ").strip()
    exclude_cities = [c.strip() for c in exclude_cities.split(",") if c.strip()] if exclude_cities else []

    print(f"\n  可选学科: 计算机/人工智能 / 电子信息/电气 / 机械/自动化 / 数学/统计 / 金融/经济 / 法学 / 生物/医学")
    prefer_disc = input("偏好专业（逗号分隔）? [计算机/人工智能]: ").strip() or "计算机/人工智能"
    prefer_disc = [d.strip() for d in prefer_disc.split(",") if d.strip()]

    exclude_disc = input("排除专业（逗号分隔）? []: ").strip()
    exclude_disc = [d.strip() for d in exclude_disc.split(",") if d.strip()] if exclude_disc else []

    plan_pg = input("有考研计划? (y/n) [n]: ").strip().lower() == "y"

    risk_str = input("风险偏好 (0=保守, 0.5=中性, 1=激进)? [0.5]: ").strip() or "0.5"
    risk = float(risk_str)

    print(f"\n  预设权重方案: {', '.join(WEIGHT_PRESETS.keys())}")
    preset = input("选择权重方案? [通用]: ").strip() or "通用"
    if preset in WEIGHT_PRESETS:
        advisor.set_weights(WEIGHT_PRESETS[preset])
    else:
        advisor.set_weights(WEIGHT_PRESETS["通用"])

    top_n_str = input("返回前多少条? [20]: ").strip() or "20"
    top_n = int(top_n_str)

    student = StudentProfile(
        province=province,
        total_score=score,
        province_rank=rank,
        preferred_cities=prefer_cities,
        excluded_cities=exclude_cities,
        preferred_disciplines=prefer_disc,
        excluded_disciplines=exclude_disc,
        career_goal=goal,
        budget_annual=budget,
        plan_postgrad=plan_pg,
        risk_tolerance=risk,
    )

    results = advisor.recommend(student, top_n=top_n)
    print_recommendations(results, student)

    # 保存建议
    save = input("\n保存结果到文件? (y/n) [n]: ").strip().lower()
    if save == "y":
        output_path = Path(f"recommendation_{student.province}_{student.province_rank}.md")
        save_results(results, student, output_path)
        print(f"结果已保存到: {output_path.absolute()}")


def save_results(results, student, path: Path):
    """保存结果为 Markdown"""
    lines = [
        "# 高考志愿量化决策报告",
        "",
        f"- **省份**: {student.province}",
        f"- **排名**: {student.province_rank:,}",
        f"- **职业目标**: {student.career_goal}",
        f"- **考研计划**: {'是' if student.plan_postgrad else '否'}",
        f"- **风险偏好**: {student.risk_tolerance}",
        f"- **预算**: {student.budget_annual:,}元/年",
        "",
        "## 推荐列表",
        "",
        "| # | 大学 | 城市 | 层次 | 专业 | 学科评估 | 总分 | 录取率 | 分类 | 学费 |",
        "|---|------|------|------|------|----------|------|--------|------|------|",
    ]

    for i, r in enumerate(results, 1):
        uni = r.university
        tags = ""
        if uni.is_985: tags += "985"
        if uni.is_211: tags += "/211" if tags else "211"
        if uni.is_double_first_class: tags += "/双一流" if tags else "双一流"
        if not tags: tags = "普本"
        cat = r.category
        lines.append(
            f"| {i} | {uni.name} | {uni.city} | {tags} | "
            f"{r.program.program_name} | {r.program.eval_grade} | "
            f"{r.total_score:.1f} | {r.admission_probability:.1%} | {cat} | "
            f"{r.program.annual_tuition:,} |"
        )
        lines.append(f"| | | | | {r.insight} | | | | | |")

    lines.append("")
    lines.append("## 冲/稳/保分布")
    cats = {"冲": [], "稳": [], "保": []}
    for r in results:
        cats[r.category].append(r)
    for cat_name, cat_results in cats.items():
        if cat_results:
            avg_prob = sum(r.admission_probability for r in cat_results) / len(cat_results)
            avg_score = sum(r.total_score for r in cat_results) / len(cat_results)
            lines.append(f"- **{cat_name}**: {len(cat_results)}个, 平均录取率 {avg_prob:.1%}, 均分 {avg_score:.1f}")

    path.write_text("\n".join(lines), encoding="utf-8")


def main():
    args = parse_args()

    # 加载数据
    advisor = AdvisorEngine().load_data()

    if args.info:
        show_info(advisor)
        return

    if args.province and args.rank:
        # CLI 参数模式
        prefer_cities = []
        if args.prefer_cities:
            prefer_cities = [c.strip() for c in args.prefer_cities.split(",")]

        exclude_cities = []
        if args.exclude_cities:
            exclude_cities = [c.strip() for c in args.exclude_cities.split(",")]

        prefer_disc = []
        if args.prefer_disciplines:
            prefer_disc = [d.strip() for d in args.prefer_disciplines.split(",")]

        exclude_disc = []
        if args.exclude_disciplines:
            exclude_disc = [d.strip() for d in args.exclude_disciplines.split(",")]

        student = StudentProfile(
            province=args.province,
            total_score=args.score or 0,
            province_rank=args.rank,
            province_total_examinees=args.total_examinees,
            subject_combo=args.subject_combo,
            preferred_cities=prefer_cities,
            excluded_cities=exclude_cities,
            preferred_disciplines=prefer_disc,
            excluded_disciplines=exclude_disc,
            career_goal=args.goal or "",
            budget_annual=args.budget,
            plan_postgrad=args.plan_postgrad,
            risk_tolerance=args.risk,
        )

        # 权重设置
        if args.weights:
            parts = [float(x) for x in args.weights.split(",")]
            if len(parts) == 6:
                advisor.set_weights({
                    "city": parts[0], "school": parts[1], "program": parts[2],
                    "rank_match": parts[3], "career_fit": parts[4], "transfer_risk": parts[5],
                })
            elif len(parts) == 5:
                advisor.set_weights({
                    "city": parts[0], "school": parts[1], "program": parts[2],
                    "rank_match": parts[3], "career_fit": parts[4], "transfer_risk": 0.08,
                })
        elif args.weight_preset:
            if args.weight_preset in WEIGHT_PRESETS:
                advisor.set_weights(WEIGHT_PRESETS[args.weight_preset])

        results = advisor.recommend(student, top_n=args.top_n)
        print_recommendations(results, student)
    else:
        # 交互模式
        interactive_mode(advisor)


if __name__ == "__main__":
    main()
