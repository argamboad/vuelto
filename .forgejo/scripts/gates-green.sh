#!/usr/bin/env bash
# Is a commit green in its newest ci.yml run? Asked of this Forgejo's own Actions API, never taken on trust.
#
#   bash .forgejo/scripts/gates-green.sh <sha> [event]
#
# env: API (…/api/v1/repos/<owner>/<repo>), TOKEN (the job token), NATIVE_LEGS and E2E_SHARDS (ci.yml's own matrix
# sizes; ForgejoCiParityTests holds every caller's copy equal to them).
#
# Exit 0: the newest attempt of every gate passed, every matrix leg included and counted, and every native smoke
#         that ran passed — what ci.yml's deploy jobs require.
# Exit 1: not green; the last line is `missing: <jobs>`.
# Exit 2: cannot tell; the last line is `api: <why>` (no jq, or the API answered something other than 200).
# With [event] (e.g. pull_request) only that event's jobs count.
#
# Callers: deploy.yml's "already green" check (LOCALCI-4), and tested-on-pr.sh, which lets a merge's push run skip
# the gates its PR run already passed (Env L23). The same file lives in perezosoft-platform, y-el-vuelto and jigger-jot.
set -u
export SHA="$1" EVENT="${2:-}"
command -v jq > /dev/null || { echo "api: jq is missing from the runner image"; exit 2; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# Job-level history, newest first. Five pages is far more than a day of runs on this instance.
: > "$tmp/jobs.json"
for page in 1 2 3 4 5; do
  code=$(curl -s -o "$tmp/page.json" -w "%{http_code}" -H "Authorization: token $TOKEN" \
         "$API/actions/tasks?limit=50&page=$page")
  [ "$code" = "200" ] || { echo "api: the Actions API answered $code"; exit 2; }
  jq -c '.workflow_runs[]? | select(.head_sha == env.SHA and .workflow_id == "ci.yml")
         | select(env.EVENT == "" or .event == env.EVENT)' "$tmp/page.json" >> "$tmp/jobs.json"
  [ "$(jq '.workflow_runs | length' "$tmp/page.json")" -lt 50 ] && break
done

# Only the newest attempt of each job counts (v4 LB-DEP-3): a job that passed and was re-run red is red,
# and a re-run still going is not green yet. Any success found, however old, used to be enough.
jq -cs 'group_by(.name) | map(max_by(.id)) | .[]' "$tmp/jobs.json" > "$tmp/newest.json"
status() { jq -r --arg n "$1" 'select(.name == $n) | .status' "$tmp/newest.json"; }
missing=""
for job in changes build-test secret-scan qa-artifacts license-scan docker-build; do
  [ "$(status "$job")" = "success" ] || missing="$missing $job"
done
# Matrix jobs: EVERY leg must be green — one red leg refuses even when the green ones reach the
# count — and the count must reach ci.yml's own matrix size, so a half-run is not green either.
for matrix in "native-build:$NATIVE_LEGS" "e2e:$E2E_SHARDS"; do
  name="${matrix%%:*}"; want="${matrix##*:}"
  red=$(jq -r --arg p "$name (" 'select(.name | startswith($p)) | select(.status != "success") | "\(.name)=\(.status)"' "$tmp/newest.json" | paste -sd' ' -)
  ok=$(jq -r --arg p "$name (" 'select(.name | startswith($p)) | select(.status == "success") | .name' "$tmp/newest.json" | grep -c . || true)
  [ -z "$red" ] || missing="$missing $red"
  [ "$ok" -ge "$want" ] || missing="$missing $name($ok/$want)"
done
# A native smoke runs only when it was selected. One that ran must have passed, exactly as ci.yml's
# deploy jobs require; one that was skipped does not block.
red=$(jq -r 'select(.name | startswith("native-smoke")) | select(.status != "success" and .status != "skipped") | "\(.name)=\(.status)"' "$tmp/newest.json" | paste -sd' ' -)
[ -z "$red" ] || missing="$missing $red"

if [ -n "$missing" ]; then
  echo "missing:$missing"
  exit 1
fi
echo "Every gate is green for $SHA in its newest ci.yml run${EVENT:+ ($EVENT)}, and every native smoke that ran passed."
