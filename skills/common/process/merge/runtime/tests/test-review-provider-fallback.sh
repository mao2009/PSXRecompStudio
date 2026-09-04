#!/bin/sh
set -eu

# Regression checks for Issue #260. This is intentionally a policy-contract test:
# the Merge Skill is the SSOT for review-provider handling, and this test keeps
# the fail-closed fallback invariants from being weakened accidentally.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../../../../../.." && pwd)
SKILL="$ROOT/skills/common/process/merge/SKILL.md"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

contains() {
    grep -F "$1" "$SKILL" >/dev/null 2>&1 || fail "missing policy text: $1"
}

contains "Provider-unavailable fallback"
contains "rate-limited"
contains "service unavailable"
contains "current-HEAD CI"
contains "unresolved actionable"
contains "final PR HEAD"
contains "current main HEAD"
contains "Fallback reason"
contains "must not be used to ignore"
contains "normal CodeRabbit review requirements resume"

echo "PASS: review-provider fallback policy invariants present"
