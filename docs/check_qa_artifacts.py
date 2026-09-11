#!/usr/bin/env python3
"""
v2 audit B11-8: CI gate keeping the QA PDFs in lock-step with QA_TEST_PLAN.md.

Both docs/QA_TEST_GUIDE.pdf and docs/QA_RUN_LOG.pdf are *generated* from QA_TEST_PLAN.md by the two
gen_qa_*.py scripts. This checker regenerates each and compares the EXTRACTED TEXT of the fresh output
against the committed PDF (normalizing out the generation date + whitespace, since PDFs aren't
byte-deterministic). A mismatch means the plan was edited but the PDFs weren't regenerated (or the
generator changed) — i.e. the printable artifacts have drifted from the source of truth.

This subsumes the "plan ⇄ PDFs change together" rule: editing QA_TEST_PLAN.md without re-running the
generators fails CI. It also proves the generators still run. Run from the repo's docs/ directory:
    python check_qa_artifacts.py

Requires: reportlab (to run the generators) + pypdf (to read text). Exits non-zero on drift.
"""
import re
import subprocess
import sys
from pathlib import Path

from pypdf import PdfReader

HERE = Path(__file__).resolve().parent
ARTIFACTS = [
    ("gen_qa_guide.py", "QA_TEST_GUIDE.pdf"),
    ("gen_qa_runlog.py", "QA_RUN_LOG.pdf"),
]
_DATE = re.compile(r"\d{4}-\d{2}-\d{2}")


def text_of(pdf: Path) -> str:
    """Normalized text: dates blanked, whitespace collapsed — stable across regenerations."""
    raw = "\n".join(page.extract_text() or "" for page in PdfReader(str(pdf)).pages)
    return re.sub(r"\s+", " ", _DATE.sub("<date>", raw)).strip()


def signoff_drift() -> list[str]:
    """
    Every case gets exactly one sign-off row, and every row an existing case. The sheet had silently
    stopped being filled in (58 rows against 184 cases, 2026-09-10) — a row that nobody adds is a case
    nobody runs. Rows live in two tables: §16 for everything, §14a for the QA-ADV-* set with its notes.
    """
    plan = (HERE / "QA_TEST_PLAN.md").read_text(encoding="utf-8")
    cases = set(re.findall(r"^### (QA-[A-Z0-9]+-\d+) — ", plan, re.M))
    rows = re.findall(r"^\| (QA-[A-Z0-9]+-\d+) \|", plan, re.M)
    problems = []
    if missing := sorted(cases - set(rows)):
        problems.append(f"cases with no sign-off row (add them to §16): {', '.join(missing)}")
    if orphan := sorted(set(rows) - cases):
        problems.append(f"sign-off rows for cases that no longer exist: {', '.join(orphan)}")
    if dupes := sorted({r for r in rows if rows.count(r) > 1}):
        problems.append(f"listed on both sheets — pick one: {', '.join(dupes)}")
    return problems


def main() -> int:
    drift = signoff_drift()
    for script, out in ARTIFACTS:
        committed = HERE / out
        if not committed.exists():
            drift.append(f"{out}: missing — run `python {script}` in docs/ and commit it.")
            continue

        before = text_of(committed)                              # the committed artifact's text
        subprocess.run([sys.executable, script], cwd=HERE, check=True)  # regenerate (overwrites in place)
        after = text_of(committed)                               # the freshly generated text

        if before != after:
            drift.append(
                f"{out}: out of date with QA_TEST_PLAN.md — regenerate with `python {script}` in docs/ and commit."
            )

    if drift:
        print("QA artifacts have drifted from QA_TEST_PLAN.md:\n  - " + "\n  - ".join(drift), file=sys.stderr)
        return 1

    print("QA PDFs and the sign-off sheet are in sync with QA_TEST_PLAN.md.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
