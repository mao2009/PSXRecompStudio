#!/bin/sh
# Test Suite: Main HEAD Refresh clears the recorded rebase base
#
# SKILL.md Step 2.3 contract: "Clear any recorded rebase base: the candidate
# is only proven rebased once the rebase completes against this refreshed
# main HEAD."
#
# The existing test suite verifies the CONSEQUENCE of this rule: the approval
# gate fails closed when RebasedOntoMainSha is absent (test-approval-ordering.sh
# Scenario 10). This suite covers the CAUSE: _merge_handle_main_head_refresh
# actively clears a pre-existing marker so a stale rebase base from a previous
# cycle cannot survive into the next one.
#
# Without this clearing, a regression path exists: a stale RebasedOntoMainSha
# left over from a previous rebase cycle would cause the approval gate to treat
# the candidate as proven-rebased against the old main HEAD, allowing a merge
# on a candidate that has not been rebased against current main.

PASS=0
FAIL=0
ok()     { PASS=$((PASS + 1)); }
bad()    { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }
assert() { _d="$1"; shift; if "$@" >/dev/null 2>&1; then ok; else bad "$_d"; fi; }
refute() { _d="$1"; shift; if "$@" >/dev/null 2>&1; then bad "$_d (expected failure)"; else ok; fi; }

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-mhr.$$")
trap 'rm -rf "$WORK"' EXIT
FAKEBIN="$WORK/bin"
mkdir -p "$FAKEBIN" "$WORK/wt"

# Fake gh so merge_gh_available returns 0 without a real GitHub connection.
printf '#!/bin/sh\nexit 0\n' > "$FAKEBIN/gh"
chmod +x "$FAKEBIN/gh"
PATH="$FAKEBIN:$PATH"
export PATH

# Minimal git repo so internal helpers that inspect the worktree do not fail.
git -C "$WORK/wt" init -q
git -C "$WORK/wt" config user.name test
git -C "$WORK/wt" config user.email test@example.com
touch "$WORK/wt/file"
git -C "$WORK/wt" add file
git -C "$WORK/wt" commit -qm initial

MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
# shellcheck disable=SC1091
. "$SCRIPT_DIR/../orchestrator.sh"

MERGE_PR_NUMBER=243
MERGE_ISSUE_NUMBER=242
MERGE_WORKTREE="$WORK/wt"
MERGE_BRANCH="issue/242-x"
MERGE_MAIN_DIR="$WORK/wt"
MERGE_REPOSITORY=""

M1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
M2=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
M3=cccccccccccccccccccccccccccccccccccccccc

echo "=== Main HEAD Refresh: rebase base clearing (SKILL.md Step 2.3) ==="
echo ""

# ------------------------------------------------------------------
# Scenario A: MAIN_HEAD_REFRESH clears a pre-existing RebasedOntoMainSha.
#
# Setup: a previous cycle left RebasedOntoMainSha = M1 (the rebase marker the
# completed rebase wrote). A re-rebase is triggered (e.g. main advanced at the
# approval gate). MAIN_HEAD_REFRESH MUST clear M1 before the new rebase runs;
# if it did not, the stale M1 marker would let the approval gate proceed
# without requiring an actual rebase against the new main HEAD.
# ------------------------------------------------------------------
echo "--- Scenario A: stale RebasedOntoMainSha cleared by MAIN_HEAD_REFRESH ---"
S="$WORK/sA.json"
merge_new_state "$MERGE_PR_NUMBER" "$MERGE_ISSUE_NUMBER" "$MERGE_WORKTREE" "$MERGE_BRANCH" > "$S"
merge_state_set_string "$S" \
    "State"              "MAIN_HEAD_REFRESH" \
    "RebasedOntoMainSha" "$M1"
MERGE_STATE_FILE="$S"

merge_gh_available()     { return 0; }
merge_get_main_head()    { printf '%s\n' "$M2"; }

_merge_handle_main_head_refresh >/dev/null 2>&1

assert "MAIN_HEAD_REFRESH advances to REBASE" \
    test "$(merge_state_get "$S" State)" = REBASE
assert "RebasedOntoMainSha cleared by MAIN_HEAD_REFRESH" \
    test -z "$(merge_state_get "$S" RebasedOntoMainSha)"
assert "new main HEAD M2 recorded in MainHeadSha" \
    test "$(merge_state_get "$S" MainHeadSha)" = "$M2"

# ------------------------------------------------------------------
# Scenario B: after MAIN_HEAD_REFRESH clears the marker, a successful rebase
# fills RebasedOntoMainSha with the new main HEAD (M2). This confirms that the
# clear in Scenario A is not accidentally re-populated by MAIN_HEAD_REFRESH
# itself, and that the rebase step is the sole writer of the marker.
# ------------------------------------------------------------------
echo ""
echo "--- Scenario B: successful REBASE fills RebasedOntoMainSha with new main HEAD ---"
merge_rebase_force_with_lease_enabled() { return 1; }
merge_rebase()            { printf '%s\n' success=true has_conflicts=false; }
merge_remote_head_state() { printf '%s\n' "relation=same"; }
merge_ff_worktree_to_remote() { return 0; }
merge_get_current_commit() {
    printf '%s\n' "$(git -C "$WORK/wt" rev-parse HEAD 2>/dev/null)"
}
merge_get_pr_info() {
    _oid=$(git -C "$WORK/wt" rev-parse HEAD 2>/dev/null)
    printf '{"number":243,"title":"t","headRefName":"issue/242-x","headRefOid":"%s","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"APPROVED","commits":[{"conclusion":"SUCCESS"}]}' \
        "$_oid"
}

# State continues from Scenario A: REBASE, RebasedOntoMainSha is empty, MainHeadSha = M2.
_merge_handle_rebase >/dev/null 2>&1

assert "REBASE advances to VALIDATING after clearing" \
    test "$(merge_state_get "$S" State)" = VALIDATING
assert "successful rebase records M2 as the new RebasedOntoMainSha" \
    test "$(merge_state_get "$S" RebasedOntoMainSha)" = "$M2"
refute "RebasedOntoMainSha is not the stale M1 value" \
    test "$(merge_state_get "$S" RebasedOntoMainSha)" = "$M1"

# ------------------------------------------------------------------
# Scenario C: A second MAIN_HEAD_REFRESH (another rebase cycle, e.g. triggered
# by a further main advance) clears the M2 marker that Scenario B recorded.
# Proves the clear is applied on EVERY entry, not only the first.
# ------------------------------------------------------------------
echo ""
echo "--- Scenario C: repeated MAIN_HEAD_REFRESH always clears the marker ---"
merge_state_set_string "$S" "State" "MAIN_HEAD_REFRESH"
merge_get_main_head() { printf '%s\n' "$M3"; }

_merge_handle_main_head_refresh >/dev/null 2>&1

assert "second MAIN_HEAD_REFRESH advances to REBASE" \
    test "$(merge_state_get "$S" State)" = REBASE
assert "second run clears the M2 marker left by the previous rebase" \
    test -z "$(merge_state_get "$S" RebasedOntoMainSha)"
assert "new main HEAD M3 recorded in MainHeadSha" \
    test "$(merge_state_get "$S" MainHeadSha)" = "$M3"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ]
