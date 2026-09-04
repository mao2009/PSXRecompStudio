#!/bin/sh
set -eu

# Regression checks for Issue #260. The provider policy is a normative part of
# the Merge Skill and keeps fallback semantics deterministic and fail-closed.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../../../../../.." && pwd)
POLICY="$ROOT/skills/common/process/merge/REVIEW_PROVIDER_POLICY.md"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

contains() {
    grep -F "$1" "$POLICY" >/dev/null 2>&1 || fail "missing policy text: $1"
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
