#!/bin/sh
# Test Suite: Contracts Integration
# Verifies contracts.sh validation is wired into orchestrator

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_exit_code() {
    _desc="$1"
    _expected="$2"
    shift 2
    "$@" 2>/dev/null
    _actual=$?
    if [ "$_actual" = "$_expected" ]; then
        _pass
    else
        _fail "$_desc: expected exit $_expected, got $_actual"
    fi
}

assert_output_contains() {
    _desc="$1"
    _expected="$2"
    shift 2
    _actual=$("$@" 2>&1)
    if printf '%s' "$_actual" | grep -q "$_expected"; then
        _pass
    else
        _fail "$_desc: output does not contain '$_expected'"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../core/contracts.sh"

echo "=== Contracts Integration Tests ==="
echo ""

# --- Version Validation ---
echo "--- Version Validation ---"

_output=$(_contracts_validate_version "2.0.0")
assert_output_contains "v2.0.0 compatible" "Compatible" printf '%s' "$_output"

_output=$(_contracts_validate_version "2.1.0")
assert_output_contains "v2.1.0 compatible" "Compatible" printf '%s' "$_output"

_output=$(_contracts_validate_version "1.0.0")
assert_output_contains "v1.0.0 incompatible" "Incompatible" printf '%s' "$_output"

assert_exit_code "empty version fails" 1 _contracts_validate_version ""

# --- Task Validation ---
echo ""
echo "--- Task Validation ---"

assert_exit_code "valid task passes" 0 _contracts_validate_task "task-1" "100" "/tmp/wt" "branch" "do stuff"
assert_exit_code "missing task_id fails" 1 _contracts_validate_task "" "100" "/tmp/wt" "branch" "do stuff"
assert_exit_code "missing issue_number fails" 1 _contracts_validate_task "task-1" "" "/tmp/wt" "branch" "do stuff"
assert_exit_code "missing worktree fails" 1 _contracts_validate_task "task-1" "100" "" "branch" "do stuff"
assert_exit_code "missing branch fails" 1 _contracts_validate_task "task-1" "100" "/tmp/wt" "" "do stuff"
assert_exit_code "missing prompt fails" 1 _contracts_validate_task "task-1" "100" "/tmp/wt" "branch" ""

# --- Result Validation ---
echo ""
echo "--- Result Validation ---"

assert_exit_code "valid result passes" 0 _contracts_validate_result "true" "task-1"
assert_exit_code "missing success fails" 1 _contracts_validate_result "" "task-1"
assert_exit_code "missing task_id fails" 1 _contracts_validate_result "true" ""

# --- Batch State Validation ---
echo ""
echo "--- Batch State Validation ---"

assert_exit_code "valid batch state passes" 0 _contracts_validate_batch_state "batch-100" "PLANNING" "5"
assert_exit_code "missing batch_id fails" 1 _contracts_validate_batch_state "" "PLANNING" "5"
assert_exit_code "missing state fails" 1 _contracts_validate_batch_state "batch-100" "" "5"
assert_exit_code "missing issue_count fails" 1 _contracts_validate_batch_state "batch-100" "PLANNING" ""

# --- Issue State Validation ---
echo ""
echo "--- Issue State Validation ---"

assert_exit_code "valid issue state passes" 0 _contracts_validate_issue_state "issue-100" "100" "SUBAGENT_RUNNING"
assert_exit_code "missing issue_id fails" 1 _contracts_validate_issue_state "" "100" "SUBAGENT_RUNNING"
assert_exit_code "missing issue_number fails" 1 _contracts_validate_issue_state "issue-100" "" "SUBAGENT_RUNNING"
assert_exit_code "missing state fails" 1 _contracts_validate_issue_state "issue-100" "100" ""

# --- Invalid State Handling ---
echo ""
echo "--- Invalid State Handling ---"

_output=$(_contracts_handle_invalid_state "" "test")
assert_output_contains "empty state suggests init" "RECOVER" printf '%s' "$_output"

_output=$(_contracts_handle_invalid_state "INVALID STATE!" "test")
assert_output_contains "invalid chars suggests reset" "RECOVER" printf '%s' "$_output"

_output=$(_contracts_handle_invalid_state "UNKNOWN_STATE" "test")
assert_output_contains "unknown state suggests FAILED" "FAILED" printf '%s' "$_output"

# --- Migration Check ---
echo ""
echo "--- Migration Check ---"

_output=$(_contracts_needs_migration "1.0.0")
assert_output_contains "v1 needs migration" "migration" printf '%s' "$_output"

_output=$(_contracts_needs_migration "2.0.0")
assert_output_contains "v2 no migration" "No migration" printf '%s' "$_output"

# --- Summary ---
echo ""
echo "====================="
echo "Contracts Integration Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
