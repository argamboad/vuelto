# -*- coding: utf-8 -*-
"""Render the tutorial lessons (docs/tutorial/lessons/*.md) into one book PDF.

A reportlab renderer in the same family as gen_qa_guide.py, but a fuller
Markdown engine tuned for a technical book. 2026-09 redesign ("Contemporary
refined"): Constantia body + Segoe UI Semibold headings + Consolas code, lesson
openers with an eyebrow line, part dividers with subsystem figures, correct
running heads (drawn at page END so the breadcrumb never lags, reset between
multiBuild passes), orphan-proof headings (KeepTogether binding), a parts +
lessons table of contents, and pre-rendered Mermaid figures pulled by reference
from the canonical docs (ARCHITECTURE.md / FLOWS.md — see gen_diagrams.py).

Pure Python + reportlab + pygments — no pandoc / weasyprint / wkhtmltopdf.
Mermaid figures are rendered ONCE by gen_diagrams.py (needs node/mmdc) and the
PNGs committed under diagrams/, so this script stays pure-Python.

Usage:  python docs/tutorial/gen_tutorial_pdf.py
Output: docs/tutorial/PEREZOSOFT_COURSE.pdf
"""
import os, re, html, glob, hashlib, datetime
from reportlab.lib import colors, utils
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT, TA_CENTER
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph,
                                Spacer, Table, TableStyle, KeepTogether, PageBreak,
                                HRFlowable, ListFlowable, ListItem, Image,
                                NextPageTemplate)
from reportlab.platypus.tableofcontents import TableOfContents

import pygments
from pygments import lex
from pygments.lexers import (CSharpLexer, YamlLexer, SqlLexer, JsonLexer,
                             BashLexer, TextLexer, get_lexer_by_name)
from pygments.token import Token

HERE = os.path.dirname(os.path.abspath(__file__))
LESSON_DIR = os.path.join(HERE, "lessons")
DIAGRAM_DIR = os.path.join(HERE, "diagrams")
OUT = os.path.join(HERE, "PEREZOSOFT_COURSE.pdf")

# ---------------------------------------------------------------------------
# Fonts — embed system TTFs that cover the measured glyph set.
# Body: Constantia (serif, screen-tuned); heads: Segoe UI Semibold; code: Consolas.
# ---------------------------------------------------------------------------
WF = "C:/Windows/Fonts"
def _reg(name, fn):
    pdfmetrics.registerFont(TTFont(name, os.path.join(WF, fn)))

_reg("Body", "constan.ttf");     _reg("Body-Bold", "constanb.ttf")
_reg("Body-Italic", "constani.ttf"); _reg("Body-BoldItalic", "constanz.ttf")
_reg("Sans", "segoeui.ttf");     _reg("Sans-Bold", "segoeuib.ttf")
try:
    _reg("Head", "seguisb.ttf")            # Segoe UI Semibold
except Exception:
    pdfmetrics.registerFont(TTFont("Head", os.path.join(WF, "segoeuib.ttf")))
_reg("Mono", "consola.ttf"); _reg("Mono-Bold", "consolab.ttf")
_reg("Sym", "seguisym.ttf")            # Segoe UI Symbol — fallback for arrows/symbols
pdfmetrics.registerFontFamily("Body", normal="Body", bold="Body-Bold",
                              italic="Body-Italic", boldItalic="Body-BoldItalic")
pdfmetrics.registerFontFamily("Sans", normal="Sans", bold="Sans-Bold",
                              italic="Sans", boldItalic="Sans-Bold")
pdfmetrics.registerFontFamily("Mono", normal="Mono", bold="Mono-Bold",
                              italic="Mono", boldItalic="Mono-Bold")

INK   = colors.HexColor("#1a3b5d")   # brand dark blue (matches QA PDFs)
INK2  = colors.HexColor("#0f2740")
ACC   = colors.HexColor("#2563a8")
TEXT  = colors.HexColor("#20262c")
MUTE  = colors.HexColor("#5f6a75")
CODEBG = colors.HexColor("#f4f6f8")
CODEBORDER = colors.HexColor("#d5dde5")
CALLBG = colors.HexColor("#eef3f8")
CALLBAR = colors.HexColor("#2563a8")
ADRBG = colors.HexColor("#f3eee2")   # warm parchment for Architecture Decision
ADRBAR = colors.HexColor("#b8873b")

# usable body width (A4 minus 28 mm margins each side)
BODYW = A4[0] - 56*mm

# Glyphs we are unsure a chosen font covers -> normalize (rare: x2, x1).
NORMALIZE = [("►", ">"), ("❌", '<font color="#c0392b">x</font>')]

# Glyphs the body (Constantia) and/or code (Consolas) faces lack but Segoe UI
# Symbol has — measured against the lessons' actual character inventory. The
# old Segoe-body build silently DROPPED ⇒/⚠/⊃/🗑/🔍/✅; now they render.
SYM_FALLBACK = "→←↔⇒⚠⊃🗑🔍✅✓"

def sym_fallback(escaped):
    escaped = escaped.replace("️", "")   # variation selector: invisible, unmapped
    # non-BMP emoji can't reach the page through reportlab markup at all —
    # normalize the two the lessons use to their textual role
    escaped = escaped.replace("🗑 ", "").replace("🗑", "")   # decorative "DELETE-ME" prefix
    escaped = escaped.replace("🔍", "(?)")                   # parity-verdict "unverified"
    for ch in SYM_FALLBACK:
        if ch in escaped:
            escaped = escaped.replace(ch, '<font name="Sym">%s</font>' % ch)
    return escaped

styles = getSampleStyleSheet()
def P(name, **kw):
    kw.setdefault("fontName", "Body")
    return ParagraphStyle(name, parent=styles["Normal"], **kw)

H1E  = P("H1E", fontName="Head", fontSize=10, leading=13, textColor=ACC,
         spaceBefore=6, spaceAfter=2)                       # "LESSON 2.4" eyebrow
H1   = P("H1", fontName="Head", fontSize=23, leading=27, textColor=INK,
         spaceBefore=0, spaceAfter=10)
H2   = P("H2", fontName="Head", fontSize=13.5, leading=17, textColor=INK,
         spaceBefore=14, spaceAfter=5)
H3   = P("H3", fontName="Head", fontSize=11.5, leading=15, textColor=INK2,
         spaceBefore=10, spaceAfter=3)
H4   = P("H4", fontName="Body-Bold", fontSize=10.5, leading=14, textColor=INK2,
         spaceBefore=8, spaceAfter=2)
BODY = P("BODY", fontSize=10, leading=14.6, spaceAfter=7, alignment=TA_LEFT,
         textColor=TEXT)
INTRO = P("INTRO", fontName="Body-Italic", fontSize=10.5, leading=15.5,
          textColor=MUTE, spaceAfter=10)
LI   = P("LI", fontSize=10, leading=14.2, spaceAfter=3, alignment=TA_LEFT,
         textColor=TEXT)
CODE = P("CODE", fontName="Mono", fontSize=8.0, leading=11.0,
         textColor=colors.HexColor("#1d2b36"),
         leftIndent=12, firstLineIndent=-12)   # hanging indent for wrapped lines
CALL = P("CALL", fontSize=9.5, leading=13.8, textColor=colors.HexColor("#243b52"))
CALLLBL = P("CALLLBL", fontName="Head", fontSize=9, leading=12, textColor=CALLBAR, spaceAfter=2)
ADRLBL = P("ADRLBL", fontName="Head", fontSize=9, leading=12,
           textColor=colors.HexColor("#8a6420"), spaceAfter=3)
CAP  = P("CAP", fontName="Head", fontSize=8.2, leading=11, textColor=MUTE,
         alignment=TA_CENTER, spaceBefore=4, spaceAfter=8)
PARTN = P("PARTN", fontName="Head", fontSize=13, leading=16, textColor=ACC,
          alignment=TA_CENTER, spaceAfter=6)
PARTT = P("PARTT", fontName="Head", fontSize=27, leading=32, textColor=INK,
          alignment=TA_CENTER)
PARTS = P("PARTS", fontName="Body-Italic", fontSize=10.5, leading=15,
          textColor=MUTE, alignment=TA_CENTER)
COVERT = P("COVERT", fontName="Head", fontSize=30, leading=37, textColor=INK,
           alignment=TA_CENTER)
COVERS = P("COVERS", fontName="Body-Italic", fontSize=13, leading=19,
           textColor=MUTE, alignment=TA_CENTER)

# ---------------------------------------------------------------------------
# Pygments syntax highlighting -> reportlab inline markup.
# ---------------------------------------------------------------------------
TOKEN_COLOR = {
    Token.Keyword: "#0b5fa5", Token.Keyword.Type: "#0b5fa5",
    Token.Name.Class: "#1a7f6b", Token.Name.Namespace: "#1a7f6b",
    Token.Name.Function: "#7a3ea8", Token.Name.Decorator: "#7a3ea8",
    Token.Name.Attribute: "#7a3ea8",
    Token.String: "#a03030", Token.String.Doc: "#a03030",
    Token.Literal.String: "#a03030", Token.Number: "#8a5000",
    Token.Comment: "#6a7d6a", Token.Comment.Single: "#6a7d6a",
    Token.Comment.Multiline: "#6a7d6a",
    Token.Operator: "#333333", Token.Punctuation: "#333333",
}
def _tok_color(tt):
    while tt is not Token:
        if tt in TOKEN_COLOR:
            return TOKEN_COLOR[tt]
        tt = tt.parent
    return "#1d2b36"

LEXERS = {"csharp": CSharpLexer, "cs": CSharpLexer, "c#": CSharpLexer,
          "yaml": YamlLexer, "yml": YamlLexer, "sql": SqlLexer,
          "json": JsonLexer, "sh": BashLexer, "bash": BashLexer, "shell": BashLexer}
def _lexer(lang):
    lang = (lang or "").strip().lower()
    if lang in LEXERS:
        return LEXERS[lang]()
    try:
        return get_lexer_by_name(lang) if lang else TextLexer()
    except Exception:
        return TextLexer()

def code_line_markup(line, lexer):
    """Markup for ONE code line, preserving leading AND internal space runs."""
    m = re.match(r"[ \t]*", line)
    indent = m.group(0).replace("\t", "    ")
    rest = line[m.end():]
    seg = ["&nbsp;" * len(indent)]
    for tt, val in lex(rest + "\n", lexer):
        val = val.rstrip("\n")
        if not val:
            continue
        esc = html.escape(val)
        # internal alignment: runs of 2+ spaces must not collapse (single spaces
        # stay breakable so long lines can still wrap)
        esc = re.sub(r"  +", lambda mo: "&nbsp;" * len(mo.group(0)), esc)
        esc = sym_fallback(esc)
        seg.append('<font color="%s">%s</font>' % (_tok_color(tt), esc))
    return "".join(seg)

# ---------------------------------------------------------------------------
# Inline markdown: **bold** *italic* `code` [text](url)
# ---------------------------------------------------------------------------
def inline(text):
    text = text.strip()
    for src, repl in NORMALIZE:
        if repl.startswith("<"):   # already-markup replacement handled after escape
            continue
        text = text.replace(src, repl)
    parts = re.split(r'(`[^`]+`)', text)
    out = []
    for part in parts:
        if part.startswith("`") and part.endswith("`") and len(part) >= 2:
            out.append('<font name="Mono" size="8.7" color="#1d2b36">%s</font>'
                       % sym_fallback(html.escape(part[1:-1])))
            continue
        seg = html.escape(part)
        seg = seg.replace("❌", '<font color="#c0392b">x</font>')
        seg = sym_fallback(seg)
        seg = re.sub(r'\*\*([^*]+)\*\*', r'<b>\1</b>', seg)
        seg = re.sub(r'(?<!\*)\*([^*]+)\*(?!\*)', r'<i>\1</i>', seg)
        seg = re.sub(r'\[([^\]]+)\]\(([^)]+)\)',
                     r'<u><font color="#2563a8">\1</font></u>', seg)
        out.append(seg)
    return "".join(out)

# ---------------------------------------------------------------------------
# Callout box / code box / figure flowables
# ---------------------------------------------------------------------------
def callout(inner_flowables, bg, bar, label=None, label_style=None):
    body = []
    if label:
        body.append(Paragraph(label, label_style))
    body.extend(inner_flowables)
    inner = Table([[body]], colWidths=[BODYW])
    inner.setStyle(TableStyle([
        ("BACKGROUND", (0,0), (-1,-1), bg),
        ("LEFTPADDING", (0,0), (-1,-1), 10), ("RIGHTPADDING", (0,0), (-1,-1), 10),
        ("TOPPADDING", (0,0), (-1,-1), 8), ("BOTTOMPADDING", (0,0), (-1,-1), 8),
        ("LINEBEFORE", (0,0), (0,-1), 2.5, bar),
    ]))
    return inner

def code_box(source, lang):
    lexer = _lexer(lang)
    paras = [Paragraph(code_line_markup(ln, lexer) or "&nbsp;", CODE)
             for ln in source.split("\n")]
    t = Table([[paras]], colWidths=[BODYW])
    t.setStyle(TableStyle([
        ("BACKGROUND", (0,0), (-1,-1), CODEBG),
        ("BOX", (0,0), (-1,-1), 0.6, CODEBORDER),
        ("LEFTPADDING", (0,0), (-1,-1), 8), ("RIGHTPADDING", (0,0), (-1,-1), 8),
        ("TOPPADDING", (0,0), (-1,-1), 6), ("BOTTOMPADDING", (0,0), (-1,-1), 6),
    ]))
    return t

def figure(png_path, caption=None, max_h=170*mm):
    """A committed diagram PNG (rendered at 2x by gen_diagrams.py) + caption."""
    iw, ih = utils.ImageReader(png_path).getSize()
    w = min(BODYW, iw * 72.0 / 144.0)          # 2x PNG -> natural size at 144 dpi
    h = ih * w / iw
    if h > max_h:
        w, h = w * max_h / h, max_h
    parts = [Image(png_path, width=w, height=h)]
    if caption:
        parts.append(Paragraph(inline(caption), CAP))
    return KeepTogether(parts)

def mermaid_png(source):
    """Committed PNG for a mermaid block, addressed by content hash."""
    h = hashlib.sha1(source.strip().encode("utf-8")).hexdigest()[:12]
    return os.path.join(DIAGRAM_DIR, "mermaid-%s.png" % h)

# ---------------------------------------------------------------------------
# Markdown -> flowables (block parser tuned for these lessons)
# ---------------------------------------------------------------------------
_key = [0]
def next_key():
    _key[0] += 1
    return "h%d" % _key[0]

FIGREF = re.compile(r'^\s*<!--\s*figure:\s*(\S+)\s*(?:\|\s*(.*?))?\s*-->\s*$')

def parse_md(md, story, figures=None):
    """figures: dict dockey -> png path (for <!-- figure: key | caption -->)."""
    lines = md.split("\n")
    i, N = 0, len(lines)
    after_lesson_h1 = False
    while i < N:
        ln = lines[i]
        s = ln.rstrip()

        if not s.strip():
            i += 1; continue

        # figure reference (canonical diagram pulled from ARCHITECTURE.md/FLOWS.md)
        m = FIGREF.match(s)
        if m:
            key, cap = m.group(1), m.group(2)
            png = (figures or {}).get(key) or os.path.join(DIAGRAM_DIR, key + ".png")
            if os.path.exists(png):
                story.append(Spacer(1, 4))
                story.append(figure(png, cap))
            else:
                print("  !! missing figure:", key)
            i += 1; continue

        # fenced code (mermaid fences become figures via committed PNGs)
        m = re.match(r'^\s*```(\w+)?\s*$', s)
        if m:
            lang = m.group(1) or ""
            buf = []
            i += 1
            while i < N and not re.match(r'^\s*```\s*$', lines[i]):
                buf.append(lines[i]); i += 1
            i += 1
            src = "\n".join(buf)
            if lang.lower() == "mermaid":
                png = mermaid_png(src)
                if os.path.exists(png):
                    story.append(Spacer(1, 4))
                    story.append(figure(png))
                else:
                    print("  !! unrendered mermaid block (run gen_diagrams.py)")
                    story.append(code_box(src, ""))
            else:
                story.append(code_box(src, lang))
            story.append(Spacer(1, 5))
            continue

        # headings
        m = re.match(r'^(#{1,4})\s+(.*)$', s)
        if m:
            level = len(m.group(1)); txt = m.group(2).strip()
            key = next_key()
            lm = re.match(r'^Lesson\s+((?:\d+|A)\.\d+)\s*[—–:\-]\s*(.*)$', txt)
            if level == 1 and lm:
                # lesson opener: eyebrow + big title (TOC/bookmark keep full text)
                eyebrow = Paragraph('<a name="%s"/>LESSON %s' % (key, lm.group(1)), H1E)
                eyebrow._toc = (1, txt, key)
                story.append(eyebrow)
                story.append(Paragraph(inline(lm.group(2)), H1))
                after_lesson_h1 = True
            else:
                style = {1: H1, 2: H2, 3: H3, 4: H4}[level]
                p = Paragraph('<a name="%s"/>%s' % (key, inline(txt)), style)
                p._toc = (level, txt, key)   # picked up in afterFlowable
                story.append(p)
            i += 1; continue

        # horizontal rule
        if re.match(r'^\s*---+\s*$', s) or re.match(r'^\s*___+\s*$', s):
            story.append(Spacer(1, 3))
            story.append(HRFlowable(width="100%", thickness=0.5, color=CODEBORDER))
            story.append(Spacer(1, 5))
            i += 1; continue

        # blockquote — the one right after a lesson H1 is the intro (italic, no
        # box); "Architecture Decision" quotes get the parchment box; the rest
        # get the blue callout
        if s.lstrip().startswith(">"):
            buf = []
            while i < N and lines[i].lstrip().startswith(">"):
                buf.append(re.sub(r'^\s*>\s?', '', lines[i])); i += 1
            inner_md = "\n".join(buf)
            if after_lesson_h1:
                story.append(Paragraph(inline(" ".join(
                    l for l in inner_md.split("\n") if l.strip())), INTRO))
                after_lesson_h1 = False
                continue
            is_adr = bool(re.search(r'architecture decision|\bthe fork\b', inner_md, re.I))
            inner = []
            parse_md(inner_md, inner, figures)
            if is_adr:
                story.append(callout(inner, ADRBG, ADRBAR, "ARCHITECTURE DECISION", ADRLBL))
            else:
                story.append(callout(inner, CALLBG, CALLBAR, None, None))
            story.append(Spacer(1, 6))
            continue

        # table
        if s.lstrip().startswith("|") and i+1 < N and re.match(r'^\s*\|?[\s:|-]+\|', lines[i+1]):
            rows = []
            while i < N and lines[i].lstrip().startswith("|"):
                rows.append(lines[i]); i += 1
            story.append(md_table(rows))
            story.append(Spacer(1, 6))
            continue

        # lists — a wrapped item continues on indented lines that don't start a
        # new item or block; absorb them into the item's paragraph
        def _gather_items(item_re):
            nonlocal i
            items = []
            while i < N and re.match(item_re, lines[i].rstrip()):
                buf = [re.sub(item_re, '', lines[i].rstrip())]
                i += 1
                while i < N and lines[i].strip() and re.match(r'^\s{2,}', lines[i]) \
                        and not re.match(r'^\s*(#{1,4}\s|```|>|[-*]\s|\d+\.\s|\||<!--)',
                                         lines[i]):
                    buf.append(lines[i].strip()); i += 1
                items.append(" ".join(buf))
            return items

        # unordered list
        if re.match(r'^\s*[-*]\s+', s):
            items = [ListItem(Paragraph(inline(t), LI), leftIndent=14, value="•")
                     for t in _gather_items(r'^\s*[-*]\s+')]
            story.append(ListFlowable(items, bulletType="bullet", start="•",
                                      leftIndent=10, bulletColor=ACC))
            story.append(Spacer(1, 4))
            continue

        # ordered list
        if re.match(r'^\s*\d+\.\s+', s):
            items = [ListItem(Paragraph(inline(t), LI), leftIndent=16)
                     for t in _gather_items(r'^\s*\d+\.\s+')]
            story.append(ListFlowable(items, bulletType="1", leftIndent=12,
                                      bulletColor=INK, bulletFontName="Head"))
            story.append(Spacer(1, 4))
            continue

        # paragraph — gather until blank / block start
        buf = [s]
        i += 1
        while i < N and lines[i].strip() and not re.match(
                r'^\s*(#{1,4}\s|```|>|[-*]\s|\d+\.\s|---+\s*$|\||<!--)', lines[i]):
            buf.append(lines[i].rstrip()); i += 1
        story.append(Paragraph(inline(" ".join(buf)), BODY))

def md_table(rows):
    def cells(r):
        return [c.strip() for c in r.strip().strip("|").split("|")]
    head = cells(rows[0])
    body = [cells(r) for r in rows[2:]]
    ncol = len(head)
    CELL = P("CELL", fontName="Sans", fontSize=8.5, leading=11)
    CELH = P("CELH", fontName="Head", fontSize=8.5, leading=11, textColor=colors.white)
    data = [[Paragraph(inline(c), CELH) for c in head]]
    for r in body:
        r = (r + [""]*ncol)[:ncol]
        data.append([Paragraph(inline(c), CELL) for c in r])
    t = Table(data, colWidths=[BODYW/ncol]*ncol, repeatRows=1)
    t.setStyle(TableStyle([
        ("BACKGROUND", (0,0), (-1,0), INK),
        ("ROWBACKGROUNDS", (0,1), (-1,-1), [colors.white, colors.HexColor("#eef2f6")]),
        ("GRID", (0,0), (-1,-1), 0.4, CODEBORDER),
        ("VALIGN", (0,0), (-1,-1), "TOP"),
        ("LEFTPADDING", (0,0), (-1,-1), 5), ("RIGHTPADDING", (0,0), (-1,-1), 5),
        ("TOPPADDING", (0,0), (-1,-1), 4), ("BOTTOMPADDING", (0,0), (-1,-1), 4),
    ]))
    return t

# ---------------------------------------------------------------------------
# Orphan-proofing: bind every heading to the flowable that follows it so a
# title can never sit alone at the bottom of a page. The heading's _toc/_part
# tag is copied onto the KeepTogether (afterFlowable sees the wrapper when it
# fits; when it splits, reportlab re-queues the children and the original
# heading paragraph fires instead — either way, exactly once).
# ---------------------------------------------------------------------------
HEADING_STYLES = {H1E.name, H1.name, H2.name, H3.name, H4.name}

def bind_headings(story):
    out, i = [], 0
    while i < len(story):
        f = story[i]
        is_heading = isinstance(f, Paragraph) and f.style.name in HEADING_STYLES
        if is_heading:
            group = [f]
            j = i + 1
            # pull in everything up to and including the first substantial flowable
            while j < len(story):
                g = story[j]
                # a TableOfContents must stay a DIRECT story element — hidden
                # inside a KeepTogether, multiBuild's indexing scan misses it
                # and the TOC renders as an empty placeholder
                if isinstance(g, (PageBreak, NextPageTemplate, TableOfContents)):
                    break
                group.append(g); j += 1
                if not isinstance(g, (Spacer, HRFlowable)) and not (
                        isinstance(g, Paragraph) and g.style.name in HEADING_STYLES):
                    break
            if len(group) > 1:
                kt = KeepTogether(group)
                for tagged in group:
                    if hasattr(tagged, "_toc"):
                        kt._toc = tagged._toc; break
                out.append(kt); i = j
                continue
        # widow-proof the "Next: Lesson …" hand-off line: bind it to the
        # preceding flowable so it can't spill onto a page of its own
        if (isinstance(f, Paragraph) and f.style.name == BODY.name
                and f.getPlainText().startswith("Next:") and out
                and not isinstance(out[-1], (PageBreak, NextPageTemplate, TableOfContents))):
            group = [out.pop()]
            while out and isinstance(group[0], (Spacer, HRFlowable)) and not isinstance(
                    out[-1], (PageBreak, NextPageTemplate, TableOfContents)):
                group.insert(0, out.pop())
            group.append(f)
            kt = KeepTogether(group)
            for tagged in group:
                if hasattr(tagged, "_toc"):
                    kt._toc = tagged._toc; break
            out.append(kt); i += 1
            continue
        out.append(f); i += 1
    return out

# ---------------------------------------------------------------------------
# Document assembly: cover, TOC, lessons, page furniture, bookmarks.
# ---------------------------------------------------------------------------
PART_TITLES = {
    "0": "Part 0 — Orientation", "1": "Part 1 — The walking skeleton",
    "2": "Part 2 — Identity & tenancy", "3": "Part 3 — The slice pattern & the UI",
    "4": "Part 4 — Reliability & operations", "5": "Part 5 — Monetization",
    "6": "Part 6 — B2B essentials & security", "7": "Part 7 — Compliance & extensibility",
    "8": "Part 8 — Ship it", "9": "Part 9 — Make it yours", "A": "Appendix",
}
PART_SUBTITLES = {
    "0": "The mental model, the decision log, and a machine that behaves "
         "identically for every reader.",
    "1": "A deployable API with a real database, real tests, and CI — before "
         "any feature exists.",
    "2": "Custom JWT auth, passwordless sign-in, OAuth — then the tenancy "
         "walls that make cross-tenant leaks structurally impossible.",
    "3": "The vertical-slice pattern, the Blazor UI, localization, and the "
         "E2E harness that drives the whole app.",
    "4": "The transactional outbox, the inbox, the scheduler, observability, "
         "and an audit log that cannot be edited.",
    "5": "Entitlements, Stripe checkout and webhooks, quotas, dunning — "
         "billing that fails closed.",
    "6": "Roles and permissions, file storage, signed downloads, the SSRF "
         "seam, and MFA on every sign-in path.",
    "7": "GDPR export and erasure, notifications, the public API, outbound "
         "webhooks, and the staff back-office.",
    "8": "Single-origin hosting, containers, the deploy pipeline, and the "
         "row-level-security backstop.",
    "9": "Rebrand the platform, delete the sample, and start building what "
         "is yours.",
    "A": "The MAUI shells: one UI on four native platforms, and the auth "
         "bridge that survives process death.",
}
# Part-divider figures: canonical subsystem diagrams (rendered by gen_diagrams.py).
# Parts 0/1/6/9 carry none — Part 1's solution map already opens the front
# matter, and Parts 0/6/9 have no single subsystem diagram that fits.
PART_FIGURES = {
    "2": ("arch-auth-server", "What Part 2 builds — the server side of auth (ARCHITECTURE.md §4)"),
    "3": ("arch-onion", "What Part 3 makes explicit — the server onion and its seams (ARCHITECTURE.md §2)"),
    "4": ("arch-background", "What Part 4 builds — background processing (ARCHITECTURE.md §5)"),
    "5": ("arch-billing", "What Part 5 builds — the billing projection (ARCHITECTURE.md §6)"),
    "7": ("arch-notify-hooks", "What Part 7 builds — notifications & outbound webhooks (ARCHITECTURE.md §7)"),
    "8": ("arch-tenancy", "The tenancy walls you built in Part 2 — and the database backstop this part adds (ARCHITECTURE.md §3)"),
    "A": ("arch-client", "The shells' shared client architecture (ARCHITECTURE.md §9)"),
}

def lesson_files():
    files = []
    for f in glob.glob(os.path.join(LESSON_DIR, "*.md")):
        base = os.path.basename(f)
        m = re.match(r'^(\d+|A)\.(\d+)-', base)
        if m:
            major = 10 if m.group(1) == "A" else int(m.group(1))  # "A.x" appendix sorts after Part 9
            files.append((major, int(m.group(2)), f))
    files.sort()
    return files

class Book(BaseDocTemplate):
    def __init__(self, path, **kw):
        super().__init__(path, pagesize=A4,
                         leftMargin=28*mm, rightMargin=28*mm,
                         topMargin=22*mm, bottomMargin=20*mm, **kw)
        frame = Frame(self.leftMargin, self.bottomMargin,
                      self.width, self.height, id="main")
        # furniture is drawn at page END (onPageEnd) so the running head
        # reflects what is ON the page, never the previous one
        self.addPageTemplates([
            PageTemplate(id="cover", frames=[frame]),
            PageTemplate(id="divider", frames=[frame], onPageEnd=self._folio_only),
            PageTemplate(id="body", frames=[frame], onPageEnd=self._furniture),
        ])
        self._reset_breadcrumbs()

    def _reset_breadcrumbs(self):
        self.cur_part = ""      # e.g. "Part 2 — Identity & tenancy"  (header, left)
        self.cur_lesson = ""    # e.g. "2.6 · Tenancy I: the global query filter"  (header, right)
        self.in_parts = False   # False while in front matter (outline depth base)

    def handle_documentBegin(self):
        # multiBuild runs several passes over the same Book instance: without
        # this reset, pass 2+ would start with pass 1's FINAL breadcrumb
        # ("Appendix · A.2 …") on the contents/front-matter pages
        self._reset_breadcrumbs()
        super().handle_documentBegin()

    def _folio_only(self, canvas, doc):
        canvas.saveState()
        canvas.setFont("Body", 8.5)
        canvas.setFillColor(MUTE)
        canvas.drawCentredString(A4[0]/2, 12*mm, str(doc.page))
        canvas.restoreState()

    def _furniture(self, canvas, doc):
        canvas.saveState()
        # header breadcrumb — Part (left) · Lesson (right): "you are here" on every page
        canvas.setFillColor(INK)
        canvas.setFont("Head", 7.6)
        left = self.cur_part or "Build a Production SaaS Platform From Scratch"
        canvas.drawString(28*mm, A4[1]-14*mm, left[:64])
        if self.cur_lesson:
            canvas.setFillColor(MUTE)
            canvas.setFont("Body-Italic", 7.9)
            canvas.drawRightString(A4[0]-28*mm, A4[1]-14*mm, self.cur_lesson[:70])
        canvas.setStrokeColor(CODEBORDER)
        canvas.setLineWidth(0.5)
        canvas.line(28*mm, A4[1]-16*mm, A4[0]-28*mm, A4[1]-16*mm)
        # footer — page number, outer (right) edge
        canvas.setFont("Body", 8.5)
        canvas.setFillColor(MUTE)
        canvas.drawRightString(A4[0]-28*mm, 12*mm, str(doc.page))
        canvas.restoreState()

    def afterFlowable(self, flowable):
        # Part-divider pages carry a _part tag: TOC level 0, outline root,
        # set the left breadcrumb, clear the lesson.
        part = getattr(flowable, "_part", None)
        if part is not None:
            title, key = part
            self.cur_part = title
            self.cur_lesson = ""
            self.in_parts = True
            self.notify("TOCEntry", (0, title, self.page, key))
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(title, key, level=0, closed=True)
            return
        toc = getattr(flowable, "_toc", None)
        if toc:
            level, text, key = toc
            # outline depth: inside parts H1 sits under the part (1); in front
            # matter H1 is a root entry (0)
            base = 0 if self.in_parts else 1
            olevel = min(max(level - base, 0), 3)
            if level == 1:
                self.notify("TOCEntry", (1 if self.in_parts else 0, text, self.page, key))
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(text, key, level=olevel, closed=(level > 1))
            # Breadcrumb: a lesson H1 ("Lesson 2.6 — Title") sets Part + Lesson and holds
            # them across the whole lesson; section H2s never clobber the header.
            if level == 1:
                m = re.match(r'^Lesson\s+(\d+|A)\.(\d+)\s*[—–:\-]\s*(.*)$', text)
                if m:
                    self.cur_part = PART_TITLES.get(m.group(1), self.cur_part)
                    self.cur_lesson = f"{m.group(1)}.{m.group(2)} · {m.group(3)}"
                else:
                    self.cur_part = text   # front-matter section (Preface, etc.)
                    self.cur_lesson = ""

def build():
    doc = Book(OUT,
               title="Build a Production SaaS Platform From Scratch",
               author="Perezosoft Platform",
               subject="A rebuild-from-zero course in architecture, engineering "
                       "practice, and the decisions behind them",
               creator="gen_tutorial_pdf.py (reportlab)")
    story = []
    today = datetime.date.today().isoformat()

    # ---- cover ----
    story.append(Spacer(1, 52*mm))
    story.append(Paragraph("Build a Production<br/>SaaS Platform<br/>From Scratch", COVERT))
    story.append(Spacer(1, 6*mm))
    story.append(HRFlowable(width=34*mm, thickness=1.1, color=ACC, hAlign="CENTER"))
    story.append(Spacer(1, 6*mm))
    story.append(Paragraph("A rebuild-from-zero course in architecture, "
                           "engineering practice, and the decisions behind them", COVERS))
    story.append(Spacer(1, 40*mm))
    story.append(Paragraph("Perezosoft Platform &nbsp;·&nbsp; generated %s" % today,
                           P("cd", fontName="Sans", fontSize=9, textColor=MUTE,
                             alignment=TA_CENTER)))
    story.append(NextPageTemplate("body"))
    story.append(PageBreak())

    # ---- TOC (parts + lessons; the PDF outline carries the deeper levels) ----
    story.append(Paragraph("Contents", H1))
    toc = TableOfContents()
    toc.levelStyles = [
        P("toc0", fontName="Head", fontSize=11, leading=17, textColor=INK, spaceBefore=8),
        P("toc1", fontSize=9.5, leading=13.5, leftIndent=12, textColor=TEXT),
    ]
    story.append(toc)
    story.append(PageBreak())

    # ---- front matter (preface etc.) ----
    fm = os.path.join(HERE, "FRONTMATTER.md")
    if os.path.exists(fm):
        parse_md(open(fm, encoding="utf-8").read(), story)
        story.append(PageBreak())

    # ---- lessons, grouped by part ----
    files = lesson_files()
    cur_part = None
    for major, minor, path in files:
        part_key = "A" if major >= 10 else str(major)
        if part_key != cur_part:
            cur_part = part_key
            part_title = PART_TITLES.get(part_key, "Part " + part_key)
            # the previous lesson (or the front matter) just appended a PageBreak;
            # the template switch must be queued BEFORE it to take effect on the
            # divider page itself
            assert isinstance(story[-1], PageBreak)
            story.insert(len(story) - 1, NextPageTemplate("divider"))
            story.append(Spacer(1, 30*mm))
            m = re.match(r'^(Part \d+|Appendix)\s*—\s*(.*)$', part_title)
            if m:
                pn = Paragraph(m.group(1).upper(), PARTN)
                pn._part = (part_title, next_key())   # TOC/outline/breadcrumb hook
                story.append(pn)
                story.append(Paragraph(inline(m.group(2)), PARTT))
            else:
                pt = Paragraph(inline(part_title), PARTT)
                pt._part = (part_title, next_key())
                story.append(pt)
            story.append(Spacer(1, 7*mm))
            story.append(Paragraph(PART_SUBTITLES.get(part_key, ""), PARTS))
            fig = PART_FIGURES.get(part_key)
            if fig:
                png = os.path.join(DIAGRAM_DIR, fig[0] + ".png")
                if os.path.exists(png):
                    story.append(Spacer(1, 12*mm))
                    story.append(figure(png, fig[1], max_h=140*mm))
            story.append(NextPageTemplate("body"))
            story.append(PageBreak())
        md = open(path, encoding="utf-8").read()
        parse_md(md, story)
        story.append(PageBreak())

    doc.multiBuild(bind_headings(story))
    print("wrote", OUT)

if __name__ == "__main__":
    build()
