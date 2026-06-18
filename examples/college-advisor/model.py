"""
高考志愿量化决策模型 v2
========================

基于真实录取规则的修正模型：

  录取规则:
    1. 平行志愿: 分数优先 → 遵循志愿 → 一轮投档
    2. 位次是唯一稳定的跨年比较锚点
    3. 两种模式: 专业(类)+院校 (无调剂) / 院校专业组 (组内调剂)

  冲稳保逻辑 (基于位次比 = 目标录取位次 / 学生位次):
    冲: ratio 0.80~0.97 (目标录取位次比学生位次更好→竞争力不足→冲刺)
    稳: ratio 0.95~1.05 (接近→匹配)
    保: ratio 1.05~1.30 (学生位次远好于目标→安全)

  核心公式:
    修正位次 = 今年位次 × (去年考生总数 / 今年考生总数)
    录取概率 = f(ratio, 趋势, 波动率, 招生计划变化)

作者: TradingStudio AI 辅助
日期: 2026-06-18
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Tuple

# ═══════════════════════════════════════════════════════════════
# 数据结构
# ═══════════════════════════════════════════════════════════════

# ── 省份录取制度配置 ──────────────────────────────────────────

@dataclass
class ProvinceConfig:
    """各省录取制度配置"""
    name: str
    mode: str                     # "专业+院校" | "院校专业组"
    total_volunteer_slots: int    # 最多可填志愿数
    has_transfer: bool            # 是否存在专业调剂
    transfer_scope: str           # "无" | "组内" | "全校"
    rush_pct: float = 0.20        # 建议冲刺比例
    stable_pct: float = 0.50      # 建议稳妥比例
    safe_pct: float = 0.30        # 建议保底比例
    batch_name: str = "本科批"    # 批次名称


# 各省录取制度表 (2026年)
PROVINCE_CONFIGS: Dict[str, ProvinceConfig] = {
    # 专业(类)+院校 模式 — 无调剂
    "浙江": ProvinceConfig("浙江", "专业+院校", 80, False, "无",
                           0.20, 0.50, 0.30),
    "山东": ProvinceConfig("山东", "专业+院校", 96, False, "无",
                           0.20, 0.50, 0.30),
    "河北": ProvinceConfig("河北", "专业+院校", 96, False, "无",
                           0.20, 0.50, 0.30),
    "重庆": ProvinceConfig("重庆", "专业+院校", 96, False, "无",
                           0.20, 0.50, 0.30),
    "辽宁": ProvinceConfig("辽宁", "专业+院校", 112, False, "无",
                           0.20, 0.50, 0.30),
    "贵州": ProvinceConfig("贵州", "专业+院校", 96, False, "无",
                           0.20, 0.50, 0.30),

    # 院校专业组 模式 — 组内调剂
    "广东": ProvinceConfig("广东", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
    "江苏": ProvinceConfig("江苏", "院校专业组", 40, True, "组内",
                           0.20, 0.50, 0.30),
    "湖北": ProvinceConfig("湖北", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
    "湖南": ProvinceConfig("湖南", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
    "福建": ProvinceConfig("福建", "院校专业组", 40, True, "组内",
                           0.20, 0.50, 0.30),
    "四川": ProvinceConfig("四川", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
    "河南": ProvinceConfig("河南", "院校专业组", 48, True, "组内",
                           0.20, 0.50, 0.30),
    "安徽": ProvinceConfig("安徽", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
    "北京": ProvinceConfig("北京", "院校专业组", 30, True, "组内",
                           0.20, 0.50, 0.30),
    "上海": ProvinceConfig("上海", "院校专业组", 24, True, "组内",
                           0.20, 0.50, 0.30),
    "天津": ProvinceConfig("天津", "院校专业组", 50, True, "组内",
                           0.20, 0.50, 0.30),
    "陕西": ProvinceConfig("陕西", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
    "山西": ProvinceConfig("山西", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
    "吉林": ProvinceConfig("吉林", "院校专业组", 40, True, "组内",
                           0.20, 0.50, 0.30),
    "黑龙江": ProvinceConfig("黑龙江", "院校专业组", 40, True, "组内",
                           0.20, 0.50, 0.30),
    "广西": ProvinceConfig("广西", "院校专业组", 40, True, "组内",
                           0.20, 0.50, 0.30),
    "甘肃": ProvinceConfig("甘肃", "院校专业组", 45, True, "组内",
                           0.20, 0.50, 0.30),
}

DEFAULT_PROVINCE_CONFIG = ProvinceConfig("默认", "院校专业组", 45, True, "组内")


def get_province_config(province_name: str) -> ProvinceConfig:
    """获取省份录取制度配置"""
    for key, cfg in PROVINCE_CONFIGS.items():
        if key in province_name or province_name in key:
            return cfg
    return DEFAULT_PROVINCE_CONFIG


@dataclass
class University:
    """高校数据"""
    name: str
    city: str
    province: str
    city_tier: float            # 1.0=一线 1.5=新一线 2.0=省会 3.0=其他
    school_tier: int            # 1=C9 2=Top985 3=Other985 4=Top211 5=Other211 6=双一流 7=省重点 8=普通
    is_985: bool
    is_211: bool
    is_double_first_class: bool
    national_rank_range: Tuple[int, int]
    industry_strength: List[str] = field(default_factory=list)
    notes: str = ""


@dataclass
class Program:
    """专业数据"""
    university_name: str
    program_name: str
    discipline: str
    eval_grade: str
    # 分省录取位次历史: {"湖北": [2023最低位次, 2024, 2025], "全国": [...]}
    admission_rank_sample: Dict[str, List[int]] = field(default_factory=dict)
    # 分省招生计划变化: {"湖北": [2023计划, 2024, 2025]}
    enrollment_quota_history: Dict[str, List[int]] = field(default_factory=dict)
    annual_tuition: int = 6000
    enrollment_quota_reference: int = 50  # 当年参考招生人数
    requires_postgrad: bool = False
    # 体检限制
    physical_requirements: List[str] = field(default_factory=list)  # ["无色盲", "无色弱", ...]
    # 单科要求
    subject_requirements: Dict[str, int] = field(default_factory=dict)  # {"英语": 120, "数学": 110}
    notes: str = ""

    def get_rank_history(self, province: str = "全国") -> Optional[List[int]]:
        """获取某省录取位次历史"""
        # 先查精确匹配
        if province in self.admission_rank_sample:
            return self.admission_rank_sample[province]
        # 再查模糊匹配
        for key in self.admission_rank_sample:
            if province in key or key in province:
                return self.admission_rank_sample[key]
        # 回退到全国
        return self.admission_rank_sample.get("全国")

    def get_quota_history(self, province: str = "全国") -> Optional[List[int]]:
        """获取招生计划变化"""
        if province in self.enrollment_quota_history:
            return self.enrollment_quota_history[province]
        for key in self.enrollment_quota_history:
            if province in key or key in province:
                return self.enrollment_quota_history[key]
        return self.enrollment_quota_history.get("全国")

    @property
    def rank_trend(self) -> float:
        """
        位次变化趋势 (线性拟合斜率)
        正值: 录取位次逐年增大 → 竞争在缓解(招更多/热度降)
        负值: 录取位次逐年减小 → 竞争在加剧(招更少/热度升)
        绝对值含义: 每年大约变化多少名
        """
        hist = self.get_rank_history()
        if not hist or len(hist) < 3:
            return 0.0
        n = len(hist)
        x_mean = (n - 1) / 2
        y_mean = sum(hist) / n
        num = sum((i - x_mean) * (hist[i] - y_mean) for i in range(n))
        den = sum((i - x_mean) ** 2 for i in range(n))
        return num / den if den != 0 else 0.0

    @property
    def rank_max_diff(self) -> int:
        """3年位次最大波动幅度 (用于大小年检测)"""
        hist = self.get_rank_history()
        if not hist or len(hist) < 2:
            return 0
        return max(hist) - min(hist)

    @property
    def is_volatile(self) -> bool:
        """是否属于"大小年"波动型 — 波动>2000名视为不稳定"""
        return self.rank_max_diff > 2000

    def quota_change_pct(self) -> float:
        """招生计划变化率 (最近一年 vs 前一年)"""
        hist = self.get_quota_history()
        if not hist or len(hist) < 2:
            return 0.0
        return (hist[-1] - hist[-2]) / hist[-2] if hist[-2] > 0 else 0.0


@dataclass
class StudentProfile:
    """考生画像"""
    province: str
    total_score: float
    province_rank: int
    province_total_examinees: int = 1_000_000
    # 上一届考生总数 (用于位次修正)
    prev_year_total_examinees: Optional[int] = None
    subject_combo: str = ""
    preferred_cities: List[str] = field(default_factory=list)
    excluded_cities: List[str] = field(default_factory=list)
    preferred_disciplines: List[str] = field(default_factory=list)
    excluded_disciplines: List[str] = field(default_factory=list)
    career_goal: str = ""
    budget_annual: int = 100_000
    plan_postgrad: bool = False
    risk_tolerance: float = 0.5        # 0=极度保守 1=极度激进
    accept_transfer: bool = True       # 是否接受调剂
    has_color_blindness: bool = False  # 色盲/色弱
    english_score: Optional[int] = None
    math_score: Optional[int] = None

    @property
    def rank_percentile(self) -> float:
        """位次百分位（越小越靠前）"""
        return self.province_rank / self.province_total_examinees

    @property
    def adjusted_rank(self) -> int:
        """
        修正位次: 考虑考生总数变化的调整
        公式: 修正位次 = 今年位次 × (去年考生总数 / 今年考生总数)

        例: 今年位次10000, 今年50万人, 去年48万人
            → 修正位次 = 10000 × (48/50) ≈ 9600
            → 今年同排名的竞争比去年稍激烈
        """
        if self.prev_year_total_examinees and self.prev_year_total_examinees > 0:
            ratio = self.prev_year_total_examinees / self.province_total_examinees
            return int(self.province_rank * ratio)
        return self.province_rank


@dataclass
class ScoredResult:
    """单个推荐结果"""
    program: Program
    university: University
    total_score: float              # 0-100 综合得分
    city_score: float               # 城市因子 (0-10)
    school_score: float             # 学校因子 (0-10)
    program_strength_score: float   # 专业实力因子 (0-10)
    rank_match_score: float         # 位次匹配因子 (0-10)
    career_fit_score: float         # 职业契合因子 (0-10)
    transfer_risk_score: float      # 调剂风险因子 (0-10, 10=无风险)
    admission_probability: float    # 录取概率 0-1
    category: str                   # "冲" / "稳" / "保"
    rank_ratio: float               # 目标位次/学生位次
    volatility_warning: str = ""    # 大小年警告
    insight: str = ""


# ═══════════════════════════════════════════════════════════════
# 打分引擎 v2
# ═══════════════════════════════════════════════════════════════

class ScoringEngine:
    """
    六因子加权模型 (v2新增调剂风险因子):

      total = w_city   * city_score
            + w_school * school_score
            + w_prog   * program_strength_score
            + w_rank   * rank_match_score
            + w_career * career_fit_score
            + w_transfer * transfer_risk_score
    """

    # 默认权重
    DEFAULT_WEIGHTS = {
        "city": 0.18,
        "school": 0.22,
        "program": 0.22,
        "rank_match": 0.20,
        "career_fit": 0.10,
        "transfer_risk": 0.08,
    }

    # ── 冲稳保: 基于位次比的阈值 (行业标准) ─────────────────

    # ratio = 录取参考位次 / 学生位次
    #  ratio < 1: 目标录取位次比学生好 → 学生竞争力不足 → "冲"
    #  ratio ≈ 1: 匹配
    #  ratio > 1: 学生远好于目标 → "保"

    RUSH_RATIO_RANGE = (0.80, 0.97)    # 冲: 目标位次在学生位次的80-97%
    STABLE_RATIO_RANGE = (0.95, 1.05)  # 稳: 目标位次在学生位次的95-105%
    SAFE_RATIO_RANGE = (1.05, 1.30)    # 保: 目标位次在学生位次的105-130%

    # 录取概率映射 (基于ratio + 波动调整)
    # 这比sigmoid更符合实际——位次比在0.95-1.05附近概率最高
    PROB_MAP = [
        # (ratio_min, prob_center, description)
        (0.60, 0.02),   # 差距巨大,几乎不可能
        (0.70, 0.05),
        (0.80, 0.12),   # 冲区间下缘
        (0.85, 0.22),
        (0.90, 0.35),   # 冲/稳分界
        (0.95, 0.50),   # 稳区间中轴
        (1.00, 0.68),   # 匹配点
        (1.05, 0.80),   # 稳/保分界
        (1.10, 0.88),
        (1.20, 0.94),
        (1.30, 0.97),
        (1.50, 0.99),
    ]

    # 行业→城市加成
    INDUSTRY_CITY_BONUS = {
        "互联网": {"北京": 1.5, "深圳": 1.5, "杭州": 1.3, "上海": 1.2, "广州": 1.0,
                   "成都": 0.8, "武汉": 0.5},
        "金融": {"上海": 1.5, "北京": 1.5, "深圳": 1.2, "广州": 0.8},
        "体制内": {"北京": 1.5, "上海": 1.0, "广州": 0.5},
        "学术": {"北京": 1.5, "上海": 1.3, "南京": 1.0, "合肥": 0.8, "杭州": 0.8},
    }

    CAREER_DISCIPLINE_MAP = {
        "互联网大厂": ["计算机/人工智能", "电子信息/电气", "数学/统计"],
        "金融": ["金融/经济", "数学/统计", "计算机/人工智能"],
        "体制内": ["法学", "金融/经济", "中文/新闻", "管理", "计算机/人工智能"],
        "学术": ["数学/统计", "物理学", "化学/化工", "生物/医学", "计算机/人工智能"],
        "制造业": ["机械/自动化", "电子信息/电气", "化学/化工"],
        "医学": ["生物/医学"],
    }

    CITY_TIER_SCORE = {1.0: 10, 1.5: 7.5, 2.0: 5.0, 3.0: 2.5}

    SCHOOL_TIER_SCORE = {
        1: 10, 2: 8.5, 3: 7.0, 4: 6.5, 5: 5.5, 6: 4.5, 7: 3.0, 8: 1.5,
    }

    EVAL_GRADE_SCORE = {
        "A+": 10, "A": 9, "A-": 8, "B+": 7, "B": 6, "B-": 5,
        "C+": 4, "C": 3, "C-": 2,
    }

    def __init__(self, weights: Optional[Dict[str, float]] = None):
        self.weights = weights or self.DEFAULT_WEIGHTS.copy()
        self._validate_weights()

    def _validate_weights(self):
        total = sum(self.weights.values())
        if abs(total - 1.0) > 0.02:
            raise ValueError(f"权重之和必须为 1.0，当前为 {total:.3f}")

    # ── 核心: 位次比计算 ─────────────────────────────────────

    def compute_rank_ratio(self, program: Program,
                           student: StudentProfile) -> Tuple[float, float, int]:
        """
        计算位次比 = 目标录取参考位次 / 学生位次

        使用三年数据的加权参考位次:
          ref_rank = 0.4 × 最近年 + 0.35 × 前一年 + 0.25 × 再前一年
          (越近年份权重越大)

        Returns:
            (ratio, ref_rank, adjusted_student_rank)
        """
        hist = program.get_rank_history(student.province)
        if not hist:
            hist = program.get_rank_history("全国")
        if not hist or len(hist) < 2:
            return (1.0, student.province_rank, student.province_rank)

        # 加权参考位次
        if len(hist) >= 3:
            ref_rank = hist[-1] * 0.4 + hist[-2] * 0.35 + hist[-3] * 0.25
        elif len(hist) == 2:
            ref_rank = hist[-1] * 0.55 + hist[-2] * 0.45
        else:
            ref_rank = hist[-1]

        # 趋势修正: 竞争加剧(负趋势)→调高参考位次(更保守)
        trend = program.rank_trend
        if trend < 0:
            # 负趋势 = 位次在变好/更卷 → 加安全边际
            ref_rank = ref_rank - trend * 0.3

        # 招生计划变化修正
        quota_change = program.quota_change_pct()
        if quota_change < -0.1:
            # 缩招>10% → 位次可能上升(更卷)
            ref_rank = ref_rank * (1.0 + quota_change)  # 调低参考位次

        adjusted_student_rank = student.adjusted_rank

        ratio = ref_rank / adjusted_student_rank if adjusted_student_rank > 0 else 1.0
        return (ratio, ref_rank, adjusted_student_rank)

    # ── 因子评分 ─────────────────────────────────────────────

    def score_city(self, university: University, student: StudentProfile) -> float:
        """城市因子"""
        base = self.CITY_TIER_SCORE.get(university.city_tier, 2.5)

        bonus = 0.0
        if student.career_goal:
            for industry, city_bonus in self.INDUSTRY_CITY_BONUS.items():
                if industry in student.career_goal or student.career_goal in industry:
                    bonus = city_bonus.get(university.city, 0.0)
                    break

        pref_bonus = 0.0
        if student.preferred_cities:
            if any(c in university.city for c in student.preferred_cities):
                pref_bonus = 1.5

        if student.excluded_cities:
            if any(c in university.city for c in student.excluded_cities):
                return 0.0

        return min(10.0, base + bonus + pref_bonus)

    def score_school(self, university: University, student: StudentProfile) -> float:
        """学校因子"""
        base = self.SCHOOL_TIER_SCORE.get(university.school_tier, 1.5)

        if student.plan_postgrad:
            if university.is_985:
                base += 1.0
            elif university.is_211:
                base += 0.5

        if "体制内" in student.career_goal:
            if university.is_985:
                base += 1.0
            elif university.is_211:
                base += 0.5

        return min(10.0, base)

    def score_program_strength(self, program: Program, _university: University) -> float:
        """专业实力因子"""
        return self.EVAL_GRADE_SCORE.get(program.eval_grade, 3.0)

    def score_rank_match(self, program: Program, student: StudentProfile) -> float:
        """位次匹配因子: 基于位次比

        以位次比 1.0 (精准匹配) 为最高分 10，向两侧衰减
        """
        ratio, _, _ = self.compute_rank_ratio(program, student)

        # 位次比在 1.0 时最优，向两侧衰减
        if 0.95 <= ratio <= 1.05:
            # 最佳匹配区间: 9-10分
            return 10.0 - abs(ratio - 1.0) * 20
        elif 0.85 <= ratio < 0.95:
            # 冲区间: 7-9分
            return 7.0 + (ratio - 0.85) / 0.10 * 2
        elif 1.05 < ratio <= 1.20:
            # 保区间: 7-9分 (安全但可能浪费分数)
            return 9.0 - (ratio - 1.05) / 0.15 * 2
        elif 0.70 <= ratio < 0.85:
            # 较难的冲: 4-7分
            return 4.0 + (ratio - 0.70) / 0.15 * 3
        elif 1.20 < ratio <= 1.50:
            # 过于安全: 4-7分
            return 7.0 - (ratio - 1.20) / 0.30 * 3
        elif ratio < 0.70:
            # 希望渺茫: 0-4分
            return max(0.0, 4.0 * ratio / 0.70)
        else:
            # 严重浪费: 0-4分
            return max(0.0, 4.0 * (2.0 - ratio) / 0.50)

    def score_career_fit(self, program: Program, university: University,
                         student: StudentProfile) -> float:
        """职业契合因子"""
        score = 5.0

        if student.career_goal:
            matched_disciplines = self.CAREER_DISCIPLINE_MAP.get(student.career_goal, [])
            if program.discipline in matched_disciplines:
                score += 2.5
            else:
                for d in matched_disciplines:
                    if d.split("/")[0] in program.discipline or program.discipline.split("/")[0] in d:
                        score += 1.5
                        break

        if student.career_goal and university.industry_strength:
            for industry in university.industry_strength:
                if industry in student.career_goal or student.career_goal in industry:
                    score += 1.5
                    break

        if student.plan_postgrad and program.requires_postgrad:
            score += 0.5
        elif not student.plan_postgrad and program.requires_postgrad:
            score -= 1.0

        return min(10.0, max(0.0, score))

    def score_transfer_risk(self, program: Program, student: StudentProfile) -> float:
        """
        调剂风险因子 (v2新增)

        评分逻辑:
          - 专业+院校模式: 无调剂, 满分10
          - 院校专业组模式:
            - 接受调剂: 基线8分
            - 不接受调剂: 基线5分 (一旦专业分不够就退档)
          - 减分项: 体检限制不满足, 单科不满足, 大小年剧烈波动
        """
        province_cfg = get_province_config(student.province)

        if province_cfg.mode == "专业+院校" or not province_cfg.has_transfer:
            return 10.0  # 无调剂风险

        # 院校专业组模式
        if student.accept_transfer:
            base = 8.0
        else:
            base = 5.0  # 不接受调剂 → 退档风险高

        # 体检限制检查
        if student.has_color_blindness:
            for req in program.physical_requirements:
                if "色盲" in req or "色弱" in req:
                    base -= 2.0
                    break

        # 单科限制检查
        if program.subject_requirements:
            for subject, min_score in program.subject_requirements.items():
                if subject == "英语" and student.english_score and student.english_score < min_score:
                    base -= 1.5
                if subject == "数学" and student.math_score and student.math_score < min_score:
                    base -= 1.5

        # 大小年警告
        if program.is_volatile:
            base -= 1.0

        return min(10.0, max(0.0, base))

    # ── 录取概率估计 (v2: 位次比驱动) ────────────────────────

    def estimate_admission_probability(
        self, program: Program, student: StudentProfile
    ) -> Tuple[float, str, float, str]:
        """
        基于位次比估计录取概率

        Returns:
            (probability, category, ratio, volatility_warning)
        """
        ratio, ref_rank, adj_student_rank = self.compute_rank_ratio(program, student)

        # 从位次比映射到概率 (查表 + 线性插值)
        prob = self._ratio_to_prob(ratio)

        # 波动调整: 大小年型 → 概率区间压缩到0.5附近(更不确定)
        if program.is_volatile:
            prob = 0.5 + (prob - 0.5) * 0.5  # 向0.5收缩50%

        # 大小年警告
        volatility_warning = ""
        if program.is_volatile:
            diff = program.rank_max_diff
            volatility_warning = f"⚠️3年位次波动{diff}名(>2000),大小年风险,慎做保底"

        # 冲稳保分类 (基于位次比)
        if ratio < self.STABLE_RATIO_RANGE[0]:
            category = "冲"
        elif ratio <= self.STABLE_RATIO_RANGE[1]:
            category = "稳"
        else:
            category = "保"

        # 风险偏好微调
        if student.risk_tolerance > 0.7 and category == "稳" and ratio < 1.0:
            # 激进用户: 边缘稳视为冲
            if ratio < 0.98:
                category = "冲"
        elif student.risk_tolerance < 0.3 and category == "稳" and ratio > 1.0:
            # 保守用户: 边缘稳视为保
            if ratio > 1.02:
                category = "保"

        # 限制概率范围
        prob = max(0.005, min(0.995, prob))

        return prob, category, ratio, volatility_warning

    def _ratio_to_prob(self, ratio: float) -> float:
        """位次比 → 录取概率 (分段线性插值)"""
        if ratio <= self.PROB_MAP[0][0]:
            return 0.01
        if ratio >= self.PROB_MAP[-1][0]:
            return 0.99

        for i in range(len(self.PROB_MAP) - 1):
            r1, p1 = self.PROB_MAP[i][0], self.PROB_MAP[i][1]
            r2, p2 = self.PROB_MAP[i + 1][0], self.PROB_MAP[i + 1][1]
            if r1 <= ratio <= r2:
                # 线性插值
                t = (ratio - r1) / (r2 - r1) if r2 != r1 else 0
                return p1 + t * (p2 - p1)

        return 0.50

    # ── 综合打分 ─────────────────────────────────────────────

    def score(self, program: Program, university: University,
              student: StudentProfile) -> ScoredResult:
        """综合打分"""
        city = self.score_city(university, student)
        school = self.score_school(university, student)
        prog = self.score_program_strength(program, university)
        rank = self.score_rank_match(program, student)
        career = self.score_career_fit(program, university, student)
        transfer = self.score_transfer_risk(program, student)
        prob, cat, ratio, vol_warn = self.estimate_admission_probability(program, student)

        # 六因子加权
        total = (
            self.weights["city"] * city
            + self.weights["school"] * school
            + self.weights["program"] * prog
            + self.weights["rank_match"] * rank
            + self.weights["career_fit"] * career
            + self.weights["transfer_risk"] * transfer
        ) * 10

        insight = self._generate_insight(program, university, student, prob, cat, ratio, vol_warn)

        return ScoredResult(
            program=program,
            university=university,
            total_score=round(total, 1),
            city_score=round(city, 2),
            school_score=round(school, 2),
            program_strength_score=round(prog, 2),
            rank_match_score=round(rank, 2),
            career_fit_score=round(career, 2),
            transfer_risk_score=round(transfer, 2),
            admission_probability=round(prob, 3),
            category=cat,
            rank_ratio=round(ratio, 4),
            volatility_warning=vol_warn,
            insight=insight,
        )

    def _generate_insight(self, program: Program, university: University,
                          student: StudentProfile, prob: float, cat: str,
                          ratio: float, vol_warn: str) -> str:
        """生成一句话分析"""
        parts = []

        labels = []
        if university.is_985: labels.append("985")
        if university.is_211: labels.append("211")
        if university.is_double_first_class: labels.append("双一流")
        tag = "/".join(labels) if labels else "普本"

        parts.append(f"{university.name}({tag})")
        parts.append(f"@{university.city}")
        parts.append(f"{program.discipline} {program.eval_grade}")

        # 位次比说明
        if ratio < 0.95:
            parts.append(f"位次比{ratio:.2f}(需冲刺)")
        elif ratio <= 1.05:
            parts.append(f"位次比{ratio:.2f}(匹配)")
        else:
            parts.append(f"位次比{ratio:.2f}(有盈余)")

        parts.append(f"录取~{prob:.0%}")

        strategy_text = {"冲": "冲刺目标", "稳": "主力志愿", "保": "安全垫"}
        parts.append(strategy_text.get(cat, ""))

        if vol_warn:
            parts.append(vol_warn)

        return " | ".join(parts)


# ═══════════════════════════════════════════════════════════════
# 推荐引擎
# ═══════════════════════════════════════════════════════════════

class AdvisorEngine:
    """高考志愿推荐引擎"""

    def __init__(self, data_dir: Optional[Path] = None):
        if data_dir is None:
            data_dir = Path(__file__).parent / "data"
        self.data_dir = Path(data_dir)
        self.universities: List[University] = []
        self.programs: List[Program] = []
        self.engine = ScoringEngine()

    def load_data(self) -> "AdvisorEngine":
        data_file = self.data_dir / "universities.json"
        if not data_file.exists():
            raise FileNotFoundError(f"数据文件不存在: {data_file}")

        with open(data_file, "r", encoding="utf-8") as f:
            data = json.load(f)

        self.universities = [
            University(**u) for u in data.get("universities", [])
        ]
        self.programs = [
            Program(**p) for p in data.get("programs", [])
        ]
        self._uni_map = {u.name: u for u in self.universities}
        return self

    def get_university(self, name: str) -> Optional[University]:
        return self._uni_map.get(name)

    def set_weights(self, weights: Dict[str, float]):
        self.engine = ScoringEngine(weights)

    def recommend(self, student: StudentProfile,
                  top_n: int = 30) -> List[ScoredResult]:
        results = []
        for program in self.programs:
            uni = self.get_university(program.university_name)
            if uni is None:
                continue
            if student.excluded_disciplines:
                if program.discipline in student.excluded_disciplines:
                    continue
            if student.excluded_cities:
                if any(c in uni.city for c in student.excluded_cities):
                    continue
            result = self.engine.score(program, uni, student)
            results.append(result)

        results.sort(key=lambda r: r.total_score, reverse=True)
        return results[:top_n]

    def get_strategy_plan(self, results: List[ScoredResult],
                          student: StudentProfile) -> Dict:
        """
        生成志愿填报策略 (考虑省份制度)
        """
        province_cfg = get_province_config(student.province)
        cats = {"冲": [], "稳": [], "保": []}
        for r in results:
            cats[r.category].append(r)

        total_slots = province_cfg.total_volunteer_slots
        rush_target = int(total_slots * province_cfg.rush_pct)
        stable_target = int(total_slots * province_cfg.stable_pct)
        safe_target = int(total_slots * province_cfg.safe_pct)

        return {
            "province_config": province_cfg,
            "total_slots": total_slots,
            "recommended_alloc": {
                "冲": rush_target,
                "稳": stable_target,
                "保": safe_target,
            },
            "actual_distribution": {
                "冲": {"count": len(cats["冲"]),
                       "avg_prob": self._avg_prob(cats["冲"]),
                       "avg_score": self._avg_score(cats["冲"])},
                "稳": {"count": len(cats["稳"]),
                       "avg_prob": self._avg_prob(cats["稳"]),
                       "avg_score": self._avg_score(cats["稳"])},
                "保": {"count": len(cats["保"]),
                       "avg_prob": self._avg_prob(cats["保"]),
                       "avg_score": self._avg_score(cats["保"])},
            },
            "warnings": self._check_warnings(cats, results, province_cfg),
        }

    @staticmethod
    def _avg_prob(items):
        return sum(r.admission_probability for r in items) / len(items) if items else 0

    @staticmethod
    def _avg_score(items):
        return sum(r.total_score for r in items) / len(items) if items else 0

    @staticmethod
    def _check_warnings(cats, results, cfg) -> List[str]:
        warnings = []
        total = len(results)
        rush_pct = len(cats["冲"]) / total if total else 0
        safe_pct = len(cats["保"]) / total if total else 0

        if rush_pct > 0.30:
            warnings.append(f"冲刺志愿占比{rush_pct:.0%}偏高(建议≤{cfg.rush_pct:.0%})")
        if safe_pct < 0.15:
            warnings.append(f"保底志愿不足({safe_pct:.0%}),存在滑档风险")
        if cfg.has_transfer:
            warnings.append(f"{cfg.mode}模式: 调剂仅限{cfg.transfer_scope},不接受调剂有退档风险")

        # 大小年警告
        volatile_items = [r for r in cats["保"] if r.program.is_volatile]
        if volatile_items:
            warnings.append(f"{len(volatile_items)}个保底志愿存在大小年波动,慎做保底")

        return warnings


# ═══════════════════════════════════════════════════════════════
# 格式化输出
# ═══════════════════════════════════════════════════════════════

def print_recommendations(results: List[ScoredResult], student: StudentProfile,
                          strategy: Optional[Dict] = None):
    """格式化打印推荐结果"""
    province_cfg = get_province_config(student.province)

    print("\n" + "=" * 100)
    print("  高考志愿量化决策报告 v2")
    print("=" * 100)
    print(f"  省份: {student.province} ({province_cfg.mode}模式, 最多{province_cfg.total_volunteer_slots}个志愿)")
    print(f"  排名: {student.province_rank:,} (修正: {student.adjusted_rank:,})  "
          f"百分位: {student.rank_percentile:.2%}")
    print(f"  职业目标: {student.career_goal or '未指定'}  "
          f"风险偏好: {'激进' if student.risk_tolerance > 0.6 else '保守' if student.risk_tolerance < 0.4 else '中性'}")
    print(f"  接受调剂: {'是' if student.accept_transfer else '否'}  "
          f"考研计划: {'是' if student.plan_postgrad else '否'}")
    print("-" * 100)

    # 表头
    header = (
        f"{'#':>2}  {'大学':<14}  {'城市':<6}  {'层次':<6}  "
        f"{'专业':<16}  {'评估':<4}  {'总分':>6}  {'位次比':>7}  "
        f"{'录取率':>7}  {'分类':<4}  {'调剂':>4}  {'学费':>7}"
    )
    print(header)
    print("-" * 100)

    for i, r in enumerate(results, 1):
        uni = r.university
        tags = ""
        if uni.is_985: tags += "9"
        if uni.is_211: tags += "2"
        if uni.is_double_first_class: tags += "双"

        cat_symbol = {"冲": ">", "稳": "~", "保": "V"}.get(r.category, "?")

        row = (
            f"{i:>2}  {uni.name:<14}  {uni.city:<6}  {tags:<6}  "
            f"{r.program.program_name:<16}  {r.program.eval_grade:<4}  "
            f"{r.total_score:>6.1f}  {r.rank_ratio:>7.3f}  "
            f"{r.admission_probability:>6.1%}  "
            f"{cat_symbol} {r.category:<2}  {r.transfer_risk_score:>4.1f}  "
            f"{r.program.annual_tuition:>7,}"
        )
        print(row)
        print(f"     | {r.insight}")

    print("-" * 100)

    # 冲稳保统计
    cats = {"冲": [], "稳": [], "保": []}
    for r in results:
        cats[r.category].append(r)

    print("\n  [冲/稳/保 分布]")
    for cat_name, cat_items in cats.items():
        if cat_items:
            avg_prob = sum(r.admission_probability for r in cat_items) / len(cat_items)
            avg_score = sum(r.total_score for r in cat_items) / len(cat_items)
            print(f"    {cat_name}: {len(cat_items)}个  均分{avg_score:.1f}  "
                  f"平均位次比{sum(r.rank_ratio for r in cat_items)/len(cat_items):.3f}  "
                  f"平均录取率{avg_prob:.1%}")

    # 策略建议
    print(f"\n  [策略建议] {province_cfg.mode}模式, {province_cfg.total_volunteer_slots}个志愿")
    print(f"    建议: 冲{int(province_cfg.total_volunteer_slots * province_cfg.rush_pct)}个 "
          f"/ 稳{int(province_cfg.total_volunteer_slots * province_cfg.stable_pct)}个 "
          f"/ 保{int(province_cfg.total_volunteer_slots * province_cfg.safe_pct)}个")
    if province_cfg.has_transfer:
        print(f"    {province_cfg.mode}: 调剂仅{province_cfg.transfer_scope}, 不接受调剂=退档风险")

    if strategy and strategy.get("warnings"):
        print(f"\n  [警告]")
        for w in strategy["warnings"]:
            print(f"    ! {w}")

    print("=" * 100 + "\n")


# ═══════════════════════════════════════════════════════════════
# 使用示例 (修改后)
# ═══════════════════════════════════════════════════════════════

if __name__ == "__main__":
    # 示例: 湖北考生，物理类，位次12000
    student = StudentProfile(
        province="湖北",
        total_score=612,
        province_rank=12000,
        province_total_examinees=500_000,
        prev_year_total_examinees=480_000,
        subject_combo="物理+化学+生物",
        preferred_cities=["武汉", "深圳", "成都", "杭州", "南京"],
        preferred_disciplines=["计算机/人工智能", "电子信息/电气"],
        career_goal="互联网大厂",
        budget_annual=60_000,
        plan_postgrad=False,
        risk_tolerance=0.5,
        accept_transfer=True,
        english_score=125,
        math_score=135,
    )

    advisor = AdvisorEngine().load_data()

    print("\n" + "=" * 100)
    print("  示例: 湖北物理类考生 | 位次12000 | 互联网方向")
    print("=" * 100)

    results = advisor.recommend(student, top_n=20)
    strategy = advisor.get_strategy_plan(results, student)
    print_recommendations(results, student, strategy)
