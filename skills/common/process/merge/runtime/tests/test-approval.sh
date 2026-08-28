#!/bin/sh
# Test Suite: Merge Approval
# Verifies behavioral parity with PowerShell MergeApproval.psm1

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_true() {
    _desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        _pass
    else
        _fail "$_desc"
    fi
}

assert_false() {
    _desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        _fail "$_desc (expected false, got true)"
    else
        _pass
    fi
}

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/../approval.sh"

echo "=== Merge Approval Tests ==="
echo ""

# --- Valid approval (matching commit + main head) ---
echo "--- Valid Approval ---"
assert_true "Valid approval with matching commit and main head" \
    merge_approval_is_valid true "abc123" "def456" "abc123" "def456"

# --- Invalid: commit SHA mismatch ---
echo ""
echo "--- Commit SHA Mismatch ---"
assert_false "Approval invalid on commit SHA mismatch" \
    merge_approval_is_valid true "abc123" "def456" "xyz789" "def456"

reasons=$(merge_approval_validation_reasons true "abc123" "def456" "xyz789" "def456")
case "$reasons" in
    *"Commit SHA mismatch"*) _pass ;;
    *) _fail "Commit SHA mismatch reason not reported";;
esac

# --- Invalid: main HEAD changed ---
echo ""
echo "--- Main HEAD Change ---"
assert_false "Approval invalid on main HEAD change" \
    merge_approval_is_valid true "abc123" "def456" "abc123" "ghi012"

reasons=$(merge_approval_validation_reasons true "abc123" "def456" "abc123" "ghi012")
case "$reasons" in
    *"Main HEAD has changed"*) _pass ;;
    *) _fail "Main HEAD change reason not reported";;
esac

# --- Invalid: invalidated flag ---
echo ""
echo "--- Invalidated Approval ---"
assert_false "Invalidated approval is always invalid" \
    merge_approval_is_valid false "abc123" "def456" "abc123" "def456"

reasons=$(merge_approval_validation_reasons false "abc123" "def456" "abc123" "def456")
case "$reasons" in
    *"Approval has been invalidated"*) _pass ;;
    *) _fail "Invalidation reason not reported";;
esac

# --- Invalid: rebase changed commit (both mismatches) ---
echo ""
echo "--- Rebase / Main HEAD combined ---"
assert_false "Approval invalid after rebase changes commit" \
    merge_approval_is_valid true "abc123" "def456" "new_commit" "def456"
assert_false "Approval invalid when main HEAD changes" \
    merge_approval_is_valid true "abc123" "def456" "abc123" "new_main_head"

# --- Invalid: missing commit SHA fails closed ---
echo ""
echo "--- Missing Commit SHA Fail-Closed ---"
assert_false "Approval invalid when approved commit SHA is missing" \
    merge_approval_is_valid true "" "def456" "abc123" "def456"
assert_false "Approval invalid when current commit SHA is missing" \
    merge_approval_is_valid true "abc123" "def456" "" "def456"
assert_false "Approval invalid when both commit SHAs are missing" \
    merge_approval_is_valid true "" "def456" "" "def456"

reasons=$(merge_approval_validation_reasons true "" "def456" "abc123" "def456")
case "$reasons" in
    *"Approved commit SHA is missing"*) _pass ;;
    *) _fail "Missing approved commit SHA reason not reported";;
esac
reasons=$(merge_approval_validation_reasons true "abc123" "def456" "" "def456")
case "$reasons" in
    *"Current commit SHA is missing"*) _pass ;;
    *) _fail "Missing current commit SHA reason not reported";;
esac

# Non-empty matching SHAs remain valid; non-empty mismatching SHAs remain the
# dedicated mismatch case (not reported as missing).
reasons=$(merge_approval_validation_reasons true "abc123" "def456" "xyz789" "def456")
case "$reasons" in
    *"Approved commit SHA is missing"*|*"Current commit SHA is missing"*) _fail "non-empty mismatch misreported as missing" ;;
    *"Commit SHA mismatch"*) _pass ;;
    *) _fail "non-empty mismatch reason not preserved";;
esac

# --- Approval summary ---
echo ""
echo "--- Approval Summary ---"
summary=$(merge_approval_summary "user" "2026-01-01T00:00:00Z" "true" "149" "148" "abc123" "def456")
case "$summary" in
    *"Approval Status: VALID"*) _pass ;;
    *) _fail "Summary should show VALID status";;
esac
case "$summary" in
    *"PR: #149"*) _pass ;;
    *) _fail "Summary should include PR number";;
esac
case "$summary" in
    *"Issue: #148"*) _pass ;;
    *) _fail "Summary should include Issue number";;
esac

summary=$(merge_approval_summary "user" "2026-01-01T00:00:00Z" "false" "149" "148" "abc123" "def456" "2026-01-02T00:00:00Z" "conflict resolution")
case "$summary" in
    *"Approval Status: INVALID"*) _pass ;;
    *) _fail "Invalid summary should show INVALID status";;
esac
case "$summary" in
    *"Reason: conflict resolution"*) _pass ;;
    *) _fail "Invalid summary should include reason";;
esac

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
