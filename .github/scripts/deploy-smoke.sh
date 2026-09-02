#!/usr/bin/env bash
#
# Post-deploy smoke (DEPLOY-3). Waits for a specific commit to actually be LIVE at $BASE, then asserts the
# deployed surface. Shared by the staging and prod deploy jobs in ci.yml so the two can't drift — prod
# shipped with no smoke at all until v3 audit DEP-7, which is exactly what duplicated inline shell invites.
#
# Usage: deploy-smoke.sh <base-url> <expected-commit-sha>
# Exit:  0 = the expected build is live and every assertion passed; 1 = anything else.

set -uo pipefail

BASE="${1:-}"
EXPECT="${2:-}"

if [ -z "$BASE" ] || [ -z "$EXPECT" ]; then
  echo "::error::usage: deploy-smoke.sh <base-url> <expected-commit-sha>"
  exit 1
fi

# Wait for the NEW build to actually be live — the old instance keeps serving during the platform's build,
# so poll /api/version until it reports THIS commit AND readiness is 200. Without the version gate the
# smoke would happily pass against the previous build. (Free-tier build + cold boot can take minutes.)
echo "Waiting for $BASE to report commit $EXPECT …"
ready=0
for i in $(seq 1 50); do
  got=$(curl -s -m 20 "$BASE/api/version" | jq -r '.commit // empty' 2>/dev/null || echo "")
  code=$(curl -s -o /dev/null -w '%{http_code}' -m 20 "$BASE/health/ready" || echo 000)
  if [ "$got" = "$EXPECT" ] && [ "$code" = "200" ]; then
    ready=1
    echo "  new build live after ~$((i * 15))s"
    break
  fi
  echo "  [$i] version=${got:-none} ready=$code; retry in 15s"
  sleep 15
done
[ "$ready" = "1" ] || {
  echo "::error::the new build did not go live in time (version never matched $EXPECT)"
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
