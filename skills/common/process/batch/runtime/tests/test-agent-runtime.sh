#!/bin/sh
# Test Suite: Agent Runtime Interface
# Verifies task/result JSON construction, provider selection, handle creation

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
RUNTIME_DIR="${SCRIPT_DIR}/.."
. "$RUNTIME_DIR/persistence.sh"
. "$RUNTIME_DIR/agent-runtime.sh"
. "$RUNTIME_DIR/adapters/test/adapter.sh"

# Create temp dir for tests
_TEST_DIR=$(mktemp -d)
trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Agent Runtime Tests ==="
echo ""

# --- Task JSON Construction ---
echo "--- Task JSON Construction ---"

_task_json=$(_ari_build_task "task-1" "42" "Fix login bug" "/tmp/wt-42" "issue/42-fix" "Implement fix" "/tmp/result.json" 30)

# Verify task JSON contains expected fields
assert_true "task has task_id" printf '%s' "$_task_json" | grep -q '"task_id": "task-1"'
assert_true "task has issue_number 42" printf '%s' "$_task_json" | grep -q '"issue_number": 42'
assert_true "task has description" printf '%s' "$_task_json" | grep -q '"description": "Fix login bug"'
assert_true "task has worktree_path" printf '%s' "$_task_json" | grep -q '"worktree_path": "/tmp/wt-42"'
assert_true "task has branch_name" printf '%s' "$_task_json" | grep -q '"branch_name": "issue/42-fix"'
assert_true "task has prompt" printf '%s' "$_task_json" | grep -q '"prompt": "Implement fix"'
assert_true "task has result_file" printf '%s' "$_task_json" | grep -q '"result_file": "/tmp/result.json"'
assert_true "task has timeout" printf '%s' "$_task_json" | grep -q '"timeout_minutes": 30'
assert_true "task has provider" printf '%s' "$_task_json" | grep -q '"provider": "test"'

# --- Task JSON with special characters ---
echo ""
echo "--- Task JSON with Special Characters ---"

_task_special=$(_ari_build_task "task-2" "1" "Test" "/tmp/wt" "branch" "This has \"quotes\" and \\backslash")
assert_true "task escapes quotes in prompt" printf '%s' "$_task_special" | grep -q 'This has \\"quotes\\" and \\\\backslash'

# --- Result JSON Construction ---
echo ""
echo "--- Result JSON Construction ---"

_result_json=$(_ari_build_result "true" "task-1" "123" "abc123" "" "" "test" "file1.txt,file2.txt")
assert_true "result has success true" printf '%s' "$_result_json" | grep -q '"success": true'
assert_true "result has task_id" printf '%s' "$_result_json" | grep -q '"task_id": "task-1"'
assert_true "result has pr_number 123" printf '%s' "$_result_json" | grep -q '"pr_number": 123'
assert_true "result has commit_sha" printf '%s' "$_result_json" | grep -q '"commit_sha": "abc123"'
assert_true "result has provider" printf '%s' "$_result_json" | grep -q '"provider": "test"'
assert_true "result has changed_files" printf '%s' "$_result_json" | grep -q '"changed_files":'

# Result with error
_result_err=$(_ari_build_result "false" "task-2" "null" "" "timeout" "TIMEOUT" "test" "")
assert_true "result has success false" printf '%s' "$_result_err" | grep -q '"success": false'
assert_true "result has error" printf '%s' "$_result_err" | grep -q '"error": "timeout"'
assert_true "result has error_category" printf '%s' "$_result_err" | grep -q '"error_category": "TIMEOUT"'
assert_true "result has pr_number null" printf '%s' "$_result_err" | grep -q '"pr_number": null'
assert_true "result has commit_sha null" printf '%s' "$_result_err" | grep -q '"commit_sha": null'

# --- Provider Handle ---
echo ""
echo "--- Provider Handle ---"

_handle=$(_ari_create_handle "test" "task-3" "12345")
assert_true "handle has provider" printf '%s' "$_handle" | grep -q '"provider": "test"'
assert_true "handle has task_id" printf '%s' "$_handle" | grep -q '"task_id": "task-3"'
assert_true "handle has pid" printf '%s' "$_handle" | grep -q '"pid": 12345'
assert_true "handle has status running" printf '%s' "$_handle" | grep -q '"status": "running"'

# Handle without pid
_handle_no_pid=$(_ari_create_handle "test" "task-4")
assert_true "handle has pid null" printf '%s' "$_handle_no_pid" | grep -q '"pid": null'

# --- Provider Selection ---
echo ""
echo "--- Provider Selection ---"

# Test provider is always available
assert_true "test provider available" _ari_provider_available "test"
assert_false "unknown provider not available" _ari_provider_available "nonexistent"

# Select test provider
_sel=$(_ari_select_provider "test")
assert_output "selected test" "test" printf '%s' "$_sel"
assert_output "get provider" "test" _ari_get_provider

# Select with invalid preferred, falls back
_sel2=$(_ari_select_provider "nonexistent")
assert_output "fallback to test" "test" _ari_get_provider

# --- Provider Interface (Test Provider) ---
echo ""
echo "--- Provider Interface (Test Provider) ---"

# Write task to file
_task_file="${_TEST_DIR}/task.json"
cat > "$_task_file" <<'JSON'
{
  "task_id": "test-task-1",
  "issue_number": 100,
  "description": "Test task",
  "worktree_path": "/tmp/wt-test",
  "branch_name": "test-branch",
  "prompt": "Do something",
  "result_file": "/tmp/result-test.json",
  "timeout_minutes": 1,
  "provider": "test"
}
JSON

# Launch test provider
_ARI_SELECTED_PROVIDER="test"
_handle_file=$(_ari_launch "$_task_file")
assert_true "launch returns handle" test -f "$_handle_file"
assert_true "launch handle has provider" grep -q '"provider": "test"' "$_handle_file"
assert_true "launch handle has task_id" grep -q '"task_id": "test-task-1"' "$_handle_file"

# Poll test provider
_status=$(_ari_poll "$_handle_file")
assert_true "poll returns status" printf '%s' "$_status" | grep -q '"status"'

# Wait test provider
_result=$(_ari_wait "$_handle_file" 5)
assert_true "wait returns result" printf '%s' "$_result" | grep -q '"success"'
assert_true "wait result has task_id" printf '%s' "$_result" | grep -q '"task_id": "test-task-1"'

# Cancel test provider
_ari_cancel "$_handle_file"
assert_true "cancel succeeds" test $? -eq 0

# Cleanup test provider
_ari_cleanup "$_handle_file"
assert_true "cleanup succeeds" test $? -eq 0

# --- Summary ---
echo ""
echo "====================="
echo "Agent Runtime Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
