#!/bin/sh
# Test Suite: Scheduler
# Verifies exact behavioral parity with PowerShell BatchScheduler.psm1

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_true() {
    _desc="$1"; shift
    if "$@" >/dev/null 2>&1; then _pass; else _fail "$_desc"; fi
}

assert_false() {
    _desc="$1"; shift
    if "$@" >/dev/null 2>&1; then _fail "$_desc (expected false)"; else _pass; fi
}

assert_output() {
    _desc="$1"; _expected="$2"; shift 2
    _actual=$("$@" 2>/dev/null)
    if [ "$_actual" = "$_expected" ]; then _pass; else _fail "$_desc: expected '$_expected', got '$_actual'"; fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../scheduler.sh"

echo "=== Scheduler Tests ==="
echo ""

# --- Initialization ---
echo "--- Initialization ---"

_sch_init 3
assert_output "max concurrency" "3" echo "$_SCH_MAX_CONCURRENCY"
assert_output "running slots" "0" echo "$_SCH_RUNNING_SLOTS"

_sch_init 1
assert_output "single slot max" "1" echo "$_SCH_MAX_CONCURRENCY"

_sch_init 5
assert_output "five slots max" "5" echo "$_SCH_MAX_CONCURRENCY"

# --- Slot Availability ---
echo ""
echo "--- Slot Availability ---"

_sch_init 3
assert_true "slot available (0/3)" _sch_slot_available

_sch_init 1
_sch_register issue-1
_sch_claim_slot issue-1
assert_false "no slot available (1/1)" _sch_slot_available

# --- Register Issues ---
echo ""
echo "--- Register Issues ---"

_sch_init 3
assert_true "register issue-1" _sch_register issue-1
assert_true "register issue-2" _sch_register issue-2
assert_true "register issue-3" _sch_register issue-3
assert_false "duplicate issue-1" _sch_register issue-1

# --- Claim/Release Slots ---
echo ""
echo "--- Claim/Release Slots ---"

_sch_init 2
_sch_register issue-1
_sch_register issue-2
_sch_register issue-3

assert_true "claim slot for issue-1" _sch_claim_slot issue-1
assert_output "issue-1 state" "SUBAGENT_STARTING" _sch_get_issue_state issue-1
assert_output "running slots 1" "1" echo "$_SCH_RUNNING_SLOTS"

assert_true "claim slot for issue-2" _sch_claim_slot issue-2
assert_output "running slots 2" "2" echo "$_SCH_RUNNING_SLOTS"
assert_false "no slot for issue-3 (2/2)" _sch_claim_slot issue-3

# Release slot
_sch_release_slot issue-1 COMPLETED
assert_output "running slots after release" "1" echo "$_SCH_RUNNING_SLOTS"
assert_output "issue-1 state" "COMPLETED" _sch_get_issue_state issue-1

# Claim again after release
assert_true "claim slot for issue-3 after release" _sch_claim_slot issue-3
assert_output "running slots 2 again" "2" echo "$_SCH_RUNNING_SLOTS"

# --- All Done ---
echo ""
echo "--- All Done ---"

_sch_init 2
_sch_register issue-1
_sch_register issue-2

# Not done yet
assert_false "not done with waiting issues" _sch_all_done

# Complete one
_sch_release_slot issue-1 COMPLETED
assert_false "not done with one remaining" _sch_all_done

# Complete both
_sch_release_slot issue-2 COMPLETED
assert_true "all done" _sch_all_done

# --- Failure Handling ---
echo ""
echo "--- Failure Handling ---"

_sch_init 3
_sch_register issue-1
_sch_register issue-2
_sch_claim_slot issue-1
_sch_claim_slot issue-2

_sch_release_slot issue-1 SUBAGENT_FAILED
assert_output "issue-1 failed" "SUBAGENT_FAILED" _sch_get_issue_state issue-1
assert_output "running slots" "1" echo "$_SCH_RUNNING_SLOTS"

_sch_release_slot issue-2 BLOCKED
assert_output "issue-2 blocked" "BLOCKED" _sch_get_issue_state issue-2
assert_output "running slots" "0" echo "$_SCH_RUNNING_SLOTS"

# --- Ready Issues ---
echo ""
echo "--- Ready Issues ---"

_sch_init 3
_sch_register issue-1
_sch_register issue-2
_sch_register issue-3

# All waiting: all should be ready
_ready=$(_sch_get_ready_issues)
_count=$(echo "$_ready" | wc -w)
assert_output "3 ready issues" "3" echo "$_count"

# Start one
_sch_claim_slot issue-1
_ready=$(_sch_get_ready_issues)
_count=$(echo "$_ready" | wc -w)
assert_output "2 ready issues after claim" "2" echo "$_count"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
