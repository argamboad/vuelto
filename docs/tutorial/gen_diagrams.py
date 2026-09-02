# -*- coding: utf-8 -*-
"""Render the course figures: Mermaid -> PNG, committed under diagrams/.

Two sources, one canonical copy each:
  1. Named subsystem/flow diagrams pulled BY REFERENCE from the canonical docs
     (docs/ARCHITECTURE.md / docs/FLOWS.md) — the CANON table below maps a
     stable figure key to the doc section whose single ```mermaid block it
     renders. Lessons embed them with `<!-- figure: <key> | <caption> -->`
     (resolved by gen_tutorial_pdf.py), so a diagram fix in the canonical doc
     flows into the book on the next render — no second copy to drift.
  2. Lesson-local ```mermaid fences (course-specific sketches with no
     canonical-doc counterpart) — rendered to mermaid-<sha1[:12]>.png,
     addressed by content hash.

Requires node (mmdc via npx) — run it after changing any canonical diagram or
lesson mermaid block, then re-run gen_tutorial_pdf.py and commit the PNGs.
gen_tutorial_pdf.py itself stays pure-Python: it only embeds the committed PNGs
and warns about missing ones.

Usage:  python docs/tutorial/gen_diagrams.py
Output: docs/tutorial/diagrams/*.png  (2x scale, white background, neutral theme)
"""
import os, re, sys, json, hashlib, subprocess, tempfile, glob

HERE = os.path.dirname(os.path.abspath(__file__))
DOCS = os.path.dirname(HERE)
OUTDIR = os.path.join(HERE, "diagrams")
LESSON_DIR = os.path.join(HERE, "lessons")

# figure key -> (canonical doc, section heading regex); each section holds
# exactly ONE ```mermaid block (asserted below)
CANON = {
    "arch-solution-map":      ("ARCHITECTURE.md", r"^## 1\. Solution map"),
    "arch-onion":             ("ARCHITECTURE.md", r"^## 2\. The server onion"),
    "arch-tenancy":           ("ARCHITECTURE.md", r"^## 3\. Tenancy"),
    "arch-auth-server":       ("ARCHITECTURE.md", r"^## 4\. Auth"),
    "arch-background":        ("ARCHITECTURE.md", r"^## 5\. Background"),
    "arch-billing":           ("ARCHITECTURE.md", r"^## 6\. Billing"),
    "arch-notify-hooks":      ("ARCHITECTURE.md", r"^## 7\. Notifications"),
    "arch-client":            ("ARCHITECTURE.md", r"^## 9\. Client"),
    "flows-startup":          ("FLOWS.md", r"^## 1\. Startup"),
    "flows-request":          ("FLOWS.md", r"^## 2\. Authenticated"),
    "flows-otp":              ("FLOWS.md", r"^## 3\. Email OTP"),
    "flows-magic-link":       ("FLOWS.md", r"^## 4\. Magic-link"),
    "flows-oauth":            ("FLOWS.md", r"^## 5\. OAuth"),
    "flows-mfa":              ("FLOWS.md", r"^## 6\. MFA"),
    "flows-refresh":          ("FLOWS.md", r"^## 7\. Refresh-token"),
    "flows-billing-webhook":  ("FLOWS.md", r"^## 8\. Billing webhook"),
    "flows-announce":         ("FLOWS.md", r"^## 9\. Admin announce-all"),
    "flows-webhook-delivery": ("FLOWS.md", r"^## 10\. Outbound webhook"),
    "flows-dissolve":         ("FLOWS.md", r"^## 11\. Household dissolve"),
}

MMDC = ('npx -y -p @mermaid-js/mermaid-cli@11 mmdc '
        '--scale 2 --backgroundColor white --theme neutral')

def section_mermaid(doc_file, heading_re):
    txt = open(os.path.join(DOCS, doc_file), encoding="utf-8").read()
    m = re.search(heading_re, txt, re.M)
    if not m:
        sys.exit("!! heading not found in %s: %s" % (doc_file, heading_re))
    seg = txt[m.end():]
    nxt = re.search(r"^## ", seg, re.M)
    if nxt:
        seg = seg[:nxt.start()]
    blocks = re.findall(r"```mermaid\s*\n(.*?)```", seg, re.S)
    if len(blocks) != 1:
        sys.exit("!! expected exactly 1 mermaid block after %s in %s, found %d"
                 % (heading_re, doc_file, len(blocks)))
    return blocks[0].strip()

def lesson_mermaid_blocks():
    out = {}
    files = sorted(glob.glob(os.path.join(LESSON_DIR, "*.md")))
    files.append(os.path.join(HERE, "FRONTMATTER.md"))
    for f in files:
        if not os.path.exists(f):
            continue
        txt = open(f, encoding="utf-8").read()
        for b in re.findall(r"```mermaid\s*\n(.*?)```", txt, re.S):
            src = b.strip()
            h = hashlib.sha1(src.encode("utf-8")).hexdigest()[:12]
            out["mermaid-" + h] = src
    return out

def render(name, source, force=False):
    png = os.path.join(OUTDIR, name + ".png")
    stamp = os.path.join(OUTDIR, name + ".sha1")
    digest = hashlib.sha1(source.encode("utf-8")).hexdigest()
    if not force and os.path.exists(png) and os.path.exists(stamp) \
            and open(stamp).read().strip() == digest:
        print("  =", name, "(unchanged)")
        return
    with tempfile.NamedTemporaryFile("w", suffix=".mmd", delete=False,
                                     encoding="utf-8") as tf:
        tf.write(source)
        mmd = tf.name
    env = dict(os.environ)
    # puppeteer's default cache is ~/.cache/puppeteer, which collides with any
    # tool that drops a FILE named ~/.cache (spotipy does); use our own dir
    env.setdefault("PUPPETEER_CACHE_DIR",
                   os.path.join(os.environ.get("LOCALAPPDATA", HERE), "puppeteer-cache"))
    try:
        subprocess.run('%s -i "%s" -o "%s"' % (MMDC, mmd, png),
                       check=True, shell=True, env=env)
    finally:
        os.unlink(mmd)
    open(stamp, "w").write(digest)
    print("  +", name)

def main():
    os.makedirs(OUTDIR, exist_ok=True)
    force = "--force" in sys.argv
    print("canonical figures:")
    for key, (doc_file, heading_re) in CANON.items():
        render(key, section_mermaid(doc_file, heading_re), force)
    print("lesson-local mermaid blocks:")
    local = lesson_mermaid_blocks()
    for name, src in local.items():
        render(name, src, force)
    # prune stale hash-named renders whose source block no longer exists
    for png in glob.glob(os.path.join(OUTDIR, "mermaid-*.png")):
        base = os.path.splitext(os.path.basename(png))[0]
        if base not in local:
            os.unlink(png)
            st = png[:-4] + ".sha1"
            if os.path.exists(st):
                os.unlink(st)
            print("  -", base, "(pruned)")

if __name__ == "__main__":
    main()
