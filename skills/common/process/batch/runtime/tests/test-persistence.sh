#!/bin/sh
# Test Suite: Persistence
# Verifies JSON state I/O, atomic writes, version validation

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

# Extract a JSON string value from a string
_json_str() {
    _json="$1"
    _key="$2"
    printf '%s' "$_json" | sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -1
}

# Extract a JSON number value from a string
_json_num() {
    _json="$1"
    _key="$2"
    printf '%s' "$_json" | sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p" | head -1
}

assert_json_str() {
    _desc="$1"
    _expected="$2"
    _json="$3"
    _key="$4"
    _actual=$(_json_str "$_json" "$_key")
    if [ "$_actual" = "$_expected" ]; then
        _pass
    else
        _fail "$_desc: expected '$_expected', got '$_actual'"
    fi
}

assert_json_num() {
    _desc="$1"
    _expected="$2"
    _json="$3"
    _key="$4"
    _actual=$(_json_num "$_json" "$_key")
    if [ "$_actual" = "$_expected" ]; then
        _pass
    else
        _fail "$_desc: expected '$_expected', got '$_actual'"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../persistence.sh"

# Create temp dir for tests
_TEST_DIR=$(mktemp -d)
trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Persistence Tests ==="
echo ""

# --- JSON Helpers ---
echo "--- JSON Helpers ---"

_test_json_file="${_TEST_DIR}/test.json"
cat > "$_test_json_file" <<'JSON'
{
  "version": "2.0.0",
  "name": "test-batch",
  "count": 42,
  "empty_field": null,
  "nullable_val": "hello"
}
JSON

assert_output "json_get_string" "test-batch" _json_get_string "$_test_json_file" "name"
assert_output "json_get_number" "42" _json_get_number "$_test_json_file" "count"

assert_true "json_is_null detects null" _json_is_null "$_test_json_file" "empty_field"
assert_false "json_is_null rejects string" _json_is_null "$_test_json_file" "name"
assert_true "json_is_null missing key" _json_is_null "$_test_json_file" "nonexistent"

assert_output "json_get_nullable_string" "hello" _json_get_nullable_string "$_test_json_file" "nullable_val"
assert_output "json_get_nullable_string null" "" _json_get_nullable_string "$_test_json_file" "empty_field"
assert_output "json_get_nullable_number" "" _json_get_nullable_number "$_test_json_file" "empty_field"

# --- File Paths ---
echo ""
echo "--- File Paths ---"

_persistence_set_state_dir "$_TEST_DIR"
assert_output "batch state path" "${_TEST_DIR}/.batch-state-test1.json" _persistence_get_batch_state_path "test1"
assert_output "issue states path" "${_TEST_DIR}/.batch-issues-test1.json" _persistence_get_issue_states_path "test1"

# --- Batch State Operations ---
echo ""
echo "--- Batch State Operations ---"

_batch_json=$(_persistence_new_batch_state "test-batch" 5)
assert_json_str "new batch version" "2.0.0" "$_batch_json" "version"
assert_json_str "new batch_id" "test-batch" "$_batch_json" "batch_id"
assert_json_num "new issue_count" "5" "$_batch_json" "issue_count"
assert_json_str "new state" "BATCH_INITIALIZING" "$_batch_json" "state"

# Save and load
_batch_file="${_TEST_DIR}/.batch-state-save-test.json"
_persistence_save_batch_state "$_batch_json" "$_batch_file"
assert_file_exists "batch state saved" "$_batch_file"

_loaded=$(_persistence_load_batch_state "$_batch_file")
assert_json_str "loaded batch version" "2.0.0" "$_loaded" "version"

# Version validation
_bad_file="${_TEST_DIR}/.batch-state-bad.json"
cat > "$_bad_file" <<'JSON'
{
  "version": "1.0.0",
  "batch_id": "old"
}
JSON
_err=$(_persistence_load_batch_state "$_bad_file" 2>&1)
assert_true "rejects v1" printf '%s' "$_err" | grep -q "Incompatible"

_no_version_file="${_TEST_DIR}/.batch-state-noversion.json"
cat > "$_no_version_file" <<'JSON'
{
  "batch_id": "no-version"
}
JSON
_err2=$(_persistence_load_batch_state "$_no_version_file" 2>&1)
assert_true "rejects missing version" printf '%s' "$_err2" | grep -q "Missing"

_not_found_file="${_TEST_DIR}/nonexistent.json"
_err3=$(_persistence_load_batch_state "$_not_found_file" 2>&1)
assert_true "rejects missing file" printf '%s' "$_err3" | grep -q "not found"

# Update batch state
_persistence_update_batch_state "$_batch_file" "state" "RUNNING"
_loaded2=$(_persistence_load_batch_state "$_batch_file")
assert_json_str "updated batch state" "RUNNING" "$_loaded2" "state"

_persistence_update_batch_state "$_batch_file" "completed_count" "3"
_loaded3=$(_persistence_load_batch_state "$_batch_file")
assert_json_num "updated completed_count" "3" "$_loaded3" "completed_count"

_persistence_update_batch_state "$_batch_file" "failure_reason" "test error"
_loaded4=$(_persistence_load_batch_state "$_batch_file")
assert_json_str "updated failure_reason" "test error" "$_loaded4" "failure_reason"

_persistence_update_batch_state "$_batch_file" "failure_reason" ""
_loaded5=$(_persistence_load_batch_state "$_batch_file")
assert_true "cleared failure_reason" printf '%s' "$_loaded5" | grep -q '"failure_reason"[[:space:]]*:[[:space:]]*null'

# --- Issue State Operations ---
echo ""
echo "--- Issue State Operations ---"

_issue_json=$(_persistence_new_issue_state "issue-42" "42" "Fix login bug")
assert_json_str "new issue issue_id" "issue-42" "$_issue_json" "issue_id"
assert_json_num "new issue issue_number" "42" "$_issue_json" "issue_number"
assert_json_str "new issue description" "Fix login bug" "$_issue_json" "description"
assert_json_str "new issue state" "WAITING_DEPENDENCY" "$_issue_json" "state"
assert_json_num "new issue retry_count" "0" "$_issue_json" "retry_count"

# Save issue states
_issues_file="${_TEST_DIR}/.batch-issues-save-test.json"
cat > "$_issues_file" <<EOF
{
  "version": "2.0.0",
  "batch_id": "save-test",
  "issues": {
${_issue_json}
  }
}
EOF

_persistence_save_issue_states "$(cat "$_issues_file")" "${_TEST_DIR}/.batch-issues-saved.json"
assert_file_exists "issue states saved" "${_TEST_DIR}/.batch-issues-saved.json"

# Load issue states
_loaded_issues=$(_persistence_load_issue_states "${_TEST_DIR}/.batch-issues-saved.json")
assert_json_str "loaded issue version" "2.0.0" "$_loaded_issues" "version"

# Update issue state
_update_file="${_TEST_DIR}/.batch-issues-update-test.json"
cat > "$_update_file" <<'JSON'
{
  "version": "2.0.0",
  "batch_id": "update-test",
  "issues": {
    "issue-7": {
      "issue_id": "issue-7",
      "issue_number": 7,
      "description": "Update test",
      "state": "WAITING_DEPENDENCY",
      "worktree_path": null,
      "branch_name": null,
      "pr_number": null,
      "pr_url": null,
      "commit_sha": null,
      "retry_count": 0,
      "last_error": null,
      "created_at": "2026-01-01T00:00:00Z",
      "updated_at": "2026-01-01T00:00:00Z"
    }
  }
}
JSON

_persistence_update_issue_state "$_update_file" "issue-7" "state" "RUNNING"
_loaded_update=$(_persistence_load_issue_states "$_update_file")
assert_json_str "updated issue state" "RUNNING" "$_loaded_update" "state"

_persistence_update_issue_state "$_update_file" "issue-7" "worktree_path" "/tmp/wt-7"
_loaded_update2=$(_persistence_load_issue_states "$_update_file")
assert_json_str "updated worktree_path" "/tmp/wt-7" "$_loaded_update2" "worktree_path"

_persistence_update_issue_state "$_update_file" "issue-7" "pr_number" "123"
_loaded_update3=$(_persistence_load_issue_states "$_update_file")
assert_json_num "updated pr_number" "123" "$_loaded_update3" "pr_number"

_persistence_update_issue_state "$_update_file" "issue-7" "retry_count" "2"
_loaded_update4=$(_persistence_load_issue_states "$_update_file")
assert_json_num "updated retry_count" "2" "$_loaded_update4" "retry_count"

_persistence_update_issue_state "$_update_file" "issue-7" "commit_sha" "abc123"
_loaded_update5=$(_persistence_load_issue_states "$_update_file")
assert_json_str "updated commit_sha" "abc123" "$_loaded_update5" "commit_sha"

# --- Convenience Functions ---
echo ""
echo "--- Convenience Functions ---"

_init_dir="${_TEST_DIR}/init-test"
mkdir -p "$_init_dir"
_persistence_init "batch-100" "$_init_dir" 3
assert_file_exists "init creates batch state" "${_init_dir}/.batch-state-batch-100.json"
assert_file_exists "init creates issues state" "${_init_dir}/.batch-issues-batch-100.json"

# Resume scenario
_output=$(_persistence_init "batch-100" "$_init_dir" 3 2>&1)
assert_true "init resume detects existing" printf '%s' "$_output" | grep -q "RESUME"

# Load through convenience
_loaded_batch=$(_persistence_load_batch "batch-100")
assert_json_str "convenience load version" "2.0.0" "$_loaded_batch" "version"

# Save through convenience
_persistence_save_batch "batch-100" "$(printf '%s' "$_loaded_batch" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "PLANNING"/')"
_reloaded=$(_persistence_load_batch "batch-100")
assert_json_str "convenience save state" "PLANNING" "$_reloaded" "state"

# --- Atomic Write ---
echo ""
echo "--- Atomic Write ---"

_atomic_file="${_TEST_DIR}/atomic-test.json"
atomic_content='{"test": "atomic"}'
_persistence_save_batch_state "$atomic_content" "$_atomic_file"
assert_file_exists "atomic write creates file" "$_atomic_file"

# Verify no .tmp files remain
_tmp_count=$(ls -1 "${_TEST_DIR}/atomic-test.json.tmp."* 2>/dev/null | wc -l)
if [ "$_tmp_count" -eq 0 ]; then
    _pass
else
    _fail "atomic write left .tmp files"
fi

# --- Summary ---
echo ""
echo "====================="
echo "Persistence Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
