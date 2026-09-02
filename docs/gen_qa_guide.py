# -*- coding: utf-8 -*-
"""Render docs/QA_TEST_PLAN.md into a printable step-by-step QA guide PDF.

This is the *full walkthrough* companion to gen_qa_runlog.py (which emits the
one-row-per-case sign-off sheet). It renders the plan's headings, intro
blockquotes, Gherkin blocks, numbered walkthroughs and tables so a manual
tester can print it and follow along. Tuned to this document's structure, not
a general Markdown engine.
"""
import re, html, datetime
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph,
                                Spacer, Table, TableStyle, KeepTogether,
                                HRFlowable, ListFlowable, ListItem)
from reportlab.lib.enums import TA_LEFT

PLAN = "QA_TEST_PLAN.md"
OUT = "QA_TEST_GUIDE.pdf"

INK = colors.HexColor("#1a3b5d")        # brand dark blue (matches run-log)
PRI_EMOJI = {"\U0001f534": ("Smoke", "#c0392b"),
             "\U0001f7e0": ("Core",  "#d35400"),
             "\U0001f7e2": ("Edge",  "#27ae60")}

# The built-in Helvetica/Courier fonts only cover WinAnsi — every other glyph
# prints as a hollow box (emoji + U+FE0F even print as TWO boxes). Swap them
# for printable equivalents before rendering. Priority circles are handled
# separately in inline().
SYMBOLS = [
    ("\ufe0f", ""),   # U+FE0F variation selector riding on emoji
    ("⚙", ""),          # gear "automated" marker — its text is alongside
    ("→", "->"),        # rightwards arrow
    ("⇒", "=>"),        # rightwards double arrow
    ("↔", "<->"),       # left right arrow
    ("≥", ">="),        # greater-than or equal
    ("⚠", "(!)"),       # warning sign
    ("\U0001f50d", "[verify]"),  # magnifying glass (parity-audit flag)
    ("☐", "[ ]"),       # ballot box
]

def sanitize(text):
    for src, repl in SYMBOLS:
        text = text.replace(src, repl)
    return text

styles = getSampleStyleSheet()
H1   = ParagraphStyle("H1", parent=styles["Title"], fontSize=20, leading=24,
                      textColor=INK, spaceAfter=4)
H2   = ParagraphStyle("H2", parent=styles["Heading1"], fontSize=14, leading=18,
                      textColor=INK, spaceBefore=12, spaceAfter=4)
H3   = ParagraphStyle("H3", parent=styles["Heading2"], fontSize=11.5, leading=15,
                      textColor=colors.HexColor("#0f2740"), spaceBefore=9, spaceAfter=3)
BODY = ParagraphStyle("BODY", parent=styles["Normal"], fontSize=9.5, leading=13,
                      spaceAfter=4, alignment=TA_LEFT)
SUB  = ParagraphStyle("SUB", parent=BODY, fontSize=9, textColor=colors.HexColor("#555555"))
LBL  = ParagraphStyle("LBL", parent=BODY, fontName="Helvetica-Bold", fontSize=9.5,
                      textColor=INK, spaceBefore=3, spaceAfter=1)
CODE = ParagraphStyle("CODE", parent=styles["Code"], fontSize=8.3, leading=11,
                      textColor=colors.HexColor("#1d3b1f"), leftIndent=2, rightIndent=2)
QUOTE = ParagraphStyle("QUOTE", parent=BODY, fontSize=9, leading=12.5,
                       leftIndent=6, textColor=colors.HexColor("#444444"))
LI   = ParagraphStyle("LI", parent=BODY, spaceAfter=2)
CELL = ParagraphStyle("CELL", parent=styles["Normal"], fontSize=8, leading=10)
CELLH = ParagraphStyle("CELLH", parent=CELL, fontName="Helvetica-Bold",
                       textColor=colors.white)

today = datetime.date.today().isoformat()

# ---------- inline markdown (bold/code/links/emoji-priority) ----------
def inline(text):
    # Split out `code` spans first; escape every segment BEFORE injecting any
    # reportlab markup so html.escape() can't clobber the tags we add.
    out = []
    text = sanitize(text)
    text = re.sub(r' {2,}', ' ', text)   # gaps left by dropped markers
    text = re.sub(r' \)', ')', text)
    parts = re.split(r'(`[^`]+`)', text)
    for part in parts:
        if part.startswith("`") and part.endswith("`") and len(part) >= 2:
            out.append('<font face="Courier" color="#1d3b1f">%s</font>'
                       % html.escape(part[1:-1]))
            continue
        seg = html.escape(part)                       # escape raw text first
        for emo, (name, col) in PRI_EMOJI.items():    # emoji survive escaping
            seg = seg.replace(emo, '<font color="%s">• %s</font>' % (col, name))
        seg = re.sub(r'\*\*([^*]+)\*\*', r'<b>\1</b>', seg)          # bold
        seg = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', r'<u>\1</u>', seg)  # md links
        seg = re.sub(r'&lt;(https?://[^&]+)&gt;', r'<u>\1</u>', seg) # <url>
        out.append(seg)
    return "".join(out)

# ---------- parse the markdown into a flat block list ----------
lines = open(PLAN, encoding="utf-8").read().split("\n")
blocks = []   # (kind, payload)
i, N = 0, len(lines)

def md_table(rows):
    # rows: list of raw "| a | b |" lines (header, sep, body...)
    def cells(r):
        r = r.strip().strip("|")
        return [c.strip() for c in r.split("|")]
    head = cells(rows[0])
    body = []
    for r in rows[2:]:
        cs = cells(r)
        # A raw '|' inside a cell (e.g. `GET|POST` in a code span) splits into
        # extra cells and makes the Table wider than the page — glue the
        # overflow back onto the last column and pad short rows.
        if len(cs) > len(head):
            cs = cs[:len(head)-1] + ["|".join(cs[len(head)-1:])]
        cs += [""] * (len(head) - len(cs))
        body.append(cs)
    return head, body

while i < N:
    ln = lines[i]
    s = ln.rstrip()
    if not s.strip():
        i += 1; continue
    # fenced code (```lang ... ```)
    if s.lstrip().startswith("```"):
        lang = s.strip().strip("`").strip()
        buf = []
        i += 1
        while i < N and not lines[i].lstrip().startswith("```"):
            buf.append(lines[i]); i += 1
        i += 1  # closing fence
        blocks.append(("code", (lang, buf)))
        continue
    # headings
    m = re.match(r'^(#{1,3})\s+(.*)$', s)
    if m:
        lvl = len(m.group(1))
        blocks.append(("h%d" % lvl, m.group(2).strip()))
        i += 1; continue
    # horizontal rule
    if re.match(r'^---+$', s.strip()):
        blocks.append(("hr", None)); i += 1; continue
    # tables (line starts with | and next line is a --- separator row)
    if s.lstrip().startswith("|") and i + 1 < N and re.match(r'^\s*\|[-:\s|]+\|\s*$', lines[i+1]):
        tbl = [s]
        i += 1
        while i < N and lines[i].lstrip().startswith("|"):
            tbl.append(lines[i].rstrip()); i += 1
        blocks.append(("table", tbl)); continue
    # blockquote (possibly multi-line, may contain nested fences)
    if s.lstrip().startswith(">"):
        buf = []
        while i < N and lines[i].lstrip().startswith(">"):
            buf.append(re.sub(r'^\s*>\s?', '', lines[i].rstrip()))
            i += 1
        blocks.append(("quote", buf)); continue
    # list items (ordered / unordered) — indented follow-up lines belong to
    # the item; splitting them off restarts the numbering and orphans text.
    def item_continuation(idx, parts):
        while idx < N and lines[idx].strip() and lines[idx][:1] in (" ", "\t") \
                and not re.match(r'^\s*(#{1,3}\s|```|>|[-*]\s|\d+\.\s|\|)', lines[idx]):
            parts.append(lines[idx].strip()); idx += 1
        return idx
    m = re.match(r'^(\d+)\.\s+(.*)$', s.strip())
    if m:
        parts = [m.group(2).strip()]
        i = item_continuation(i + 1, parts)
        blocks.append(("ol", " ".join(parts))); continue
    m = re.match(r'^[-*]\s+(.*)$', s.strip())
    if m:
        parts = [m.group(1).strip()]
        i = item_continuation(i + 1, parts)
        blocks.append(("ul", " ".join(parts))); continue
    # plain paragraph (gather continuation lines)
    buf = [s.strip()]
    i += 1
    while i < N and lines[i].strip() and not re.match(
            r'^\s*(#{1,3}\s|```|>|[-*]\s|\d+\.\s|\|)', lines[i]) \
            and not re.match(r'^---+$', lines[i].strip()):
        buf.append(lines[i].strip()); i += 1
    blocks.append(("p", " ".join(buf)))

# ---------- render blocks -> flowables ----------
story = []
story.append(Paragraph("QA Test Guide — Step by Step", H1))
story.append(Paragraph(
    "Printable walkthrough generated from <u>docs/QA_TEST_PLAN.md</u>. Each case shows its "
    "Gherkin scenario and the plain-English steps. Record results on the companion "
    "<u>QA Run Log</u> sheet. Generated " + today + ".", SUB))
story.append(HRFlowable(width="100%", thickness=1, color=INK, spaceBefore=6, spaceAfter=8))

def code_block(lang, buf):
    buf = [sanitize(l) for l in (buf or [" "])]
    para = Paragraph("<br/>".join(html.escape(l) if l else "&nbsp;"
                                  for l in buf), CODE)
    bg = colors.HexColor("#eef4ec") if lang == "gherkin" else colors.HexColor("#f3f3f3")
    t = Table([[para]], colWidths=[doc_width])
    t.setStyle(TableStyle([
        ("BACKGROUND", (0,0), (-1,-1), bg),
        ("BOX", (0,0), (-1,-1), 0.4, colors.HexColor("#c9d6c5") if lang=="gherkin"
                                       else colors.HexColor("#d8d8d8")),
        ("LEFTPADDING", (0,0), (-1,-1), 6), ("RIGHTPADDING", (0,0), (-1,-1), 6),
        ("TOPPADDING", (0,0), (-1,-1), 5), ("BOTTOMPADDING", (0,0), (-1,-1), 5),
    ]))
    return t

doc_width = A4[0] - 28*mm  # set properly after doc is built; placeholder

def make_table(data, head, body, width):
    # Equal column widths starve text-heavy columns (§15's traceability matrix
    # ends up with page-tall rows that spill off the page). Weight each column
    # by its longest cell instead, with a floor so tiny columns stay legible,
    # and let oversized rows split across pages when the engine supports it.
    ncols = max(1, len(head))
    weights = []
    for ci in range(ncols):
        cells = [head[ci]] + [r[ci] for r in body if ci < len(r)]
        longest = max(8, min(max(len(c) for c in cells), 320))
        weights.append(longest ** 0.5)  # sqrt keeps short columns readable
    total = float(sum(weights))
    raw = [width * w / total for w in weights]
    floor = min(16*mm, width / ncols)
    fixed = sum(floor for w in raw if w < floor)
    flex = sum(w for w in raw if w >= floor) or 1.0
    scale = (width - fixed) / flex
    cw = [floor if w < floor else w * scale for w in raw]
    try:
        t = Table(data, colWidths=cw, repeatRows=1, splitInRow=1)
    except TypeError:  # older reportlab without splitInRow
        t = Table(data, colWidths=cw, repeatRows=1)
    t.setStyle(TableStyle([
        ("GRID", (0,0), (-1,-1), 0.3, colors.HexColor("#bbbbbb")),
        ("BACKGROUND", (0,0), (-1,0), INK),
        ("VALIGN", (0,0), (-1,-1), "TOP"),
        ("ROWBACKGROUNDS", (0,1), (-1,-1), [colors.white, colors.HexColor("#f6f8fa")]),
        ("TOPPADDING", (0,0), (-1,-1), 3), ("BOTTOMPADDING", (0,0), (-1,-1), 3),
        ("LEFTPADDING", (0,0), (-1,-1), 4), ("RIGHTPADDING", (0,0), (-1,-1), 4),
    ]))
    return t

# pending list grouping
def flush_list(items, ordered, out):
    if not items:
        return
    flow = ListFlowable(
        [ListItem(Paragraph(inline(t), LI), leftIndent=12) for t in items],
        bulletType="1" if ordered else "bullet",
        bulletFontSize=8.5, leftIndent=14, bulletColor=INK,
    )
    out.append(flow)
    items.clear()

def build(width):
    global doc_width
    doc_width = width
    out = list(story)
    ol_buf, ul_buf = [], []
    for kind, payload in blocks:
        if kind != "ol" and ol_buf: flush_list(ol_buf, True, out)
        if kind != "ul" and ul_buf: flush_list(ul_buf, False, out)
        if kind == "h1":
            continue  # the doc title; we already set our own
        elif kind == "h2":
            out.append(Spacer(1, 2*mm))
            out.append(Paragraph(inline(payload), H2))
            out.append(HRFlowable(width="100%", thickness=0.6,
                                  color=colors.HexColor("#c5d0db"), spaceAfter=4))
        elif kind == "h3":
            # case header — keep with following block
            out.append(Paragraph(inline(payload), H3))
        elif kind == "p":
            # bold-label paragraphs like **Gherkin** / **Walkthrough**
            if re.match(r'^\*\*[^*]+\*\*$', payload.strip()):
                out.append(Paragraph(inline(payload), LBL))
            else:
                out.append(Paragraph(inline(payload), BODY))
        elif kind == "code":
            lang, buf = payload
            out.append(code_block(lang, buf))
            out.append(Spacer(1, 2*mm))
        elif kind == "quote":
            qpara = Paragraph("<br/>".join(inline(l) if l.strip() else "&nbsp;"
                                           for l in payload), QUOTE)
            t = Table([[qpara]], colWidths=[width])
            t.setStyle(TableStyle([
                ("BACKGROUND", (0,0), (-1,-1), colors.HexColor("#fbf6e7")),
                ("LINEBEFORE", (0,0), (0,-1), 2.2, colors.HexColor("#e0a800")),
                ("LEFTPADDING", (0,0), (-1,-1), 8), ("RIGHTPADDING", (0,0), (-1,-1), 6),
                ("TOPPADDING", (0,0), (-1,-1), 5), ("BOTTOMPADDING", (0,0), (-1,-1), 5),
            ]))
            out.append(t)
            out.append(Spacer(1, 1.5*mm))
        elif kind == "hr":
            out.append(Spacer(1, 1*mm))
        elif kind == "table":
            head, body = md_table(payload)
            data = [[Paragraph(inline(c), CELLH) for c in head]]
            for row in body:
                data.append([Paragraph(inline(c), CELL) for c in row])
            t = make_table(data, head, body, width)
            out.append(t)
            out.append(Spacer(1, 2*mm))
        elif kind == "ol":
            ol_buf.append(payload)
        elif kind == "ul":
            ul_buf.append(payload)
    flush_list(ol_buf, True, out)
    flush_list(ul_buf, False, out)
    return out

# ---------- page furniture ----------
def header_footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7.5)
    canvas.setFillColor(colors.HexColor("#888888"))
    canvas.drawString(14*mm, 8*mm, "QA Test Guide — generated from QA_TEST_PLAN.md")
    canvas.drawRightString(A4[0]-14*mm, 8*mm, "Page %d" % canvas.getPageNumber())
    canvas.restoreState()

doc = BaseDocTemplate(OUT, pagesize=A4,
                      leftMargin=14*mm, rightMargin=14*mm,
                      topMargin=14*mm, bottomMargin=14*mm)
frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height, id="main")
doc.addPageTemplates([PageTemplate(id="main", frames=[frame], onPage=header_footer)])

doc.build(build(doc.width))
print("wrote", OUT)
