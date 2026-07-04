#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成《中国本科专业洗牌数据分析（2021-2025）》PDF 报告
为 陈文谦 2026 年高考志愿填报提供数据支撑
"""

import os
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm, cm
from reportlab.lib.enums import TA_CENTER, TA_LEFT, TA_JUSTIFY
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle,
    PageBreak, HRFlowable, KeepTogether
)
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

# ── 字体注册 ─────────────────────────────────────────────
FONT_DIR = 'C:/Windows/Fonts'
pdfmetrics.registerFont(TTFont('SimHei', os.path.join(FONT_DIR, 'simhei.ttf')))
pdfmetrics.registerFont(TTFont('SimKai', os.path.join(FONT_DIR, 'simkai.ttf')))
pdfmetrics.registerFont(TTFont('STSONG', os.path.join(FONT_DIR, 'STSONG.TTF')))
pdfmetrics.registerFont(TTFont('STXIHEI', os.path.join(FONT_DIR, 'STXIHEI.TTF')))

# ── 样式定义 ─────────────────────────────────────────────
styles = getSampleStyleSheet()

body_style = ParagraphStyle(
    'CNBody', fontName='STSONG', fontSize=10, leading=18,
    spaceAfter=6, alignment=TA_JUSTIFY,
)

h1_style = ParagraphStyle(
    'CNH1', fontName='SimHei', fontSize=20, leading=28,
    spaceAfter=12, spaceBefore=12, alignment=TA_CENTER,
    textColor=colors.HexColor('#1a1a2e'),
)

h2_style = ParagraphStyle(
    'CNH2', fontName='SimHei', fontSize=14, leading=22,
    spaceAfter=8, spaceBefore=16,
    textColor=colors.HexColor('#16213e'),
)

h3_style = ParagraphStyle(
    'CNH3', fontName='SimHei', fontSize=12, leading=18,
    spaceAfter=6, spaceBefore=12,
    textColor=colors.HexColor('#0f3460'),
)

note_style = ParagraphStyle(
    'CNNote', fontName='STSONG', fontSize=8, leading=12,
    textColor=colors.HexColor('#888888'), spaceAfter=4,
)

em_style = ParagraphStyle(
    'CNEm', fontName='SimHei', fontSize=10, leading=18,
    textColor=colors.HexColor('#c0392b'), spaceAfter=6,
)

cell_style = ParagraphStyle(
    'CNCell', fontName='STSONG', fontSize=9, leading=14,
    alignment=TA_CENTER,
)
cell_left_style = ParagraphStyle(
    'CNCellL', fontName='STSONG', fontSize=9, leading=14,
    alignment=TA_LEFT,
)
cell_header_style = ParagraphStyle(
    'CNCellH', fontName='SimHei', fontSize=9, leading=14,
    alignment=TA_CENTER, textColor=colors.white,
)


def P(text, style=body_style):
    return Paragraph(text, style)


def Hr():
    return HRFlowable(width='100%', thickness=0.5, color=colors.HexColor('#cccccc'), spaceAfter=8, spaceBefore=4)


def make_table(headers, rows, col_widths=None):
    data = [[Paragraph(h, cell_header_style) for h in headers]]
    for row in rows:
        data.append([Paragraph(str(c), cell_style) for c in row])

    if col_widths is None:
        n = len(headers)
        col_widths = [460 / n] * n

    t = Table(data, colWidths=col_widths, repeatRows=1)
    style_cmds = [
        ('BACKGROUND', (0, 0), (-1, 0), colors.HexColor('#2c3e50')),
        ('TEXTCOLOR', (0, 0), (-1, 0), colors.white),
        ('FONTNAME', (0, 0), (-1, 0), 'SimHei'),
        ('FONTSIZE', (0, 0), (-1, 0), 9),
        ('BOTTOMPADDING', (0, 0), (-1, 0), 8),
        ('TOPPADDING', (0, 0), (-1, 0), 8),
        ('GRID', (0, 0), (-1, -1), 0.4, colors.HexColor('#bdc3c7')),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, colors.HexColor('#f8f9fa')]),
        ('TOPPADDING', (0, 1), (-1, -1), 5),
        ('BOTTOMPADDING', (0, 1), (-1, -1), 5),
        ('VALIGN', (0, 0), (-1, -1), 'MIDDLE'),
    ]
    t.setStyle(TableStyle(style_cmds))
    return t


def build_pdf():
    output_path = os.path.join(os.path.dirname(__file__), '..', 'docs', '中国本科专业洗牌数据分析_2021-2025.pdf')
    output_path = os.path.abspath(output_path)
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    doc = SimpleDocTemplate(
        output_path, pagesize=A4,
        leftMargin=20*mm, rightMargin=20*mm,
        topMargin=18*mm, bottomMargin=18*mm,
        title='中国本科专业洗牌数据分析（2021-2025）',
        author='陈新勇',
    )

    story = []

    # ═══════════════════════════════════════════════
    # 封面区
    # ═══════════════════════════════════════════════
    story.append(Spacer(1, 30*mm))
    story.append(P('中国本科专业洗牌数据分析', h1_style))
    story.append(P('2021 — 2025', ParagraphStyle('sub', fontName='SimHei', fontSize=14, leading=20, alignment=TA_CENTER, textColor=colors.HexColor('#7f8c8d'), spaceAfter=20)))
    story.append(Hr())
    story.append(Spacer(1, 8*mm))
    story.append(P('基于教育部官方备案数据、麦可思就业报告及高校公示信息', ParagraphStyle('sub2', fontName='STSONG', fontSize=11, leading=16, alignment=TA_CENTER, textColor=colors.HexColor('#555555'))))
    story.append(P('为 陈文谦 2026 年高考志愿填报提供数据参考', ParagraphStyle('sub2', fontName='STSONG', fontSize=11, leading=16, alignment=TA_CENTER, textColor=colors.HexColor('#555555'))))
    story.append(Spacer(1, 10*mm))
    story.append(P('生成日期：2026 年 6 月 23 日', ParagraphStyle('sub2', fontName='STSONG', fontSize=10, leading=14, alignment=TA_CENTER, textColor=colors.HexColor('#999999'))))
    story.append(P('数据来源：教育部 · 新华社 · 南华早报 · 麦可思研究院 · 财新 · 经济观察报', ParagraphStyle('sub2', fontName='STSONG', fontSize=8, leading=12, alignment=TA_CENTER, textColor=colors.HexColor('#aaaaaa'))))
    story.append(PageBreak())

    # ═══════════════════════════════════════════════
    # 第一章：宏观全景
    # ═══════════════════════════════════════════════
    story.append(P('一、宏观全景：五年裁撤 12,200，新增 10,200', h2_style))
    story.append(Hr())
    story.append(P(
        '据新华社 2026 年 6 月 14 日援引教育部数据报道，2021 年至 2025 年间，'
        '中国高校取消或暂停了 <b>12,200 个</b>本科专业布点，'
        '并新增了 <b>10,200 个</b>新专业布点。这一调整影响了全国超过 <b>30%</b> 的大学专业。'
        '——《南华早报》报道指出，裁撤主要集中在艺术、人文、外语和管理类专业，'
        '背后的逻辑很简单：那些无法直接就业的学位被视为累赘。',
        body_style
    ))

    story.append(Spacer(1, 4*mm))
    story.append(make_table(
        ['指标', '数据', '来源'],
        [
            ['撤销/停招专业点（2021-2025）', '12,200 个', '教育部 · 新华社 2026.6.14'],
            ['新增专业点（2021-2025）', '10,200 个', '同上'],
            ['调整覆盖面', '> 30% 本科专业', '同上'],
            ['2024 年撤销专业点', '1,428 个', '教育部 2024 年度备案审批结果'],
            ['2024 年停招专业点', '2,220 个', '同上'],
            ['2024 年新增专业点', '1,839 个', '同上'],
            ['2024 年裁撤/新增比', '1.98 : 1（裁 > 增）', '计算值'],
            ['2026 年应届毕业生', '1,270 万', '教育部'],
            ['青年失业率（16-24 岁）', '16.9%（2026.3）', '国家统计局'],
        ],
        col_widths=[160, 130, 170],
    ))
    story.append(Spacer(1, 2*mm))
    story.append(P(
        '注：2024 年停招 2,220 + 撤销 1,428 = 近 4,000 个专业点退出招生，已超过当年新增数 1,839。',
        note_style
    ))

    # 逐年趋势
    story.append(Spacer(1, 6*mm))
    story.append(P('2020—2024 年本科专业撤销数量逐年趋势', h3_style))
    story.append(make_table(
        ['年份', '撤销专业点数', '较上年变化', '关键事件'],
        [
            ['2020', '518', '—', '疫情元年，调整温和'],
            ['2021', '804', '+55%', '双减政策出台，师范类开始调整'],
            ['2022', '925', '+15%', '新高考改革全面落地'],
            ['2023', '1,670', '+81%', '五部门《改革方案》目标年，峰值'],
            ['2024', '1,428', '-14%', '29 种全新专业列入招生目录'],
        ],
        col_widths=[60, 100, 100, 200],
    ))
    story.append(P('数据来源：教育部《普通高等学校本科专业备案和审批结果》（2020-2024 年度）', note_style))

    story.append(PageBreak())

    # ═══════════════════════════════════════════════
    # 第二章：裁撤专业详解
    # ═══════════════════════════════════════════════
    story.append(P('二、哪些专业在被裁？—— 分学科门类数据', h2_style))
    story.append(Hr())

    story.append(P('2024 年度撤销专业的学科门类构成', h3_style))
    story.append(make_table(
        ['学科门类', '占撤销总数', '代表专业（括号内为 2024 年撤销数）'],
        [
            ['工学（落后方向）', '32.84%', '网络工程（26）、传统工科'],
            ['管理学', '18.73%', '信管（38）、市场营销（34）、公共事业管理'],
            ['理学', '13.99%', '信息与计算科学（27）'],
            ['艺术学', '12.67%', '产品设计（24）、服装设计'],
            ['文学（含外语）', '9.26%', '英语（41 校撤）、日语（34 校撤）'],
            ['其他', '12.51%', '法学、经济学等少量调整'],
        ],
        col_widths=[100, 80, 280],
    ))
    story.append(Spacer(1, 2*mm))
    story.append(P(
        '▲ 管理 + 艺术 + 文学三类合计占裁撤总量的 40.66%，是名副其实的“重灾区”。',
        em_style
    ))

    story.append(Spacer(1, 6*mm))
    story.append(P('2024 年度撤销数量 Top 5 专业', h3_style))
    story.append(make_table(
        ['排名', '专业名称', '2024 年撤销数', '五年累计撤销'],
        [
            ['1', '信息管理与信息系统', '38', '160（连续四年第一）'],
            ['2', '市场营销', '34', '> 100'],
            ['3', '信息与计算科学', '27', '—'],
            ['4', '网络工程', '26', '—'],
            ['5', '产品设计', '24', '98（十年）'],
        ],
        col_widths=[50, 180, 110, 120],
    ))

    story.append(Spacer(1, 6*mm))
    story.append(P('典型高校自查行动（2024-2025）', h3_style))
    story.append(make_table(
        ['高校', '一次撤销/停招数', '涉及专业（部分）'],
        [
            ['四川大学（985）', '31 个', '音乐学、表演、动画、广电、信管、电商…'],
            ['中国传媒大学（211）', '16 个', '翻译、摄影、漫画、视觉传达…'],
            ['吉林大学（985）', '19 个停招', '6 个属艺术学类'],
            ['西北工业大学（985）', '多个', '停招电子商务'],
            ['山东师范大学', '25 个（累计）', '人力资源管理、广播电视学…'],
        ],
        col_widths=[120, 100, 240],
    ))
    story.append(P('数据来源：各高校官网公示、教育部备案审批结果、经济观察报（2026.4）', note_style))

    story.append(PageBreak())

    # ═══════════════════════════════════════════════
    # 第三章：新增专业
    # ═══════════════════════════════════════════════
    story.append(P('三、哪些专业在增长？—— 国家战略方向', h2_style))
    story.append(Hr())

    story.append(P(
        '教育部 2024 年度（2025 年 4 月公布）审批结果显示全国新增专业点 <b>1,839 个</b>。'
        '新增数量前十的专业全部集中在 AI、集成电路、机器人等国家战略新兴产业方向。',
        body_style
    ))

    story.append(Spacer(1, 4*mm))
    story.append(P('2024 年度新增数量 Top 10 专业', h3_style))
    story.append(make_table(
        ['排名', '专业名称', '新增数量', '专业方向归属'],
        [
            ['1', '人工智能', '91', '计算机科学延伸'],
            ['2', '数字经济', '76', '经管 + 技术交叉'],
            ['3', '智能建造', '49', '土木 + AI'],
            ['4', '大数据管理与应用', '40', '管理 + 数据'],
            ['5', '机器人工程', '32', '电子 + 机械 + 计算机'],
            ['6', '智能制造工程', '32', '机械 + AI'],
            ['7', '集成电路设计与集成系统', '31', '电子信息（硬件）'],
            ['8', '智能科学与技术', '25', '计算机科学延伸'],
            ['9', '网络与新媒体', '23', '传播 + 技术'],
            ['10', '新能源材料与器件', '21', '材料 + 能源'],
        ],
        col_widths=[45, 170, 80, 165],
    ))

    story.append(Spacer(1, 6*mm))
    story.append(P('2025 年首次招生的 29 种全新专业（教育部 2024 年审批）', h3_style))
    story.append(P(
        '具身智能（9 校首批设立）· 脑机接口 · 农业机器人 · 碳中和科学与工程 · '
        '智能视听工程 · 集成电路科学与工程（复旦/南邮/重邮首批）· '
        '数字人文 · AI 辅助传播 · 数据治理 等 29 种。',
        body_style
    ))
    story.append(Spacer(1, 2*mm))
    story.append(P(
        '⚠ 注意：新专业名字虽好，但课程体系、师资、就业出口均未经验证。'
        '本科阶段慎选未经市场检验的“新专业”。',
        em_style
    ))

    story.append(PageBreak())

    # ═══════════════════════════════════════════════
    # 第四章：就业率数据
    # ═══════════════════════════════════════════════
    story.append(P('四、就业率数据：打破“学 CS 就一定好就业”的迷思', h2_style))
    story.append(Hr())

    story.append(P('2024 届本科毕业生分专业类去向落实率（麦可思研究院《2025 年中国本科生就业报告》）', h3_style))
    story.append(make_table(
        ['专业大类', '2024 届去向落实率', '2023 届', '趋势'],
        [
            ['全国本科平均', '86.7%', '86.4%', '→ 基本持平'],
            ['历史学类', '87.2%', '—', '↑'],
            ['外国语言文学类', '86.9%', '—', '→ 高于 CS'],
            ['计算机类', '82.4%', '83.2%', '↓ 连续三年下降'],
            ['金融学类', '81.7%', '81.6%', '→'],
            ['音乐与舞蹈学类', '81.0%', '83.5%', '↓'],
            ['美术学类', '80.0%', '80.0%', '→'],
            ['法学类', '75.0%', '74.9%', '→ 垫底'],
        ],
        col_widths=[120, 120, 100, 120],
    ))
    story.append(Spacer(1, 2*mm))
    story.append(P(
        '▲ 计算机类 82.4%，在 61 个专业类中排名倒数第 11，低于全国平均 4.3 个百分点。'
        '这是该专业类连续第三年就业率下滑（2022: 86.6% → 2023: 83.2% → 2024: 82.4%）。',
        em_style
    ))

    story.append(Spacer(1, 6*mm))
    story.append(P('为什么 CS 就业率反而不高？', h3_style))
    story.append(make_table(
        ['原因', '具体说明'],
        [
            ['供给爆炸', '全国 900+ 高校开设计算机专业，每年毕业 40 万+，远超市场吸纳能力'],
            ['腰部以下塌陷', '985/211 的 CS 依然抢手（90%+），但双非院校 CS 供给严重过剩，拉低整体数据'],
            ['互联网行业调整', '2022-2024 年大厂持续裁员缩编，初级岗位需求锐减'],
            ['AI 替代低端岗位', '基础 CRUD、前端切图、初级测试等岗位正在被 AI 工具快速替代'],
            ['扩招惯性', '各校为迎合热门专业大规模扩张 CS 招生，供给远超需求增速'],
        ],
        col_widths=[140, 320],
    ))

    story.append(Spacer(1, 6*mm))
    story.append(P('各专业类薪资对比（2023 届本科毕业生月收入）', h3_style))
    story.append(make_table(
        ['专业类', '月收入（元）', '三年趋势', '2024 就业率'],
        [
            ['计算机类', '6,771', '↓ 连续三年下降但仍领跑', '82.4%'],
            ['电子信息类', '6,500+', '↑ 半导体产业拉动', '绿牌专业 ✓'],
            ['自动化类', '6,300+', '→ 稳定', '—'],
            ['电气类', '6,200+', '↑ 新能源拉动', '—'],
            ['管理类', '5,200-5,800', '→ 分化大', '~47% offer 率'],
            ['外语类', '5,000-5,500', '→ 稳但低', '86.9%'],
            ['艺术类', '4,800-5,300', '→ 分化极大', '80-81%'],
        ],
        col_widths=[100, 120, 140, 100],
    ))
    story.append(P('数据来源：麦可思研究院《2025 年中国本科生就业报告》；智联招聘/猎聘《2024 届高校毕业生就业数据报告》', note_style))

    story.append(PageBreak())

    # ═══════════════════════════════════════════════
    # 第五章：电子信息 vs 计算机
    # ═══════════════════════════════════════════════
    story.append(P('五、电子信息类 vs 计算机类 —— 关键对比', h2_style))
    story.append(Hr())

    story.append(P(
        '文谦同时喜欢计算机/AI 和电子信息/通信两个方向。以下是两个方向的系统对比：',
        body_style
    ))

    story.append(Spacer(1, 4*mm))
    story.append(make_table(
        ['对比维度', '计算机类', '电子信息类'],
        [
            ['2024 届就业率', '82.4%（低于平均）↓', '绿牌专业 ↑（麦可思连续三年）'],
            ['2023 届月收入', '6,771 元', '6,500+ 元'],
            ['开设高校数', '900+ 所（供给过剩）', '适中（供给受限于硬件投入）'],
            ['核心需求侧', '互联网（调整期）', '半导体/集成电路（国家战略）'],
            ['AI 替代风险', '中低端岗位有风险', '硬件层抗替代性强'],
            ['被裁撤风险', '极低（基础学科）', '极低（基础学科）'],
            ['五年后供给', '持续过剩', '供给受限于实验室投入，增速可控'],
            ['政策支持', '一般', '强（集成电路国家战略）'],
        ],
        col_widths=[130, 165, 165],
    ))
    story.append(Spacer(1, 4*mm))
    story.append(P(
        '结论：两个方向都是安全选择，不会被裁撤。但计算机类面临“腰部以下供给过剩”的结构性问题，'
        '电子信息类受益于半导体国产替代的国家战略，供需结构更健康。'
        '<b>如果进入的是 211 以上学校，两者皆优；'
        '如果学校层次一般，电子信息类的就业安全边际更高。</b>',
        body_style
    ))

    story.append(PageBreak())

    # ═══════════════════════════════════════════════
    # 第六章：文谦志愿建议
    # ═══════════════════════════════════════════════
    story.append(P('六、给文谦的志愿填报建议 —— 数据驱动版', h2_style))
    story.append(Hr())

    story.append(P('基本情况', h3_style))
    story.append(make_table(
        ['维度', '状态'],
        [
            ['省份', '江苏 — 竞争激烈，但省内高校资源全国第二'],
            ['选科', '物理 + 化学 + 生物（物理组）'],
            ['估分区间', '580 - 650（跨度大，等 6/24 精确分数）'],
            ['兴趣方向', '计算机/AI + 电子信息/通信（双方向）'],
            ['保底院校', '✓ 西交利物浦大学（综评已录取）'],
        ],
        col_widths=[140, 320],
    ))

    story.append(Spacer(1, 6*mm))
    story.append(P('各分数段志愿策略', h3_style))

    story.append(P('<b>第一层：640-650（省排名 ~3,000-6,000）→ 冲刺东南大学</b>', body_style))
    story.append(make_table(
        ['院校', '2025 江苏物理投档', '推荐专业', '说明'],
        [
            ['东南大学（985）', '644-657', 'CS / 电子信息 / 集成电路', '最优选！南京本地，通信 A-'],
            ['电子科技大学（985）', '~640', 'CS / 通信 / 微电子', '成都，通信 A+，华为直通车'],
            ['北京邮电大学（211）', '~640', 'CS / 通信 / 人工智能', '北京，通信 A+，运营商核心池'],
        ],
        col_widths=[110, 110, 120, 120],
    ))

    story.append(Spacer(1, 4*mm))
    story.append(P('<b>第二层：620-640（省排名 ~6,000-12,000）→ 西电 / 南航 / 南理</b>', body_style))
    story.append(make_table(
        ['院校', '2025 江苏物理投档', '推荐专业', '说明'],
        [
            ['西安电子科技大学（211）', '~630', 'CS / 通信 / 电子信息', '211 但通信 A+，华为核心招聘池'],
            ['南京航空航天大学（211）', '615-654', 'CS / 电子信息 / 自动化', '省内 211，工科强，留南京就业好'],
            ['南京理工大学（211）', '607-643', 'CS / 电子信息', '省内 211，军工背景'],
            ['苏州大学（211）', '600-630', 'CS / AI / 数据科学', '苏州产业环境好，实习方便'],
        ],
        col_widths=[130, 100, 120, 110],
    ))

    story.append(Spacer(1, 4*mm))
    story.append(P('<b>第三层：600-620（省排名 ~12,000-22,000）→ 稳苏大 / 河海</b>', body_style))
    story.append(make_table(
        ['院校', '2025 江苏物理投档', '推荐专业', '说明'],
        [
            ['南航/南理（中外合作）', '607-620', 'AI / 电子信息 / 自动化', '分数要求略低，有经济条件可选'],
            ['河海大学（211）', '~643（主体组）', 'CS / 通信', '211，水利强但 CS 也不错'],
            ['苏州大学（211）', '~600-624', 'CS / AI / 数据科学', '性价比之选：苏州有微软/华为/AI 公司'],
            ['中国矿业大学（211）', '~600', 'CS / 电子信息', '徐州，211 保底'],
        ],
        col_widths=[130, 100, 120, 110],
    ))

    story.append(Spacer(1, 4*mm))
    story.append(P('<b>第四层：580-600（省排名 ~22,000-40,000）→ 保 211 / XJTLU 已保底</b>', body_style))
    story.append(make_table(
        ['院校', '2025 江苏物理投档', '推荐专业', '说明'],
        [
            ['南京农业大学（211）', '606-612', 'CS / AI（信息工科组）', '211 新工科方向'],
            ['江南大学（211）', '~590', 'CS / 物联网', '无锡，211'],
            ['扬州大学', '~591', 'CS / 软件工程 / 电子信息', '双非但 CS 不弱'],
            ['西交利物浦大学', '✓ 已录取', 'CS / 信息与计算科学 / 数据科学', '保底！国际化路线，2+2 利物浦'],
        ],
        col_widths=[120, 100, 130, 110],
    ))

    story.append(Spacer(1, 10*mm))
    story.append(Hr())
    story.append(P('核心判断矩阵：各专业方向 × 数据维度', h3_style))
    story.append(make_table(
        ['专业方向', '就业率', '薪资', '被裁风险', '供给过剩度', '文谦适配'],
        [
            ['计算机科学与技术', '⚠ 中（82.4%）', '✅ 高（6,771）', '✅ 极低', '⚠ 高', '✅'],
            ['人工智能（新专业）', '✅ 高', '✅ 高', '⚠ 存疑（新专业）', '⚠ 中', '✅'],
            ['电子信息工程', '✅ 高（绿牌）', '✅ 中高（6,500+）', '✅ 极低', '✅ 低', '✅'],
            ['通信工程', '✅ 高', '✅ 中高', '✅ 极低', '✅ 低', '✅'],
            ['集成电路/微电子', '✅ 极高', '✅ 高', '✅ 极低', '✅ 极低', '❓ 未知'],
            ['大数据类（新专业）', '✅ 高', '✅ 中高', '⚠ 存疑', '⚠ 中', '❓ 未知'],
            ['管理类（信管等）', '⚠ 中', '❌ 中低', '❌ 极高', '❌ 高', '—'],
            ['外语类', '⚠ 中高', '❌ 中', '❌ 高', '❌ 高', '—'],
            ['艺术/设计类', '❌ 低', '❌ 低', '❌ 极高', '❌ 高', '—'],
        ],
        col_widths=[110, 70, 80, 65, 70, 70],
    ))

    story.append(Spacer(1, 8*mm))
    story.append(P('志愿填报五原则', h3_style))
    story.append(P('1. <b>位次 > 分数</b> — 每年题目难度不同，全省位次是唯一稳定的参考系', body_style))
    story.append(P('2. <b>专业优先于学校</b> — 同档次学校，谁给 CS/电子信息专业组就填谁', body_style))
    story.append(P('3. <b>省内优先（同档次）</b> — 南航/南理的 CS 在江苏就业不输外地 985', body_style))
    story.append(P('4. <b>通信只冲强校</b> — 北邮/西电/电子科大/东南的通信值得，其他优先选 CS', body_style))
    story.append(P('5. <b>保底已有，大胆冲</b> — XJTLU 综评已录取，常规批次不设保底志愿，全部往上冲', body_style))

    story.append(Spacer(1, 10*mm))
    story.append(Hr())
    story.append(P('数据来源', h3_style))
    story.append(P('· 教育部《普通高等学校本科专业备案和审批结果》（2020-2024 年度）', note_style))
    story.append(P('· 新华社 2026.6.14 报道（援引教育部数据）', note_style))
    story.append(P('· South China Morning Post (2026.6.14): China’s universities cut 12,000 obsolete degrees', note_style))
    story.append(P('· 麦可思研究院《2025 年中国本科生就业报告》（2024 届数据）', note_style))
    story.append(P('· 智联招聘/猎聘《2024 届高校毕业生就业数据报告》', note_style))
    story.append(P('· 经济观察报（2026.4）：当高校“砍”掉 5000 多个专业后', note_style))
    story.append(P('· 财新（2026）：中国高校五年撤销 5,345 个专业', note_style))
    story.append(P('· 高绩《中国高校本科专业新增与撤销分析报告（2020-2024）》', note_style))
    story.append(P('· 各高校官网：四川大学/中国传媒大学/吉林大学等撤销专业公示', note_style))
    story.append(P('· 江苏省教育考试院：2025 年本科批次平行志愿投档线', note_style))

    # ── 生成 ──
    doc.build(story)
    return output_path


if __name__ == '__main__':
    path = build_pdf()
    print(f'PDF 已生成: {path}')
