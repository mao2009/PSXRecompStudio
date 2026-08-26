#!/bin/sh
# Test Suite: Contracts
# Verifies state schema validation, version handling, and field validation

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

assert_output_contains() {
    _desc="$1"; _needle="$2"; shift 2
    _actual=$("$@" 2>/dev/null)
    case "$_actual" in *"$_needle"*) _pass ;; *) _fail "$_desc: '$_needle' not in '$_actual'" ;; esac
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../contracts.sh"

echo "=== Contracts Tests ==="
echo ""

# --- Version Validation ---
echo "--- Version Validation ---"

assert_true "version 2.0.0 valid" _contracts_validate_version 2.0.0
assert_true "version 2.1.0 valid" _contracts_validate_version 2.1.0
assert_true "version 2.10.5 valid" _contracts_validate_version 2.10.5
assert_false "version 1.0.0 invalid" _contracts_validate_version 1.0.0
assert_false "version 3.0.0 invalid" _contracts_validate_version 3.0.0
assert_false "version empty invalid" _contracts_validate_version ""
assert_false "version garbage invalid" _contracts_validate_version "not-a-version"

# --- Upgrade Check ---
echo ""
echo "--- Upgrade Check ---"

assert_true "1.0 -> 2.0 needs upgrade" _contracts_needs_upgrade 1.0.0 2.0.0
assert_false "2.0 -> 2.0 no upgrade" _contracts_needs_upgrade 2.0.0 2.0.0
assert_false "2.1 -> 2.0 no upgrade" _contracts_needs_upgrade 2.1.0 2.0.0
assert_true "1.5 -> 2.0 needs upgrade" _contracts_needs_upgrade 1.5.0 2.0.0

# --- Task Validation ---
echo ""
echo "--- Task Validation ---"

assert_true "valid task" _contracts_validate_task "task-1" 169 "/worktrees/169" "issue/169-desc" "Implement feature"
assert_false "missing task_id" _contracts_validate_task "" 169 "/worktrees/169" "issue/169" "prompt"
assert_false "missing issue_number" _contracts_validate_task "task-1" "" "/worktrees/169" "issue/169" "prompt"
assert_false "missing worktree_path" _contracts_validate_task "task-1" 169 "" "issue/169" "prompt"
assert_false "missing branch_name" _contracts_validate_task "task-1" 169 "/worktrees/169" "" "prompt"
assert_false "missing prompt" _contracts_validate_task "task-1" 169 "/worktrees/169" "issue/169" ""

# --- Result Validation ---
echo ""
echo "--- Result Validation ---"

assert_true "valid result" _contracts_validate_result "true" "task-1"
assert_false "missing success" _contracts_validate_result "" "task-1"
assert_false "missing task_id" _contracts_validate_result "true" ""

# --- Batch State Validation ---
echo ""
echo "--- Batch State Validation ---"

assert_true "valid batch state" _contracts_validate_batch_state "batch-1" "RUNNING" 3
assert_false "missing batch_id" _contracts_validate_batch_state "" "RUNNING" 3
assert_false "missing state" _contracts_validate_batch_state "batch-1" "" 3
assert_false "missing issue_count" _contracts_validate_batch_state "batch-1" "RUNNING" ""

# --- Issue State Validation ---
echo ""
echo "--- Issue State Validation ---"

assert_true "valid issue state" _contracts_validate_issue_state "issue-1" 169 "SUBAGENT_RUNNING"
assert_false "missing issue_id" _contracts_validate_issue_state "" 169 "SUBAGENT_RUNNING"
assert_false "missing issue_number" _contracts_validate_issue_state "issue-1" "" "SUBAGENT_RUNNING"
assert_false "missing state" _contracts_validate_issue_state "issue-1" 169 ""

# --- Unknown Field Detection ---
echo ""
echo "--- Unknown Field Detection ---"

assert_true "all fields known" _contracts_check_unknown_fields "a,b,c" "a,b,c"
assert_true "subset of known" _contracts_check_unknown_fields "a,b,c,d" "a,c"
assert_false "has unknown field" _contracts_check_unknown_fields "a,b,c" "a,b,x"

# --- Invalid State Handling ---
echo ""
echo "--- Invalid State Handling ---"

assert_output_contains "empty state recovery" "RECOVERY" _contracts_handle_invalid_state "" "batch"
assert_output_contains "unknown state recovery" "RECOVERY" _contracts_handle_invalid_state "BOGUS" "issue"
assert_output_contains "invalid chars recovery" "RECOVERY" _contracts_handle_invalid_state "running" "batch"

# --- Migration ---
echo ""
echo "--- Migration ---"

assert_true "v1 needs migration" _contracts_needs_migration 1.0.0
assert_false "v2 no migration" _contracts_needs_migration 2.0.0
assert_false "v3 no migration" _contracts_needs_migration 3.0.0

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
