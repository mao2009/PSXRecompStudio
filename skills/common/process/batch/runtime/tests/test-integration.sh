#!/bin/sh
# Test Suite: Runtime Orchestrator Integration
# Verifies orchestrator end-to-end with Test Provider (no AI agent needed)

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

assert_file_exists() {
    _desc="$1"
    _file="$2"
    if [ -f "$_file" ]; then
        _pass
    else
        _fail "$_desc: file not found: $_file"
    fi
}

assert_json_str() {
    _desc="$1"
    _expected="$2"
    _json="$3"
    _key="$4"
    _actual=$(printf '%s' "$_json" | sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -1)
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
    _actual=$(printf '%s' "$_json" | sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p" | head -1)
    if [ "$_actual" = "$_expected" ]; then
        _pass
    else
        _fail "$_desc: expected '$_expected', got '$_actual'"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
RUNTIME_DIR="${SCRIPT_DIR}/.."

. "$RUNTIME_DIR/core/state-machine.sh"
. "$RUNTIME_DIR/core/dependency-graph.sh"
. "$RUNTIME_DIR/core/scheduler.sh"
. "$RUNTIME_DIR/core/retry.sh"
. "$RUNTIME_DIR/core/contracts.sh"
. "$RUNTIME_DIR/persistence.sh"
. "$RUNTIME_DIR/git-operations.sh"
. "$RUNTIME_DIR/agent-runtime.sh"
. "$RUNTIME_DIR/github-operations.sh"
. "$RUNTIME_DIR/merge-queue.sh"
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
_STATE_DIR="${_TEST_DIR}/state"
mkdir -p "$_STATE_DIR"

trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Runtime Orchestrator Integration Tests ==="
echo ""

# --- Test 1: Full Lifecycle ---
echo "--- Test 1: Full Lifecycle (1 issue) ---"

_BATCH_ID="integration-1"
_persistence_set_state_dir "$_STATE_DIR"

# Initialize batch
_persistence_init "$_BATCH_ID" "$_STATE_DIR" 1
assert_file_exists "batch state created" "${_STATE_DIR}/.batch-state-${_BATCH_ID}.json"
assert_file_exists "issues state created" "${_STATE_DIR}/.batch-issues-${_BATCH_ID}.json"

# Add issue
_issue_state=$(_persistence_new_issue_state "issue-1" "1" "Test issue 1")
_issues_file="${_STATE_DIR}/.batch-issues-${_BATCH_ID}.json"
cat > "$_issues_file" <<EOF
{
  "version": "2.0.0",
  "batch_id": "${_BATCH_ID}",
  "issues": {
${_issue_state}
  }
}
EOF

# Select provider
_ari_select_provider "test"
_provider=$(_ari_get_provider)
if [ "$_provider" = "test" ]; then
    _pass
else
    _fail "provider is test, got: $_provider"
fi

# Create worktree
_wt="${_WORKTREE_ROOT}/wt-1"
_git_create_worktree "$_wt" "issue/1-test" "HEAD" "$_TEST_REPO"
assert_file_exists "worktree created" "$_wt/README.md"

# Update issue state
_persistence_update_issue_state "$_issues_file" "issue-1" \
    "state" "SUBAGENT_STARTING" \
    "worktree_path" "$_wt" \
    "branch_name" "issue/1-test"

# Build and dispatch task
_result_file="${_wt}/.subagent/result.json"
mkdir -p "${_wt}/.subagent"
_task_json=$(_ari_build_task "issue-1" "1" "Test issue 1" "$_wt" "issue/1-test" "Implement feature" "$_result_file" 1)
_task_file="${_TEST_DIR}/task-1.json"
printf '%s' "$_task_json" > "$_task_file"

_handle=$(_ari_launch "$_task_file")
if [ -n "$_handle" ]; then
    _pass
else
    _fail "handle returned"
fi

# Wait for completion
sleep 3

# Check result
assert_file_exists "result file exists" "$_result_file"
_success=$(sed -n 's/.*"success"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' "$_result_file" | head -1)
if [ "$_success" = "true" ]; then
    _pass
else
    _fail "task succeeded"
fi

# Verify commit in worktree
_commit_count=$(git -C "$_wt" log --oneline 2>/dev/null | wc -l)
if [ "$_commit_count" -gt 1 ]; then
    _pass
else
    _fail "expected git commit in worktree"
fi

# --- Test 2: Dependency Graph Integration ---
echo ""
echo "--- Test 2: Dependency Graph Integration ---"

_dg_init
_dg_add_node "A" ""
_dg_add_node "B" ""
_dg_add_node "C" ""

_dg_add_edge "B" "A"
_dg_add_edge "C" "B"

assert_false "cycle detected" _dg_detect_cycle

_completed=""
_ready=$(_dg_get_ready_issues "$_completed")
if echo "$_ready" | grep -q "A"; then
    _pass
else
    _fail "A is ready initially"
fi

_completed="A"
_ready2=$(_dg_get_ready_issues "$_completed")
if echo "$_ready2" | grep -q "B"; then
    _pass
else
    _fail "B ready after A"
fi
if echo "$_ready2" | grep -q "C"; then
    _fail "C not ready yet"
else
    _pass
fi

_completed="A B"
_ready3=$(_dg_get_ready_issues "$_completed")
if echo "$_ready3" | grep -q "C"; then
    _pass
else
    _fail "C ready after A,B"
fi

# --- Test 3: Retry Integration ---
echo ""
echo "--- Test 3: Retry Integration ---"

_retry_count=0
_should=$(_retry_should_retry "NETWORK" "$_retry_count" "3")
_retryable=$?
if [ "$_retryable" -eq 0 ]; then
    _pass
else
    _fail "NETWORK is retryable"
fi

_backoff=$(_retry_calculate_backoff 1 5 120)
if [ "$_backoff" -ge 5 ] && [ "$_backoff" -le 120 ]; then
    _pass
else
    _fail "backoff in range"
fi

# --- Test 4: Merge Queue Integration ---
echo ""
echo "--- Test 4: Merge Queue Integration ---"

_merge_queue_init
_merge_queue_add "200" "issue-200" "/tmp/wt-nonexistent-200" "branch-200"
_merge_queue_add "201" "issue-201" "/tmp/wt-nonexistent-201" "branch-201"

_count=$(_merge_queue_count)
if [ "$_count" = "2" ]; then
    _pass
else
    _fail "queue has 2 items, got: $_count"
fi

_merge_queue_process_all "."
_failed=$(_merge_queue_get_failed)
if echo "$_failed" | grep -q "200" && echo "$_failed" | grep -q "201"; then
    _pass
else
    _fail "both items failed"
fi

# --- Test 5: State Machine Integration ---
echo ""
echo "--- Test 5: State Machine Integration ---"

assert_true "valid: INIT->PLANNING" _sm_valid_batch_transition BATCH_INITIALIZING PLANNING
assert_true "valid: PLANNING->SCHEDULING" _sm_valid_batch_transition PLANNING SCHEDULING
assert_true "valid: SCHEDULING->RUNNING" _sm_valid_batch_transition SCHEDULING RUNNING
assert_true "valid: RUNNING->WAITING_FOR_MERGE" _sm_valid_batch_transition RUNNING WAITING_FOR_MERGE
assert_true "valid: WAITING_FOR_MERGE->MERGING" _sm_valid_batch_transition WAITING_FOR_MERGE MERGING
assert_true "valid: MERGING->CLEANUP" _sm_valid_batch_transition MERGING CLEANUP
assert_true "valid: CLEANUP->COMPLETED" _sm_valid_batch_transition CLEANUP COMPLETED

# --- Summary ---
echo ""
echo "====================="
echo "Integration Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
