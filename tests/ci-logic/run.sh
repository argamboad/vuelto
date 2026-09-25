#!/usr/bin/env bash
# CI shell-logic harness (v4 audit T8): runs every verdict block and script in tests/ci-logic/targets against its
# fixtures with the real tools. Linux only (GNU bash/awk/grep/sed, python3, jq, as on the CI runners).
set -euo pipefail
exec python3 "$(dirname "$0")/run.py" "$@"
