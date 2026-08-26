#!/bin/sh
# Test Suite: Test Adapter
# Verifies test provider behavior (success, fail, noop, error)

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

assert_file_exists() {
    _desc="$1"
    _file="$2"
    if [ -f "$_file" ]; then
        _pass
    else
        _fail "$_desc: file not found: $_file"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
RUNTIME_DIR="${SCRIPT_DIR}/.."
. "$RUNTIME_DIR/persistence.sh"
. "$RUNTIME_DIR/agent-runtime.sh"
. "$RUNTIME_DIR/adapters/test/adapter.sh"

# Create temp dir and git repo for tests
_TEST_DIR=$(mktemp -d)
_TEST_REPO="${_TEST_DIR}/repo"
mkdir -p "$_TEST_REPO"
git -C "$_TEST_REPO" init --quiet 2>/dev/null
git -C "$_TEST_REPO" config user.email "test@test.com" 2>/dev/null
git -C "$_TEST_REPO" config user.name "Test" 2>/dev/null
echo "init" > "${_TEST_REPO}/README.md"
git -C "$_TEST_REPO" add -A 2>/dev/null
git -C "$_TEST_REPO" commit -m "init" --quiet 2>/dev/null

_WORKTREE_ROOT="${_TEST_DIR}/worktrees"
mkdir -p "$_WORKTREE_ROOT"

trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Test Adapter Tests ==="
echo ""

# --- Provider Availability ---
echo "--- Provider Availability ---"

assert_true "test provider available" _ari_provider_available "test"
assert_true "test provider available via interface" _ari_provider_available_test

# --- Success Behavior ---
echo ""
echo "--- Success Behavior ---"

_wt_success="${_WORKTREE_ROOT}/wt-success"
git -C "$_TEST_REPO" worktree add -b "test-success" "$_wt_success" HEAD --quiet 2>/dev/null

_result_file_success="${_TEST_DIR}/result-success.json"
_task_file_success="${_TEST_DIR}/task-success.json"
cat > "$_task_file_success" <<EOF
{
  "task_id": "success-task",
  "issue_number": 500,
  "description": "success task",
  "worktree_path": "$_wt_success",
  "branch_name": "test-success",
  "prompt": "Implement feature",
  "result_file": "$_result_file_success",
  "timeout_minutes": 1,
  "provider": "test"
}
EOF

_handle=$(_ari_launch_test "$_task_file_success")
assert_true "success handle exists" test -n "$_handle"

# Wait for background process
sleep 3

assert_file_exists "success result file" "$_result_file_success"
_success_val=$(sed -n 's/.*"success"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' "$_result_file_success" | head -1)
assert_output "success result is true" "true" printf '%s' "$_success_val"

# Verify git commit was made in worktree
_commit_count=$(git -C "$_wt_success" log --oneline 2>/dev/null | wc -l)
if [ "$_commit_count" -gt 1 ]; then
    _pass
else
    _fail "success: expected git commit in worktree"
fi

# --- Fail Behavior ---
echo ""
echo "--- Fail Behavior ---"

_wt_fail="${_WORKTREE_ROOT}/wt-fail"
git -C "$_TEST_REPO" worktree add -b "test-fail" "$_wt_fail" HEAD --quiet 2>/dev/null

_result_file_fail="${_TEST_DIR}/result-fail.json"
_task_file_fail="${_TEST_DIR}/task-fail.json"
cat > "$_task_file_fail" <<EOF
{
  "task_id": "fail-task",
  "issue_number": 501,
  "description": "fail task",
  "worktree_path": "$_wt_fail",
  "branch_name": "test-fail",
  "prompt": "This will fail",
  "result_file": "$_result_file_fail",
  "timeout_minutes": 1,
  "provider": "test"
}
EOF

_handle_fail=$(_ari_launch_test "$_task_file_fail")
sleep 3

assert_file_exists "fail result file" "$_result_file_fail"
_fail_val=$(sed -n 's/.*"success"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' "$_result_file_fail" | head -1)
assert_output "fail result is false" "false" printf '%s' "$_fail_val"

_fail_cat=$(sed -n 's/.*"error_category"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$_result_file_fail" | head -1)
assert_output "fail error_category" "test_failure" printf '%s' "$_fail_cat"

# --- Noop Behavior ---
echo ""
echo "--- Noop Behavior ---"

_result_file_noop="${_TEST_DIR}/result-noop.json"
_task_file_noop="${_TEST_DIR}/task-noop.json"
cat > "$_task_file_noop" <<EOF
{
  "task_id": "noop-task",
  "issue_number": 502,
  "description": "no-op task",
  "worktree_path": "",
  "branch_name": "",
  "prompt": "Do nothing",
  "result_file": "$_result_file_noop",
  "timeout_minutes": 1,
  "provider": "test"
}
EOF

_handle_noop=$(_ari_launch_test "$_task_file_noop")
sleep 2

assert_file_exists "noop result file" "$_result_file_noop"
_noop_val=$(sed -n 's/.*"success"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' "$_result_file_noop" | head -1)
assert_output "noop result is true" "true" printf '%s' "$_noop_val"

# --- Error Behavior ---
echo ""
echo "--- Error Behavior ---"

_wt_error="${_WORKTREE_ROOT}/wt-error"
git -C "$_TEST_REPO" worktree add -b "test-error" "$_wt_error" HEAD --quiet 2>/dev/null

_result_file_error="${_TEST_DIR}/result-error.json"
_task_file_error="${_TEST_DIR}/task-error.json"
cat > "$_task_file_error" <<EOF
{
  "task_id": "error-task",
  "issue_number": 503,
  "description": "error task",
  "worktree_path": "$_wt_error",
  "branch_name": "test-error",
  "prompt": "Encounter error",
  "result_file": "$_result_file_error",
  "timeout_minutes": 1,
  "provider": "test"
}
EOF

_handle_error=$(_ari_launch_test "$_task_file_error")
sleep 3

assert_file_exists "error result file" "$_result_file_error"
_error_val=$(sed -n 's/.*"success"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' "$_result_file_error" | head -1)
assert_output "error result is false" "false" printf '%s' "$_error_val"

_error_cat=$(sed -n 's/.*"error_category"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$_result_file_error" | head -1)
assert_output "error error_category" "code_error" printf '%s' "$_error_cat"

# --- Cancel/Cleanup ---
echo ""
echo "--- Cancel/Cleanup ---"

_ari_cancel_test "$_handle"
assert_true "cancel succeeds" test $? -eq 0

_ari_cleanup_test "$_handle"
assert_true "cleanup succeeds" test $? -eq 0

# --- Summary ---
echo ""
echo "====================="
echo "Test Adapter Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
