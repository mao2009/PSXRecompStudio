#!/bin/sh
# Test Suite: State Machine
# Verifies exact behavioral parity with PowerShell BatchStateMachine + IssueStateMachine

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

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../state-machine.sh"

echo "=== State Machine Tests ==="
echo ""

# --- Batch State Transitions ---
echo "--- Batch State Transitions ---"

assert_true "BATCH_INITIALIZING -> PLANNING" _sm_valid_batch_transition BATCH_INITIALIZING PLANNING
assert_true "BATCH_INITIALIZING -> FAILED" _sm_valid_batch_transition BATCH_INITIALIZING FAILED
assert_false "BATCH_INITIALIZING -> RUNNING (invalid)" _sm_valid_batch_transition BATCH_INITIALIZING RUNNING
assert_false "BATCH_INITIALIZING -> COMPLETED (invalid)" _sm_valid_batch_transition BATCH_INITIALIZING COMPLETED

assert_true "PLANNING -> SCHEDULING" _sm_valid_batch_transition PLANNING SCHEDULING
assert_true "PLANNING -> FAILED" _sm_valid_batch_transition PLANNING FAILED
assert_false "PLANNING -> RUNNING (invalid)" _sm_valid_batch_transition PLANNING RUNNING

assert_true "SCHEDULING -> RUNNING" _sm_valid_batch_transition SCHEDULING RUNNING
assert_true "SCHEDULING -> FAILED" _sm_valid_batch_transition SCHEDULING FAILED
assert_false "SCHEDULING -> PLANNING (invalid)" _sm_valid_batch_transition SCHEDULING PLANNING

assert_true "RUNNING -> WAITING_FOR_MERGE" _sm_valid_batch_transition RUNNING WAITING_FOR_MERGE
assert_true "RUNNING -> FAILED" _sm_valid_batch_transition RUNNING FAILED
assert_false "RUNNING -> COMPLETED (invalid)" _sm_valid_batch_transition RUNNING COMPLETED

assert_true "WAITING_FOR_MERGE -> MERGING" _sm_valid_batch_transition WAITING_FOR_MERGE MERGING
assert_true "WAITING_FOR_MERGE -> COMPLETED" _sm_valid_batch_transition WAITING_FOR_MERGE COMPLETED
assert_true "WAITING_FOR_MERGE -> FAILED" _sm_valid_batch_transition WAITING_FOR_MERGE FAILED
assert_false "WAITING_FOR_MERGE -> CLEANUP (invalid)" _sm_valid_batch_transition WAITING_FOR_MERGE CLEANUP

assert_true "MERGING -> CLEANUP" _sm_valid_batch_transition MERGING CLEANUP
assert_true "MERGING -> FAILED" _sm_valid_batch_transition MERGING FAILED
assert_false "MERGING -> COMPLETED (invalid)" _sm_valid_batch_transition MERGING COMPLETED

assert_true "CLEANUP -> COMPLETED" _sm_valid_batch_transition CLEANUP COMPLETED
assert_true "CLEANUP -> FAILED" _sm_valid_batch_transition CLEANUP FAILED
assert_false "CLEANUP -> MERGING (invalid)" _sm_valid_batch_transition CLEANUP MERGING

assert_false "COMPLETED -> any (terminal)" _sm_valid_batch_transition COMPLETED PLANNING
assert_false "FAILED -> any (terminal)" _sm_valid_batch_transition FAILED RUNNING

# --- Batch Terminal States ---
echo ""
echo "--- Batch Terminal States ---"

assert_true "COMPLETED is terminal" _sm_is_batch_terminal COMPLETED
assert_true "FAILED is terminal" _sm_is_batch_terminal FAILED
assert_false "RUNNING is not terminal" _sm_is_batch_terminal RUNNING
assert_false "PLANNING is not terminal" _sm_is_batch_terminal PLANNING

# --- Batch Valid Transitions ---
echo ""
echo "--- Batch Valid Transitions ---"

assert_output "BATCH_INITIALIZING transitions" "PLANNING FAILED" _sm_get_valid_batch_transitions BATCH_INITIALIZING
assert_output "RUNNING transitions" "WAITING_FOR_MERGE FAILED" _sm_get_valid_batch_transitions RUNNING
assert_output "COMPLETED transitions (empty)" "" _sm_get_valid_batch_transitions COMPLETED

# --- Issue State Transitions ---
echo ""
echo "--- Issue State Transitions ---"

assert_true "SUBAGENT_STARTING -> SUBAGENT_RUNNING" _sm_valid_issue_transition SUBAGENT_STARTING SUBAGENT_RUNNING
assert_true "SUBAGENT_STARTING -> SUBAGENT_RETRYING" _sm_valid_issue_transition SUBAGENT_STARTING SUBAGENT_RETRYING
assert_true "SUBAGENT_STARTING -> SUBAGENT_FAILED" _sm_valid_issue_transition SUBAGENT_STARTING SUBAGENT_FAILED
assert_false "SUBAGENT_STARTING -> PR_READY (invalid)" _sm_valid_issue_transition SUBAGENT_STARTING PR_READY

assert_true "SUBAGENT_RUNNING -> PR_READY" _sm_valid_issue_transition SUBAGENT_RUNNING PR_READY
assert_true "SUBAGENT_RUNNING -> SUBAGENT_RETRYING" _sm_valid_issue_transition SUBAGENT_RUNNING SUBAGENT_RETRYING
assert_true "SUBAGENT_RUNNING -> SUBAGENT_FAILED" _sm_valid_issue_transition SUBAGENT_RUNNING SUBAGENT_FAILED

assert_true "SUBAGENT_RETRYING -> SUBAGENT_STARTING" _sm_valid_issue_transition SUBAGENT_RETRYING SUBAGENT_STARTING
assert_true "SUBAGENT_RETRYING -> SUBAGENT_FAILED" _sm_valid_issue_transition SUBAGENT_RETRYING SUBAGENT_FAILED
assert_false "SUBAGENT_RETRYING -> PR_READY (invalid)" _sm_valid_issue_transition SUBAGENT_RETRYING PR_READY

assert_true "READY_FOR_NATIVE_DISPATCH -> DISPATCHED" _sm_valid_issue_transition READY_FOR_NATIVE_DISPATCH DISPATCHED
assert_true "READY_FOR_NATIVE_DISPATCH -> FAILED" _sm_valid_issue_transition READY_FOR_NATIVE_DISPATCH FAILED
assert_false "READY_FOR_NATIVE_DISPATCH -> SUBAGENT_RUNNING (invalid)" _sm_valid_issue_transition READY_FOR_NATIVE_DISPATCH SUBAGENT_RUNNING

assert_true "DISPATCHED -> SUBAGENT_RUNNING" _sm_valid_issue_transition DISPATCHED SUBAGENT_RUNNING
assert_true "DISPATCHED -> FAILED" _sm_valid_issue_transition DISPATCHED FAILED
assert_false "DISPATCHED -> PR_READY (invalid)" _sm_valid_issue_transition DISPATCHED PR_READY

assert_false "SUBAGENT_FAILED -> any (terminal)" _sm_valid_issue_transition SUBAGENT_FAILED SUBAGENT_STARTING

assert_true "WAITING_FOR_SUBAGENT -> SUBAGENT_STARTING" _sm_valid_issue_transition WAITING_FOR_SUBAGENT SUBAGENT_STARTING
assert_true "WAITING_FOR_SUBAGENT -> BLOCKED" _sm_valid_issue_transition WAITING_FOR_SUBAGENT BLOCKED

assert_true "WAITING_DEPENDENCY -> SUBAGENT_STARTING" _sm_valid_issue_transition WAITING_DEPENDENCY SUBAGENT_STARTING
assert_false "WAITING_DEPENDENCY -> BLOCKED (invalid)" _sm_valid_issue_transition WAITING_DEPENDENCY BLOCKED

assert_true "PR_READY -> WAITING_FOR_APPROVAL" _sm_valid_issue_transition PR_READY WAITING_FOR_APPROVAL
assert_false "PR_READY -> MERGING (invalid)" _sm_valid_issue_transition PR_READY MERGING

assert_true "WAITING_FOR_APPROVAL -> READY_FOR_MERGE" _sm_valid_issue_transition WAITING_FOR_APPROVAL READY_FOR_MERGE
assert_true "WAITING_FOR_APPROVAL -> PR_READY" _sm_valid_issue_transition WAITING_FOR_APPROVAL PR_READY

assert_true "READY_FOR_MERGE -> MERGING" _sm_valid_issue_transition READY_FOR_MERGE MERGING
assert_false "READY_FOR_MERGE -> COMPLETED (invalid)" _sm_valid_issue_transition READY_FOR_MERGE COMPLETED

assert_true "MERGING -> COMPLETED" _sm_valid_issue_transition MERGING COMPLETED
assert_true "MERGING -> FAILED" _sm_valid_issue_transition MERGING FAILED
assert_true "MERGING -> PR_READY (conflict)" _sm_valid_issue_transition MERGING PR_READY

assert_false "COMPLETED -> any (terminal)" _sm_valid_issue_transition COMPLETED MERGING

assert_true "BLOCKED -> WAITING_FOR_SUBAGENT" _sm_valid_issue_transition BLOCKED WAITING_FOR_SUBAGENT
assert_false "BLOCKED -> SUBAGENT_STARTING (invalid)" _sm_valid_issue_transition BLOCKED SUBAGENT_STARTING

assert_false "FAILED -> any (terminal)" _sm_valid_issue_transition FAILED SUBAGENT_STARTING

# --- Issue Terminal States ---
echo ""
echo "--- Issue Terminal States ---"

assert_true "SUBAGENT_FAILED is terminal" _sm_is_issue_terminal SUBAGENT_FAILED
assert_true "COMPLETED is terminal" _sm_is_issue_terminal COMPLETED
assert_true "FAILED is terminal" _sm_is_issue_terminal FAILED
assert_false "RUNNING is not terminal" _sm_is_issue_terminal SUBAGENT_RUNNING
assert_false "BLOCKED is not terminal" _sm_is_issue_terminal BLOCKED

# --- Issue Active States ---
echo ""
echo "--- Issue Active States ---"

assert_true "SUBAGENT_STARTING is active" _sm_is_issue_active SUBAGENT_STARTING
assert_true "SUBAGENT_RUNNING is active" _sm_is_issue_active SUBAGENT_RUNNING
assert_true "SUBAGENT_RETRYING is active" _sm_is_issue_active SUBAGENT_RETRYING
assert_true "WAITING_FOR_SUBAGENT is active" _sm_is_issue_active WAITING_FOR_SUBAGENT
assert_true "WAITING_DEPENDENCY is active" _sm_is_issue_active WAITING_DEPENDENCY
assert_true "PR_READY is active" _sm_is_issue_active PR_READY
assert_true "WAITING_FOR_APPROVAL is active" _sm_is_issue_active WAITING_FOR_APPROVAL
assert_true "READY_FOR_MERGE is active" _sm_is_issue_active READY_FOR_MERGE
assert_true "MERGING is active" _sm_is_issue_active MERGING
assert_false "COMPLETED is not active" _sm_is_issue_active COMPLETED
assert_false "FAILED is not active" _sm_is_issue_active FAILED
assert_false "SUBAGENT_FAILED is not active" _sm_is_issue_active SUBAGENT_FAILED
assert_false "BLOCKED is not active" _sm_is_issue_active BLOCKED

# --- Issue Valid Transitions ---
echo ""
echo "--- Issue Valid Transitions ---"

assert_output "MERGING transitions" "COMPLETED FAILED PR_READY" _sm_get_valid_issue_transitions MERGING
assert_output "BLOCKED transitions" "WAITING_FOR_SUBAGENT" _sm_get_valid_issue_transitions BLOCKED
assert_output "COMPLETED transitions (empty)" "" _sm_get_valid_issue_transitions COMPLETED
assert_output "READY_FOR_NATIVE_DISPATCH transitions" "DISPATCHED SUBAGENT_FAILED FAILED" _sm_get_valid_issue_transitions READY_FOR_NATIVE_DISPATCH
assert_output "DISPATCHED transitions" "SUBAGENT_RUNNING SUBAGENT_FAILED FAILED" _sm_get_valid_issue_transitions DISPATCHED
assert_output "FAILED transitions (empty)" "" _sm_get_valid_issue_transitions FAILED

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
