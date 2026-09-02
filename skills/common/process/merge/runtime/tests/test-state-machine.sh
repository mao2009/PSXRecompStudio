#!/bin/sh
# Test Suite: Merge State Machine
# Verifies behavioral parity with PowerShell MergeStateMachine.psm1

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

assert_output() {
    _desc="$1"
    _expected="$2"
    shift 2
    _actual=$("$@" 2>/dev/null)
    if [ "$_actual" = "$_expected" ]; then
        _pass
    else
        _fail "$_desc: expected '$_expected', got '$_actual'"
    fi
}

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/../state-machine.sh"

echo "=== Merge State Machine Tests ==="
echo ""

# --- All states present ---
echo "--- All States Present ---"
assert_true "TRIGGER_CHECK is a valid state" merge_is_valid_state TRIGGER_CHECK
assert_true "APPROVAL_VALIDATION is a valid state" merge_is_valid_state APPROVAL_VALIDATION
assert_true "MAIN_HEAD_REFRESH is a valid state" merge_is_valid_state MAIN_HEAD_REFRESH
assert_true "REBASE is a valid state" merge_is_valid_state REBASE
assert_true "CONFLICT is a valid state" merge_is_valid_state CONFLICT
assert_true "VALIDATING is a valid state" merge_is_valid_state VALIDATING
assert_true "MERGING is a valid state" merge_is_valid_state MERGING
assert_true "MERGED is a valid state" merge_is_valid_state MERGED
assert_true "CLEANUP is a valid state" merge_is_valid_state CLEANUP
assert_true "COMPLETED is a valid state" merge_is_valid_state COMPLETED
assert_true "FAILED is a valid state" merge_is_valid_state FAILED
assert_false "UNKNOWN is not a valid state" merge_is_valid_state UNKNOWN

# --- Valid transitions ---
echo ""
echo "--- Valid Transitions ---"
assert_true "TRIGGER_CHECK -> APPROVAL_VALIDATION" merge_valid_transition TRIGGER_CHECK APPROVAL_VALIDATION
assert_true "TRIGGER_CHECK -> FAILED" merge_valid_transition TRIGGER_CHECK FAILED
assert_true "APPROVAL_VALIDATION -> MAIN_HEAD_REFRESH" merge_valid_transition APPROVAL_VALIDATION MAIN_HEAD_REFRESH
assert_true "APPROVAL_VALIDATION -> FAILED" merge_valid_transition APPROVAL_VALIDATION FAILED
assert_true "MAIN_HEAD_REFRESH -> REBASE" merge_valid_transition MAIN_HEAD_REFRESH REBASE
assert_true "REBASE -> VALIDATING" merge_valid_transition REBASE VALIDATING
assert_true "REBASE -> APPROVAL_VALIDATION" merge_valid_transition REBASE APPROVAL_VALIDATION
assert_true "REBASE -> CONFLICT" merge_valid_transition REBASE CONFLICT
assert_true "REBASE -> FAILED" merge_valid_transition REBASE FAILED
assert_true "VALIDATING -> MERGING" merge_valid_transition VALIDATING MERGING
assert_true "VALIDATING -> FAILED" merge_valid_transition VALIDATING FAILED
assert_true "MERGING -> MERGED" merge_valid_transition MERGING MERGED
assert_true "MERGING -> FAILED" merge_valid_transition MERGING FAILED
assert_true "MERGED -> CLEANUP" merge_valid_transition MERGED CLEANUP
assert_true "CLEANUP -> COMPLETED" merge_valid_transition CLEANUP COMPLETED
assert_true "CLEANUP -> FAILED" merge_valid_transition CLEANUP FAILED

# --- Invalid transitions ---
echo ""
echo "--- Invalid Transitions ---"
assert_false "TRIGGER_CHECK -> MERGING (invalid)" merge_valid_transition TRIGGER_CHECK MERGING
assert_false "COMPLETED -> TRIGGER_CHECK (invalid)" merge_valid_transition COMPLETED TRIGGER_CHECK
assert_false "CONFLICT -> VALIDATING (invalid)" merge_valid_transition CONFLICT VALIDATING
assert_false "FAILED -> REBASE (invalid)" merge_valid_transition FAILED REBASE
assert_false "REBASE -> VALIDATING (terminal conflict no-op)" merge_valid_transition CONFLICT CLEANUP

# --- Valid transitions from a state ---
echo ""
echo "--- Get Valid Transitions ---"
assert_output "REBASE transitions" "VALIDATING APPROVAL_VALIDATION CONFLICT FAILED" merge_get_valid_transitions REBASE
assert_output "TRIGGER_CHECK transitions" "APPROVAL_VALIDATION FAILED" merge_get_valid_transitions TRIGGER_CHECK
assert_output "MAIN_HEAD_REFRESH transitions" "REBASE" merge_get_valid_transitions MAIN_HEAD_REFRESH
assert_output "CONFLICT transitions (empty)" "" merge_get_valid_transitions CONFLICT
assert_output "COMPLETED transitions (empty)" "" merge_get_valid_transitions COMPLETED
assert_output "FAILED transitions (empty)" "" merge_get_valid_transitions FAILED

# --- Terminal states ---
echo ""
echo "--- Terminal States ---"
assert_true "COMPLETED is terminal" merge_is_terminal COMPLETED
assert_true "FAILED is terminal" merge_is_terminal FAILED
assert_false "TRIGGER_CHECK is not terminal" merge_is_terminal TRIGGER_CHECK
assert_false "MERGED is not terminal" merge_is_terminal MERGED

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
