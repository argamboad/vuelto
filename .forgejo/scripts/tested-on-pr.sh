#!/usr/bin/env bash
# Did a merge's pull request already test exactly this code? (Env L23)
#
#   bash .forgejo/scripts/tested-on-pr.sh <sha>
#
# Yes (exit 0) only when <sha> is a two-parent merge whose tree is byte-identical to its second parent's — the PR
# head, so the PR was up to date with its base when it merged — AND that head's newest pull_request run of ci.yml
# passed every gate (gates-green.sh, same env). Otherwise no (exit 1) or cannot tell (exit 2); the last line says
# why. A merge of a PR that was behind its base is a tree nothing has tested yet, so it is a no.
#
# Callers: ci.yml's `changes` job (a yes skips the code gates on the push run) and deploy.yml's "already green"
# check (a yes deploys the merge). The same file lives in perezosoft-platform, y-el-vuelto and jigger-jot.
set -u
sha="$1"

read -r _ _ head extra <<< "$(git rev-list --parents -n 1 "$sha" 2>/dev/null)"
if [ -z "${head:-}" ] || [ -n "${extra:-}" ]; then
  echo "no: $sha is not a two-parent merge"
  exit 1
fi
if [ "$(git rev-parse "$sha^{tree}")" != "$(git rev-parse "$head^{tree}")" ]; then
  echo "no: $sha is not its PR head $head's tree — the base moved after the PR was tested"
  exit 1
fi

verdict=$(bash "$(dirname "$0")/gates-green.sh" "$head" pull_request)
rc=$?
printf '%s\n' "$verdict"
if [ "$rc" -eq 0 ]; then
  echo "yes: $sha is exactly its PR head $head, which passed every gate on its pull request"
  exit 0
fi
echo "no: its PR head $head is not green on its pull request ($(printf '%s\n' "$verdict" | tail -n 1))"
exit "$rc"
