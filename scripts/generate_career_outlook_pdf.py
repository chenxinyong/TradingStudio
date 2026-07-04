#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成《高考志愿的十年视角——产业趋势、城市选择与就业前景》PDF 报告
为 陈文谦 2026 年高考志愿填报提供长期视角
"""

import os
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm
from reportlab.lib.enums import TA_CENTER, TA_LEFT, TA_JUSTIFY
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle,
    PageBreak, HRFlowable
)
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

FONT_DIR = 'C:/Windows/Fonts'
pdfmetrics.registerFont(TTFont('SimHei', os.path.join(FONT_DIR, 'simhei.ttf')))
pdfmetrics.registerFont(TTFont('SimKai', os.path.join(FONT_DIR, 'simkai.ttf')))
pdfmetrics.registerFont(TTFont('STSONG', os.path.join(FONT_DIR, 'STSONG.TTF')))

# ── 样式 ──
body = ParagraphStyle('body', fontName='STSONG', fontSize=10, leading=18,
                      spaceAfter=6, alignment=TA_JUSTIFY)
h1 = ParagraphStyle('h1', fontName='SimHei', fontSize=20, leading=28,
                    spaceAfter=12, spaceBefore=12, alignment=TA_CENTER,
                    textColor=colors.HexColor('#1a1a2e'))
h2 = ParagraphStyle('h2', fontName='SimHei', fontSize=14, leading=22,
                    spaceAfter=8, spaceBefore=16, textColor=colors.HexColor('#16213e'))
h3 = ParagraphStyle('h3', fontName='SimHei', fontSize=12, leading=18,
                    spaceAfter=6, spaceBefore=12, textColor=colors.HexColor('#0f3460'))
note = ParagraphStyle('note', fontName='STSONG', fontSize=8, leading=12,
                      textColor=colors.HexColor('#888888'), spaceAfter=4)
em = ParagraphStyle('em', fontName='SimHei', fontSize=10, leading=18,
                    textColor=colors.HexColor('#c0392b'), spaceAfter=6)
ch = ParagraphStyle('ch', fontName='SimHei', fontSize=9, leading=14,
                    alignment=TA_CENTER, textColor=colors.white)
cc = ParagraphStyle('cc', fontName='STSONG', fontSize=9, leading=14,
                    alignment=TA_CENTER)
cl = ParagraphStyle('cl', fontName='STSONG', fontSize=9, leading=14,
                    alignment=TA_LEFT)

def P(text, style=body): return Paragraph(text, style)
def Hr(): return HRFlowable(width='100%', thickness=0.5, color=colors.HexColor('#cccccc'), spaceAfter=8, spaceBefore=4)

def T(headers, rows, col_widths=None):
    data = [[Paragraph(h, ch) for h in headers]]
    for row in rows:
        data.append([Paragraph(str(c), cc) for c in row])
    if col_widths is None:
        col_widths = [460 / len(headers)] * len(headers)
    t = Table(data, colWidths=col_widths, repeatRows=1)
    t.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, 0), colors.HexColor('#2c3e50')),
        ('TEXTCOLOR', (0, 0), (-1, 0), colors.white),
        ('GRID', (0, 0), (-1, -1), 0.4, colors.HexColor('#bdc3c7')),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, colors.HexColor('#f8f9fa')]),
        ('TOPPADDING', (0, 1), (-1, -1), 5),
        ('BOTTOMPADDING', (0, 1), (-1, -1), 5),
        ('VALIGN', (0, 0), (-1, -1), 'MIDDLE'),
    ]))
    return t

def TL(headers, rows, col_widths=None):
    """表格 - 左对齐版，用于说明性文本"""
    data = [[Paragraph(h, ch) for h in headers]]
    for row in rows:
        data.append([Paragraph(str(c), cl if i == len(row)-1 else cc) for i, c in enumerate(row)])
    if col_widths is None:
        col_widths = [460 / len(headers)] * len(headers)
    t = Table(data, colWidths=col_widths, repeatRows=1)
    t.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, 0), colors.HexColor('#2c3e50')),
        ('TEXTCOLOR', (0, 0), (-1, 0), colors.white),
        ('GRID', (0, 0), (-1, -1), 0.4, colors.HexColor('#bdc3c7')),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, colors.HexColor('#f8f9fa')]),
        ('TOPPADDING', (0, 1), (-1, -1), 5),
        ('BOTTOMPADDING', (0, 1), (-1, -1), 5),
        ('VALIGN', (0, 0), (-1, -1), 'MIDDLE'),
    ]))
    return t

def build():
    out = os.path.join(os.path.dirname(__file__), '..', 'docs', '高考志愿十年视角_产业趋势与就业前景.pdf')
    out = os.path.abspath(out)
    os.makedirs(os.path.dirname(out), exist_ok=True)

    doc = SimpleDocTemplate(out, pagesize=A4,
        leftMargin=20*mm, rightMargin=20*mm, topMargin=18*mm, bottomMargin=18*mm,
        title='高考志愿的十年视角——产业趋势、城市选择与就业前景',
        author='陈新勇')

    S = []

    # ═══ 封面 ═══
    S.append(Spacer(1, 25*mm))
    S.append(P('高考志愿的十年视角', h1))
    S.append(P('产业趋势 · 城市选择 · 就业前景', ParagraphStyle('sub', fontName='SimHei', fontSize=14, leading=20, alignment=TA_CENTER, textColor=colors.HexColor('#7f8c8d'), spaceAfter=20)))
    S.append(Hr())
    S.append(Spacer(1, 8*mm))
    S.append(P('站在 2026 年看 2036 年——为 陈文谦 高考志愿提供长期决策框架', ParagraphStyle('sub2', fontName='STSONG', fontSize=11, leading=16, alignment=TA_CENTER, textColor=colors.HexColor('#555555'))))
    S.append(Spacer(1, 6*mm))
    S.append(P('核心命题：选大学不仅是选学校，是选城市、选产业、选十年后的人生坐标', ParagraphStyle('sub2', fontName='SimHei', fontSize=10, leading=16, alignment=TA_CENTER, textColor=colors.HexColor('#8b0000'))))
    S.append(Spacer(1, 12*mm))
    S.append(P('考生信息：陈文谦 | 2026 江苏物理类 | 611 分 | 全省位次 25,594', ParagraphStyle('sub2', fontName='STSONG', fontSize=10, leading=14, alignment=TA_CENTER, textColor=colors.HexColor('#333333'))))
    S.append(P('生成日期：2026 年 6 月 27 日', ParagraphStyle('sub2', fontName='STSONG', fontSize=9, leading=12, alignment=TA_CENTER, textColor=colors.HexColor('#999999'))))
    S.append(PageBreak())

    # ═══ 第一章：专业大洗牌 ═══
    S.append(P('第一章  专业大洗牌：12200 个专业消失意味着什么', h2))
    S.append(Hr())

    S.append(P('据新华社 2026 年 6 月 14 日援引教育部数据，2021 至 2025 年间，中国高校撤销或暂停了 12,200 个本科专业布点，新增 10,200 个新专业布点，调整面覆盖全国超过 30% 的大学专业。这不是小修小补——这是一场以就业为导向的学科大换血。', body))
    S.append(Spacer(1, 4*mm))

    S.append(P('一、裁撤重灾区：管理 + 艺术 + 文学 = 40.66%', h3))
    S.append(T(
        ['学科门类', '占撤销总量', '2024 年典型裁撤专业（撤销校数）'],
        [
            ['工学（落后方向）', '32.84%', '网络工程（26）、部分传统工科'],
            ['管理学', '18.73%', '信息管理与信息系统（38）、市场营销（34）'],
            ['艺术学', '12.67%', '产品设计（24）、服装与服饰设计（108/十年）'],
            ['文学（含外语）', '9.26%', '英语（41）、日语（34）、翻译、广电'],
        ],
        col_widths=[100, 80, 280],
    ))

    S.append(Spacer(1, 4*mm))
    S.append(P('二、增长方向：全部集中在国家战略新兴产业', h3))
    S.append(T(
        ['排名', '新增专业', '2024 新增数', '产业方向'],
        [
            ['1', '人工智能', '91', 'AI 产业全链'],
            ['2', '数字经济', '76', '数字产业化'],
            ['5', '机器人工程', '32', '具身智能/智能制造'],
            ['7', '集成电路设计与集成系统', '31', '芯片国产替代（国家战略）'],
        ],
        col_widths=[45, 170, 80, 165],
    ))

    S.append(Spacer(1, 4*mm))
    S.append(P('三、就业率真相：CS 的 82.4% 意味着什么', h3))
    S.append(T(
        ['专业大类', '2024 届去向落实率', '月收入（2023 届）', '核心解读'],
        [
            ['全国本科平均', '86.7%', '—', '基准线'],
            ['计算机类', '82.4% ↓', '6,771 元', '低于平均 4.3pp，连续三年下降'],
            ['电子信息类', '绿牌专业 ↑', '6,500+ 元', '麦可思连续三年绿牌，半导体拉动'],
            ['管理类', '~47% offer率', '5,200-5,800', '供给严重过剩，裁撤重灾区'],
            ['外语类', '86.9%', '5,000-5,500', '就业率不低，但薪资天花板明显'],
            ['艺术类', '80-81%', '4,800-5,300', '院校间差异极大，纯艺术方向困难'],
        ],
        col_widths=[90, 110, 100, 160],
    ))
    S.append(Spacer(1, 2*mm))
    S.append(P('▲ 关键洞察：CS 就业率低不是因为行业不行，是因为供给爆炸（900+ 高校开设）。985/211 的 CS 依然抢手，塌陷的是腰部以下的学校。电子信息类受半导体国产替代拉动，供需结构更健康。', em))

    S.append(PageBreak())

    # ═══ 第二章：长三角产业地图 ═══
    S.append(P('第二章  长三角：2036 年中国科技产业的超级集群', h2))
    S.append(Hr())

    S.append(P('长三角（上海+江苏+浙江+安徽）是全国唯一同时拥有芯片制造、AI 研发、金融资本、高校人才四要素，并且物理上 1 小时高铁可达的超级产业带。这不是某一年的热点，而是未来 10-15 年不可逆的结构性趋势。', body))
    S.append(Spacer(1, 4*mm))

    S.append(P('一、三大城市群产业对比（2036 年展望）', h3))
    S.append(T(
        ['维度', '京津冀', '长三角', '粤港澳'],
        [
            ['AI 岗位占比（预估）', '~20%', '~40%', '~25%'],
            ['芯片岗位占比（预估）', '~15%', '~45%', '~20%'],
            ['制造业岗位占比', '~8%', '~40%', '~30%'],
            ['高校密度（双一流）', '35 所', '37 所', '8 所'],
            ['产业+高校协同度', '中（北京独大）', '高（多中心 1h 圈）', '中（深广双核）'],
            ['2036 综合竞争力', '稳定', '领跑', '分化'],
        ],
        col_widths=[120, 110, 110, 120],
    ))

    S.append(Spacer(1, 6*mm))
    S.append(P('二、长三角六大城市 × 核心产业 × 代表企业', h3))
    S.append(TL(
        ['城市', '核心产业方向', '代表企业/机构（2026）', '10 年后趋势'],
        [
            ['上海', '芯片制造 + 大模型 + 金融科技',
             '中芯国际、上海微电子、商汤、MiniMax、集成电路材料研究院',
             '14nm 以下量产，AI 基础层核心'],
            ['苏州', '封装测试 + AI+制造 + 生物医药',
             '通富微电、微软苏州、华为苏研所、信达生物',
             '全球封测之都 + 工业 AI 应用中心'],
            ['南京', '软件 + 通信 + EDA 工具',
             '华为南研所、台积电南京、中兴通讯、南瑞继保',
             '中国软件名城 + 芯片设计集群'],
            ['无锡', '功率半导体 + MEMS + 物联网',
             '华润微电子、SK 海力士、先导智能',
             '传感器+物联网国家基地'],
            ['合肥', '存储芯片 + AI+量子 + 显示面板',
             '长鑫存储、科大讯飞、本源量子、京东方',
             '从黑马到科技枢纽（上升最快）'],
            ['杭州', 'AI 应用 + 电商 + 金融科技',
             '阿里巴巴、海康威视、DeepSeek、宇树科技',
             'AI+消费互联网双轮驱动'],
        ],
        col_widths=[60, 120, 160, 120],
    ))

    S.append(Spacer(1, 6*mm))
    S.append(P('三、三大确定性的 10 年产业赛道', h3))

    S.append(P('<b>① 半导体全链（10-15 年周期）</b>——中国芯片自给率要从 20% 提到 40%+，这不是一个政策周期能完成的事。上海临港/苏州/无锡/合肥/南京，五个城市各占一个环节，形成全国唯一的完整闭环。对口专业：电子信息工程、通信工程、微电子、集成电路。', body))
    S.append(P('<b>② AI 产业化（5-10 年周期）</b>——从大模型到具身智能，从算法到落地。长三角拥有全国最密集的 AI 应用场景：上海的金融 AI、苏州的工业 AI、杭州的消费 AI、合肥的语音 AI。对口专业：计算机科学与技术、人工智能、数据科学。', body))
    S.append(P('<b>③ 低空经济/具身智能（10-20 年新赛道）</b>——飞行汽车、人形机器人、无人机物流。南京/苏州/合肥是核心研发基地。南航、南理、苏大近年新增的具身智能、智能感知工程等专业直接对接。需要 CS+EE 交叉人才——文谦的双兴趣刚好匹配。', body))

    S.append(PageBreak())

    # ═══ 第三章：城市选择的真实成本 ═══
    S.append(P('第三章  十年后，在哪座城市能真正生活', h2))
    S.append(Hr())

    S.append(P('一个 2026 年入学、2033 年硕士毕业的工程师，在 2036 年的各城市生活成本对比。所有数据为基于当前趋势的合理推估。', body))
    S.append(Spacer(1, 4*mm))

    S.append(T(
        ['城市', '2036 预测房价\n(元/m²)', 'CS/EE 5年经验\n月薪预测(元)', '月供/收入比', '生活可负担性'],
        [
            ['苏州', '30,000-40,000', '30,000-40,000', '~1:1', '★★★ 最优'],
            ['无锡', '20,000-30,000', '25,000-35,000', '~0.9:1', '★★★ 最优'],
            ['合肥', '25,000-35,000', '28,000-38,000', '~0.9:1', '★★★ 最优（上升）'],
            ['南京', '30,000-45,000', '28,000-38,000', '~1.1:1', '★★☆ 较好'],
            ['杭州', '45,000-65,000', '35,000-45,000', '~1.4:1', '★★☆ 有压力'],
            ['上海', '65,000-90,000', '40,000-55,000', '~1.5-2:1', '★☆☆ 困难'],
            ['深圳', '55,000-80,000', '38,000-50,000', '~1.5:1', '★☆☆ 困难'],
            ['北京', '60,000-85,000', '40,000-55,000', '~1.5-2:1', '★☆☆ 困难'],
        ],
        col_widths=[50, 90, 90, 70, 160],
    ))
    S.append(Spacer(1, 2*mm))
    S.append(P('▲ 同样做工程师，苏州/无锡/合肥是买得起房、养得起家的生活。上海深圳的绝对薪资更高，但扣除居住成本后的实际购买力远低于长三角二线。', em))

    S.append(Spacer(1, 6*mm))
    S.append(P('四、大学四年的隐形资产：实习机会的地理密度', h3))
    S.append(P('这不是学校排名的差距，是地理位置自带的机会密度。同一个专业的 211 学生，在苏州和在西安，四年积累的实习经历差 3-5 倍——不是学校的问题，是产业密度的问题。', body))
    S.append(Spacer(1, 3*mm))
    S.append(TL(
        ['阶段', '在南京/苏州上学', '在西部/东北城市上学'],
        [
            ['大一暑假', '苏州微软/华为参观，建立认知', '回家，缺乏产业接触'],
            ['大二暑假', '南京华为研究所实习（高铁 30min）', '回家，或自费到大城市租房实习'],
            ['大三暑假', '上海/合肥/杭州实习（高铁 1-2h）', '想去上海——租房、无校友网、从零开始'],
            ['大四', '长三角 3 段实习 + 行业人脉积累', '和留在长三角的同学比，差 3 段经历'],
            ['毕业求职', '同学遍布长三角科技公司', '投简历进长三角，是外来者'],
        ],
        col_widths=[70, 175, 215],
    ))

    S.append(PageBreak())

    # ═══ 第四章：文谦的具体路径 ═══
    S.append(P('第四章  文谦的十年路线图', h2))
    S.append(Hr())

    S.append(P('基本情况', h3))
    S.append(T(
        ['维度', '数据', '含义'],
        [
            ['高考', '611 分 / 全省 25,594 位', '超特控线 98 分，物理类前 7.6%'],
            ['2026 物理类扩招', '+10,736 人 (+5.75%)', '同位次录取机会优于 2025 年'],
            ['等效 2025 位次', '约 24,200 名', '扩招让门槛下移约 1,400 名'],
            ['兴趣方向', 'CS/AI + 电子信息/通信', '两方向均处国家战略增长赛道'],
            ['区域定位', '长三角', '全国科技产业密度最高的超级集群'],
        ],
        col_widths=[100, 160, 200],
    ))

    S.append(Spacer(1, 6*mm))
    S.append(P('2026 → 2036 节点路线', h3))
    S.append(TL(
        ['年份', '阶段', '关键动作', '城市'],
        [
            ['2026-2030', '本科', 'CS/通信主修 + 数理基础；大二起暑期实习', '南京/苏州'],
            ['2030-2033', '硕士（推荐）', 'AI/集成电路方向深造；进入导师项目组', '长三角 985/211'],
            ['2033-2036', '前 3 年职场', '入行积累；跟对项目/技术栈；建立行业信用', '苏州/南京/合肥'],
            ['2036+', '第 4-10 年', '技术骨干→架构师/技术管理；或创业', '长三角自由流动'],
        ],
        col_widths=[70, 60, 270, 60],
    ))

    S.append(Spacer(1, 8*mm))
    S.append(P('志愿推荐：三角匹配模型', h3))
    S.append(P('最优选择 = 学校 × 城市 × 产业的三角重合点。以下学校全部在长三角，CS/EE 均为其优势方向。', body))
    S.append(Spacer(1, 4*mm))

    S.append(TL(
        ['层级', '院校', '城市', '核心优势', '2025 参考位次', '文谦匹配度'],
        [
            ['冲刺', '苏州大学', '苏州', '211 + 苏州产业环境 + CS/软工 247 人', '~20,000', '偏高，扩招后更可期'],
            ['冲刺', '矿大 122106 组', '徐州', '211 + 工科底蕴', '~21,500', '冲，扩招利好'],
            ['稳', '南京邮电大学 07 组', '南京', '双一流 + 通信 A+ + 华为中兴核心池', '~26,300', '★ 最佳匹配'],
            ['稳', '南京邮电大学 05 组', '南京', 'CS 大类全覆盖', '~28,000', '★★ 很稳'],
            ['稳', '中国矿业大学 122104', '徐州', '211 + CS 可选', '~28,000', '★★ 稳'],
            ['稳', '南京农业大学', '南京', '211 + 信息工科组(CS/AI)', '~27,000', '★★ 稳'],
            ['保', '江南大学', '无锡', '211 + 物联网/AI + 无锡半导体圈', '~38,000', '安全边际充足'],
            ['保', '扬州大学', '扬州', '省属重点 + CS/软件稳定', '~50,000', '绝对保底'],
            ['保', '西交利物浦', '苏州', '国际化路线 + 2+2 利物浦', '~55,000', '兜底选项'],
        ],
        col_widths=[40, 100, 45, 150, 70, 65],
    ))

    S.append(PageBreak())

    # ═══ 第五章：五个核心判断 ═══
    S.append(P('第五章  给文谦和家长的核心判断', h2))
    S.append(Hr())

    judgements = [
        ('<b>判断一：长三角是唯一的答案</b>',
         '十年后回头看，2026 年选择在长三角读 CS/EE，等于 2006 年选择在北京读互联网，等于 1996 年选择在深圳学电子。'
         '这不是投机，是看清了中国科技产业的空间结构。长三角的半导体+AI 产业集群是全球级别的，不是几年的风口，是几十年的趋势。'),
        ('<b>判断二：CS 和 EE 都是安全选择，但逻辑不同</b>',
         'CS 就业率 82.4% 不是行业不行，是供给过剩。但 211 以上学校的 CS 就业仍然强劲（薪资领跑）。'
         'EE 受益于半导体国产替代（国家意志+市场需求双重驱动），供需结构更健康，抗 AI 替代性更强。'
         '文谦的双兴趣正好横跨这两个方向——在长三角，这两个方向不是二选一，是互补的。'),
        ('<b>判断三：学校和城市同等重要</b>',
         '同样学 CS，在苏州/南京上学四年积累的实习和行业人脉，远超在西部/东北上 985。'
         '这不是贬低西部学校——是产业密度决定了机会密度。大学毕业时，你简历上的实习经历和校友网络，往往比学校的牌子更重要。'),
        ('<b>判断四：看好南邮——不是最响亮的牌子，是最精准的匹配</b>',
         '南京邮电大学是通信领域的双一流，华为每年在南京招 200+ 人，南邮是核心目标院校之一。'
         '文谦 25,594 位次进南邮通信/CS 专业非常稳妥。南京的软件+通信产业生态，加上长三角的实习网络，'
         '四年后他的竞争力不输很多 985 毕业生。'),
        ('<b>判断五：十年后最重要的不是第一份工作的薪资</b>',
         '是 35 岁时，你在哪个城市、做什么方向、身边有什么样的人。'
         '选长三角的 211 CS/EE，十年后你大概率在一个产业上升的城市、做一个有积累的技术方向、'
         '周围是一群和你一样在这个产业里深耕了十年的人——这不是钱能买到的，是时间+地理位置+产业周期的复利。'),
    ]

    for title, content in judgements:
        S.append(P(title, h3))
        S.append(P(content, body))
        S.append(Spacer(1, 2*mm))

    S.append(Spacer(1, 10*mm))
    S.append(Hr())
    S.append(P('数据来源', h3))
    S.append(P('· 教育部《普通高等学校本科专业备案和审批结果》（2020-2024 年度）', note))
    S.append(P('· 新华社 2026.6.14 / 南华早报 2026.6.14 报道', note))
    S.append(P('· 麦可思研究院《2025 年中国本科生就业报告》', note))
    S.append(P('· 智联招聘/猎聘《2024 届高校毕业生就业数据报告》', note))
    S.append(P('· 江苏省教育考试院 2026 年招生计划与逐分段统计表', note))
    S.append(P('· 各高校 2026 年招生章程与预估线（新华日报·交汇点/扬子晚报/现代快报）', note))
    S.append(P('· 苏州大学/南京邮电大学/中国矿业大学/江南大学/南京农业大学 2026 招生计划', note))
    S.append(P('', note))
    S.append(P('免责声明：本报告中的产业趋势分析、薪资预测和房价推估基于当前政策方向和市场趋势的合理外推，', note))
    S.append(P('仅供参考。实际发展受政策调整、技术突破、宏观经济等多种因素影响，存在不确定性。', note))

    doc.build(S)
    return out

if __name__ == '__main__':
    path = build()
    print(f'PDF 已生成: {path}')
