#!/bin/sh
set -eu

# Regression checks for Issue #270 (amends ADR-010). The Review Provider Policy
# is the single source of truth for provider states and fallback eligibility.
# These checks lock the fail-closed decision logic: a provider failure state is
# never a review pass, and the fallback path is only ever unlocked by an
# independent current-HEAD review.
#
# Complements test-review-provider-fallback.sh, which guards the Issue #260
# invariants the fallback path was originally built on.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../../../../../.." && pwd)
POLICY="$ROOT/skills/common/process/merge/REVIEW_PROVIDER_POLICY.md"
MERGE_SKILL="$ROOT/skills/common/process/merge/SKILL.md"
SELF_REVIEW="$ROOT/skills/common/process/self-review/SKILL.md"
REVIEW_SKILL="$ROOT/skills/common/task/review/SKILL.md"
REPORTING="$ROOT/skills/common/process/reporting/SKILL.md"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

contains() {
    # contains <file> <literal text> <description>
    grep -F "$2" "$1" >/dev/null 2>&1 || fail "$3 (missing: $2)"
}

absent() {
    # absent <file> <literal text> <description>
    if grep -F "$2" "$1" >/dev/null 2>&1; then
        fail "$3 (unexpectedly present: $2)"
    fi
}

# Rows of the policy's decision table, header and separator excluded.
table() {
    awk '/^## Decision table/ { f = 1; next }
         /^## / { f = 0 }
         f && /^\| *`CODERABBIT_/ { print }' "$POLICY"
}

# --- 1. Provider state vocabulary is defined and parseable -------------------

for state in CODERABBIT_REVIEWED CODERABBIT_RATE_LIMITED CODERABBIT_UNAVAILABLE \
             CODERABBIT_SKIPPED CODERABBIT_PENDING CODERABBIT_UNKNOWN; do
    contains "$POLICY" "$state" "provider state vocabulary incomplete"
done

for state in FALLBACK_REVIEW_PASS FALLBACK_REVIEW_FAIL FALLBACK_REVIEW_ABSENT; do
    contains "$POLICY" "$state" "fallback review evidence vocabulary incomplete"
done

contains "$POLICY" "Provider state vocabulary" "state vocabulary section missing"
contains "$POLICY" "positively established" \
    "a provider state must require positive evidence"
contains "$POLICY" "never a review pass" \
    "provider failure states must be stated not to be a review pass"

# --- 2. Single source of truth, referenced by adapters -----------------------

contains "$POLICY" "single source" "policy must declare itself the SSOT"
contains "$POLICY" "Ownership and adapters" "adapter ownership section missing"
contains "$MERGE_SKILL" "REVIEW_PROVIDER_POLICY.md" \
    "Merge Skill must reference the provider policy"
absent "$MERGE_SKILL" "do not block Merge Skill validation" \
    "Merge Skill must not restate a looser provider rule than the policy"

# --- 3. Independent current-HEAD review is mandatory on the fallback path ----

contains "$POLICY" "Independent current-HEAD review" \
    "independent review section missing"
contains "$POLICY" "separate from the one that authored the change" \
    "independent review must require a separate reviewing context"
contains "$POLICY" "self review" \
    "policy must address why a self review is not independent evidence"
contains "$POLICY" "exact PR HEAD SHA" \
    "independent review must bind to the merge candidate SHA"
contains "$SELF_REVIEW" "not independent review evidence" \
    "Self-Review Skill must disclaim being independent review evidence"
contains "$REVIEW_SKILL" "Independent review as merge evidence" \
    "Review Skill must document the independent-review application"
contains "$REVIEW_SKILL" "FALLBACK_REVIEW_PASS" \
    "Review Skill must name the state its independent review produces"

# --- 4. Fallback preconditions are all still mandatory ----------------------

contains "$POLICY" "current-HEAD CI is green" "fallback must require green CI"
contains "$POLICY" "no unresolved actionable findings" \
    "fallback must require zero unresolved actionable findings"
contains "$POLICY" "SHA-bound to the final PR HEAD" \
    "fallback must require SHA-bound human approval"
contains "$POLICY" "final HEAD revalidation" \
    "fallback must require final HEAD revalidation"
contains "$POLICY" "FALLBACK_REVIEW_PASS" \
    "fallback must require an independent review pass"
contains "$POLICY" "changes **nothing else**" \
    "fallback must state that it relaxes no other merge gate"

# --- 5. Decision table invariants -------------------------------------------

rows=$(table)
[ -n "$rows" ] || fail "decision table rows could not be parsed"

row_count=$(printf '%s\n' "$rows" | grep -c '^|')
[ "$row_count" -ge 8 ] || fail "decision table has too few rows ($row_count)"

# Only a completed review yields the normal path.
normal=$(printf '%s\n' "$rows" | grep -F 'normal review path' || true)
[ -n "$normal" ] || fail "no normal review path row found"
normal_count=$(printf '%s\n' "$normal" | grep -c '^|')
[ "$normal_count" -eq 1 ] || \
    fail "expected exactly one normal review path row, found $normal_count"
printf '%s\n' "$normal" | grep -F 'CODERABBIT_REVIEWED' >/dev/null || \
    fail "the normal review path row is not bound to CODERABBIT_REVIEWED"

# Every fallback-allowed row requires an independent review pass.
allowed=$(printf '%s\n' "$rows" | grep -F 'fallback allowed' || true)
[ -n "$allowed" ] || fail "no fallback allowed row found"
allowed_count=$(printf '%s\n' "$allowed" | grep -c '^|')
pass_count=$(printf '%s\n' "$allowed" | grep -cF 'FALLBACK_REVIEW_PASS' || true)
[ "$allowed_count" -eq "$pass_count" ] || \
    fail "a fallback allowed row does not require FALLBACK_REVIEW_PASS"

# A failed or absent independent review can never unlock the fallback.
if printf '%s\n' "$allowed" | grep -E 'FALLBACK_REVIEW_(FAIL|ABSENT)' >/dev/null; then
    fail "a fallback allowed row accepts a failed or absent independent review"
fi

# A completed review is never routed through the fallback.
if printf '%s\n' "$allowed" | grep -F 'CODERABBIT_REVIEWED' >/dev/null; then
    fail "CODERABBIT_REVIEWED must not appear on a fallback allowed row"
fi

# Each provider failure state that Issue #270 names is fallback-eligible.
for state in CODERABBIT_RATE_LIMITED CODERABBIT_UNAVAILABLE CODERABBIT_SKIPPED; do
    printf '%s\n' "$allowed" | grep -F "$state" >/dev/null || \
        fail "$state has no fallback allowed row"
done

# Non-established states stay fail-closed.
for state in CODERABBIT_PENDING CODERABBIT_UNKNOWN; do
    state_rows=$(printf '%s\n' "$rows" | grep -F "$state" || true)
    [ -n "$state_rows" ] || fail "$state has no decision table row"
    printf '%s\n' "$state_rows" | grep -F 'blocked' >/dev/null || \
        fail "$state is not blocked"
    if printf '%s\n' "$state_rows" | grep -F 'fallback allowed' >/dev/null; then
        fail "$state must never be fallback-eligible"
    fi
done

# Unresolved actionable findings block regardless of provider state.
contains "$POLICY" "must not be used to ignore" \
    "the fallback must not be usable to waive an actual finding"

# --- 6. Reporting distinguishes provider failure from a review verdict ------

contains "$REPORTING" "review-provider failure" \
    "Reporting Skill must separate provider failure from a review result"

# --- 7. Existing merge safety guarantees are not weakened -------------------

for guard in 'gh pr merge --admin' \
             'Direct push' \
             'git push --force' \
             'Never skip rebase' \
             'No valid approval = No merge.' \
             'Final HEAD Revalidation'; do
    contains "$MERGE_SKILL" "$guard" "merge safety guarantee was weakened"
done

echo "PASS: review-provider state vocabulary and fallback decision logic"
