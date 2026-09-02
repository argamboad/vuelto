#!/usr/bin/env bash
# QA sign-off sheet append-only guard (v3 audit S0-G5, T49 / R75).
#
# The §16 sign-off sheet in docs/QA_TEST_PLAN.md is the QA RUN LOG — executed records are history and
# must never be rewritten. A row counts as EXECUTED once its Tester or Date column is filled; such a
# row must survive verbatim in the new version. Blank template rows and the pre-seeded
# "Blocked (known defect)" annotations (result filled, but no tester/date) stay editable — filling a
# row in, or closing a known-defect marker, is the normal workflow this guard must not block.
#
# Usage: qa-runlog-append-only.sh <base-file> <head-file>
#   base-file — the QA plan as of the diff base (e.g. `git show $BASE_SHA:docs/QA_TEST_PLAN.md > base.md`)
#   head-file — the QA plan in the working tree
# Exits non-zero listing every executed base row missing from (or altered in) head.
set -euo pipefail

base_file="$1"
head_file="$2"

# The sheet spans from the §16 heading to the §17 heading.
extract_block() {
  sed -n '/^## 16\. Sign-off sheet/,/^## 17\./p' "$1"
}

base_block="$(extract_block "$base_file")"
if [ -z "$base_block" ]; then
  echo "No §16 sign-off sheet in the base version — nothing to guard."
  exit 0
fi
head_block="$(extract_block "$head_file")"

# Executed rows: markdown table rows whose Tester ($5) or Date ($7) cell is non-blank.
# Fields ('|' delimited): $1 "" | $2 Case | $3 Client | $4 Result | $5 Tester | $6 Build | $7 Date | $8 Notes.
executed_rows="$(printf '%s\n' "$base_block" | awk -F'|' '
  /^\|/ {
    line=$0                                                  # keep the raw line — touching $N rebuilds $0
    caseId=$2; gsub(/^[ \t]+|[ \t]+$/, "", caseId)
    if (caseId == "Case ID" || caseId ~ /^-+$/ || caseId == "…") next   # header / separator / ellipsis rows
    tester=$5; date=$7
    gsub(/[ \t]/, "", tester); gsub(/[ \t]/, "", date)
    if (tester != "" || date != "") print line
  }')"

if [ -z "$executed_rows" ]; then
  echo "No executed (tester/date-stamped) rows in the base sign-off sheet — nothing to guard."
  exit 0
fi

violations=0
while IFS= read -r row; do
  if ! printf '%s\n' "$head_block" | grep -Fxq -- "$row"; then
    echo "::error file=docs/QA_TEST_PLAN.md::Executed QA run-log row rewritten or deleted: $row"
    violations=$((violations + 1))
  fi
done <<< "$executed_rows"

if [ "$violations" -gt 0 ]; then
  echo ""
  echo "The §16 sign-off sheet is append-only for EXECUTED rows (v3 audit S0-G5): a recorded run"
  echo "(tester/date filled) is history — append a new row for a re-run instead of editing the old one."
  exit 1
fi

echo "QA run-log append-only guard: all executed base rows intact."
