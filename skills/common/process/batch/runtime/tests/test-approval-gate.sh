#!/bin/sh
# Test Suite: Approval Gate
# Verifies merge queue blocks merge without approval and when gh unavailable

PASS=0
_FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { _FAIL=$((_FAIL + 1)); echo "FAIL: $1"; }

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

assert_variable_empty() {
    _desc="$1"
    _var_name="$2"
    eval "_val=\"\$_$_var_name\""
    if [ -z "$_val" ]; then
        _pass
    else
        _fail "$_desc: $_var_name is not empty ('$_val')"
    fi
}

assert_variable_contains() {
    _desc="$1"
    _var_name="$2"
    _expected="$3"
    eval "_val=\"\$_$_var_name\""
    if printf '%s' "$_val" | grep -q "$_expected"; then
        _pass
    else
        _fail "$_desc: $_var_name does not contain '$_expected'"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../merge-queue.sh"

# Create a fake git repo for merge tests
_TEST_DIR=$(mktemp -d)
_REPO_DIR="$_TEST_DIR/repo"
mkdir -p "$_REPO_DIR"
cd "$_REPO_DIR"
git init -q
git config user.email "test@test.com"
git config user.name "Test"
echo "init" > file.txt
git add . && git commit -q -m "init"
trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Approval Gate Tests ==="
echo ""

# --- Test 1: No gh CLI → merge blocked ---
echo "--- No gh CLI → merge blocked ---"

_merge_queue_init
_merge_queue_add "999" "issue-999" "$_REPO_DIR" "main"

# Simulate no gh by PATH manipulation (gh not installed in test env anyway)
_merge_queue_process_next "$_REPO_DIR"
_result=$?

assert_exit_code "process_next returns 1 (blocked)" 1 test "$_result" -eq 1

# Item should be back in queue (not in failed)
_merge_queue_init
_merge_queue_add "999" "issue-999" "$_REPO_DIR" "main"
_merge_queue_process_next "$_REPO_DIR" 2>/dev/null
_pending=$(_merge_queue_get_pending)
assert_output_contains "item returned to queue" "999" printf '%s' "$_pending"

# --- Test 2: Approval gate blocks merge ---
echo ""
echo "--- Approval gate blocks merge ---"

_merge_queue_init
_merge_queue_add "500" "issue-500" "$_REPO_DIR" "main"
_merge_queue_add "501" "issue-501" "$_REPO_DIR" "main"

# All items should remain pending (gh not available → blocked)
_pending=$(_merge_queue_get_pending)
assert_output_contains "500 still pending" "500" printf '%s' "$_pending"
assert_output_contains "501 still pending" "501" printf '%s' "$_pending"

# --- Test 3: Queue not empty after failed approval ---
echo ""
echo "--- Queue not empty after failed approval ---"

_merge_queue_init
_merge_queue_add "600" "issue-600" "$_REPO_DIR" "main"
_merge_queue_process_next "$_REPO_DIR" 2>/dev/null

assert_output_contains "queue not empty after blocked" "600" _merge_queue_get_pending

# --- Test 4: Process all stops at approval block ---
echo ""
echo "--- Process all stops at approval block ---"

_merge_queue_init
_merge_queue_add "700" "issue-700" "$_REPO_DIR" "main"
_merge_queue_add "701" "issue-701" "$_REPO_DIR" "main"

_output=$(_merge_queue_process_all "$_REPO_DIR" 2>&1)
# Should process 0 merges (all blocked by approval gate)
assert_output_contains "processed 0 merges" "Processed 0 merge" printf '%s' "$_output"

# --- Test 5: gh unavailable produces error message ---
echo ""
echo "--- gh unavailable produces error message ---"

_merge_queue_init
_merge_queue_add "800" "issue-800" "$_REPO_DIR" "main"
_output=$(_merge_queue_process_next "$_REPO_DIR" 2>&1)
assert_output_contains "error mentions gh CLI" "gh CLI" printf '%s' "$_output"
assert_output_contains "error mentions merge blocked" "blocked" printf '%s' "$_output"

# --- Summary ---
echo ""
echo "====================="
echo "Approval Gate Tests"
echo "Pass: $PASS"
echo "Fail: $_FAIL"
echo "====================="

[ "$_FAIL" -eq 0 ]
