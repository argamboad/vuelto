#!/usr/bin/env bash
#
# Post-deploy smoke (DEPLOY-3). Waits for a specific commit to actually be LIVE at $BASE, then asserts the
# deployed surface. Shared by the staging and prod deploy jobs in ci.yml so the two can't drift — prod
# shipped with no smoke at all until v3 audit DEP-7, which is exactly what duplicated inline shell invites.
#
# Usage: deploy-smoke.sh <base-url> <expected-commit-sha>
# Env:   GH_TOKEN + GITHUB_REPOSITORY (both present in Actions) let the script recognise a NEWER build that
#        superseded the expected one; without them it only accepts the exact commit (the old behaviour).
# Exit:  0 = the expected build (or a newer descendant of it) is live and every assertion passed; 1 = anything else.

set -uo pipefail

BASE="${1:-}"
EXPECT="${2:-}"

if [ -z "$BASE" ] || [ -z "$EXPECT" ]; then
  echo "::error::usage: deploy-smoke.sh <base-url> <expected-commit-sha>"
  exit 1
fi

# Is $1 a descendant of $EXPECT on this repository, i.e. a build that legitimately superseded ours? Answered
# by the compare API ("ahead" = $1 is ahead of $EXPECT, nothing missing). Any failure (no token, offline,
# unknown SHA) answers "no" so the gate stays as strict as before.
superseded_by() {
  [ -n "${GH_TOKEN:-}" ] && [ -n "${GITHUB_REPOSITORY:-}" ] || return 1
  local status
  status=$(gh api "repos/$GITHUB_REPOSITORY/compare/$EXPECT...$1" --jq .status 2>/dev/null) || return 1
  [ "$status" = "ahead" ]
}

# Wait for the NEW build to actually be live — the old instance keeps serving during the platform's build,
# so poll /api/version until it reports THIS commit AND readiness is 200. Without the version gate the
# smoke would happily pass against the previous build. (Free-tier build + cold boot can take minutes.)
#
# Back-to-back merges: the host builds the branch head, so when a second push lands before this one goes
# live, the version reported is the NEWER commit and ours never appears. That is not a failed deploy — the
# newer build carries our commit — so a live version that is a descendant of $EXPECT is accepted and smoked
# instead of timing out red. (The concurrency group in ci.yml cancels the OLDER in-flight job when the runs
# start in order; this covers the case where they don't.)
echo "Waiting for $BASE to report commit $EXPECT …"
ready=0
checked=""
for i in $(seq 1 50); do
  got=$(curl -s -m 20 "$BASE/api/version" | jq -r '.commit // empty' 2>/dev/null || echo "")
  code=$(curl -s -o /dev/null -w '%{http_code}' -m 20 "$BASE/health/ready" || echo 000)
  if [ "$got" = "$EXPECT" ] && [ "$code" = "200" ]; then
    ready=1
    echo "  new build live after ~$((i * 15))s"
    break
  fi
  if [ -n "$got" ] && [ "$got" != "$EXPECT" ] && [ "$got" != "$checked" ]; then
    checked="$got"
    if superseded_by "$got"; then
      if [ "$code" = "200" ]; then
        ready=1
        echo "::notice::$EXPECT was superseded by $got (a newer commit that contains it) before it went live — smoking the newer build instead."
        break
      fi
      checked="" # a descendant is live but not ready yet — check it again next round
    fi
  fi
  echo "  [$i] version=${got:-none} ready=$code; retry in 15s"
  sleep 15
done
[ "$ready" = "1" ] || {
  echo "::error::the new build did not go live in time (version never matched $EXPECT, nor a newer commit containing it)"
  exit 1
}

fail=0
assert() { # label expected url
  code=$(curl -s -o /dev/null -w '%{http_code}' -m 20 "$3")
  if [ "$code" = "$2" ]; then
    echo "PASS  $1  ($3 → $code)"
  else
    echo "::error::FAIL $1 ($3 → $code, want $2)"
    fail=1
  fi
}
assert liveness         200 "$BASE/health"
assert readiness        200 "$BASE/health/ready"
assert build-version    200 "$BASE/api/version"
assert spa-shell        200 "$BASE/"
assert spa-deeplink     200 "$BASE/settings"
assert api-not-shadowed 404 "$BASE/api/does-not-exist"
assert providers        200 "$BASE/api/auth/providers"
exit $fail
